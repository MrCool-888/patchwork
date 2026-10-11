using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;

namespace Patchwork
{
    internal static class ControllerTests
    {
        static int assertions;
        internal static void Run(string root)
        {
            assertions = 0;
            EnsureDispatcher();

            PreviewDispatchAndRetry(root);
            StalePreviewAfterCatalogRefresh(root);
            InspectionCoalescesAndIgnoresClosedWindow(root);
            HistoryIgnoresPageChanges(root);
            ApplyInspectsOffDispatcher(root);
            RestoreUsesControllerAndProtectsBusyWindow(root);
            TransactionLockDoesNotBlockBundleSelection(root);
            PreviewControlsPageLargeFiles(root);
            Console.WriteLine("Controller regression checks: " + assertions + " assertions passed.");
        }

        internal static void EnsureDispatcher()
        {
            if (Application.Current == null)
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        }

        static void PreviewDispatchAndRetry(string root)
        {
            using (var fixture = new Fixture(root, "preview"))
            {
                fixture.Load(); fixture.Ready();
                var entered = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
                int attempts = 0;
                fixture.Controller.PreviewGateForTest = delegate
                {
                    if (Interlocked.Increment(ref attempts) == 1) throw new IOException("fixture preview failure");
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Preview worker gate timed out.");
                };
                DispatcherTimer timer = null;
                try
                {
                    Task failed = (Task)Call(fixture.Controller, "BuildPreview");
                    PumpTask(failed);
                    Assert(!Field<bool>(fixture.Controller, "busy") && fixture.Control<Button>("ApplyButton").IsEnabled == false, "A failed preview left the controller busy or apply enabled.");
                    Assert(fixture.Control<TextBlock>("StatusText").Text.Contains("fixture preview failure"), "The preview failure was not shown.");

                    int ticks = 0;
                    timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
                    timer.Tick += delegate { ticks++; }; timer.Start();
                    Task preview = (Task)Call(fixture.Controller, "BuildPreview");
                    WaitFor(() => entered.IsSet, "Preview did not reach its worker gate.");
                    Assert(Field<bool>(fixture.Controller, "busy"), "Preview did not set the busy state.");
                    Assert(fixture.Control<ProgressBar>("OperationProgress").Visibility == Visibility.Visible, "Preview did not show its progress indicator.");
                    Assert(!fixture.Control<Button>("ApplyButton").IsEnabled, "Apply was enabled while preview was running.");
                    fixture.Window.Close();
                    Assert(fixture.Window.IsVisible, "Closing during a busy preview was not blocked.");
                    PumpTask((Task)Call(fixture.Controller, "BuildPreview"));
                    WaitFor(() => ticks >= 2, "Dispatcher timer did not tick while preview remained gated.");
                    Assert(!preview.IsCompleted, "Preview completed before its worker gate was released.");
                    release.Set(); PumpTask(preview); timer.Stop();
                    Assert(!Field<bool>(fixture.Controller, "busy") && Field<PatchPlan>(fixture.Controller, "preview") != null && fixture.Control<Button>("ApplyButton").IsEnabled, "Retry did not produce an applicable preview.");
                }
                finally { release.Set(); if (timer != null) timer.Stop(); fixture.Controller.PreviewGateForTest = null; fixture.Close(); }
            }
        }

        static void StalePreviewAfterCatalogRefresh(string root)
        {
            using (var fixture = new Fixture(root, "stale-preview"))
            {
                fixture.Load(); fixture.Ready();
                var entered = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
                fixture.Controller.PreviewGateForTest = delegate
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Preview worker gate timed out.");
                };
                try
                {
                    Task preview = (Task)Call(fixture.Controller, "BuildPreview"); WaitFor(() => entered.IsSet, "Preview did not reach its worker gate.");
                    string recipe = Path.Combine(fixture.Data, "recipes", "sandbox-v1.json");
                    var refreshed = Json.Parse(TestFixtures.DemoRecipe()); refreshed["author"] = "Refreshed fixture";
                    File.WriteAllText(recipe, Json.Pretty(refreshed), new UTF8Encoding(false));
                    SetField(fixture.Controller, "catalogRefreshPending", true);
                    release.Set(); PumpTask(preview); fixture.Ready();
                    Assert(Field<PatchBundle>(fixture.Controller, "current").Author == "Refreshed fixture", "Busy completion did not refresh the saved fixture recipe.");
                    Assert(Field<PatchPlan>(fixture.Controller, "preview") == null && !fixture.Control<Button>("ApplyButton").IsEnabled, "A preview from the old recipe was accepted after refresh.");
                    Assert(Field<string>(fixture.Controller, "page") == "library", "Stale preview navigated away from the library.");
                }
                finally { release.Set(); fixture.Controller.PreviewGateForTest = null; fixture.Close(); }
            }
        }

        static void InspectionCoalescesAndIgnoresClosedWindow(string root)
        {
            using (var fixture = new Fixture(root, "inspection"))
            {
                var entered = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
                var latestEntered = new ManualResetEventSlim(); var latestRelease = new ManualResetEventSlim(); int jobs = 0;
                fixture.Controller.InspectionGateForTest = delegate
                {
                    int job = Interlocked.Increment(ref jobs);
                    if (job == 1)
                    {
                        entered.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Inspection worker gate timed out.");
                        throw new IOException("stale fixture inspection failure");
                    }
                    latestEntered.Set();
                    if (!latestRelease.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Latest inspection worker gate timed out.");
                };
                int ticks = 0;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
                timer.Tick += delegate { ticks++; }; timer.Start();
                try
                {
                    fixture.Load(); WaitFor(() => entered.IsSet, "Initial target inspection did not start.");
                    string wrongTarget = TestFixtures.CreateDemo(Path.Combine(fixture.Root, "wrong-target"));
                    File.WriteAllText(Path.Combine(wrongTarget, "app.version"), "wrong version");
                    for (int i = 0; i < 20; i++)
                    {
                        Field<Dictionary<string, string>>(fixture.Controller, "targets")[Field<PatchBundle>(fixture.Controller, "current").Id] = i == 19 ? wrongTarget : fixture.Target;
                        Call(fixture.Controller, "UpdateSession");
                    }
                    Task inspection = Field<Task>(fixture.Controller, "sessionTask");
                    WaitFor(() => ticks >= 2, "Dispatcher timer did not tick during target inspection.");
                    Assert(!inspection.IsCompleted, "Target inspection completed before its worker gate was released.");
                    release.Set(); WaitFor(() => latestEntered.IsSet, "The latest inspection did not run after a stale worker exception.");
                    WaitFor(() => ticks >= 4, "Dispatcher timer did not tick while the latest inspection was gated.");
                    Assert(!inspection.IsCompleted, "The latest inspection completed before its worker gate was released.");
                    latestRelease.Set(); PumpTask(inspection); fixture.Ready(); timer.Stop();
                    Assert(jobs <= 2, "Target changes queued more than the running and latest inspections.");
                    Assert(jobs == 2, "The latest target inspection was not run after the in-flight result.");
                    Assert(fixture.Control<TextBlock>("TargetStatus").Text.Contains("Version fingerprint mismatch"), "The latest target result did not replace the stale result.");
                }
                finally { release.Set(); latestRelease.Set(); timer.Stop(); fixture.Controller.InspectionGateForTest = null; fixture.Close(); }
            }

            using (var fixture = new Fixture(root, "inspection-close"))
            {
                var entered = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
                fixture.Controller.InspectionGateForTest = delegate
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Inspection worker gate timed out.");
                };
                try
                {
                    fixture.Load(); WaitFor(() => entered.IsSet, "Target inspection did not start before close.");
                    string status = fixture.Control<TextBlock>("TargetStatus").Text;
                    fixture.Window.Close();
                    Assert(!fixture.Window.IsVisible, "A read-only target inspection prevented the window from closing.");
                    release.Set(); PumpTask(Field<Task>(fixture.Controller, "sessionTask"));
                    Assert(Field<bool>(fixture.Controller, "closed") && fixture.Control<TextBlock>("TargetStatus").Text == status, "A pending inspection updated the closed window.");
                }
                finally { release.Set(); fixture.Controller.InspectionGateForTest = null; fixture.Close(); }
            }
        }

        static void HistoryIgnoresPageChanges(string root)
        {
            using (var fixture = new Fixture(root, "history"))
            {
                fixture.Load(); fixture.Ready();
                var entered = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
                fixture.Controller.HistoryGateForTest = delegate
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("History worker gate timed out.");
                };
                int ticks = 0; var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
                timer.Tick += delegate { ticks++; }; timer.Start();
                try
                {
                    Call(fixture.Controller, "ShowPage", "history"); WaitFor(() => entered.IsSet, "History load did not reach its worker gate.");
                    Task history = Field<Task>(fixture.Controller, "historyTask");
                    WaitFor(() => ticks >= 2, "Dispatcher timer did not tick during history loading.");
                    Assert(!history.IsCompleted, "History loading completed before its worker gate was released.");
                    Call(fixture.Controller, "ShowPage", "about"); release.Set(); PumpTask(Field<Task>(fixture.Controller, "historyTask"));
                    timer.Stop();
                    var list = fixture.Control<StackPanel>("HistoryList");
                    Assert(list.Children.Count == 1 && ((TextBlock)list.Children[0]).Text == "Loading patch history…", "A stale history load rendered after leaving the history page.");
                    Assert(Field<string>(fixture.Controller, "page") == "about", "History completion changed the active page.");
                }
                finally { release.Set(); timer.Stop(); fixture.Controller.HistoryGateForTest = null; fixture.Close(); }
            }
        }

        static void ApplyInspectsOffDispatcher(string root)
        {
            using (var fixture = new Fixture(root, "apply"))
            {
                fixture.Load(); fixture.Ready(); PumpTask((Task)Call(fixture.Controller, "BuildPreview"));
                var entered = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
                fixture.Controller.InspectionGateForTest = delegate
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Post-apply inspection gate timed out.");
                };
                DispatcherTimer timer = null;
                try
                {
                    int ticks = 0; timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
                    timer.Tick += delegate { ticks++; }; timer.Start();
                    Task apply = (Task)Call(fixture.Controller, "Apply");
                    WaitFor(() => entered.IsSet, "Apply did not schedule its follow-up target inspection.");
                    PumpTask(apply);
                    WaitFor(() => ticks >= 2, "Dispatcher timer did not tick while post-apply inspection remained gated.");
                    Assert(!Field<Task>(fixture.Controller, "sessionTask").IsCompleted, "Post-apply inspection completed before its worker gate was released.");
                    timer.Stop();
                    Assert(File.ReadAllText(Path.Combine(fixture.Target, "settings.json")).Contains("midnight"), "Controller apply did not write the fixture change.");
                    Assert(Field<PatchEngine>(fixture.Controller, "engine").History().Single().State == "Applied", "Controller apply did not record its transaction: " + fixture.Control<TextBlock>("StatusText").Text);
                }
                finally { release.Set(); if (timer != null) timer.Stop(); fixture.Controller.InspectionGateForTest = null; fixture.Close(); }
            }
        }

        static void RestoreUsesControllerAndProtectsBusyWindow(string root)
        {
            using (var fixture = new Fixture(root, "restore"))
            {
                fixture.Load(); fixture.Ready();
                string config = Path.Combine(fixture.Target, "settings.json");
                byte[] original = File.ReadAllBytes(config);
                PumpTask((Task)Call(fixture.Controller, "BuildPreview"));
                PumpTask((Task)Call(fixture.Controller, "Apply")); fixture.Ready();
                PatchEngine engine = Field<PatchEngine>(fixture.Controller, "engine");
                Journal journal = engine.History().Single();
                Assert(journal.State == "Applied", "Restore fixture did not begin with an applied transaction.");

                SetField(fixture.Controller, "ConfirmDialogForTest", new Func<string, bool>(delegate { return true; }));
                var restoreEntered = new ManualResetEventSlim(); var restoreRelease = new ManualResetEventSlim();
                var inspectionEntered = new ManualResetEventSlim(); var inspectionRelease = new ManualResetEventSlim();
                SetField(fixture.Controller, "RestoreGateForTest", new Action(delegate
                {
                    restoreEntered.Set();
                    if (!restoreRelease.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Restore worker gate timed out.");
                }));
                fixture.Controller.InspectionGateForTest = delegate
                {
                    inspectionEntered.Set();
                    if (!inspectionRelease.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Post-restore inspection gate timed out.");
                };
                int ticks = 0; var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
                timer.Tick += delegate { ticks++; }; timer.Start();
                try
                {
                    Task restore = (Task)Call(fixture.Controller, "Restore", journal);
                    WaitFor(() => restoreEntered.IsSet, "Restore did not reach its background worker gate.");
                    Assert(Field<bool>(fixture.Controller, "busy") && fixture.Control<ProgressBar>("OperationProgress").Visibility == Visibility.Visible, "Restore did not show its busy phase.");
                    fixture.Window.Close();
                    Assert(fixture.Window.IsVisible, "Closing during restore was not blocked.");
                    WaitFor(() => ticks >= 2, "Dispatcher timer did not tick during restore.");
                    Assert(!restore.IsCompleted, "Restore completed before its worker gate was released.");
                    restoreRelease.Set();
                    WaitFor(() => inspectionEntered.IsSet, "Restore did not start its follow-up fingerprint inspection.");
                    PumpTask(restore);
                    Assert(File.ReadAllBytes(config).SequenceEqual(original), "Controller restore did not restore the exact fixture bytes.");
                    Assert(engine.History().Single().State == "Restored", "Controller restore did not persist the restored journal state.");
                    Assert(!Field<Task>(fixture.Controller, "sessionTask").IsCompleted, "Post-restore inspection completed before its gate was released.");
                }
                finally
                {
                    restoreRelease.Set(); inspectionRelease.Set(); timer.Stop();
                    fixture.Controller.InspectionGateForTest = null;
                    SetField(fixture.Controller, "RestoreGateForTest", null);
                    SetField(fixture.Controller, "ConfirmDialogForTest", null);
                    fixture.Close();
                }
                fixture.Ready();
            }
        }

        static void PreviewControlsPageLargeFiles(string root)
        {
            using (var fixture = new Fixture(root, "paging", true))
            {
                fixture.Load(); fixture.Ready(); PumpTask((Task)Call(fixture.Controller, "BuildPreview"));
                var picker = fixture.Control<ComboBox>("PreviewFilePicker");
                var preview = Field<PatchPlan>(fixture.Controller, "preview");
                var hugeIndex = preview.Files.FindIndex(x => x.RelativePath == "large.js");
                var smallIndex = preview.Files.FindIndex(x => x.RelativePath == "small.txt");
                Assert(hugeIndex >= 0 && smallIndex >= 0, "The fixture preview omitted one of its files.");
                picker.SelectedIndex = hugeIndex;
                Call(fixture.Controller, "MovePreviewPage", 1);
                Assert(fixture.Control<TextBlock>("PreviewPageLabel").Text.StartsWith("Page 2 of "), "The actual preview controls did not advance the large file to page two.");
                Assert(fixture.Control<TextBox>("BeforeText").Text.Length <= PreviewPaging.PageSize && fixture.Control<TextBox>("AfterText").Text.Length <= PreviewPaging.PageSize, "A preview control exceeded the page size.");
                picker.SelectedIndex = smallIndex;
                Assert(fixture.Control<TextBlock>("PreviewPageLabel").Text == "Page 1 of 1", "Changing to a small file did not reset the preview page.");
                Assert(!fixture.Control<Button>("PreviewPreviousButton").IsEnabled && !fixture.Control<Button>("PreviewNextButton").IsEnabled, "Paging controls stayed enabled for the one-page file.");
                Assert(fixture.Control<TextBox>("BeforeText").Text == "small original" && fixture.Control<TextBox>("AfterText").Text == "small patched", "The small-file preview displayed unexpected content.");
            }
        }

        static void TransactionLockDoesNotBlockBundleSelection(string root)
        {
            using (var fixture = new Fixture(root, "transaction-lock"))
            {
                fixture.Load(); fixture.Ready();
                object transactionLock = Field<object>(Field<PatchEngine>(fixture.Controller, "engine"), "transactionLock");
                var entered = new ManualResetEventSlim(); var release = new ManualResetEventSlim(); int lockHeld = 0;
                Task holder = Task.Run(() =>
                {
                    Monitor.Enter(transactionLock);
                    try { Interlocked.Exchange(ref lockHeld, 1); entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); }
                    finally { Interlocked.Exchange(ref lockHeld, 0); Monitor.Exit(transactionLock); }
                });
                var watchdog = Task.Run(() => { if (!release.Wait(TimeSpan.FromMilliseconds(2500))) release.Set(); });
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) }; int ticks = 0;
                timer.Tick += delegate { ticks++; };
                try
                {
                    WaitFor(() => entered.IsSet, "Could not acquire the isolated engine transaction lock.");
                    timer.Start();
                    Call(fixture.Controller, "SetBundle", 0);
                    Assert(fixture.Control<TextBlock>("SessionTitle").Text == "Demo sandbox", "Bundle metadata did not update while history inspection was locked.");
                    WaitFor(() => ticks >= 2 && Interlocked.CompareExchange(ref lockHeld, 0, 0) == 1, "Bundle selection blocked dispatcher heartbeats behind the transaction lock.");
                    Assert(Interlocked.CompareExchange(ref lockHeld, 0, 0) == 1 && !Field<Task>(fixture.Controller, "selectionTask").IsCompleted, "Applied-selection inspection did not remain asynchronous while the lock was held.");
                    Assert(!Field<Task>(fixture.Controller, "sessionTask").IsCompleted, "Target inspection completed while the transaction lock was held.");
                }
                finally
                {
                    release.Set(); timer.Stop();
                    PumpTask(holder); PumpTask(watchdog);
                    fixture.Controller.InspectionGateForTest = null;
                    fixture.Close();
                }
                fixture.Ready();
            }
        }

        static void Assert(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }

        static T Field<T>(object controller, string name)
        {
            return (T)controller.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
        }

        static void SetField(object controller, string name, object value)
        {
            typeof(MainController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(controller, value);
        }

        static object Call(MainController controller, string name, params object[] arguments)
        {
            return typeof(MainController).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(controller, arguments);
        }

        internal static void WaitFor(Func<bool> predicate, string message)
        {
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            DateTime start = DateTime.UtcNow; bool timedOut = false;
            timer.Tick += delegate
            {
                if (predicate()) { timer.Stop(); frame.Continue = false; }
                else if (DateTime.UtcNow - start > TimeSpan.FromSeconds(5)) { timedOut = true; timer.Stop(); frame.Continue = false; }
            };
            timer.Start(); Dispatcher.PushFrame(frame);
            if (timedOut) throw new TimeoutException(message);
        }

        internal static void PumpTask(Task task)
        {
            if (task == null) throw new InvalidOperationException("Expected an asynchronous controller operation.");
            WaitFor(() => task.IsCompleted, "Controller operation did not finish within five seconds.");
            task.GetAwaiter().GetResult();
        }

        sealed class Fixture : IDisposable
        {
            internal readonly string Root, Data, Target;
            internal readonly Window Window;
            internal readonly MainController Controller;

            internal Fixture(string root, string name, bool largeFiles = false)
            {
                Root = Path.Combine(root, "c-" + name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Data = Path.Combine(Root, "data"); Directory.CreateDirectory(Data);
                Target = TestFixtures.CreateDemo(Path.Combine(Root, "target"));
                Directory.CreateDirectory(Path.Combine(Data, "recipes"));
                string recipe = TestFixtures.DemoRecipe();
                if (largeFiles) recipe = AddLargeFileOperations(recipe);
                File.WriteAllText(Path.Combine(Data, "recipes", "sandbox-v1.json"), recipe, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(Data, "settings.json"), Json.Pretty(new Dictionary<string, object> { { "sandbox-v1", Target } }), new UTF8Encoding(false));
                Window = LoadWindow(); Window.ShowInTaskbar = false; Window.Left = -20000; Window.Top = -20000; Window.WindowStartupLocation = WindowStartupLocation.Manual;
                Controller = new MainController(Window, Data);
                Field<HashSet<string>>(Controller, "selected").Add("midnight-theme");
                Call(Controller, "UpdateSession");
            }

            internal void Load() { Window.Show(); }
            internal void Ready() { WaitFor(() => Field<Task>(Controller, "selectionTask").IsCompleted && Field<Task>(Controller, "sessionTask").IsCompleted && !Field<bool>(Controller, "inspectingSession"), "Initial or queued controller inspection did not finish."); }
            internal T Control<T>(string name) where T : FrameworkElement { return (T)Window.FindName(name); }
            internal void Close() { if (Window.IsVisible) Window.Close(); }
            public void Dispose() { Close(); }

            static Window LoadWindow()
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Patchwork.MainWindow.xaml"))
                    return (Window)XamlReader.Load(stream);
            }

            string AddLargeFileOperations(string recipe)
            {
                string large = new string('x', 8700000) + " LARGE_MARKER";
                string small = "small original";
                File.WriteAllText(Path.Combine(Target, "large.js"), large, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(Target, "small.txt"), small, new UTF8Encoding(false));
                var document = Json.Parse(recipe); var patches = Json.Array(document, "patches");
                var patch = Json.Object(patches[0]); var operations = Json.Array(patch, "operations");
                operations.Add(new Dictionary<string, object> { { "kind", "textReplace" }, { "file", "large.js" }, { "sha256", PatchEngine.Hash(Encoding.UTF8.GetBytes(large)) }, { "find", "LARGE_MARKER" }, { "replacement", "LARGE_PATCHED" }, { "count", 1 } });
                operations.Add(new Dictionary<string, object> { { "kind", "textReplace" }, { "file", "small.txt" }, { "sha256", PatchEngine.Hash(Encoding.UTF8.GetBytes(small)) }, { "find", "original" }, { "replacement", "patched" }, { "count", 1 } });
                patch["operations"] = operations;
                return Json.Pretty(document);
            }
        }
    }
}
