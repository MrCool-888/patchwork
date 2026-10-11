using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Patchwork
{
    public static class CommunityTargetTests
    {
        public static int Run(string root)
        {
            Directory.CreateDirectory(root);
            var legacy = PatchBundle.Parse(TestFixtures.DemoRecipe());
            if (legacy.TargetLocations != null || legacy.Processes != null) throw new InvalidOperationException("Legacy recipes must retain null targeting and process policies.");
            var raw = Json.Parse(TestFixtures.DemoRecipe());
            raw["minimumPatcherVersion"] = "0.10.0";
            string hintName = "PatchworkTest-" + Guid.NewGuid().ToString("N");
            raw["targetLocations"] = new object[] { new Dictionary<string, object> { { "base", "localAppData" }, { "path", hintName + "\\Target" } } };
            raw["processes"] = new object[] { new Dictionary<string, object> { { "name", "demo" }, { "scope", "target" } }, new Dictionary<string, object> { { "name", "launcher" }, { "scope", "all" } } };
            PatchBundle bundle = PatchBundle.Parse(Json.Pretty(raw));
            if (bundle.TargetLocations == null || bundle.TargetLocations.Count != 1 || bundle.Processes == null || bundle.Processes.Count != 2) throw new InvalidOperationException("Target/process hint parsing failed.");
            var emptyPolicies = Json.Parse(TestFixtures.DemoRecipe()); emptyPolicies["minimumPatcherVersion"] = "0.10.0"; emptyPolicies["processes"] = new object[0];
            var emptyBundle = PatchBundle.Parse(Json.Pretty(emptyPolicies));
            if (emptyBundle.Processes == null || emptyBundle.Processes.Count != 0) throw new InvalidOperationException("An explicitly empty process policy must remain distinct from an omitted one.");
            ExpectInvalid(() => { var bad = Json.Parse(TestFixtures.DemoRecipe()); bad["targetLocations"] = new object[] { new Dictionary<string, object> { { "base", "userProfile" }, { "path", "..\\escape" } } }; PatchBundle.Parse(Json.Pretty(bad)); });
            ExpectInvalid(() => { var bad = Json.Parse(TestFixtures.DemoRecipe()); bad["processes"] = new object[] { new Dictionary<string, object> { { "name", "java.exe" }, { "scope", "all" } } }; PatchBundle.Parse(Json.Pretty(bad)); });
            string data = Path.Combine(Path.GetTempPath(), "PatchworkTargetData-" + Guid.NewGuid().ToString("N"));
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string hintRoot = Path.Combine(appData, hintName), hinted = Path.Combine(hintRoot, "Target");
            string recipe = Path.Combine(root, "community-validation.patchwork");
            Directory.CreateDirectory(data); Directory.CreateDirectory(hinted);
            try
            {
                File.WriteAllText(Path.Combine(hinted, bundle.VersionFile), TestFixtures.DemoVersion, new UTF8Encoding(false));
                var engine = new PatchEngine(data);
                var matches = Targeting.FindTargets(bundle, engine);
                if (matches.Count != 1 || !Path.GetFullPath(matches[0]).Equals(Path.GetFullPath(hinted), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Hinted target matching failed.");
                if (!Targeting.MatchesProcess(bundle.Processes[0], Path.Combine(hinted, "demo.exe"), hinted) || Targeting.MatchesProcess(bundle.Processes[0], Path.Combine(data, "demo.exe"), hinted) || !Targeting.MatchesProcess(bundle.Processes[1], null, hinted)) throw new InvalidOperationException("Process rule matching failed.");
                File.WriteAllText(recipe, TestFixtures.DemoRecipe(), new UTF8Encoding(false));
                if (AuthorValidation.Run(new[] { "--validate-patch", recipe }) != 0) throw new InvalidOperationException("CLI bundle validation failed.");
                byte[] recipeBytes = File.ReadAllBytes(recipe);
                if (AuthorValidation.Run(new[] { "--validate-patch", recipe, "--report", recipe }) == 0 || !recipeBytes.SequenceEqual(File.ReadAllBytes(recipe))) throw new InvalidOperationException("CLI report path overwrote its patch input.");
                string demo = TestFixtures.CreateDemo(data);
                byte[] original = File.ReadAllBytes(Path.Combine(demo, "settings.json"));
                if (AuthorValidation.Run(new[] { "--validate-patch", recipe, "--target", demo, "--select", "midnight-theme" }) != 0) throw new InvalidOperationException("CLI preview validation failed.");
                if (!original.SequenceEqual(File.ReadAllBytes(Path.Combine(demo, "settings.json")))) throw new InvalidOperationException("CLI validation wrote to a target.");
                string unsafeReport = Path.Combine(demo, "validation.txt");
                if (AuthorValidation.Run(new[] { "--validate-patch", recipe, "--target", demo, "--select", "midnight-theme", "--report", unsafeReport }) == 0 || File.Exists(unsafeReport) || !original.SequenceEqual(File.ReadAllBytes(Path.Combine(demo, "settings.json")))) throw new InvalidOperationException("CLI wrote a report inside the target folder.");
                if (AuthorValidation.Run(new[] { "--validate-patch", recipe, "--target", demo }) == 0) throw new InvalidOperationException("CLI accepted a target without selection.");
                return 0;
            }
            finally
            {
                try { Directory.Delete(data, true); } catch { }
                try { Directory.Delete(hintRoot, true); } catch { }
                try { File.Delete(recipe); } catch { }
            }
        }

        static void ExpectInvalid(Action action)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("An invalid targeting rule was accepted.");
        }
    }
}
