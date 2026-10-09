using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Reflection;
using System.Linq;

namespace Patchwork
{
    public static class UpdaterTests
    {
        static Dictionary<string, object> Release()
        {
            byte[] bytes = Encoding.ASCII.GetBytes("MZtest installer");
            return new Dictionary<string, object> {
                { "draft", false }, { "prerelease", false }, { "tag_name", "v0.4.0" }, { "html_url", Updates.ReleasesUrl + "/tag/v0.4.0" },
                { "assets", new object[] { new Dictionary<string, object> { { "name", "Patchwork-Setup.exe" }, { "browser_download_url", Updates.ReleasesUrl + "/download/v0.4.0/Patchwork-Setup.exe" }, { "size", bytes.Length }, { "digest", "sha256:" + PatchEngine.Hash(bytes) } } } }
            };
        }
        static void Assert(bool value) { if (!value) throw new Exception("Update verification failed."); }
        static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Unsafe update was accepted."); }
        public static void Versions()
        {
            string json = Json.Pretty(Release());
            Assert(Updates.Parse(json, new Version(0, 3, 0, 0)).Tag == "v0.4.0");
            Assert(Updates.Parse(json, new Version(0, 4, 0, 0)) == null);
            Assert(Updates.Parse(json, new Version(0, 5, 0, 0)) == null);
            foreach (string flag in new[] { "draft", "prerelease" }) { var release = Release(); release[flag] = true; Reject(() => Updates.Parse(Json.Pretty(release), new Version(0, 3, 0, 0))); }
        }
        public static void UntrustedAssets()
        {
            foreach (string key in new[] { "browser_download_url", "digest", "size" })
            {
                var release = Release(); var asset = Json.Object(Json.Array(release, "assets")[0]);
                asset[key] = key == "size" ? (object)(16 * 1024 * 1024 + 1) : key == "digest" ? "sha256:invalid" : "https://example.org/installer.exe";
                Reject(() => Updates.Parse(Json.Pretty(release), new Version(0, 3, 0, 0)));
            }
            var wrongRepo = Release(); wrongRepo["html_url"] = "https://github.com/other/patchwork/releases/tag/v0.4.0";
            Reject(() => Updates.Parse(Json.Pretty(wrongRepo), new Version(0, 3, 0, 0)));
            var missing = Release(); missing["assets"] = new object[0]; Reject(() => Updates.Parse(Json.Pretty(missing), new Version(0, 3, 0, 0)));
        }
        public static void Tampering()
        {
            var release = Updates.Parse(Json.Pretty(Release()), new Version(0, 3, 0, 0));
            byte[] bytes = Encoding.ASCII.GetBytes("MZtest installer"); Updates.Verify(release, bytes);
            bytes[3] ^= 1; Reject(() => Updates.Verify(release, bytes));
            Reject(() => Updates.Verify(release, new byte[1]));
            byte[] text = Encoding.ASCII.GetBytes("not an executable"); release.Size = text.Length; release.Sha256 = PatchEngine.Hash(text);
            Reject(() => Updates.Verify(release, text));
        }
        public static void TransportBoundaries()
        {
            foreach (string host in new[] { "api.github.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com" })
                Assert(GitHubHttp.Address("https://" + host + "/asset").Host == host);
            foreach (string url in new[] { "http://github.com/asset", "https://github.com:444/asset", "https://github.com.example.org/asset", "https://person:secret@github.com/asset", "https://example.org/asset", "https://github.com/asset#fragment" })
                Reject(() => GitHubHttp.Address(url));
            foreach (int maximum in new[] { 0, -1, 16 * 1024 * 1024 + 1 })
            {
                bool rejected = false;
                try { GitHubHttp.Fetch("https://api.github.com/", maximum); } catch (ArgumentOutOfRangeException) { rejected = true; }
                Assert(rejected);
            }
        }
        public static void SetupHandoff(string root)
        {
            root = Path.GetFullPath(root);
            string app = Path.Combine(root, "custom program folder"); Directory.CreateDirectory(app);
            string download = Path.Combine(root, "downloads", "Patchwork-Setup.exe");
            File.WriteAllText(Path.Combine(app, "patchwork-install.json"), "test marker");
            var start = Updates.SetupStartInfo(download, app, 42, 123456);
            Assert(start.FileName == download && start.WorkingDirectory == Path.GetDirectoryName(download) && start.UseShellExecute);
            Assert(start.Arguments == "--wait-for-process 42 --wait-for-start-ticks 123456 --install-root \"" + app + "\"");
            File.Delete(Path.Combine(app, "patchwork-install.json"));
            Assert(!Updates.SetupStartInfo(download, app, 42, 123456).Arguments.Contains("--install-root"));
            try { Updates.SetupStartInfo(Path.Combine(app, "updates", "Setup.exe"), app, 42, 123456); }
            catch (IOException) { return; }
            throw new Exception("Setup was allowed to pin its own installation folder.");
        }
        public static void ImportOnly(string root)
        {
            string data = Path.Combine(root, "import-only");
            Action<Action<Window, MainController>> open = action => {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Patchwork.MainWindow.xaml"))
                {
                    var window = (Window)XamlReader.Load(stream); var controller = new MainController(window, data);
                    try { action(window, controller); } finally { window.Close(); }
                }
            };
            string fixture = Path.Combine(root, "external-test.json"); File.WriteAllText(fixture, TestFixtures.DemoRecipe());
            open((window, controller) => {
                Assert(((ComboBox)window.FindName("AppPicker")).Items.Count == 0);
                Assert(((Border)window.FindName("EmptyLibrary")).Visibility == Visibility.Visible);
                controller.ImportFile(fixture);
                Assert(((ComboBox)window.FindName("AppPicker")).Items.Count == 1);
                Assert(((Border)window.FindName("EmptyLibrary")).Visibility == Visibility.Collapsed);
                controller.ImportFile(fixture);
                Assert(((ComboBox)window.FindName("AppPicker")).Items.Count == 1);
            });
            open((window, controller) => Assert(((ComboBox)window.FindName("AppPicker")).Items.Count == 1));
            Assert(File.Exists(Path.Combine(data, "recipes", "sandbox-v1.json")));
        }
        public static void VersionMetadata(string root)
        {
            string data = Path.Combine(root, "versions"); var engine = new PatchEngine(data); string target = TestFixtures.CreateDemo(data);
            var raw = Json.Parse(TestFixtures.DemoRecipe()); raw["packVersion"] = "1.1.0"; raw["minimumPatcherVersion"] = "0.4.0";
            Json.Object(Json.Array(raw, "patches")[0])["version"] = "1.0.0";
            string content = Json.Pretty(raw); var bundle = PatchBundle.Parse(content);
            Assert(bundle.PackVersion == "1.1.0" && bundle.Patches[0].Version == "1.0.0" && bundle.Patches[1].Version == "1.1.0");
            var journal = engine.Apply(engine.Preview(bundle, target, new[] { bundle.Patches[0].Id }));
            Assert(engine.History().Single().PackVersion == "1.1.0" && engine.History().Single().PatchNames.Single().Contains("v1.0.0") && engine.History().Single().BundleSha256 == PatchEngine.Hash(Encoding.UTF8.GetBytes(content)));
            raw["packVersion"] = "1.2.0"; bundle = PatchBundle.Parse(Json.Pretty(raw)); Assert(engine.History().Single().PackVersion == "1.1.0"); engine.Restore(journal);
            foreach (string invalid in new[] { "v1.0.0", "1.0", "1.0.0\n", "01.0.0", "<bad>" }) { raw["packVersion"] = invalid; Reject(() => PatchBundle.Parse(Json.Pretty(raw))); }
            raw["packVersion"] = "1.1.0"; raw["minimumPatcherVersion"] = "99.0.0"; Reject(() => PatchBundle.Parse(Json.Pretty(raw)));
            var legacy = PatchBundle.Parse(TestFixtures.DemoRecipe()); Assert(legacy.PackVersion == "Unversioned" && legacy.Patches.All(x => x.Version == "Unversioned"));
            string fixture = Path.Combine(data, "versioned.json"); File.WriteAllText(fixture, content);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Patchwork.MainWindow.xaml"))
            {
                var window = (Window)XamlReader.Load(stream); var controller = new MainController(window, Path.Combine(data, "ui"));
                try {
                    controller.ImportFile(fixture); Assert(((TextBlock)window.FindName("AppMeta")).Text.Contains("Patch pack v1.1.0"));
                    Assert(((ComboBox)window.FindName("AppPicker")).Items[0].ToString().Contains("pack v1.1.0"));
                    controller.ImportFile(fixture); Assert(((ComboBox)window.FindName("AppPicker")).Items.Count == 1);
                } finally { window.Close(); }
            }
        }
    }
}
