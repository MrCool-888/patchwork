using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Patchwork
{
    public class TargetLocation
    {
        public string Base { get; set; }
        public string Path { get; set; }
    }

    public class ProcessRule
    {
        public string Name { get; set; }
        public string Scope { get; set; }
    }

    public static class Targeting
    {
        static readonly HashSet<string> Bases = new HashSet<string>(new[] { "localAppData", "appData", "programFiles", "programFilesX86", "userProfile" }, StringComparer.Ordinal);

        public static void Parse(PatchBundle bundle, Dictionary<string, object> raw)
        {
            object value;
            if (raw.TryGetValue("targetLocations", out value))
            {
                var items = AsArray(value, "targetLocations");
                if (items.Count > 32) throw new InvalidDataException("targetLocations may contain at most 32 entries.");
                bundle.TargetLocations = new List<TargetLocation>();
                foreach (object item in items)
                {
                    var entry = Json.Object(item);
                    string locationBase = Json.String(entry, "base"), path = Json.String(entry, "path");
                    if (!Bases.Contains(locationBase)) throw new InvalidDataException("Unknown target location base: " + locationBase);
                    ValidateHintPath(path);
                    bundle.TargetLocations.Add(new TargetLocation { Base = locationBase, Path = path });
                }
            }
            if (raw.TryGetValue("processes", out value))
            {
                var items = AsArray(value, "processes");
                if (items.Count > 32) throw new InvalidDataException("processes may contain at most 32 entries.");
                bundle.Processes = new List<ProcessRule>();
                foreach (object item in items)
                {
                    var entry = Json.Object(item);
                    string name = Json.String(entry, "name"), scope = Json.String(entry, "scope");
                    if (!Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9 ._-]{0,127}$") || name.TrimEnd().EndsWith(".", StringComparison.Ordinal) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name != name.Trim())
                        throw new InvalidDataException("Process names must be extensionless executable names.");
                    if (scope != "target" && scope != "all") throw new InvalidDataException("Process scope must be target or all.");
                    bundle.Processes.Add(new ProcessRule { Name = name, Scope = scope });
                }
            }
        }

        static List<object> AsArray(object value, string key)
        {
            var array = value as System.Collections.IEnumerable;
            if (array == null || value is string || value is Dictionary<string, object>) throw new InvalidDataException("Expected an array in " + key + ".");
            return array.Cast<object>().ToList();
        }

        static void ValidateHintPath(string path)
        {
            if (path == "") return;
            PatchBundle.ValidateRelative(path);
        }

        public static List<string> FindTargets(PatchBundle bundle, PatchEngine engine)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (bundle.TargetLocations == null) return found.ToList();
            foreach (var hint in bundle.TargetLocations)
            {
                string parent = LocationRoot(hint.Base);
                if (String.IsNullOrEmpty(parent)) continue;
                string candidate = hint.Path.Length == 0 ? parent : Path.GetFullPath(Path.Combine(parent, hint.Path.Replace('\\', Path.DirectorySeparatorChar)));
                if (!Directory.Exists(candidate)) continue;
                try
                {
                    var applied = engine.VerifiedAppliedSession(bundle, candidate);
                    if (applied != null || PatchEngine.Hash(PatchEngine.Read(PatchEngine.Resolve(candidate, bundle.VersionFile), PatchEngine.VersionLimit(bundle))).Equals(bundle.VersionSha256, StringComparison.OrdinalIgnoreCase))
                        found.Add(Path.GetFullPath(candidate));
                }
                catch { /* A hint is only a candidate; invalid or inaccessible locations are ignored. */ }
            }
            return found.ToList();
        }

        static string LocationRoot(string key)
        {
            Environment.SpecialFolder folder = key == "localAppData" ? Environment.SpecialFolder.LocalApplicationData :
                key == "appData" ? Environment.SpecialFolder.ApplicationData : key == "programFiles" ? Environment.SpecialFolder.ProgramFiles :
                key == "programFilesX86" ? Environment.SpecialFolder.ProgramFilesX86 : Environment.SpecialFolder.UserProfile;
            return Environment.GetFolderPath(folder);
        }

        public static bool MatchesProcess(ProcessRule rule, string executable, string target)
        {
            if (rule == null || rule.Scope == "all") return true;
            if (rule.Scope != "target" || String.IsNullOrEmpty(executable)) return false;
            string root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(executable).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
    }
}
