using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Patchwork
{
    public static class AuthorValidation
    {
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int handle);
        [DllImport("kernel32.dll")] static extern uint GetFileType(IntPtr handle);
        [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr handle, out uint mode);
        [DllImport("kernel32.dll")] static extern bool AttachConsole(uint process);
        static void InitializeConsole()
        {
            IntPtr output = GetStdHandle(-11); uint mode;
            uint type = GetFileType(output);
            if (type == 1 || type == 3)
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
                return;
            }
            if (GetConsoleMode(output, out mode)) return;
            if (!AttachConsole(0xffffffff)) return;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        public static int Run(string[] args)
        {
            InitializeConsole();
            var diagnostics = new List<string>();
            string report = null;
            string reportPath = null;
            bool reportSafe = false;
            int result = 1;
            string temporary = null;
            try
            {
                string file = null, target = null, selection = null;
                for (int i = 0; i < args.Length; i++)
                {
                    string flag = args[i];
                    if (flag != "--validate-patch" && flag != "--target" && flag != "--select" && flag != "--report")
                        throw new InvalidDataException("Unknown validation argument: " + flag);
                    if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new InvalidDataException("Missing value for " + flag + ".");
                    string value = args[i];
                    if (flag == "--validate-patch") { if (file != null) throw new InvalidDataException("Use --validate-patch once."); file = value; }
                    else if (flag == "--target") { if (target != null) throw new InvalidDataException("Use --target once."); target = value; }
                    else if (flag == "--select") { if (selection != null) throw new InvalidDataException("Use --select once."); selection = value; }
                    else { if (report != null) throw new InvalidDataException("Use --report once."); report = value; }
                }
                if (String.IsNullOrWhiteSpace(file)) throw new InvalidDataException("Usage: Patchwork.exe --validate-patch FILE [--target DIR --select ID,ID] [--report FILE]");
                if (target != null && String.IsNullOrWhiteSpace(selection)) throw new InvalidDataException("--target requires --select with one or more patch IDs.");
                if (selection != null && target == null) throw new InvalidDataException("--select requires --target.");
                file = Path.GetFullPath(file);
                if (target != null) target = Path.GetFullPath(target);
                if (report != null)
                {
                    reportPath = Path.GetFullPath(report);
                    if (reportPath.Equals(file, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The report must not overwrite the patch file.");
                    if (target != null)
                    {
                        string prefix = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (reportPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || reportPath.Equals(target, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The report must be outside the target folder.");
                    }
                    ValidateReportPath(reportPath);
                    reportSafe = true;
                }
                diagnostics.Add("Patch file: " + file);
                if (!File.Exists(file)) throw new FileNotFoundException("Patch file was not found.", file);
                byte[] source = PatchEngine.Read(file, 1024 * 1024 + 3);
                int offset = source.Length >= 3 && source[0] == 0xef && source[1] == 0xbb && source[2] == 0xbf ? 3 : 0;
                string content = new UTF8Encoding(false, true).GetString(source, offset, source.Length - offset);
                PatchBundle bundle = PatchBundle.Parse(content);
                diagnostics.Add("Valid patch bundle " + bundle.Id + " · " + bundle.AppName + " " + bundle.AppVersion + " · pack " + bundle.PackVersion + ".");
                diagnostics.Add("Patches: " + bundle.Patches.Count(x => x.Ready) + " ready, " + bundle.Patches.Count(x => !x.Ready) + " planned.");
                if (target != null)
                {
                    if (!Directory.Exists(target)) throw new DirectoryNotFoundException("Target folder does not exist: " + target);
                    var ids = selection.Split(',').Select(x => x.Trim()).ToList();
                    if (ids.Any(String.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) throw new InvalidDataException("--select must be a comma-separated list of unique patch IDs.");
                    var known = new HashSet<string>(bundle.Patches.Where(x => x.Ready).Select(x => x.Id), StringComparer.Ordinal);
                    string unknown = ids.FirstOrDefault(x => !known.Contains(x));
                    if (unknown != null) throw new InvalidDataException("Unknown or unavailable patch ID: " + unknown);
                    temporary = Path.Combine(Path.GetTempPath(), "Patchwork-validate-" + Guid.NewGuid().ToString("N"));
                    var engine = new PatchEngine(temporary);
                    try
                    {
                        var plan = engine.Preview(bundle, target, ids);
                        diagnostics.Add("Preview succeeded for " + bundle.Id + " patches [" + String.Join(", ", ids) + "] at " + target + ": " + plan.Files.Count + " file(s), " + plan.PatchNames.Count + " patch(es). No target files were written.");
                    }
                    catch (Exception error)
                    {
                        throw new InvalidOperationException("Preview failed for bundle " + bundle.Id + " and patches [" + String.Join(", ", ids) + "]: " + error.Message, error);
                    }
                }
                result = 0;
            }
            catch (Exception error) { diagnostics.Add("Validation failed: " + error.Message); }
            finally
            {
                if (temporary != null) try { Directory.Delete(temporary, true); } catch { diagnostics.Add("Temporary validation data could not be removed: " + temporary); result = 1; }
                foreach (string line in diagnostics) Console.WriteLine(line);
                if (report != null && reportSafe)
                {
                    try
                    {
                        using (var output = new StreamWriter(new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false)))
                            foreach (string line in diagnostics) output.WriteLine(line);
                    }
                    catch (Exception error) { Console.Error.WriteLine("Could not write validation report: " + error.Message); result = 1; }
                }
            }
            return result;
        }

        static void ValidateReportPath(string reportPath)
        {
            string current = reportPath;
            while (!String.IsNullOrEmpty(current))
            {
                if (File.Exists(current) || Directory.Exists(current))
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The report path cannot pass through a symlink or junction.");
                string parent = Path.GetDirectoryName(current);
                if (String.IsNullOrEmpty(parent) || parent == current) break;
                current = parent;
            }
        }
    }
}
