// Compile separately against Patchwork-Setup.exe. Never included in the installer.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using PatchworkSetup;

public static class UpdateTests
{
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static Process Holder(string exe, int milliseconds)
    {
        return Process.Start(new ProcessStartInfo(exe, "--hold " + milliseconds) { UseShellExecute = false, CreateNoWindow = true });
    }
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--hold") { Thread.Sleep(Int32.Parse(args[1])); return 0; }
        try { return Run(Path.GetFullPath(args[0])); }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static int Run(string scratch)
    {
        if (Directory.Exists(scratch)) throw new Exception("Use a fresh test directory.");
        Directory.CreateDirectory(scratch); int passed = 0;
        string helper = Path.Combine(scratch, "holder"); Directory.CreateDirectory(helper);
        string exe = Path.Combine(helper, "Patchwork.exe");
        File.Copy(Assembly.GetExecutingAssembly().Location, exe);
        File.Copy(typeof(InstallerEngine).Assembly.Location, Path.Combine(helper, "Patchwork-Setup.exe"));
        using (var child = Holder(exe, 800))
        {
            long ticks = child.StartTime.ToUniversalTime().Ticks;
            InstallerEngine.WaitForUpdater(child.Id, ticks + 1, 0);
            Assert(!child.HasExited, "Reused PID identity should not wait."); passed++;
            try { InstallerEngine.WaitForUpdater(child.Id, ticks, 20); throw new Exception("Expected bounded timeout."); }
            catch (IOException) { Assert(!child.HasExited, "Timeout killed the process."); passed++; }
            InstallerEngine.WaitForUpdater(child.Id, ticks, 5000);
            Assert(child.HasExited, "Update did not wait for exit."); passed++;
            InstallerEngine.WaitForUpdater(child.Id, ticks, 0); passed++;
        }
        using (var child = Holder(exe, 800))
        {
            try { InstallerEngine.WaitForRunning(helper, 20); throw new Exception("Expected running-app timeout."); }
            catch (IOException) { Assert(!child.HasExited, "Running-app timeout killed the process."); passed++; }
            InstallerEngine.WaitForRunning(helper, 5000);
            Assert(child.HasExited, "Legacy updater did not wait for the matching process."); passed++;
        }
        string target = Path.Combine(scratch, "installed"); InstallerEngine.Install(target, false, false);
        // Reproduce the old updater inheriting the installation as its current folder.
        string initial = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = target;
            string renamed = Path.Combine(scratch, "locked-rename"); bool locked = false;
            try { Directory.Move(target, renamed); Directory.Move(renamed, target); }
            catch (IOException) { locked = true; }
            Assert(locked, "Windows did not reproduce the working-directory lock."); passed++;
            InstallerEngine.Install(target, false, false);
            Assert(!Environment.CurrentDirectory.Equals(target, StringComparison.OrdinalIgnoreCase) && InstallerEngine.ReadInfo(target).Version == InstallerEngine.Version, "Inherited-CWD update failed."); passed++;
            string note = Path.Combine(target, "locked-note.txt"); File.WriteAllText(note, "Keep me");
            byte[] original = File.ReadAllBytes(Path.Combine(target, "Patchwork.exe"));
            using (var lockedFile = new FileStream(note, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { InstallerEngine.Install(target, false, false); throw new Exception("Expected locked-folder failure."); }
                catch (IOException) { }
                Assert(Convert.ToBase64String(original) == Convert.ToBase64String(File.ReadAllBytes(Path.Combine(target, "Patchwork.exe"))) && InstallerEngine.ReadInfo(target) != null, "Lock failure damaged the old installation."); passed++;
            }
            InstallerEngine.Install(target, false, false);
            Assert(Directory.GetFiles(scratch, "locked-note.txt", SearchOption.AllDirectories).Length == 1, "Update lost an unrelated file."); passed++;
        }
        finally { Environment.CurrentDirectory = initial; }
        File.WriteAllText(Path.Combine(scratch, "update-results.txt"), passed + " update lock checks passed. No live installation, registry, shortcuts or user data were changed.");
        Console.WriteLine(passed + " update lock checks passed."); return 0;
    }
}
