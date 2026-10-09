using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Reflection;

namespace Patchwork
{
    public static class SourceTests
    {
        const string Repo = "test-owner/test-patches";
        static void Assert(bool value) { if (!value) throw new Exception("Patch source check failed."); }
        static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid source input was accepted."); }
        public static PatchBundle Bundle(string version, string theme = "midnight")
        {
            var raw = Json.Parse(TestFixtures.DemoRecipe()); raw["packVersion"] = version;
            Json.Object(Json.Array(Json.Object(Json.Array(raw, "patches")[0]), "operations")[0])["value"] = theme;
            return PatchBundle.Parse(Json.Pretty(raw));
        }
        static Dictionary<string, object> Release(string tag, string date, byte[] bytes, bool pre = false, bool draft = false)
        {
            return new Dictionary<string, object> {
                { "draft", draft }, { "prerelease", pre }, { "tag_name", tag }, { "published_at", date },
                { "html_url", "https://github.com/" + Repo + "/releases/tag/" + tag },
                { "assets", new object[] { new Dictionary<string, object> { { "name", "pack.patchwork.json" }, { "state", "uploaded" }, { "size", bytes.Length }, { "digest", "sha256:" + PatchEngine.Hash(bytes) }, { "browser_download_url", "https://github.com/" + Repo + "/releases/download/" + tag + "/pack.patchwork.json" } } } }
            };
        }
        static SourceDownload Incoming(string repository, PatchBundle bundle)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(bundle.Content);
            var asset = new PatchAsset { Name = "pack.patchwork.json", Size = bytes.Length, Sha256 = PatchEngine.Hash(bytes), ReleaseUrl = "https://github.com/" + repository + "/releases/tag/v" + bundle.PackVersion };
            return new SourceDownload { Repository = repository, IncludePrereleases = true, Packs = new List<Tuple<PatchAsset, PatchBundle>> { Tuple.Create(asset, PatchSources.Verify(asset, bytes)) } };
        }
        public static void Registration(string root)
        {
            Assert(PatchSources.Repository("https://github.com/" + Repo + "/releases/tag/v1#assets") == Repo);
            Assert(PatchSources.Repository("https://github.com/" + Repo + ".git") == Repo);
            foreach (string link in new[] { "http://github.com/" + Repo, "https://github.com.example.org/" + Repo, "https://person:secret@github.com/" + Repo, "https://github.com/" + Repo + "?token=private", "https://github.com/test-owner", "https://github.com/" + Repo + "/settings", "https://github.com/test-owner/%2e%2e" }) Reject(() => PatchSources.Repository(link));
            string data = Path.Combine(root, "source-registration"); var store = new PatchSourceStore(data);
            var source = store.Add("https://github.com/" + Repo, true);
            Reject(() => store.Add("https://github.com/TEST-OWNER/TEST-PATCHES/releases", false));
            Assert(MainController.IsSourceDue(source, DateTime.UtcNow));
            source.LastCheckUtc = DateTime.UtcNow.ToString("o"); Assert(!MainController.IsSourceDue(source, DateTime.UtcNow));
            Assert(MainController.IsSourceDue(source, DateTime.UtcNow.AddHours(1.1)));
            store.Options(Repo, false, false); store = new PatchSourceStore(data);
            Assert(!store.Sources.Single().Automatic && !store.Sources.Single().IncludePrereleases && !MainController.IsSourceDue(store.Sources.Single(), DateTime.UtcNow.AddDays(1)));
            store.Remove(Repo); Assert(new PatchSourceStore(data).Sources.Count == 0);
        }
        public static void ReleasesAndIntegrity()
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Bundle("1.0.0").Content);
            var stable = Release("stable", "2026-01-01T00:00:00Z", bytes);
            var experimental = Release("experimental", "2026-02-01T00:00:00Z", bytes, true);
            var draft = Release("draft", "2026-03-01T00:00:00Z", bytes, false, true);
            string feed = Json.Pretty(new object[] { stable, draft, experimental });
            Assert(PatchSources.ParseReleases(feed, Repo, false).Single().Url.Contains("/stable/"));
            var asset = PatchSources.ParseReleases(feed, Repo, true).Single(); Assert(asset.Url.Contains("/experimental/"));
            Assert(PatchSources.Verify(asset, bytes).PackVersion == "1.0.0");
            byte[] corrupt = bytes.ToArray(); corrupt[20] ^= 1; Reject(() => PatchSources.Verify(asset, corrupt));
            Reject(() => PatchSources.Verify(asset, new byte[1]));
            foreach (string field in new[] { "digest", "size", "browser_download_url", "name" })
            {
                var release = Release("v1", "2026-01-01T00:00:00Z", bytes); var raw = Json.Object(Json.Array(release, "assets")[0]);
                raw[field] = field == "size" ? (object)(PatchSources.MaximumPack + 1) : field == "digest" ? "invalid" : field == "name" ? "../pack.json" : "https://github.com/other/repo/releases/download/v1/pack.patchwork.json";
                Reject(() => PatchSources.ParseReleases(Json.Pretty(new[] { release }), Repo, true));
            }
            var legacy = Encoding.UTF8.GetBytes(TestFixtures.DemoRecipe()); asset.Size = legacy.Length; asset.Sha256 = PatchEngine.Hash(legacy); Reject(() => PatchSources.Verify(asset, legacy));
            var unsafeBytes = Encoding.UTF8.GetBytes(Bundle("1.0.0").Content.Replace("jsonSet", "runCommand")); asset.Size = unsafeBytes.Length; asset.Sha256 = PatchEngine.Hash(unsafeBytes); Reject(() => PatchSources.Verify(asset, unsafeBytes));
        }
        public static void CatalogAndHistory(string root)
        {
            string data = Path.Combine(root, "source-catalog"); var store = new PatchSourceStore(data); store.Add("https://github.com/" + Repo, true);
            var original = Bundle("1.0.0"); store.ImportLocal(original);
            var engine = new PatchEngine(data); string target = TestFixtures.CreateDemo(data);
            var first = engine.Apply(engine.Preview(original, target, new[] { "midnight-theme" }));
            Assert(store.Apply(Incoming(Repo, Bundle("1.1.0", "dark"))) == 1);
            Assert(PatchBundle.Parse(File.ReadAllText(Path.Combine(data, "recipes/sandbox-v1.json"))).PackVersion == "1.1.0");
            Assert(engine.History().Single().PackVersion == "1.0.0" && engine.History().Single().PatchIds.Single() == "midnight-theme");
            Assert(store.Apply(Incoming(Repo, Bundle("1.0.0"))) == 0);
            string saved = File.ReadAllText(Path.Combine(data, "recipes/sandbox-v1.json"));
            Reject(() => store.Apply(Incoming(Repo, Bundle("1.1.0", "changed-without-version"))));
            Assert(File.ReadAllText(Path.Combine(data, "recipes/sandbox-v1.json")) == saved);
            store.Add("https://github.com/another-owner/patches", true);
            Reject(() => store.Apply(Incoming("another-owner/patches", Bundle("2.0.0"))));
            Reject(() => store.ImportLocal(original));
            Assert(store.Owner(original.Id) == Repo);
            var current = PatchBundle.Parse(saved); var second = engine.Apply(engine.Preview(current, target, new[] { "midnight-theme" }));
            Assert(first.PackVersion == "1.0.0" && second.PackVersion == "1.1.0");
            store.Remove(Repo); Assert(File.ReadAllText(Path.Combine(data, "recipes/sandbox-v1.json")) == saved && engine.History().Count == 2);
            engine.Restore(second); Assert(File.ReadAllText(Path.Combine(target, "settings.json")) == TestFixtures.DemoConfig);
        }
        public static void DownloadCacheAndAtomicValidation(string root)
        {
            string data = Path.Combine(root, "source-download"); var store = new PatchSourceStore(data); var source = store.Add("https://github.com/" + Repo, true);
            byte[] bytes = Encoding.UTF8.GetBytes(Bundle("1.0.0").Content);
            byte[] feed = Encoding.UTF8.GetBytes(Json.Pretty(new[] { Release("v1", "2026-01-01T00:00:00Z", bytes) }));
            int calls = 0; Func<string, int, byte[]> fetch = (url, limit) => { calls++; return url.StartsWith("https://api.github.com/") ? feed : bytes; };
            var download = PatchSources.Download(source, data, fetch); Assert(calls == 2 && store.Apply(download) == 1);
            calls = 0; source = new PatchSourceStore(data).Sources.Single(); download = PatchSources.Download(source, data, fetch); Assert(calls == 1 && download.Packs.Count == 0 && store.Apply(download) == 0);
            string saved = File.ReadAllText(Path.Combine(data, "recipes/sandbox-v1.json"));
            var invalidBatch = Incoming(Repo, Bundle("1.1.0", "dark"));
            var differentAsset = Incoming(Repo, Bundle("1.1.0", "other")); differentAsset.Packs[0].Item1.Name = "other.json";
            invalidBatch.Packs.Add(differentAsset.Packs[0]); Reject(() => store.Apply(invalidBatch));
            Assert(File.ReadAllText(Path.Combine(data, "recipes/sandbox-v1.json")) == saved && store.Sources.Single().Packs.Single().Version == "1.0.0");
            File.AppendAllText(Path.Combine(data, "recipes/sandbox-v1.json"), " ");
            Reject(() => store.Apply(Incoming(Repo, Bundle("1.1.0"))));
            Assert(File.ReadAllText(Path.Combine(data, "recipes/sandbox-v1.json")) == saved + " ");
            store.RecordError(Repo, "Offline. Existing patches kept."); Assert(new PatchSourceStore(data).Sources.Single().Status.Contains("Offline"));
        }
        public static void SourcesInterface(string root)
        {
            string data = Path.Combine(root, "source-ui"); var store = new PatchSourceStore(data); store.Add("https://github.com/" + Repo, true); store.Apply(Incoming(Repo, Bundle("1.0.0")));
            var engine = new PatchEngine(data); string target = TestFixtures.CreateDemo(data);
            engine.Apply(engine.Preview(Bundle("1.0.0"), target, new[] { "midnight-theme" }));
            store.Apply(Incoming(Repo, Bundle("1.1.0", "dark")));
            File.WriteAllText(Path.Combine(data, "settings.json"), Json.Pretty(new Dictionary<string, string> { { "sandbox-v1", target } }));
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Patchwork.MainWindow.xaml"))
            {
                var window = (Window)XamlReader.Load(stream); var controller = new MainController(window, data);
                try
                {
                    ((Button)window.FindName("SourcesNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(((Grid)window.FindName("SourcesPage")).Visibility == Visibility.Visible && ((StackPanel)window.FindName("SourcesList")).Children.Count == 1);
                    Assert(((CheckBox)window.FindName("SourcePrereleases")).IsChecked == true && ((Button)window.FindName("CheckSourcesButton")).IsEnabled);
                    Assert(((TextBlock)window.FindName("SessionDescription")).Text.Contains(Repo));
                    Assert(((TextBlock)window.FindName("SelectedCount")).Text == "1 patch");
                    Assert(((TextBlock)window.FindName("TargetStatus")).Text.Contains("no manual restore needed"));
                    ((Button)window.FindName("PreviewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(((TextBlock)window.FindName("BeforeLabel")).Text == "CURRENTLY APPLIED");
                    Assert(((Button)window.FindName("ApplyButton")).Content.ToString() == "Update patches");
                    Assert(engine.History().Count == 1 && File.ReadAllText(Path.Combine(target, "settings.json")).Contains("midnight"));
                }
                finally { window.Close(); }
            }
        }
    }
}
