#if TEST_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Patchwork
{
    public static class CommunityFileTests
    {
        static void Assert(bool ok, string message) { if (!ok) throw new Exception("Community file assertion failed: " + message); }
        static void Reject(Action action, string fragment)
        {
            try { action(); } catch (Exception error) { Assert(error.Message.Contains(fragment), error.Message); return; }
            throw new Exception("Expected rejection: " + fragment);
        }
        static byte[] Bytes(string value) { return Encoding.UTF8.GetBytes(value); }
        static Dictionary<string, object> Payload(string kind, string file, byte[] original, byte[] result)
        {
            var op = new Dictionary<string, object> { { "kind", kind }, { "file", file }, { "dataBase64", Convert.ToBase64String(result) }, { "resultSha256", PatchEngine.Hash(result) } };
            if (original != null) op["sha256"] = PatchEngine.Hash(original);
            return op;
        }
        static Dictionary<string, object> Text(string file, byte[] bytes)
        {
            return new Dictionary<string, object> { { "kind", "textReplace" }, { "file", file }, { "sha256", PatchEngine.Hash(bytes) }, { "find", "before" }, { "replacement", "after" }, { "count", 1 } };
        }
        static Dictionary<string, object> Document(Dictionary<string, object> operations)
        {
            var patches = operations.Select(x => new Dictionary<string, object> { { "id", x.Key }, { "name", x.Key }, { "description", "Synthetic community fixture" }, { "operations", new[] { x.Value } } }).ToList();
            return new Dictionary<string, object> { { "schemaVersion", 1 }, { "id", "unknown-community-pack" }, { "appId", "new-unregistered-app" }, { "appName", "Community fixture" }, { "appVersion", "1" }, { "versionFile", "app.version" }, { "versionSha256", PatchEngine.Hash(Bytes("version")) }, { "packVersion", "1.0.0" }, { "minimumPatcherVersion", "1.0.0" }, { "processes", new object[0] }, { "patches", patches } };
        }
        static string Target(string root)
        {
            string target = Path.Combine(root, "target"); Directory.CreateDirectory(target); File.WriteAllBytes(Path.Combine(target, "app.version"), Bytes("version")); return target;
        }
        public static void Run(string root)
        {
            string area = Path.Combine(root, "community-files"); string target = Target(area);
            var engine = new PatchEngine(Path.Combine(area, "data"));
            byte[] native = new byte[9 * 1024 * 1024]; native[0] = 0x4d; native[1] = 0x5a;
            byte[] changedNative = (byte[])native.Clone(); changedNative[changedNative.Length - 1] = 3;
            byte[] text = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Bytes("before\n")).ToArray(), picture = new byte[] { 255, 0, 9 };
            File.WriteAllBytes(Path.Combine(target, "Native.dll"), native); File.WriteAllBytes(Path.Combine(target, "resource.custom"), text); File.WriteAllBytes(Path.Combine(target, "image.png"), picture);
            var nativeOp = new Dictionary<string, object> { { "kind", "binarySplice" }, { "file", "Native.dll" }, { "sha256", PatchEngine.Hash(native) }, { "resultSha256", PatchEngine.Hash(changedNative) }, { "edits", new[] { new Dictionary<string, object> { { "offset", native.Length - 1 }, { "length", 1 }, { "dataBase64", "Aw==" } } } } };
            var add = Payload("fileAdd", "helpers/local.bin", null, new byte[] { 0, 255, 1 });
            var document = Document(new Dictionary<string, object> { { "native", nativeOp }, { "text", Text("resource.custom", text) }, { "helper", add }, { "image", Payload("fileReplace", "image.png", picture, new byte[] { 255, 7 }) } });
            var bundle = PatchBundle.Parse(Json.Pretty(document)); string[] all = bundle.Patches.Select(x => x.Id).ToArray();
            var plan = engine.Preview(bundle, target, all);
            Assert(plan.Files.Count == 4 && plan.Files.Single(x => x.RelativePath == "Native.dll").TransformKind == "binary", "native DLL routing");
            Assert(!File.Exists(Path.Combine(target, "helpers/local.bin")) && File.ReadAllBytes(Path.Combine(target, "Native.dll")).SequenceEqual(native), "preview is read only");
            var first = engine.Apply(plan);
            Assert(first.Files.Single(x => x.RelativePath == "helpers/local.bin").BeforeExists == false, "original absence persisted");
            Assert(first.Processes != null && first.Processes.Count == 0, "explicit process rules persisted");
            Assert(File.ReadAllBytes(Path.Combine(target, "resource.custom")).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "BOM preserved for arbitrary extension");
            add["dataBase64"] = "AP8C"; add["resultSha256"] = PatchEngine.Hash(new byte[] { 0, 255, 2 }); document["packVersion"] = "1.1.0";
            var updated = PatchBundle.Parse(Json.Pretty(document));
            engine.BeforeWriteForTest = index => { if (index == 2) throw new IOException("simulated update interruption"); };
            Reject(() => engine.Apply(engine.Preview(updated, target, all)), "previous patch version was restored"); engine.BeforeWriteForTest = null;
            Assert(File.ReadAllBytes(Path.Combine(target, "helpers/local.bin")).SequenceEqual(new byte[] { 0, 255, 1 }), "failed update restores prior helper");
            var next = engine.Apply(engine.Preview(updated, target, all));
            Assert(File.ReadAllBytes(Path.Combine(target, "helpers/local.bin")).SequenceEqual(new byte[] { 0, 255, 2 }), "helper directly upgraded");
            var removed = engine.Apply(engine.Preview(updated, target, all.Where(x => x != "helper")));
            Assert(!File.Exists(Path.Combine(target, "helpers/local.bin")), "deselection removes tracked addition");
            var readded = engine.Apply(engine.Preview(updated, target, all));
            File.WriteAllBytes(Path.Combine(target, "helpers/local.bin"), new byte[] { 9 });
            Reject(() => engine.Restore(readded), "changed outside Patchwork");
            Assert(File.ReadAllBytes(Path.Combine(target, "Native.dll")).SequenceEqual(changedNative), "outside helper blocks all restore writes");
            File.WriteAllBytes(Path.Combine(target, "helpers/local.bin"), new byte[] { 0, 255, 2 });
            engine.Restore(readded);
            Assert(File.ReadAllBytes(Path.Combine(target, "Native.dll")).SequenceEqual(native) && File.ReadAllBytes(Path.Combine(target, "image.png")).SequenceEqual(picture) && File.ReadAllBytes(Path.Combine(target, "resource.custom")).SequenceEqual(text), "byte-exact original restore");
            Assert(!Directory.Exists(Path.Combine(target, "helpers")), "created empty folder removed");
            // Crash recovery must distinguish an absent helper from an empty original file.
            var crashed = engine.Apply(engine.Preview(bundle, target, all)); crashed.State = "Prepared";
            File.WriteAllText(Path.Combine(crashed.DirectoryPath, "journal.json"), Json.Pretty(crashed));
            engine.Restore(engine.History().Single(x => x.Id == crashed.Id));
            Assert(!File.Exists(Path.Combine(target, "helpers/local.bin")), "prepared journal recovers absence");
            File.WriteAllBytes(Path.Combine(target, "helpers-placeholder"), Bytes("outside"));
            Directory.CreateDirectory(Path.Combine(target, "helpers")); File.WriteAllBytes(Path.Combine(target, "helpers/local.bin"), Bytes("outside"));
            Reject(() => engine.Preview(bundle, target, all), "already exists"); File.Delete(Path.Combine(target, "helpers/local.bin")); Directory.Delete(Path.Combine(target, "helpers"));
            Directory.CreateDirectory(Path.Combine(target, "helpers/local.bin")); Reject(() => engine.Preview(bundle, target, all), "regular file"); Directory.Delete(Path.Combine(target, "helpers/local.bin")); Directory.Delete(Path.Combine(target, "helpers"));
            string source = Json.Pretty(document);
            Reject(() => PatchBundle.Parse(source.Replace("1.0.0", "0.9.3")), "minimumPatcherVersion");
            var bad = Payload("fileAdd", "new.bin", null, new byte[] { 1 }); bad["resultSha256"] = new string('0', 64);
            Reject(() => PatchBundle.Parse(Json.Pretty(Document(new Dictionary<string, object> { { "bad", bad } }))), "payload fingerprint");
            bad = Payload("fileAdd", "../new.bin", null, new byte[] { 1 }); Reject(() => PatchBundle.Parse(Json.Pretty(Document(new Dictionary<string, object> { { "bad", bad } }))), "Unsafe");
            foreach (string path in new[] { "NUL.txt", "helpers/CON", "COM1.bin", "bad?.bin", "bad|path.bin" }) { bad = Payload("fileAdd", path, null, new byte[] { 1 }); Reject(() => PatchBundle.Parse(Json.Pretty(Document(new Dictionary<string, object> { { "bad", bad } }))), "Unsafe"); }
            bad = Payload("fileAdd", "new.bin", null, new byte[262145]); Reject(() => PatchBundle.Parse(Json.Pretty(Document(new Dictionary<string, object> { { "bad", bad } }))), "256 KiB");
            var incompatible = Document(new Dictionary<string, object> { { "text", Text("resource.custom", text) }, { "replace", Payload("fileReplace", "resource.custom", text, Bytes("replacement")) } });
            Reject(() => engine.Preview(PatchBundle.Parse(Json.Pretty(incompatible)), target, new[] { "text", "replace" }), "Incompatible");
            // Worker requests re-preview null fingerprints and refuse forged state.
            plan = engine.Preview(bundle, target, all); string request = Worker.CreateJob(engine, bundle, plan, null);
            var job = Json.Parse(File.ReadAllText(request)); Json.Object(job["before"])["helpers/local.bin"] = new string('0', 64); File.WriteAllText(request, Json.Pretty(job));
            Assert(Worker.Execute(request, engine.DataRoot) == 1 && !File.Exists(Path.Combine(target, "helpers/local.bin")), "forged worker addition rejected");
            request = Worker.CreateJob(engine, bundle, plan, null); Assert(Worker.Execute(request, engine.DataRoot) == 0, "worker applies absent addition");
            var workerJournal = engine.ActiveSession(target); request = Worker.CreateJob(engine, null, null, workerJournal); Assert(Worker.Execute(request, engine.DataRoot) == 0, "worker restores absent original");
            Assert(File.ReadAllBytes(Path.Combine(target, "Native.dll")).SequenceEqual(native) && !File.Exists(Path.Combine(target, "helpers/local.bin")), "worker exact restoration");
            OldJournal(root); ManagedExe(root); EmptyFiles(root);
        }
        static void EmptyFiles(string root)
        {
            string area = Path.Combine(root, "community-empty"); string target = Target(area);
            File.WriteAllBytes(Path.Combine(target, "empty.bin"), new byte[0]); File.WriteAllBytes(Path.Combine(target, "note.txt"), Bytes("before"));
            var document = Document(new Dictionary<string, object> { { "addition", Payload("fileAdd", "fresh/zero.bin", null, new byte[0]) }, { "replace", Payload("fileReplace", "empty.bin", new byte[0], new byte[] { 1 }) }, { "text", Text("note.txt", Bytes("before")) } });
            var bundle = PatchBundle.Parse(Json.Pretty(document)); var engine = new PatchEngine(Path.Combine(area, "data")); var ids = new[] { "addition", "replace", "text" };
            engine.BeforeWriteForTest = index => { if (index == 1) throw new IOException("initial addition interrupted"); };
            Reject(() => engine.Apply(engine.Preview(bundle, target, ids)), "Original files were restored"); engine.BeforeWriteForTest = null;
            Assert(!File.Exists(Path.Combine(target, "fresh/zero.bin")) && !Directory.Exists(Path.Combine(target, "fresh")), "initial rollback removes empty addition and directory");
            var first = engine.Apply(engine.Preview(bundle, target, ids));
            Assert(File.Exists(Path.Combine(target, "fresh/zero.bin")) && new FileInfo(Path.Combine(target, "fresh/zero.bin")).Length == 0, "empty addition exists");
            var next = engine.Apply(engine.Preview(bundle, target, new[] { "text" }));
            Assert(!File.Exists(Path.Combine(target, "fresh/zero.bin")) && File.Exists(Path.Combine(target, "empty.bin")) && new FileInfo(Path.Combine(target, "empty.bin")).Length == 0, "absence and zero-byte original differ on deselection");
            engine.Restore(next); Assert(File.ReadAllText(Path.Combine(target, "note.txt")) == "before", "empty file session restores all originals");
        }
        static void OldJournal(string root)
        {
            string area = Path.Combine(root, "community-legacy"); string target = Target(area); byte[] original = Bytes("before"); File.WriteAllBytes(Path.Combine(target, "old.txt"), original);
            var bundle = PatchBundle.Parse(Json.Pretty(Document(new Dictionary<string, object> { { "text", Text("old.txt", original) } }))); var engine = new PatchEngine(Path.Combine(area, "data"));
            var journal = engine.Apply(engine.Preview(bundle, target, new[] { "text" })); var raw = Json.Parse(Json.Pretty(journal)); raw.Remove("Processes"); raw.Remove("CreatedDirectories");
            foreach (object item in Json.Array(raw, "Files")) { var file = Json.Object(item); foreach (string key in new[] { "BeforeExists", "AfterExists", "PreviousExists", "TransformKind", "SizeLimit" }) file.Remove(key); }
            File.WriteAllText(Path.Combine(journal.DirectoryPath, "journal.json"), Json.Pretty(raw)); engine.Restore(engine.History()[0]);
            Assert(File.ReadAllBytes(Path.Combine(target, "old.txt")).SequenceEqual(original), "old journal default existence remains true");
        }
        static void ManagedExe(string root)
        {
            string area = Path.Combine(root, "community-managed-exe"); string target = Target(area); string file = Path.Combine(target, "Toy.exe");
            using (var module = ModuleDefinition.CreateModule("CommunityExe" + Guid.NewGuid().ToString("N"), ModuleKind.Console))
            {
                var type = new TypeDefinition("CommunityFixture", "Program", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class, module.TypeSystem.Object); module.Types.Add(type);
                var flag = new MethodDefinition("Flag", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Boolean); flag.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1)); flag.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); type.Methods.Add(flag);
                var main = new MethodDefinition("Main", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void); main.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); type.Methods.Add(main); module.EntryPoint = main; module.Write(file);
            }
            byte[] original = File.ReadAllBytes(file);
            var op = new Dictionary<string, object> { { "kind", "managedReturn" }, { "file", "Toy.exe" }, { "sha256", PatchEngine.Hash(original) }, { "method", "System.Boolean CommunityFixture.Program::Flag()" }, { "returnType", "boolean" }, { "value", false } };
            var bundle = PatchBundle.Parse(Json.Pretty(Document(new Dictionary<string, object> { { "flag", op } }))); var engine = new PatchEngine(Path.Combine(area, "data")); var plan = engine.Preview(bundle, target, new[] { "flag" });
            Assert(!(bool)Assembly.Load(plan.Files[0].AfterBytes).GetType("CommunityFixture.Program").GetMethod("Flag").Invoke(null, null), "patched IL-only EXE method runtime");
            var journal = engine.Apply(plan); var change = Json.Parse(bundle.Content); change["packVersion"] = "1.1.0"; var next = engine.Apply(engine.Preview(PatchBundle.Parse(Json.Pretty(change)), target, new[] { "flag" })); engine.Restore(next);
            Assert(File.ReadAllBytes(file).SequenceEqual(original), "managed EXE update preview and exact restore");
        }
    }
}
#endif
