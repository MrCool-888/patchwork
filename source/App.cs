using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Patchwork
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string dataRoot = Argument(args, "--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Patchwork", "Data");
            try
            {
                string worker = Argument(args, "--worker");
                if (worker != null) return Worker.Execute(worker, dataRoot);
#if TEST_BUILD
                if (args.Contains("--self-test")) return SelfTests.Run(dataRoot);
#endif
                var application = new Application();
                using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Patchwork.MainWindow.xaml"))
                {
                    var window = (Window)XamlReader.Load(resource);
                    var controller = new MainController(window, dataRoot);
                    string import = Argument(args, "--import-patch");
                    if (import != null) controller.ImportFile(import);
                    application.MainWindow = window;
                    string screenshot = Argument(args, "--screenshot");
                    if (screenshot == null) window.Loaded += async delegate { await controller.StartUpdateChecks(); };
                    if (screenshot != null)
                    {
                        string page = Argument(args, "--page") ?? "library";
                        if (args.Contains("--compact")) { window.Width = window.MinWidth; window.Height = window.MinHeight; }
                        window.ShowInTaskbar = false;
                        window.Left = -10000; window.Top = -10000; window.WindowStartupLocation = WindowStartupLocation.Manual;
                        window.Loaded += async delegate
                        {
                            try
                            {
                                controller.PrepareScreenshot(page);
                                await Task.Delay(300);
                                window.UpdateLayout();
                                var surface = (FrameworkElement)window.Content;
                                var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                                bitmap.Render(surface);
                                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshot)));
                                using (var output = File.Create(screenshot)) encoder.Save(output);
                            }
                            catch (Exception error) { File.WriteAllText(screenshot + ".error.txt", error.ToString()); Environment.ExitCode = 1; }
                            finally { window.Close(); }
                        };
                    }
                    application.Run(window);
                }
                return Environment.ExitCode;
            }
            catch (Exception error)
            {
                string message = "Patchwork could not start. " + error.Message;
                if (args.Contains("--self-test") || args.Contains("--screenshot") || args.Contains("--worker")) { Console.Error.WriteLine(error.ToString()); return 1; }
                MessageBox.Show(message, "Patchwork", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
        static string Argument(string[] args, string key)
        {
            int index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }

    public partial class MainController
    {
        readonly Window window;
        readonly PatchEngine engine;
        readonly List<PatchBundle> bundles = new List<PatchBundle>();
        readonly Dictionary<string, string> targets = new Dictionary<string, string>();
        readonly HashSet<string> selected = new HashSet<string>();
        PatchBundle current;
        PatchPlan preview;
        bool busy;
        bool changingPicker;
        bool checkingUpdates;
        AppRelease availableUpdate;
        string page = "library";
        public MainController(Window window, string dataRoot)
        {
            this.window = window; engine = new PatchEngine(dataRoot);
            InitializeSources();
            LoadSettings(); LoadColorOptions(); LoadRecipes();
            Control<TextBlock>("UpdateVersion").Text = "Installed v" + Updates.DisplayVersion + " · " + Updates.Repository;
            Control<CheckBox>("AutoUpdateCheck").IsChecked = LoadUpdatePreference();
            Control<CheckBox>("AutoUpdateCheck").Click += delegate { SaveUpdatePreference(); };
            Control<CheckBox>("AppPrereleases").IsChecked = LoadUpdatePreference("includePrereleases", false);
            Control<CheckBox>("AppPrereleases").Click += delegate { SaveUpdatePreference(); availableUpdate = null; Control<Button>("InstallUpdateButton").Visibility = Visibility.Collapsed; };
            Control<Button>("CheckUpdatesButton").Click += async delegate { await CheckUpdates(true); };
            Control<Button>("InstallUpdateButton").Click += async delegate { await InstallUpdate(); };
            Control<Button>("ReleasesButton").Click += delegate { OpenReleasePage(); };
            Control<Button>("LibraryNav").Click += delegate { ShowPage("library"); };
            Control<Button>("HistoryNav").Click += delegate { ShowPage("history"); };
            Control<Button>("AboutNav").Click += delegate { ShowPage("about"); };
            Control<Button>("BackButton").Click += delegate { ShowPage("library"); };
            Control<Button>("PatchGuideButton").Click += delegate { OpenGuide("PATCH-FORMAT.md"); };
            Control<Button>("EmptyImportButton").Click += delegate { ImportRecipe(); };
            Control<Button>("ImportButton").Click += delegate { ImportRecipe(); };
            Control<Button>("BrowseButton").Click += delegate { Browse(); };
            Control<Button>("SelectAllButton").Click += delegate {
                if (current == null) return;
                var ready = current.Patches.Where(x => x.Ready).ToList();
                bool all = ready.All(x => selected.Contains(x.Id));
                selected.Clear(); if (!all) foreach (var patch in ready) selected.Add(patch.Id);
                preview = null; RenderPatches(); UpdateSession();
            };
            Control<TextBox>("SearchBox").TextChanged += delegate { if (current != null) RenderPatches(); };
            Control<Button>("PreviewButton").Click += delegate { BuildPreview(); };
            Control<Button>("ApplyButton").Click += async delegate { await Apply(); };
            Control<ComboBox>("PreviewFilePicker").SelectionChanged += delegate { ShowPreviewFile(); };
            Control<Button>("OpenDataButton").Click += delegate { OpenFolder(engine.DataRoot); };
            Control<Button>("OpenDocsButton").Click += delegate { OpenGuide("README.md"); };
            Control<ComboBox>("AppPicker").SelectionChanged += delegate { if (!changingPicker) SetBundle(Control<ComboBox>("AppPicker").SelectedIndex); };
            RefreshPicker(); SetBundle(bundles.Count == 0 ? -1 : 0);
            window.SourceInitialized += delegate { DarkTitleBar(window); };
            int recoveries = engine.History().Count(x => x.State == "Prepared" || x.State == "Restoring" || x.State == "RecoveryRequired");
            if (recoveries > 0) Notify(recoveries + " unfinished transaction(s). Open History & restore to recover.", true);
        }
        T Control<T>(string name) where T : FrameworkElement { return (T)window.FindName(name); }
        bool LoadUpdatePreference() { return LoadUpdatePreference("checkOnStartup", true); }
        bool LoadUpdatePreference(string key, bool missing)
        {
            try
            {
                string file = Path.Combine(engine.DataRoot, "update-settings.json");
                if (!File.Exists(file)) return missing;
                object enabled; var settings = Json.Parse(File.ReadAllText(file));
                return settings.TryGetValue(key, out enabled) && enabled is bool ? (bool)enabled : missing;
            }
            catch { return false; }
        }
        void SaveUpdatePreference()
        {
            try { File.WriteAllText(Path.Combine(engine.DataRoot, "update-settings.json"), Json.Pretty(new Dictionary<string, object> { { "checkOnStartup", Control<CheckBox>("AutoUpdateCheck").IsChecked == true }, { "includePrereleases", Control<CheckBox>("AppPrereleases").IsChecked == true } }), new UTF8Encoding(false)); }
            catch (Exception error) { Notify("Could not save update preference: " + error.Message, true); }
        }
        public async Task StartUpdateChecks()
        {
            await Task.WhenAll(Control<CheckBox>("AutoUpdateCheck").IsChecked == true ? CheckUpdates(false) : Task.FromResult(0), CheckSources(false, null));
        }
        async Task CheckUpdates(bool manual)
        {
            if (checkingUpdates || busy) return;
            checkingUpdates = true; Control<Button>("CheckUpdatesButton").IsEnabled = false;
            Control<CheckBox>("AppPrereleases").IsEnabled = false;
            Control<Button>("InstallUpdateButton").Visibility = Visibility.Collapsed;
            Control<TextBlock>("UpdateStatus").Text = "Checking GitHub releases…";
            availableUpdate = null;
            try
            {
                bool prereleases = Control<CheckBox>("AppPrereleases").IsChecked == true;
                availableUpdate = await Task.Run(() => Updates.Check(prereleases));
                string result = availableUpdate == null ? "You're up to date. Installed v" + Updates.DisplayVersion + "." : "Version " + availableUpdate.Tag + (availableUpdate.Prerelease ? " (prerelease)" : "") + " is available. Downloading verifies GitHub's SHA-256 digest before opening setup.";
                Control<TextBlock>("UpdateStatus").Text = result;
                Control<Button>("InstallUpdateButton").Visibility = availableUpdate == null ? Visibility.Collapsed : Visibility.Visible;
                if (manual || availableUpdate != null) Notify(availableUpdate == null ? result : "An app update is available. Open About this build to install it.");
            }
            catch (Exception error) { Control<TextBlock>("UpdateStatus").Text = error.Message; if (manual) Notify(error.Message, true); }
            finally { checkingUpdates = false; Control<Button>("CheckUpdatesButton").IsEnabled = true; Control<CheckBox>("AppPrereleases").IsEnabled = true; }
        }
        async Task InstallUpdate()
        {
            if (availableUpdate == null || checkingUpdates || busy) return;
            var release = availableUpdate;
            SetBusy(true); Control<Button>("InstallUpdateButton").IsEnabled = false; Control<Button>("CheckUpdatesButton").IsEnabled = false;
            Control<CheckBox>("AppPrereleases").IsEnabled = false;
            Control<TextBlock>("UpdateStatus").Text = "Downloading " + release.Tag + " and verifying its installer…";
            try
            {
                string installer = await Task.Run(() => Updates.Download(release, engine.DataRoot));
                // A recognized installation can only update after the running app closes.
                using (var current = Process.GetCurrentProcess())
                    Process.Start(Updates.SetupStartInfo(installer, AppDomain.CurrentDomain.BaseDirectory, current.Id, current.StartTime.ToUniversalTime().Ticks));
                SetBusy(false); window.Close();
            }
            catch (Exception error)
            {
                SetBusy(false); Control<Button>("InstallUpdateButton").IsEnabled = true; Control<Button>("CheckUpdatesButton").IsEnabled = true;
                Control<CheckBox>("AppPrereleases").IsEnabled = true;
                Control<TextBlock>("UpdateStatus").Text = error.Message; Notify("Update failed: " + error.Message, true);
            }
        }
        void OpenReleasePage() { try { Process.Start(new ProcessStartInfo(availableUpdate == null ? Updates.ReleasesUrl : availableUpdate.PageUrl) { UseShellExecute = true }); } catch (Exception error) { Notify(error.Message, true); } }
        static Brush Color(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }
        void RefreshPicker()
        {
            changingPicker = true;
            var picker = Control<ComboBox>("AppPicker"); picker.Items.Clear();
            foreach (var bundle in bundles) picker.Items.Add(bundle.AppName + " · " + bundle.AppVersion + " · pack " + VersionLabel(bundle.PackVersion));
            changingPicker = false;
        }
        void SetBundle(int index)
        {
            if (index < 0 || index >= bundles.Count)
            {
                current = null; selected.Clear(); preview = null;
                Control<Border>("AppHeader").Visibility = Visibility.Collapsed;
                Control<Grid>("LibraryContent").Visibility = Visibility.Collapsed;
                Control<Border>("EmptyLibrary").Visibility = Visibility.Visible;
                UpdateSession(); ShowPage("library"); return;
            }
            Control<Border>("AppHeader").Visibility = Visibility.Visible;
            Control<Grid>("LibraryContent").Visibility = Visibility.Visible;
            Control<Border>("EmptyLibrary").Visibility = Visibility.Collapsed;
            current = bundles[index]; selected.Clear(); preview = null;
            changingPicker = true; Control<ComboBox>("AppPicker").SelectedIndex = index; changingPicker = false;
            Control<TextBox>("SearchBox").Text = "";
            bool proton = current.AppId == "proton-vpn";
            Control<TextBlock>("AppIcon").Text = proton ? "P" : "↗";
            Control<Border>("AppIconBorder").Background = Color(proton ? "#36284E" : "#1B3936");
            Control<TextBlock>("AppIcon").Foreground = Color(proton ? "#C4AAFF" : "#89DDC4");
            bool available = current.Patches.Any(x => x.Ready);
            Control<TextBlock>("AppMeta").Text = "App " + current.AppVersion + " · Patch pack " + VersionLabel(current.PackVersion) + " · " + current.Patches.Count(x => x.Ready) + " available";
            Control<TextBlock>("AppBadge").Text = !available ? "PLANNED ONLY" : proton ? "EXPERIMENTAL" : "LOCAL PATCH FILE";
            Control<TextBlock>("AppBadge").Foreground = Color(proton ? "#E9C985" : "#8DE2C5");
            Control<Border>("AppBadgeBorder").Background = Color(proton ? "#322A1C" : "#18322E");
            Control<TextBlock>("SessionTitle").Text = current.AppName;
            Control<TextBlock>("SessionDescription").Text = "Patch pack " + VersionLabel(current.PackVersion) + " by " + current.Author + " · Requires app " + current.AppVersion + ".";
            Control<TextBlock>("SessionNote").Text = proton ? "Close Proton's desktop app before applying. Client patches do not change server account permissions. Read each patch's scope." : "Preview exact file changes before applying. Originals are backed up automatically.";
            if (!targets.ContainsKey(current.Id)) targets[current.Id] = "";
            if (proton && String.IsNullOrEmpty(targets[current.Id])) DetectProton();
            if (current.AppId == "blitz" && String.IsNullOrEmpty(targets[current.Id])) DetectBlitz();
            if (current.AppId == "lunar-client" && String.IsNullOrEmpty(targets[current.Id])) DetectLunar();
            if (current.AppId == "lunar-client") Control<TextBlock>("SessionNote").Text = "Close Lunar Client and Minecraft. This pack covers both folders under your Windows user folder. Unequip online cosmetics first. Restore before updating Lunar.";
            try { var active = String.IsNullOrEmpty(targets[current.Id]) ? null : engine.ActiveSession(targets[current.Id]); if (active != null && active.BundleId == current.Id && active.PatchIds != null) foreach (string id in active.PatchIds.Where(x => current.Patches.Any(p => p.Id == x && p.Ready))) selected.Add(id); } catch { }
            string sourceOwner = patchSources == null ? null : patchSources.Owner(current.Id);
            if (sourceOwner != null) Control<TextBlock>("SessionDescription").Text += "\nSource: " + sourceOwner;
            RenderPatches(); UpdateSession(); ShowPage("library");
        }
        void RenderPatches()
        {
            var container = Control<StackPanel>("PatchList"); container.Children.Clear();
            if (current == null) return;
            string query = Control<TextBox>("SearchBox").Text.Trim();
            var matches = current.Patches.Where(x => query.Length == 0 || (x.Name + " " + x.Description + " " + x.Category).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            Control<TextBlock>("PatchCount").Text = matches.Count + " PATCHES  /  " + current.Patches.Count(x => x.Ready) + " AVAILABLE";
            Control<Button>("SelectAllButton").Visibility = current.Patches.Any(x => x.Ready) ? Visibility.Visible : Visibility.Collapsed;
            Control<Button>("SelectAllButton").Content = current.Patches.Where(x => x.Ready).All(x => selected.Contains(x.Id)) ? "Clear selection" : "Select all ready";
            if (matches.Count == 0) { container.Children.Add(Text("No patches match your search.", 14, "#929DB1")); return; }
            foreach (var patch in matches)
            {
                var card = new Border { Background = Color(selected.Contains(patch.Id) ? "#201F32" : "#141C29"), BorderBrush = Color(selected.Contains(patch.Id) ? "#65538E" : "#283247"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 9) };
                var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition());
                var check = new CheckBox { IsChecked = selected.Contains(patch.Id), IsEnabled = patch.Ready, Margin = new Thickness(0, 0, 14, 0), ToolTip = patch.Ready ? "Select " + patch.Name : "Windows backend not implemented yet" };
                check.Click += delegate { if (check.IsChecked == true) SelectDependencies(patch); else { selected.Remove(patch.Id); foreach (var dependent in current.Patches.Where(x => x.Dependencies.Contains(patch.Id))) selected.Remove(dependent.Id); } preview = null; RenderPatches(); UpdateSession(); };
                grid.Children.Add(check);
                var content = new StackPanel(); Grid.SetColumn(content, 1);
                var headline = new Grid(); headline.ColumnDefinitions.Add(new ColumnDefinition()); headline.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = Text(patch.Name + "  ·  " + (patch.Ready ? VersionLabel(patch.Version) : "Planned"), 14, "#E8EDF6"); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 0, 8, 0); headline.Children.Add(title);
                var category = Text(patch.Ready ? patch.Category.ToUpperInvariant() : "PLANNED", 9, patch.Ready ? "#8EDCC5" : "#A698BD"); category.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(category, 1); headline.Children.Add(category);
                content.Children.Add(headline);
                var description = Text(patch.Description, 12, "#929DB1"); description.Margin = new Thickness(0, 7, 0, 0); description.LineHeight = 18; content.Children.Add(description);
                if (patch.Operations.Any(x => x.Kind == "managedEmbeddedHook" || x.Kind == "asarTextReplace" || BinaryPatches.IsOperation(x.Kind))) {
                    var code = Text("Executable client code · runs inside the target app", 11, "#E5BB77"); code.Margin = new Thickness(0, 7, 0, 0); content.Children.Add(code);
                }
                if (patch.Ready && selected.Contains(patch.Id)) AddColorOptions(content, patch);
                grid.Children.Add(content); card.Child = grid; container.Children.Add(card);
            }
        }
        void UpdateSession()
        {
            if (current == null) { Control<Button>("PreviewButton").IsEnabled = false; return; }
            string target = targets.ContainsKey(current.Id) ? targets[current.Id] : "";
            Control<TextBox>("TargetBox").Text = target.Length == 0 ? "No folder selected" : target;
            Control<TextBox>("TargetBox").ToolTip = target.Length == 0 ? "Choose the target folder" : target;
            Control<TextBlock>("SelectedCount").Text = selected.Count + (selected.Count == 1 ? " patch" : " patches");
            Control<Button>("PreviewButton").IsEnabled = !busy && selected.Count > 0 && target.Length > 0;
            var status = Control<TextBlock>("TargetStatus");
            status.Foreground = Color("#929DB1");
            if (target.Length == 0) status.Text = "Select a folder to inspect the app.";
            else if (!current.Patches.Any(x => x.Ready))
            {
                string detected = ProtonVersion(target);
                status.Text = detected.Length == 0 ? "Proton executable not found. Import a compatible patch file." : "Detected v" + detected + ". Import a compatible Proton patch file.";
                status.Foreground = Color("#E9C985");
            }
            else
            {
                try
                {
                    var applied = engine.VerifiedAppliedSession(current, target);
                    if (applied != null) { status.Text = "Applied pack " + VersionLabel(applied.PackVersion) + ". Preview to update from verified originals; no manual restore needed."; status.Foreground = Color("#85DCC0"); return; }
                    bool match = PatchEngine.Hash(PatchEngine.Read(PatchEngine.Resolve(target, current.VersionFile))).Equals(current.VersionSha256, StringComparison.OrdinalIgnoreCase);
                    bool changed = current.Patches.SelectMany(x => x.Operations).GroupBy(x => x.File).Any(group => !PatchEngine.Hash(PatchEngine.Read(PatchEngine.Resolve(target, group.Key))).Equals(group.First().Sha256, StringComparison.OrdinalIgnoreCase));
                    status.Text = !match ? "Version fingerprint mismatch. Choose a compatible folder." : changed ? "Files differ and no matching applied session was found. Choose a compatible folder or recover its original files." : "Target fingerprints match. Ready to preview.";
                    status.Foreground = Color(match && !changed ? "#85DCC0" : "#E9C985");
                }
                catch (Exception error) { status.Text = error.Message; status.Foreground = Color("#E9C985"); }
            }
        }
        void ShowPage(string next)
        {
            if (busy) return;
            page = next;
            foreach (string name in new[] { "LibraryPage", "PreviewPage", "HistoryPage", "AboutPage", "SourcesPage" }) Control<Grid>(name).Visibility = Visibility.Collapsed;
            string control = next == "sources" ? "SourcesPage" : next == "preview" ? "PreviewPage" : next == "history" ? "HistoryPage" : next == "about" ? "AboutPage" : "LibraryPage";
            Control<Grid>(control).Visibility = Visibility.Visible;
            Control<TextBlock>("PageTitle").Text = next == "sources" ? "Keep your patches current." : next == "preview" ? "Review every change." : next == "history" ? "Every patch, accounted for." : next == "about" ? "Built for your desktop." : "Make your apps yours.";
            Control<TextBlock>("PageSubtitle").Text = next == "sources" ? "Add a GitHub repository and choose how its patch packs update." : next == "preview" ? preview != null && preview.PreviousJournalId != null ? "Review the update. Verified originals and the previous patch version will be kept." : "Nothing has been written yet. Originals will be backed up before applying." : next == "history" ? "Inspect past sessions and bring your original files back." : next == "about" ? "Local patch files. Verified changes. Recoverable originals." : "Import a patch file or add a GitHub source. Pick your patches.";
            Control<Button>("ImportButton").Visibility = next == "library" ? Visibility.Visible : Visibility.Collapsed;
            Control<Button>("SourcesButton").Visibility = next == "library" ? Visibility.Visible : Visibility.Collapsed;
            foreach (string nav in new[] { "LibraryNav", "HistoryNav", "AboutNav", "SourcesNav" }) { Control<Button>(nav).Background = Brushes.Transparent; Control<Button>(nav).Foreground = Color("#E5E9F3"); }
            string active = next == "sources" ? "SourcesNav" : next == "history" ? "HistoryNav" : next == "about" ? "AboutNav" : "LibraryNav";
            Control<Button>(active).Background = Color("#272239"); Control<Button>(active).Foreground = Color("#CCBEFF");
            if (next == "history") RenderHistory();
            if (next == "sources") RenderSources();
        }
        void Browse()
        {
            if (current == null) return;
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose the app installation or configuration folder", ShowNewFolderButton = false })
            {
                string existing = targets[current.Id]; if (Directory.Exists(existing)) dialog.SelectedPath = existing;
                if (dialog.ShowDialog(new NativeOwner(new WindowInteropHelper(window).Handle)) != System.Windows.Forms.DialogResult.OK) return;
                targets[current.Id] = dialog.SelectedPath; preview = null;
                SaveSettings(); UpdateSession(); Notify("Target folder selected. Preview will check compatibility before any changes.");
            }
        }
        void ImportRecipe()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Add a separate Patchwork patch file", Filter = "Patchwork patch files (*.patchwork;*.json)|*.patchwork;*.json", CheckFileExists = true, Multiselect = false };
            if (dialog.ShowDialog(window) != true) return;
            try
            {
                ImportFile(dialog.FileName);
            }
            catch (Exception error) { Notify("Import failed: " + error.Message, true); }
        }
        public void ImportFile(string fileName)
        {
            string content = PatchEngine.Decode(PatchEngine.Read(fileName));
            var bundle = PatchBundle.Parse(content);
            if (patchSources == null) throw new InvalidOperationException(sourceLoadError);
            patchSources.ImportLocal(bundle);
            AddBundle(bundle); RefreshPicker(); SetBundle(bundles.FindIndex(x => x.Id == bundle.Id));
            Notify("Imported patch pack " + VersionLabel(bundle.PackVersion) + " · " + bundle.Patches.Count(x => x.Ready) + " available patches. Preview before applying.");
        }
        void BuildPreview()
        {
            if (current == null) return;
            try
            {
                preview = engine.Preview(current, targets[current.Id], selected, SelectedOptions());
                var picker = Control<ComboBox>("PreviewFilePicker"); picker.Items.Clear();
                foreach (var file in preview.Files) picker.Items.Add(file.RelativePath);
                int firstChanged = preview.Files.FindIndex(x => x.BeforeHash != x.AfterHash);
                picker.SelectedIndex = firstChanged < 0 ? 0 : firstChanged;
                Control<TextBlock>("PreviewSummary").Text = preview.PatchNames.Count + " patch(es) · " + preview.Files.Count + " file(s) · " + preview.AppName + " " + preview.AppVersion + " · Pack " + VersionLabel(preview.PackVersion) + "\n" + preview.TargetRoot;
                Control<Button>("ApplyButton").IsEnabled = true;
                bool updating = preview.PreviousJournalId != null;
                Control<TextBlock>("BeforeLabel").Text = updating ? "CURRENTLY APPLIED" : "ORIGINAL";
                Control<Button>("ApplyButton").Content = (updating ? "Update patches" : "Apply patches") + (Worker.Protected(preview.TargetRoot) ? " · administrator" : "");
                if (updating) Control<TextBlock>("PreviewSummary").Text += "\nUpdate from verified original backups · replaces session " + preview.PreviousJournalId;
                ShowPage("preview"); Notify(updating ? "Update preview verified. Review the current and updated content, then update patches." : "Preview verified. Review the original and patched content, then apply.");
            }
            catch (Exception error) { preview = null; Notify("Preview blocked: " + error.Message, true); }
        }
        void ShowPreviewFile()
        {
            int index = Control<ComboBox>("PreviewFilePicker").SelectedIndex;
            if (preview == null || index < 0 || index >= preview.Files.Count) return;
            var file = preview.Files[index]; Control<TextBox>("BeforeText").Text = file.BeforeText; Control<TextBox>("AfterText").Text = file.AfterText;
        }
        async Task Apply()
        {
            if (preview == null || busy) return;
            var plan = preview;
            SetBusy(true); Notify(plan.PreviousJournalId == null ? "Backing up originals, applying changes, and verifying results…" : "Updating from verified originals and keeping the previous patch version for rollback…");
            try
            {
                var bundle = current;
                var journal = await Task.Run(() => {
                    Worker.CheckClientClosed(plan.TargetRoot);
                    if (Worker.Protected(plan.TargetRoot)) { string id = Worker.RunElevated(engine, bundle, plan, null); return engine.History().Single(x => x.Id == id); }
                    return engine.Apply(plan);
                });
                selected.Clear(); preview = null; SetBusy(false); RenderPatches(); UpdateSession(); ShowPage("history");
                Notify((plan.PreviousJournalId == null ? "Applied " : "Updated ") + journal.PatchNames.Count + " patch(es). Originals are backed up and ready to restore.");
            }
            catch (Exception error) { SetBusy(false); preview = null; Control<Button>("ApplyButton").IsEnabled = false; Notify(error.Message, true); ShowPage("history"); }
        }
        async Task Restore(Journal journal)
        {
            if (busy) return;
            if (!Dialog(journal.RestoreToPrevious ? "Recover previous patches?" : "Restore original files?", "Patchwork will verify the current files, then recover " + (journal.RestoreToPrevious ? "the patch version from before this interrupted update." : "the backed-up originals.") + "\n\n" + journal.TargetRoot, journal.RestoreToPrevious ? "Recover previous patches" : "Restore originals", true)) return;
            SetBusy(true); Notify("Verifying current files and restoring originals…");
            try { await Task.Run(() => { Worker.CheckClientClosed(journal.TargetRoot); if (Worker.Protected(journal.TargetRoot)) Worker.RunElevated(engine, null, null, journal); else engine.Restore(journal); }); SetBusy(false); preview = null; UpdateSession(); RenderHistory(); Notify(journal.RestoreToPrevious ? "Previous patch version recovered and verified." : "Original files restored and fingerprints verified."); }
            catch (Exception error) { SetBusy(false); RenderHistory(); Notify(error.Message, true); }
        }
        void RenderHistory()
        {
            var list = Control<StackPanel>("HistoryList"); list.Children.Clear();
            var journals = engine.History();
            if (journals.Count == 0)
            {
                var empty = new Border { Background = Color("#151D2B"), BorderBrush = Color("#2B354A"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(32), Margin = new Thickness(0, 18, 0, 0) };
                var content = new StackPanel(); content.Children.Add(Text("A fresh start.", 23, "#E8EDF6"));
                var note = Text("Patch sessions appear here after you apply a recipe. Each session keeps verified originals for restoration.", 14, "#929DB1"); note.Margin = new Thickness(0, 14, 0, 22); content.Children.Add(note);
                var browse = new Button { Content = "Open patch library", HorizontalAlignment = HorizontalAlignment.Left, Style = (Style)window.FindResource("PrimaryButton") }; browse.Click += delegate { ShowPage("library"); }; content.Children.Add(browse); empty.Child = content; list.Children.Add(empty); return;
            }
            foreach (var journal in journals)
            {
                bool restored = journal.State == "Restored" || journal.State == "RolledBack" || journal.State == "Superseded";
                bool recover = journal.State == "Prepared" || journal.State == "Restoring" || journal.State == "RecoveryRequired";
                var card = new Border { Background = Color("#151D2B"), BorderBrush = Color(recover ? "#80623E" : "#2B354A"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(21), Margin = new Thickness(0, 0, 6, 14) };
                var stack = new StackPanel();
                var header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = Text(journal.AppName + "  ·  " + journal.AppVersion, 18, "#E8EDF6"); title.FontWeight = FontWeights.SemiBold; header.Children.Add(title);
                var badge = Text(recover ? "RECOVERY NEEDED" : journal.State.ToUpperInvariant(), 10, recover ? "#E9C985" : restored ? "#929DB1" : "#85DCC0"); badge.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(badge, 1); header.Children.Add(badge); stack.Children.Add(header);
                DateTime created; string date = DateTime.TryParse(journal.CreatedUtc, out created) ? created.ToLocalTime().ToString("MMM d, yyyy · h:mm tt") : journal.CreatedUtc;
                var meta = Text("Patch pack " + VersionLabel(journal.PackVersion) + "  /  " + date + "  /  " + journal.Files.Count + " file(s)", 11, "#78869E"); meta.Margin = new Thickness(0, 7, 0, 15); stack.Children.Add(meta);
                stack.Children.Add(Text(String.Join("  ·  ", journal.PatchNames), 13, "#BEABD8"));
                if (!String.IsNullOrEmpty(journal.PreviousJournalId)) stack.Children.Add(Text("Updates session " + journal.PreviousJournalId, 11, "#78869E"));
                var target = Text(journal.TargetRoot, 11, "#929DB1"); target.Margin = new Thickness(0, 10, 0, 0); stack.Children.Add(target);
                if (!String.IsNullOrWhiteSpace(journal.Error)) { var error = Text(journal.Error, 12, "#E9C985"); error.Margin = new Thickness(0, 12, 0, 0); stack.Children.Add(error); }
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 17, 0, 0) };
                var restore = new Button { Content = journal.State == "Superseded" ? "Replaced by newer session" : recover ? journal.RestoreToPrevious ? "Recover previous patches" : "Recover originals" : restored ? journal.State == "RolledBack" ? "Rolled back" : "Originals restored" : "Restore originals", IsEnabled = !restored, Padding = new Thickness(14, 9, 14, 9), FontSize = 12, Margin = new Thickness(0, 0, 10, 0) };
                restore.Click += async delegate { await Restore(journal); }; buttons.Children.Add(restore);
                var inspect = new Button { Content = "Open backup folder", Padding = new Thickness(14, 9, 14, 9), FontSize = 12, Background = Brushes.Transparent };
                inspect.Click += delegate { OpenFolder(journal.DirectoryPath); }; buttons.Children.Add(inspect); stack.Children.Add(buttons); card.Child = stack; list.Children.Add(card);
            }
        }
        void SetBusy(bool value)
        {
            busy = value;
            foreach (string name in new[] { "LibraryPage", "PreviewPage", "HistoryPage", "SourcesPage", "SourcesNav", "SourcesButton", "PatchGuideButton", "ImportButton", "LibraryNav", "HistoryNav", "AboutNav" }) Control<FrameworkElement>(name).IsEnabled = !value;
            window.Closing -= PreventBusyClose;
            if (value) window.Closing += PreventBusyClose;
            if (!value && catalogRefreshPending) RefreshSourceCatalog();
        }
        void PreventBusyClose(object sender, System.ComponentModel.CancelEventArgs e) { e.Cancel = true; Notify("Wait for the current file transaction to finish before closing.", true); }
        void Notify(string message, bool error = false) { Control<TextBlock>("StatusText").Text = message; Control<TextBlock>("StatusText").Foreground = Color(error ? "#F0BB9B" : "#92BDAF"); }
        static TextBlock Text(string text, double size, string color) { return new TextBlock { Text = text, FontSize = size, Foreground = Color(color), TextWrapping = TextWrapping.Wrap }; }
        bool Dialog(string title, string message, string action, bool cancel)
        {
            var dialog = new Window { Title = title, Owner = window, Width = 510, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Color("#151D2B"), Foreground = Color("#E8EDF6"), FontFamily = new FontFamily("Segoe UI"), ShowInTaskbar = false };
            var body = new StackPanel { Margin = new Thickness(26) }; var heading = Text(title, 22, "#E8EDF6"); heading.FontWeight = FontWeights.SemiBold; body.Children.Add(heading);
            var description = Text(message, 13, "#A3AEC2"); description.Margin = new Thickness(0, 16, 0, 24); description.LineHeight = 21; body.Children.Add(description);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            if (cancel) { var close = new Button { Content = "Cancel", Style = (Style)window.FindResource(typeof(Button)), Margin = new Thickness(0, 0, 10, 0), IsCancel = true }; close.Click += delegate { dialog.DialogResult = false; }; buttons.Children.Add(close); }
            var okay = new Button { Content = action, Style = (Style)window.FindResource("PrimaryButton"), IsDefault = true }; okay.Click += delegate { dialog.DialogResult = true; }; buttons.Children.Add(okay);
            body.Children.Add(buttons); dialog.Content = body; dialog.SourceInitialized += delegate { DarkTitleBar(dialog); };
            return dialog.ShowDialog() == true;
        }
        void LoadRecipes()
        {
            string recipes = Path.Combine(engine.DataRoot, "recipes");
            if (!Directory.Exists(recipes)) return;
            int errors = 0;
            foreach (string file in Directory.GetFiles(recipes, "*.json").Take(100))
                try { AddBundle(PatchBundle.Parse(PatchEngine.Decode(PatchEngine.Read(file)))); }
                catch { errors++; }
            if (errors > 0) Notify(errors + " invalid saved recipe(s) were skipped.", true);
        }
        void AddBundle(PatchBundle bundle)
        {
            int index = bundles.FindIndex(x => x.Id == bundle.Id);
            if (index >= 0) bundles[index] = bundle; else bundles.Add(bundle);
        }
        void SelectDependencies(PatchDefinition patch)
        {
            if (!selected.Add(patch.Id)) return;
            foreach (string id in patch.Dependencies) SelectDependencies(current.Patches.Single(x => x.Id == id));
        }
        void LoadSettings()
        {
            string file = Path.Combine(engine.DataRoot, "settings.json");
            if (!File.Exists(file)) return;
            try
            {
                var saved = Json.Parse(PatchEngine.Decode(PatchEngine.Read(file)));
                foreach (var entry in saved) if (entry.Value is string) targets[entry.Key] = (string)entry.Value;
            }
            catch { Notify("Saved target settings could not be read. Choose your folders again.", true); }
        }
        void SaveSettings()
        {
            try { File.WriteAllText(Path.Combine(engine.DataRoot, "settings.json"), Json.Pretty(targets), new UTF8Encoding(false)); }
            catch (Exception error) { Notify("Could not save target settings: " + error.Message, true); }
        }
        void DetectProton()
        {
            try
            {
                string protonRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Proton", "VPN");
                if (!Directory.Exists(protonRoot)) return;
                var candidates = new List<string> { protonRoot }; candidates.AddRange(Directory.GetDirectories(protonRoot).Take(30));
                string best = candidates.Select(x => new { Path = x, Version = ProtonVersion(x) }).Where(x => x.Version.Length > 0).OrderByDescending(x => ParseVersion(x.Version)).Select(x => x.Path).FirstOrDefault();
                if (best != null) targets[current.Id] = best;
            }
            catch { /* Folder browsing remains available if automatic detection fails. */ }
        }
        void DetectLunar()
        {
            try
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (File.Exists(PatchEngine.Resolve(root, current.VersionFile)) && current.Patches.SelectMany(x => x.Operations).All(x => File.Exists(PatchEngine.Resolve(root, x.File)))) targets[current.Id] = root;
            }
            catch { /* Folder browsing remains available. */ }
        }
        void DetectBlitz()
        {
            try
            {
                string best = FindBlitzFolder(new[] {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Blitz"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blitz"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Blitz")
                });
                if (best != null) targets[current.Id] = best;
            }
            catch { /* Folder browsing remains available if automatic detection fails. */ }
        }
        internal static string FindBlitzFolder(IEnumerable<string> candidates)
        {
            return candidates.FirstOrDefault(root => File.Exists(Path.Combine(root, "Blitz.exe")) &&
                File.Exists(Path.Combine(root, "resources", "app.asar")));
        }
        static string VersionLabel(string value) { return String.IsNullOrEmpty(value) || value == "Unversioned" ? "Unversioned" : "v" + value; }
        static Version ParseVersion(string value) { Version version; return Version.TryParse(value, out version) ? version : new Version(0, 0); }
        static string ProtonVersion(string root)
        {
            foreach (string name in new[] { "ProtonVPN.Client.exe", "ProtonVPN.exe", "ProtonVPN.Client.dll" })
                try { string path = Path.Combine(root, name); if (File.Exists(path)) return FileVersionInfo.GetVersionInfo(path).FileVersion ?? ""; } catch { }
            return "";
        }
        void OpenFolder(string path) { try { Process.Start("explorer.exe", "\"" + Path.GetFullPath(path) + "\""); } catch (Exception error) { Notify(error.Message, true); } }
        void OpenGuide(string file) { try { Process.Start("notepad.exe", "\"" + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, file) + "\""); } catch (Exception error) { Notify(error.Message, true); } }
        public void PrepareScreenshot(string shot)
        {
            if (shot == "proton-preview" || shot == "update-preview")
            {
                if (!bundles.Any(x => x.AppId == "proton-vpn")) throw new InvalidOperationException("Import a separate Proton patch file before previewing.");
                SetBundle(bundles.FindIndex(x => x.AppId == "proton-vpn"));
                if (shot == "update-preview") foreach (var patch in current.Patches.Where(x => x.Ready)) SelectDependencies(patch);
                else { selected.Add("disable-telemetry"); selected.Add("server-delay"); }
                RenderPatches(); UpdateSession(); BuildPreview();
            }
            else if (shot == "about") ShowPage("about");
            else if (shot == "free-selector") { Control<TextBox>("SearchBox").Text = "Free server"; RenderPatches(); ShowPage("library"); }
            else if (shot == "updates") { ShowPage("about"); window.UpdateLayout(); Control<ScrollViewer>("AboutScroll").ScrollToEnd(); }
            else if (shot == "history" || shot == "empty-history") ShowPage("history");
            else if (shot == "sources") ShowPage("sources");
            else if (shot == "themes") { foreach (var patch in current.Patches.Where(x => x.Ready && x.Category == "Appearance")) selected.Add(patch.Id); Control<TextBox>("SearchBox").Text = "Appearance"; RenderPatches(); UpdateSession(); ShowPage("library"); }
        }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        static void DarkTitleBar(Window window) { try { int enabled = 1; DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref enabled, sizeof(int)); } catch { } }
        class NativeOwner : System.Windows.Forms.IWin32Window { public IntPtr Handle { get; private set; } public NativeOwner(IntPtr handle) { Handle = handle; } }
    }
}
