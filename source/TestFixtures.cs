using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
namespace Patchwork
{
    public static class TestFixtures
    {
        public const string DemoConfig = "{\r\n  \"appearance\": {\r\n    \"theme\": \"system\",\r\n    \"accent\": \"#6D6AF0\"\r\n  },\r\n  \"privacy\": {\r\n    \"telemetry\": true\r\n  },\r\n  \"interface\": {\r\n    \"showPromotions\": true\r\n  }\r\n}\r\n";
        public const string DemoVersion = "Patchwork Sandbox 1.0.0\r\n";
        public static string DemoRecipe()
        {
            string hash = PatchEngine.Hash(Encoding.UTF8.GetBytes(DemoConfig));
            var patches = new List<object>();
            Action<string, string, string, string, string[], object, object> add = (id, name, description, category, path, expected, value) =>
                patches.Add(new Dictionary<string, object> {
                    { "id", id }, { "name", name }, { "description", description }, { "category", category },
                    { "operations", new object[] { new Dictionary<string, object> { { "kind", "jsonSet" }, { "file", "settings.json" }, { "sha256", hash }, { "path", path }, { "expected", expected }, { "value", value } } } }
                });
            add("midnight-theme", "Midnight theme", "Set the demo's appearance preference to midnight.", "Appearance", new[] { "appearance", "theme" }, "system", "midnight");
            add("violet-accent", "Violet accent", "Change the demo accent color to soft violet.", "Appearance", new[] { "appearance", "accent" }, "#6D6AF0", "#B4A1FF");
            add("no-telemetry", "Disable demo telemetry", "Turn off the demo configuration's telemetry flag.", "Privacy", new[] { "privacy", "telemetry" }, true, false);
            add("hide-promotions", "Hide demo promotions", "Hide promotions in the demo configuration.", "Interface", new[] { "interface", "showPromotions" }, true, false);
            return Json.Pretty(new Dictionary<string, object> {
                { "schemaVersion", 1 }, { "id", "sandbox-v1" }, { "appId", "patchwork-sandbox" }, { "appName", "Demo sandbox" }, { "appVersion", "1.0.0" },
                { "author", "Patchwork" }, { "source", "Self-test fixture" }, { "versionFile", "app.version" }, { "versionSha256", PatchEngine.Hash(Encoding.UTF8.GetBytes(DemoVersion)) }, { "patches", patches }
            });
        }
        public static string CreateDemo(string dataRoot)
        {
            string directory = Path.Combine(dataRoot, "sandbox"); Directory.CreateDirectory(directory);
            string config = Path.Combine(directory, "settings.json"), version = Path.Combine(directory, "app.version");
            if (!File.Exists(config)) File.WriteAllText(config, DemoConfig, new UTF8Encoding(false));
            if (!File.Exists(version)) File.WriteAllText(version, DemoVersion, new UTF8Encoding(false));
            return directory;
        }
    }

}
