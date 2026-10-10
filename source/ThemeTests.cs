// Test-only WinUI-shaped fixture. Real WinUI loading is checked separately in the Proton pack probes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Mono.Cecil;

namespace Microsoft.UI.Xaml
{
    public class ResourceDictionary { public string Xaml; public IList<ResourceDictionary> MergedDictionaries { get; private set; } public ResourceDictionary() { MergedDictionaries = new List<ResourceDictionary>(); } }
    public class Application { static readonly Application instance = new Application(); public static Application Current { get { return instance; } } public ResourceDictionary Resources { get; private set; } public Application() { Resources = new ResourceDictionary(); } }
}
namespace Microsoft.UI.Xaml.Markup { public static class XamlReader { public static object Load(string text) { return new Microsoft.UI.Xaml.ResourceDictionary { Xaml = text }; } } }
namespace Patchwork
{
    public class ThemeFixture { public void Merge() { Microsoft.UI.Xaml.Application.Current.Resources.MergedDictionaries.Add(new Microsoft.UI.Xaml.ResourceDictionary()); } }
    public static class ThemeTests
    {
        static void Assert(bool value) { if (!value) throw new Exception("Theme option verification failed."); }
        static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid theme definition was accepted."); }
        static PatchBundle Bundle(string root)
        {
            Directory.CreateDirectory(root); string file = Path.Combine(root, "Fixture.dll"); if (!File.Exists(file)) File.Copy(Assembly.GetExecutingAssembly().Location, file); File.WriteAllText(Path.Combine(root, "app.version"), "1"); string method;
            using (var module = ModuleDefinition.ReadModule(file)) method = module.Types.Single(x => x.FullName == "Patchwork.ThemeFixture").Methods.Single(x => x.Name == "Merge").FullName;
            Func<string, string, string, object> patch = (id, key, value) => new Dictionary<string, object> {
                { "id", id }, { "name", id }, { "description", "Theme fixture" }, { "status", "ready" }, { "version", "1.0.0" },
                { "options", id == "accent" ? new object[] { new Dictionary<string, object> { { "id", "color" }, { "type", "color" }, { "label", "Accent color" }, { "default", "#8A66FF" } } } : new object[0] },
                { "operations", new object[] { new Dictionary<string, object> { { "kind", "managedThemeResources" }, { "method", method }, { "file", "Fixture.dll" }, { "sha256", PatchEngine.Hash(File.ReadAllBytes(file)) }, { "resources", new object[] { new Dictionary<string, object> { { "theme", "Dark" }, { "key", key }, { "type", "brush" }, { "value", value } } } } } } }
            };
            return PatchBundle.Parse(Json.Pretty(new Dictionary<string, object> { { "schemaVersion", 1 }, { "id", "theme-fixture" }, { "appId", "fixture" }, { "appName", "Theme fixture" }, { "appVersion", "1.0.0" }, { "packVersion", "1.0.0" }, { "versionFile", "app.version" }, { "versionSha256", PatchEngine.Hash(Encoding.UTF8.GetBytes("1")) }, { "patches", new object[] { patch("accent", "PrimaryColorBrush", "$color"), patch("black", "BackgroundNormColorBrush", "#000000") } } }));
        }
        public static void RuntimeAndUpdates(string root)
        {
            string target = Path.Combine(root, "theme-runtime-target"); var bundle = Bundle(target); var engine = new PatchEngine(Path.Combine(root, "theme-runtime-data")); byte[] original = File.ReadAllBytes(Path.Combine(target, "Fixture.dll"));
            var choices = new Dictionary<string, string> { { "accent.color", "#123abc" } }; var plan = engine.Preview(bundle, target, new[] { "accent", "black" }, choices);
            Assert(plan.Options["accent.color"] == "#123ABC" && bundle.Patches[0].Operations[0].Resources[0].Value == "$color"); engine.Apply(plan);
            var assembly = Assembly.Load(plan.Files.Single().AfterBytes); var host = Activator.CreateInstance(assembly.GetType("Patchwork.ThemeFixture")); host.GetType().GetMethod("Merge").Invoke(host, null);
            var app = assembly.GetType("Microsoft.UI.Xaml.Application").GetProperty("Current").GetValue(null, null); var resources = app.GetType().GetProperty("Resources").GetValue(app, null); var dictionaries = ((System.Collections.IEnumerable)resources.GetType().GetProperty("MergedDictionaries").GetValue(resources, null)).Cast<object>().ToList();
            Assert(dictionaries.Count == 3 && dictionaries[1].GetType().GetField("Xaml").GetValue(dictionaries[1]).ToString().Contains("#123ABC"));
            choices["accent.color"] = "#FEDCBA"; var update = engine.Preview(bundle, target, new[] { "accent", "black" }, choices); Assert(update.PreviousJournalId != null && update.Files.Single().AfterHash != plan.Files.Single().AfterHash); var journal = engine.Apply(update);
            Assert(engine.History().Single(x => x.State == "Applied").Options["accent.color"] == "#FEDCBA"); engine.Restore(journal); Assert(File.ReadAllBytes(Path.Combine(target, "Fixture.dll")).SequenceEqual(original));
        }
        public static void Validation(string root)
        {
            var bundle = Bundle(Path.Combine(root, "theme-validation")); Dictionary<string, string> choices;
            Reject(() => ThemeOptions.Resolve(bundle, new[] { "accent" }, new Dictionary<string, string> { { "accent.color", "#123456\"/><Button/>" } }, out choices));
            Reject(() => ThemeOptions.Resolve(bundle, new[] { "black" }, new Dictionary<string, string> { { "accent.color", "#123456" } }, out choices));
            Reject(() => PatchBundle.Parse(bundle.Content.Replace("\"$color\"", "\"$missing\"")));
            Reject(() => PatchBundle.Parse(bundle.Content.Replace("\"Dark\"", "\"HighContrast\"")));
            Reject(() => PatchBundle.Parse(bundle.Content.Replace("\"brush\"", "\"Button\"")));
            Reject(() => PatchBundle.Parse(bundle.Content.Replace("\"PrimaryColorBrush\"", "\"Bad Key\"")));
            Reject(() => PatchBundle.Parse(bundle.Content.Replace("\"#8A66FF\"", "\"red\"")));
            var repeated = PatchBundle.Parse(bundle.Content.Replace("BackgroundNormColorBrush", "PrimaryColorBrush")); var engine = new PatchEngine(Path.Combine(root, "theme-conflict-data"));
            try { engine.Preview(repeated, Path.Combine(root, "theme-validation"), new[] { "accent", "black" }); throw new Exception("Resource conflict accepted."); } catch (InvalidOperationException error) { Assert(error.Message.Contains("Conflicting theme resource")); }
        }
        public static void WorkerAndInterface(string root)
        {
            string target = Path.Combine(root, "theme-worker-target"), data = Path.Combine(root, "theme-worker-data"); var bundle = Bundle(target); var engine = new PatchEngine(data);
            var options = new Dictionary<string, string> { { "accent.color", "#ABCDEF" } }; var plan = engine.Preview(bundle, target, new[] { "accent" }, options); string job = Worker.CreateJob(engine, bundle, plan, null);
            Assert(Worker.Execute(job, data) == 0 && engine.ActiveSession(target).Options["accent.color"] == "#ABCDEF"); engine.Restore(engine.ActiveSession(target));
            job = Worker.CreateJob(engine, bundle, plan, null); var raw = Json.Parse(File.ReadAllText(job)); Json.Object(raw["options"])["accent.color"] = "#000000"; File.WriteAllText(job, Json.Pretty(raw)); Assert(Worker.Execute(job, data) == 1 && engine.ActiveSession(target) == null);
            string recipes = Path.Combine(data, "recipes"); Directory.CreateDirectory(recipes); File.WriteAllText(Path.Combine(recipes, "theme-fixture.json"), bundle.Content);
            File.WriteAllText(Path.Combine(data, "patch-options.json"), Json.Pretty(new Dictionary<string, string> { { "theme-fixture|accent.color", "#ABCDEF" } }));
            Window window; using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Patchwork.MainWindow.xaml")) window = (Window)System.Windows.Markup.XamlReader.Load(resource);
            var controller = new MainController(window, data); var flags = BindingFlags.Instance | BindingFlags.NonPublic; var selection = (HashSet<string>)typeof(MainController).GetField("selected", flags).GetValue(controller); selection.Add("accent"); typeof(MainController).GetMethod("RenderPatches", flags).Invoke(controller, null);
            var panel = (StackPanel)window.FindName("PatchList"); var boxes = Descendants(panel).OfType<TextBox>().ToList(); Assert(boxes.Count == 1 && boxes[0].Text == "#ABCDEF"); boxes[0].Text = "#123456";
            var saved = Json.Parse(File.ReadAllText(Path.Combine(data, "patch-options.json"))); Assert((string)saved["theme-fixture|accent.color"] == "#123456"); boxes[0].Text = "#1"; typeof(MainController).GetMethod("RenderPatches", flags).Invoke(controller, null); window.Close();
        }
        static IEnumerable<DependencyObject> Descendants(DependencyObject root) { foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) { yield return child; foreach (var item in Descendants(child)) yield return item; } }
    }
}
