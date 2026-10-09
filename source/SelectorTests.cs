using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

namespace Patchwork
{
    public class SelectorInput { public string Code { get; set; } public bool Offline { get; set; } }
    public class SelectorOutput { public string Code { get; set; } public bool Offline { get; set; } }
    public class SelectorSource
    {
        public bool Enabled { get; set; }
        public IReadOnlyList<SelectorOutput> Full() { return new[] { new SelectorOutput { Code = "PAID" } }; }
        public IReadOnlyList<SelectorOutput> Free() { return new[] { new SelectorOutput { Code = "FREE" } }; }
    }
    public class SelectorHost
    {
        public bool Paid;
        public SelectorSource Source = new SelectorSource();
        public IReadOnlyList<SelectorInput> Input() { return new[] { new SelectorInput { Code = "NL", Offline = true } }; }
        public IReadOnlyList<SelectorOutput> List() { return Source.Full(); }
        public IReadOnlyList<SelectorOutput> Choose() { return Source.Full(); }
        public bool Flag() { return Source.Enabled; }
        public List<SelectorOutput> KeepLinqReferences() { return Input().Select(x => new SelectorOutput { Code = x.Code }).ToList(); }
    }
    public class SelectorParent
    {
        public bool Restricted { get; set; }
        public bool Captured;
        public virtual void Accept(bool value) { Captured = value; Restricted = !value; }
    }
    public class SelectorGeneric<T> : SelectorParent { public bool Propagated; public override void Accept(bool value) { Propagated = value; base.Accept(value); } }
    public class SelectorChild : SelectorGeneric<int> { }
    public static class SelectorTests
    {
        static void Assert(bool value) { if (!value) throw new Exception("Selector transformation failed."); }
        static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid selector definition was accepted."); }
        static PatchBundle Fixture(string root, out List<Dictionary<string, object>> ops)
        {
            Directory.CreateDirectory(root); File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(root, "Fixture.dll")); File.WriteAllText(Path.Combine(root, "app.version"), "1");
            var definitions = new List<Dictionary<string, object>>(); ops = definitions;
            using (var module = ModuleDefinition.ReadModule(Path.Combine(root, "Fixture.dll")))
            {
                var types = ManagedPatches.Types(module.Types).ToDictionary(x => x.FullName);
                Func<string, string, string> method = (type, name) => types["Patchwork." + type].Methods.Single(x => x.Name == name).FullName;
                Action<string, string, string, object[]> add = (kind, type, name, fields) => {
                    var op = new Dictionary<string, object> { { "kind", kind }, { "file", "Fixture.dll" }, { "sha256", PatchEngine.Hash(File.ReadAllBytes(Path.Combine(root, "Fixture.dll"))) }, { "method", method(type, name) } };
                    for (int i = 0; i < fields.Length; i += 2) op[(string)fields[i]] = fields[i + 1]; definitions.Add(op);
                };
                string[] condition = { types["Patchwork.SelectorHost"].Fields.Single(x => x.Name == "Paid").FullName };
                add("managedConditionalCall", "SelectorHost", "Choose", new object[] { "condition", condition, "calledMethod", method("SelectorSource", "Full"), "replacementMethod", method("SelectorSource", "Free"), "count", 1 });
                add("managedConditionalBooleanCall", "SelectorHost", "Flag", new object[] { "condition", condition, "calledMethod", method("SelectorSource", "get_Enabled"), "count", 1, "value", false });
                add("managedConditionalProjection", "SelectorHost", "List", new object[] { "condition", condition, "sourceMethod", method("SelectorHost", "Input"), "mappings", new object[] {
                    new Dictionary<string, object> { { "getter", method("SelectorInput", "get_Code") }, { "setter", method("SelectorOutput", "set_Code") } },
                    new Dictionary<string, object> { { "getter", method("SelectorInput", "get_Offline") }, { "setter", method("SelectorOutput", "set_Offline") } }
                } });
                add("managedOverrideBooleanSetter", "SelectorParent", "Accept", new object[] { "type", "Patchwork.SelectorChild", "setterMethod", method("SelectorParent", "set_Restricted"), "value", false });
            }
            return Parse(root, ops);
        }
        static PatchBundle Parse(string root, List<Dictionary<string, object>> ops)
        {
            return PatchBundle.Parse(Json.Pretty(new Dictionary<string, object> {
                { "schemaVersion", 1 }, { "id", "selector-fixture" }, { "packVersion", "1.0.0" }, { "appId", "fixture" }, { "appName", "Fixture" }, { "appVersion", "1" }, { "versionFile", "app.version" }, { "versionSha256", PatchEngine.Hash(File.ReadAllBytes(Path.Combine(root, "app.version"))) },
                { "patches", new object[] { new Dictionary<string, object> { { "id", "selector" }, { "name", "Selector" }, { "description", "Fixture" }, { "operations", ops } } } }
            }));
        }
        public static void Runtime(string root)
        {
            root = Path.Combine(root, "selectors"); List<Dictionary<string, object>> ops; var bundle = Fixture(root, out ops); var engine = new PatchEngine(Path.Combine(root, "data"));
            var plan = engine.Preview(bundle, root, new[] { "selector" }); var assembly = Assembly.Load(plan.Files.Single().AfterBytes);
            var type = assembly.GetType("Patchwork.SelectorHost"); var host = Activator.CreateInstance(type); var source = type.GetField("Source").GetValue(host);
            Func<string, object> invoke = name => type.GetMethod(name).Invoke(host, null);
            Func<object, object> first = values => ((System.Collections.IEnumerable)values).Cast<object>().First();
            Assert((string)first(invoke("Choose")).GetType().GetProperty("Code").GetValue(first(invoke("Choose"))) == "FREE");
            source.GetType().GetProperty("Enabled").SetValue(source, true); Assert(!(bool)invoke("Flag"));
            var projected = first(invoke("List")); Assert((string)projected.GetType().GetProperty("Code").GetValue(projected) == "NL" && (bool)projected.GetType().GetProperty("Offline").GetValue(projected));
            type.GetField("Paid").SetValue(host, true); var full = first(invoke("Choose")); Assert((string)full.GetType().GetProperty("Code").GetValue(full) == "PAID" && (bool)invoke("Flag"));
            source.GetType().GetProperty("Enabled").SetValue(source, false); Assert(!(bool)invoke("Flag"));
            full = first(invoke("List")); Assert((string)full.GetType().GetProperty("Code").GetValue(full) == "PAID");
            var childType = assembly.GetType("Patchwork.SelectorChild"); var child = Activator.CreateInstance(childType); childType.GetMethod("Accept").Invoke(child, new object[] { false });
            Assert(!(bool)childType.GetProperty("Restricted").GetValue(child) && !(bool)childType.GetField("Captured").GetValue(child) && !(bool)childType.GetField("Propagated").GetValue(child));
            childType.GetMethod("Accept").Invoke(child, new object[] { true }); Assert((bool)childType.GetField("Captured").GetValue(child) && (bool)childType.GetField("Propagated").GetValue(child));
            var journal = engine.Apply(plan); engine.Restore(journal); Assert(PatchEngine.Hash(File.ReadAllBytes(Path.Combine(root, "Fixture.dll"))) == plan.Files.Single().BeforeHash);
        }
        public static void Rejections(string root)
        {
            root = Path.Combine(root, "selector-rejections"); List<Dictionary<string, object>> ops; var bundle = Fixture(root, out ops); var engine = new PatchEngine(Path.Combine(root, "data"));
            ops[0]["count"] = 2; Reject(() => engine.Preview(Parse(root, ops), root, new[] { "selector" })); ops[0]["count"] = 1;
            ops[0]["replacementMethod"] = "System.Boolean Patchwork.SelectorSource::get_Enabled()"; Reject(() => engine.Preview(Parse(root, ops), root, new[] { "selector" }));
            ops[0]["condition"] = new[] { "Patchwork.SelectorSource Patchwork.SelectorHost::Source" }; Reject(() => engine.Preview(Parse(root, ops), root, new[] { "selector" }));
            ops[0]["condition"] = new string[9]; Reject(() => Parse(root, ops));
            ops.RemoveAt(0); var mappings = (object[])ops[1]["mappings"]; ((Dictionary<string, object>)mappings[0])["setter"] = "System.Void Patchwork.SelectorOutput::set_Offline(System.Boolean)";
            Reject(() => engine.Preview(Parse(root, ops), root, new[] { "selector" }));
        }
    }
}
