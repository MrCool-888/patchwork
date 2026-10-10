using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Patchwork
{
    public class PatchColorOption { public string Id, Label, Default; }
    public class ThemeResource { public string Theme, Key, Type, Value; }
    public static class ThemeOptions
    {
        public static string Color(string value)
        {
            if (value == null || !Regex.IsMatch(value, "^#[0-9a-fA-F]{6}\\z")) throw new InvalidDataException("Colors must use #RRGGBB, for example #8A66FF.");
            return value.ToUpperInvariant();
        }
        public static List<PatchColorOption> ParseOptions(Dictionary<string, object> raw)
        {
            var result = new List<PatchColorOption>(); var entries = Json.Array(raw, "options", true);
            if (entries.Count > 8) throw new InvalidDataException("A patch can have at most 8 color options.");
            foreach (var entry in entries)
            {
                var item = Json.Object(entry); if (Json.String(item, "type") != "color") throw new InvalidDataException("Only color options are supported.");
                var option = new PatchColorOption { Id = Json.String(item, "id"), Label = Json.String(item, "label"), Default = Color(Json.String(item, "default")) };
                PatchBundle.ValidateId(option.Id); if (String.IsNullOrWhiteSpace(option.Label) || option.Label.Length > 80 || result.Any(x => x.Id == option.Id)) throw new InvalidDataException("Invalid or duplicate color option."); result.Add(option);
            }
            return result;
        }
        public static void Validate(PatchDefinition patch)
        {
            foreach (var resource in patch.Operations.SelectMany(x => x.Resources))
                if (resource.Value.StartsWith("$", StringComparison.Ordinal) && !patch.Options.Any(x => "$" + x.Id == resource.Value)) throw new InvalidDataException("Unknown color option: " + resource.Value);
            if (patch.Options.Any(x => !patch.Operations.SelectMany(o => o.Resources).Any(r => r.Value == "$" + x.Id))) throw new InvalidDataException("A color option is unused.");
        }
        public static Dictionary<string, string> Read(object raw)
        {
            if (raw == null) return new Dictionary<string, string>();
            var values = Json.Object(raw); if (values.Count > 800) throw new InvalidDataException("Too many patch options.");
            return values.ToDictionary(x => x.Key, x => Color(x.Value as string), StringComparer.Ordinal);
        }
        public static PatchBundle Resolve(PatchBundle bundle, IEnumerable<string> selection, Dictionary<string, string> supplied, out Dictionary<string, string> choices)
        {
            choices = new Dictionary<string, string>();
            if (!bundle.Patches.Any(x => x.Options.Count != 0)) { if (supplied != null && supplied.Count != 0) throw new InvalidDataException("This pack has no color options."); return bundle; }
            var ids = new HashSet<string>(selection); var copy = PatchBundle.Parse(bundle.Content);
            foreach (var patch in copy.Patches.Where(x => ids.Contains(x.Id)))
                foreach (var option in patch.Options)
                {
                    string key = patch.Id + "." + option.Id, value; value = Color(supplied != null && supplied.TryGetValue(key, out value) ? value : option.Default); choices.Add(key, value);
                    foreach (var resource in patch.Operations.SelectMany(x => x.Resources).Where(x => x.Value == "$" + option.Id)) resource.Value = value;
                }
            if (supplied != null) foreach (string key in supplied.Keys) if (!choices.ContainsKey(key)) throw new InvalidDataException("Unknown or unselected patch option.");
            return copy;
        }
    }
}
