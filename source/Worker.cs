using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;

namespace Patchwork
{
    public static class Worker
    {
        public static bool Protected(string target)
        {
            var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return false;
            string full = Path.GetFullPath(target).TrimEnd('\\') + "\\";
            return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows }
                .Select(Environment.GetFolderPath).Where(x => x.Length > 0).Any(x => full.StartsWith(Path.GetFullPath(x).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        }
        public static void CheckClientClosed(string target)
        {
            bool lunarGame = File.Exists(Path.Combine(target, ".lunarclient", "offline", "multiver", "lunar.jar"));
            foreach (string name in new[] { "ProtonVPN.Client", "ProtonVPN", "Blitz", "Lunar Client", "java", "javaw" })
                foreach (var process in Process.GetProcessesByName(name))
                    using (process)
                    {
                        try
                        {
                            if (lunarGame && (name == "java" || name == "javaw")) throw new InvalidOperationException("Close Minecraft and other Java processes before applying, updating or restoring Lunar patches.");
                            string executable = process.MainModule.FileName;
                            if (executable.StartsWith(Path.GetFullPath(target).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Close " + name + " from its tray menu before applying, updating or restoring patches.");
                        }
                        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException(name + " is running and could not be inspected. Close it before patching."); }
                    }
        }
        public static string CreateJob(PatchEngine engine, PatchBundle bundle, PatchPlan plan, Journal restore)
        {
            string jobs = Path.Combine(engine.DataRoot, "jobs"); Directory.CreateDirectory(jobs);
            var job = new Dictionary<string, object> { { "mode", restore == null ? "apply" : "restore" }, { "targetRoot", restore == null ? plan.TargetRoot : restore.TargetRoot } };
            if (restore == null)
            {
                job["bundle"] = bundle.Content; job["selected"] = plan.PatchIds; job["options"] = plan.Options;
                job["before"] = plan.Files.ToDictionary(x => x.RelativePath, x => x.BeforeHash);
                job["after"] = plan.Files.ToDictionary(x => x.RelativePath, x => x.AfterHash);
                job["previousJournalId"] = plan.PreviousJournalId ?? "";
            }
            else job["journalId"] = restore.Id;
            string file = PatchEngine.Resolve(jobs, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(file, Json.Pretty(job), new UTF8Encoding(false)); return file;
        }
        public static int Execute(string file, string dataRoot)
        {
            string jobs = PatchEngine.Resolve(dataRoot, "jobs"); string absolute = Path.GetFullPath(file);
            string name = Path.GetFileName(file);
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-f0-9]{32}\\.json$") || !PatchEngine.Resolve(jobs, name).Equals(absolute, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid worker request location.");
            string result = PatchEngine.Resolve(jobs, name + ".result");
            try
            {
                var engine = new PatchEngine(dataRoot); var job = Json.Parse(PatchEngine.Decode(PatchEngine.Read(absolute)));
                string target = Json.String(job, "targetRoot"); string mode = Json.String(job, "mode"); string journalId;
                CheckClientClosed(target);
                if (mode == "apply")
                {
                    var bundle = PatchBundle.Parse(Json.String(job, "bundle"));
                    object options; job.TryGetValue("options", out options);
                    var plan = engine.Preview(bundle, target, Json.Array(job, "selected").Cast<string>(), ThemeOptions.Read(options));
                    if ((plan.PreviousJournalId ?? "") != Json.String(job, "previousJournalId", "")) throw new InvalidOperationException("The applied session changed after preview. Preview again.");
                    var before = Json.Object(job["before"]); var after = Json.Object(job["after"]);
                    if (before.Count != plan.Files.Count || after.Count != plan.Files.Count || plan.Files.Any(x => !before.ContainsKey(x.RelativePath) || !after.ContainsKey(x.RelativePath) || (string)before[x.RelativePath] != x.BeforeHash || (string)after[x.RelativePath] != x.AfterHash)) throw new InvalidOperationException("Files or patches changed after preview. Preview again.");
                    journalId = engine.Apply(plan).Id;
                }
                else if (mode == "restore")
                {
                    var journal = engine.History().Single(x => x.Id == Json.String(job, "journalId"));
                    if (!Path.GetFullPath(journal.TargetRoot).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Restore target changed.");
                    engine.Restore(journal); journalId = journal.Id;
                }
                else throw new InvalidDataException("Unknown worker operation.");
                File.WriteAllText(result, Json.Pretty(new { success = true, journalId = journalId }), new UTF8Encoding(false)); return 0;
            }
            catch (Exception error) { File.WriteAllText(result, Json.Pretty(new { success = false, message = error.Message }), new UTF8Encoding(false)); return 1; }
        }
        public static string RunElevated(PatchEngine engine, PatchBundle bundle, PatchPlan plan, Journal restore)
        {
            string file = CreateJob(engine, bundle, plan, restore);
            try
            {
                var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--worker \"" + file + "\" --data-dir \"" + engine.DataRoot + "\"") { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory };
                using (var process = Process.Start(start)) process.WaitForExit();
                string result = file + ".result";
                if (!File.Exists(result)) throw new IOException("The administrator helper did not return a result.");
                var response = Json.Parse(PatchEngine.Decode(PatchEngine.Read(result)));
                if (!(bool)response["success"]) throw new IOException(Json.String(response, "message"));
                return Json.String(response, "journalId");
            }
            finally
            {
                if (File.Exists(file)) File.Delete(file);
                if (File.Exists(file + ".result")) File.Delete(file + ".result");
            }
        }
    }
}
