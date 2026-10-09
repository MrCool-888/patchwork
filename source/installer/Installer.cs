using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

[assembly: AssemblyTitle("Patchwork Setup")]
[assembly: AssemblyProduct("Patchwork")]
[assembly: AssemblyVersion("0.5.0.0")]
[assembly: AssemblyFileVersion("0.5.0.0")]

namespace PatchworkSetup
{
    public class InstallInfo
    {
        public string ProductId { get; set; }
        public string Version { get; set; }
        public string InstallRoot { get; set; }
        public Dictionary<string, string> Files { get; set; }
    }
    public static class InstallerEngine
    {
        public const string ProductId = "8b72ca27-3a4b-45af-9d7c-61951c6bdf70";
        public const string Version = "0.5.0";
        const string Marker = "patchwork-install.json";
        const string RegistryPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Patchwork";
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
        public static string DefaultRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Patchwork"); } }
        public static string DataRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Patchwork", "Data"); } }
        public static Action<int> BeforeStageForTest;
        static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static string Canonical(string root)
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (root.Length < 8 || root.StartsWith("\\\\", StringComparison.Ordinal) || String.Equals(root, Path.GetPathRoot(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a local application folder.");
            string current = root;
            while (!String.IsNullOrEmpty(current))
            {
                if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Setup cannot use a directory junction or symbolic link.");
                current = Path.GetDirectoryName(current);
            }
            if (String.Equals(root, DataRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("Program files and backup data must use separate folders.");
            return root;
        }
        static string Resolve(string root, string relative)
        {
            if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0 || relative.Replace('/', '\\').Split('\\').Any(x => x.Length == 0 || x == "." || x == "..")) throw new IOException("Invalid package path.");
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', '\\')));
            if (!full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Package file escapes the installation folder.");
            string current = full;
            while (current.Length >= root.Length)
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked package files are not supported.");
                current = Path.GetDirectoryName(current);
                if (current == null) break;
            }
            return full;
        }
        public static InstallInfo ReadInfo(string root)
        {
            root = Canonical(root); string file = Path.Combine(root, Marker);
            if (!File.Exists(file)) return null;
            var info = Json.Deserialize<InstallInfo>(File.ReadAllText(file));
            if (info == null || info.ProductId != ProductId || info.Files == null || !String.Equals(root, info.InstallRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("This folder is not a recognized Patchwork installation.");
            foreach (var entry in info.Files) Resolve(root, entry.Key);
            return info;
        }
        public static void ReleaseWorkingDirectory(string root)
        {
            root = Path.GetFullPath(root).TrimEnd('\\');
            string current = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd('\\');
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase) || current.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                Environment.CurrentDirectory = Path.GetTempPath();
        }
        public static void WaitForUpdater(int processId, long startTicks, int timeout)
        {
            if (processId <= 0 || startTicks <= 0 || timeout < 0) throw new ArgumentException("Invalid update process identity.");
            Process process;
            try { process = Process.GetProcessById(processId); }
            catch (ArgumentException) { return; } // The old application already exited.
            using (process)
            {
                try
                {
                    if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startTicks) return; // PID was reused.
                    if (!process.WaitForExit(timeout)) throw new IOException("Patchwork is still running. Close it, then retry setup. No program files were replaced.");
                }
                catch (InvalidOperationException) { return; } // Exited during inspection.
            }
        }
        public static void WaitForRunning(string root, int timeout)
        {
            var elapsed = Stopwatch.StartNew();
            foreach (var process in Process.GetProcessesByName("Patchwork"))
            {
                using (process)
                {
                    string location = "";
                    try { location = process.MainModule.FileName; } catch { }
                    if (String.Equals(location, Path.Combine(root, "Patchwork.exe"), StringComparison.OrdinalIgnoreCase) &&
                        !process.WaitForExit(Math.Max(0, timeout - (int)elapsed.ElapsedMilliseconds)))
                        throw new IOException("Patchwork is still running. Close it, then retry setup. No program files were replaced.");
                }
            }
        }
        public static void Install(string root, bool desktop, bool integration)
        {
            root = Canonical(root); ReleaseWorkingDirectory(root); WaitForRunning(root, 30000);
            var old = ReadInfo(root);
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && old == null) throw new IOException("The installation folder contains unrelated files. Setup has left it unchanged.");
            string parent = Path.GetDirectoryName(root); Directory.CreateDirectory(parent);
            string stage = Path.Combine(parent, ".patchwork-stage-" + Guid.NewGuid().ToString("N"));
            string previous = Path.Combine(parent, ".patchwork-previous-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            var info = new InstallInfo { ProductId = ProductId, Version = Version, InstallRoot = root, Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };
            bool swapped = false, hadOriginal = false;
            try
            {
                using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Patchwork.Payload.zip"))
                using (var zip = new ZipArchive(resource, ZipArchiveMode.Read))
                {
                    int index = 0;
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.FullName.EndsWith("/")) continue;
                        if (BeforeStageForTest != null) BeforeStageForTest(index++);
                        string file = Resolve(stage, entry.FullName);
                        if (info.Files.ContainsKey(entry.FullName)) throw new IOException("Duplicate package file.");
                        if (entry.Length > 16 * 1024 * 1024) throw new IOException("Invalid package size.");
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        using (var input = entry.Open()) using (var output = File.Create(file)) { input.CopyTo(output); output.Flush(true); }
                        info.Files[entry.FullName] = Hash(File.ReadAllBytes(file));
                    }
                }
                if (!info.Files.ContainsKey("Patchwork.exe")) throw new IOException("The package is missing its application executable.");
                string uninstaller = Path.Combine(stage, "Uninstall.exe");
                File.Copy(Assembly.GetExecutingAssembly().Location, uninstaller); info.Files["Uninstall.exe"] = Hash(File.ReadAllBytes(uninstaller));
                File.WriteAllText(Path.Combine(stage, Marker), Json.Serialize(info), new UTF8Encoding(false));
                if (Directory.Exists(root))
                {
                    try { Directory.Move(root, previous); hadOriginal = true; }
                    catch (IOException error)
                    {
                        int code = Marshal.GetHRForException(error) & 0xffff;
                        if (code == 32 || code == 33) throw new IOException("Windows is holding the Patchwork program folder open. Close Patchwork and any windows using that folder, then retry setup. Your existing installation is preserved.", error);
                        throw;
                    }
                }
                Directory.Move(stage, root); swapped = true;
                if (integration) Register(root, desktop);
                if (hadOriginal)
                {
                    // Setup has committed. A locked old file must not roll back a working installation.
                    try { if (old != null) RemoveKnown(previous, old, false); TryRemoveEmpty(previous); }
                    catch { /* Keep the old directory if cleanup is blocked. */ }
                }
            }
            catch
            {
                if (swapped) { RemoveKnown(root, info, false); TryRemoveEmpty(root); }
                if (hadOriginal && Directory.Exists(previous) && !Directory.Exists(root)) Directory.Move(previous, root);
                if (integration && swapped) { try { if (old != null) Register(root, desktop); else Unregister(root); } catch { } }
                throw;
            }
            finally
            {
                if (Directory.Exists(stage)) { RemoveKnown(stage, info, false); TryRemoveEmpty(stage); }
            }
        }
        public static int Uninstall(string root, bool integration)
        {
            root = Canonical(root); ReleaseWorkingDirectory(root); WaitForRunning(root, 30000);
            var info = ReadInfo(root);
            if (info == null) throw new IOException("No recognized Patchwork installation was found.");
            int retained = RemoveKnown(root, info, true);
            if (integration) Unregister(root);
            TryRemoveEmpty(root);
            return retained;
        }
        static int RemoveKnown(string root, InstallInfo info, bool preserveModified)
        {
            root = Canonical(root); int retained = 0;
            foreach (var entry in info.Files)
            {
                string file = Resolve(root, entry.Key);
                if (!File.Exists(file)) continue;
                if (preserveModified && Hash(File.ReadAllBytes(file)) != entry.Value) { retained++; continue; }
                File.Delete(file);
            }
            string marker = Path.Combine(root, Marker); if (File.Exists(marker)) File.Delete(marker);
            var folders = info.Files.Keys.Select(x => Path.GetDirectoryName(Resolve(root, x))).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Length).ToList();
            foreach (string folder in folders) if (!String.Equals(folder, root, StringComparison.OrdinalIgnoreCase)) TryRemoveEmpty(folder);
            return retained;
        }
        static void TryRemoveEmpty(string folder) { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder, false); }
        static string ShortcutFolder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Patchwork"); } }
        static void Shortcut(string path, string target, string description)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell"); object shell = Activator.CreateInstance(shellType), link = null;
            try
            {
                link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                var type = link.GetType();
                if (File.Exists(path))
                {
                    string existing = (string)type.InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null);
                    if (!String.Equals(existing, target, StringComparison.OrdinalIgnoreCase)) throw new IOException("A shortcut with the same name belongs to another program: " + path);
                }
                type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
                type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(target) });
                type.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { description });
                type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { target + ",0" });
                type.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally { if (link != null) Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); }
        }
        static void RemoveShortcut(string path, string target)
        {
            if (!File.Exists(path)) return;
            var type = Type.GetTypeFromProgID("WScript.Shell"); object shell = Activator.CreateInstance(type), link = null;
            try
            {
                link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                string existing = (string)link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null);
                if (String.Equals(existing, target, StringComparison.OrdinalIgnoreCase)) File.Delete(path);
            }
            finally { if (link != null) Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); }
        }
        static void Register(string root, bool desktop)
        {
            using (var existing = Registry.CurrentUser.OpenSubKey(RegistryPath))
                if (existing != null && !String.Equals(existing.GetValue("InstallLocation") as string, root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Another product owns the Patchwork uninstall entry.");
            Directory.CreateDirectory(ShortcutFolder);
            Shortcut(Path.Combine(ShortcutFolder, "Patchwork.lnk"), Path.Combine(root, "Patchwork.exe"), "Windows patch manager");
            Shortcut(Path.Combine(ShortcutFolder, "Uninstall Patchwork.lnk"), Path.Combine(root, "Uninstall.exe"), "Uninstall Patchwork and keep backups");
            string desktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Patchwork.lnk");
            if (desktop) Shortcut(desktopLink, Path.Combine(root, "Patchwork.exe"), "Windows patch manager");
            else RemoveShortcut(desktopLink, Path.Combine(root, "Patchwork.exe"));
            using (var key = Registry.CurrentUser.CreateSubKey(RegistryPath))
            {
                key.SetValue("DisplayName", "Patchwork"); key.SetValue("DisplayVersion", Version); key.SetValue("Publisher", "Patchwork");
                key.SetValue("InstallLocation", root); key.SetValue("DisplayIcon", Path.Combine(root, "Patchwork.exe") + ",0");
                key.SetValue("UninstallString", "\"" + Path.Combine(root, "Uninstall.exe") + "\" --uninstall");
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd")); key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length) / 1024), RegistryValueKind.DWord);
            }
        }
        static void Unregister(string root)
        {
            RemoveShortcut(Path.Combine(ShortcutFolder, "Patchwork.lnk"), Path.Combine(root, "Patchwork.exe"));
            RemoveShortcut(Path.Combine(ShortcutFolder, "Uninstall Patchwork.lnk"), Path.Combine(root, "Uninstall.exe")); TryRemoveEmpty(ShortcutFolder);
            RemoveShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Patchwork.lnk"), Path.Combine(root, "Patchwork.exe"));
            using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath))
                if (key == null || !String.Equals(key.GetValue("InstallLocation") as string, root, StringComparison.OrdinalIgnoreCase)) return;
            Registry.CurrentUser.DeleteSubKey(RegistryPath, false);
        }
        public static int SelfTest(string scratch)
        {
            scratch = Canonical(scratch); Directory.CreateDirectory(scratch);
            string target = Path.Combine(scratch, "installed");
            if (Directory.Exists(target)) throw new IOException("Installer tests require a fresh scratch directory.");
            string data = Path.Combine(scratch, "user-data"); Directory.CreateDirectory(data); string sentinel = Path.Combine(data, "backup.original"); File.WriteAllText(sentinel, "Keep this backup.");
            int passed = 0;
            Install(target, false, false);
            if (!File.Exists(Path.Combine(target, "Patchwork.exe")) || ReadInfo(target).ProductId != ProductId) throw new Exception("Install test failed."); passed++;
            Install(target, false, false);
            if (!File.Exists(Path.Combine(target, "Uninstall.exe"))) throw new Exception("Update test failed."); passed++;
            BeforeStageForTest = i => { if (i == 1) throw new IOException("Simulated staging failure"); };
            try { Install(target, false, false); throw new Exception("Expected setup failure."); } catch (IOException) { }
            BeforeStageForTest = null;
            if (!File.Exists(Path.Combine(target, "Patchwork.exe")) || ReadInfo(target) == null) throw new Exception("Failed update did not preserve the installation."); passed++;
            File.WriteAllText(Path.Combine(target, "user-note.txt"), "Keep user-added file.");
            int retained = Uninstall(target, false);
            if (File.Exists(Path.Combine(target, "Patchwork.exe")) || !File.Exists(Path.Combine(target, "user-note.txt")) || !File.Exists(sentinel)) throw new Exception("Uninstall preservation test failed."); passed++;
            try { Uninstall(target, false); throw new Exception("Expected unrecognized-folder rejection."); } catch (IOException) { } passed++;
            string unrelated = Path.Combine(scratch, "unrelated"); Directory.CreateDirectory(unrelated); File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "Keep me.");
            try { Install(unrelated, false, false); throw new Exception("Expected unrelated-folder rejection."); } catch (IOException) { }
            if (File.ReadAllText(Path.Combine(unrelated, "keep.txt")) != "Keep me.") throw new Exception("Unrelated file changed."); passed++;
            string shortcut = Path.Combine(scratch, "test-shortcut.lnk");
            Task.Run(() => Shortcut(shortcut, Assembly.GetExecutingAssembly().Location, "Test shortcut")).GetAwaiter().GetResult();
            if (!File.Exists(shortcut)) throw new Exception("Shortcut creation failed."); passed++;
            try { Shortcut(shortcut, Path.Combine(scratch, "another.exe"), "Another target"); throw new Exception("Expected shortcut ownership rejection."); } catch (IOException) { } passed++;
            RemoveShortcut(shortcut, Assembly.GetExecutingAssembly().Location);
            if (File.Exists(shortcut)) throw new Exception("Owned shortcut removal failed."); passed++;
            File.WriteAllText(Path.Combine(scratch, "installer-results.txt"), passed + " installer checks passed. Tests used isolated program folders and a temporary shortcut; the real Installed Apps registry and user shortcuts were not modified.\r\n");
            Console.WriteLine(passed + " installer checks passed."); return 0;
        }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                string tests = Argument(args, "--self-test"); if (tests != null) return InstallerEngine.SelfTest(tests);
                bool uninstall = args.Contains("--uninstall") || Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location).Equals("Uninstall", StringComparison.OrdinalIgnoreCase);
                string root = Path.GetFullPath(Argument(args, "--install-root") ?? (uninstall ? AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\') : InstallerEngine.DefaultRoot));
                string screenshot = Argument(args, "--screenshot"); if (screenshot != null) screenshot = Path.GetFullPath(screenshot);
                InstallerEngine.ReleaseWorkingDirectory(root);
                int waitPid = 0; long waitTicks = 0;
                string pidArgument = Argument(args, "--wait-for-process"), ticksArgument = Argument(args, "--wait-for-start-ticks");
                if (pidArgument != null || ticksArgument != null)
                    if (!Int32.TryParse(pidArgument, out waitPid) || waitPid <= 0 || !Int64.TryParse(ticksArgument, out waitTicks) || waitTicks <= 0)
                        throw new IOException("Invalid update process identity.");
                if (uninstall && screenshot == null && String.Equals(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                {
                    string temporary = Path.Combine(Path.GetTempPath(), "Patchwork-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                    File.Copy(Assembly.GetExecutingAssembly().Location, temporary);
                    Process.Start(new ProcessStartInfo(temporary, "--uninstall --install-root \"" + root.TrimEnd('\\') + "\"") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(temporary) });
                    return 0;
                }
                var application = new Application(); var window = new SetupWindow(root, uninstall, waitPid, waitTicks);
                if (screenshot != null)
                {
                    window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000;
                    window.Loaded += async delegate {
                        try {
                            await Task.Delay(250); window.UpdateLayout(); var surface = (FrameworkElement)window.Content;
                            var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
                            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(screenshot))); using (var file = File.Create(screenshot)) png.Save(file);
                        } finally { window.Close(); }
                    };
                }
                application.Run(window); return 0;
            }
            catch (Exception error)
            {
                if (args.Contains("--self-test")) { Console.Error.WriteLine(error); return 1; }
                MessageBox.Show(error.Message, "Patchwork Setup", MessageBoxButton.OK, MessageBoxImage.Error); return 1;
            }
        }
        static string Argument(string[] args, string key) { int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    }

    public class SetupWindow : Window
    {
        readonly string root;
        readonly bool uninstall;
        readonly int waitPid;
        readonly long waitTicks;
        readonly TextBlock status;
        readonly Button action, cancel;
        readonly CheckBox desktop;
        bool busy, finished;
        public SetupWindow(string root, bool uninstall, int waitPid = 0, long waitTicks = 0)
        {
            this.root = root; this.uninstall = uninstall; this.waitPid = waitPid; this.waitTicks = waitTicks;
            Title = uninstall ? "Uninstall Patchwork" : "Install Patchwork"; Width = 620; Height = 680; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Color("#0B1019"); Foreground = Color("#EBEFF7"); FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
            var body = new Grid { Margin = new Thickness(34), Background = Background };
            body.RowDefinitions.Add(new RowDefinition()); body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var content = new StackPanel();
            var logo = new Border { Width = 48, Height = 48, Background = Color("#B4A1FF"), CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 24) };
            logo.Child = new TextBlock { Text = "P", FontSize = 32, FontWeight = FontWeights.Bold, Foreground = Color("#171125"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; content.Children.Add(logo);
            content.Children.Add(Text("PATCHWORK  /  WINDOWS", 10, "#A18BCF", 0));
            var heading = Text(uninstall ? "Uninstall Patchwork." : "Your apps. Your changes.", 30, "#EBEFF7", 11); heading.FontWeight = FontWeights.SemiBold; content.Children.Add(heading);
            var description = Text(uninstall ? "Remove the program and its shortcuts. Your patch backups and history will stay on this PC." : "Install the Windows patch manager with previews, verified backups, and one-click restoration.", 14, "#929DB1", 14); description.LineHeight = 22; content.Children.Add(description);
            var folder = new Border { Background = Color("#151D2B"), BorderBrush = Color("#2B354A"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(17), Margin = new Thickness(0, 24, 0, 0) };
            var folderText = new StackPanel(); folderText.Children.Add(Text(uninstall ? "PROGRAM FOLDER" : "INSTALL LOCATION", 10, "#73819A", 0));
            string displayRoot = String.Equals(root, InstallerEngine.DefaultRoot, StringComparison.OrdinalIgnoreCase) ? "%LOCALAPPDATA%\\Programs\\Patchwork" : Path.GetFullPath(root);
            var location = Text(displayRoot, 12, "#E5E9F3", 9); location.ToolTip = root; folderText.Children.Add(location); folder.Child = folderText; content.Children.Add(folder);
            if (!uninstall)
            {
                desktop = new CheckBox { Content = "Create a desktop shortcut", IsChecked = true, Foreground = Foreground, Margin = new Thickness(0, 20, 0, 0) }; content.Children.Add(desktop);
                content.Children.Add(Text("For your Windows user · No administrator rights needed", 12, "#85DCC0", 15));
                content.Children.Add(Text("Add separate patch files after installation.", 12, "#929DB1", 12));
            }
            else { content.Children.Add(Text("Restore any active patches in Patchwork before uninstalling if you want the target files returned to their originals.", 12, "#E9C985", 20)); }
            status = Text("Version 0.5.0 · Local patching · GitHub app updates", 12, "#73819A", 22); content.Children.Add(status); body.Children.Add(content);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) }; Grid.SetRow(buttons, 1);
            cancel = Button("Cancel", false); cancel.Margin = new Thickness(0, 0, 12, 0); cancel.Click += delegate { Close(); }; buttons.Children.Add(cancel);
            action = Button(uninstall ? "Uninstall" : "Install Patchwork", true); action.Click += async delegate { await RunAction(); }; buttons.Children.Add(action); body.Children.Add(buttons);
            var surface = new Grid { Background = Background }; surface.Children.Add(body); Content = surface;
            SourceInitialized += delegate { try { int enabled = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int)); } catch { } };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (busy) { e.Cancel = true; status.Text = "Please wait for setup to finish."; } };
        }
        async Task RunAction()
        {
            if (busy) return;
            if (finished)
            {
                if (!uninstall) Process.Start(new ProcessStartInfo(Path.Combine(root, "Patchwork.exe")) { UseShellExecute = true, WorkingDirectory = root });
                Close(); return;
            }
            bool shortcut = desktop != null && desktop.IsChecked == true;
            busy = true; action.IsEnabled = false; cancel.IsEnabled = false; if (desktop != null) desktop.IsEnabled = false;
            status.Text = uninstall ? "Waiting for Patchwork to close, then removing program files…" : "Waiting for Patchwork to close, then installing…"; status.Foreground = Color("#B4A1FF");
            try
            {
                int retained = 0;
                await Task.Run(() => { if (waitPid > 0) InstallerEngine.WaitForUpdater(waitPid, waitTicks, 30000); if (uninstall) retained = InstallerEngine.Uninstall(root, true); else InstallerEngine.Install(root, shortcut, true); });
                finished = true; status.Foreground = Color("#85DCC0"); status.Text = uninstall ? "Uninstalled. Backups and history are preserved." + (retained > 0 ? " Modified program files were retained." : "") : "Installed. Patchwork is ready in your Start menu.";
                action.Content = uninstall ? "Finish" : "Launch Patchwork"; cancel.Content = "Close";
            }
            catch (Exception error) { status.Text = error.Message; status.Foreground = Color("#F0BB9B"); if (desktop != null) desktop.IsEnabled = true; }
            finally { busy = false; action.IsEnabled = true; cancel.IsEnabled = true; }
        }
        static Brush Color(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }
        static TextBlock Text(string value, double size, string color, double top) { return new TextBlock { Text = value, FontSize = size, Foreground = Color(color), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) }; }
        static Button Button(string text, bool primary)
        {
            var button = new Button { Content = text, Foreground = Color(primary ? "#171125" : "#E5E9F3"), Background = Color(primary ? "#B4A1FF" : "#1B2332"), BorderBrush = Color("#303A50"), FontSize = 13, FontWeight = FontWeights.SemiBold, Padding = new Thickness(18, 12, 18, 12), Cursor = System.Windows.Input.Cursors.Hand };
            var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8)); border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) }); border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center); border.AppendChild(presenter); button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            return button;
        }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
