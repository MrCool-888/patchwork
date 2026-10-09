using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Reflection;

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
    }
}
