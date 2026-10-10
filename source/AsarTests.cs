#if TEST_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;

namespace Patchwork
{
    public static class AsarTests
    {
        static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        static void Reject(Action action, string expected)
        {
            try { action(); } catch (Exception error) { if (error.Message.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0) return; throw; }
            throw new Exception("Expected rejection: " + expected);
        }
        public static byte[] Archive(string text, int padding)
        {
            byte[] member = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();
            byte[] binary = Enumerable.Repeat((byte)0xa5, padding).ToArray();
            var files = new Dictionary<string, object> {
                { "client.js", new Dictionary<string, object> { { "size", member.Length }, { "offset", "0" }, { "executable", true } } },
                { "binary.dat", new Dictionary<string, object> { { "size", binary.Length }, { "offset", member.Length.ToString() } } },
                { "native.node", new Dictionary<string, object> { { "size", 100 }, { "unpacked", true } } },
                { "alias.js", new Dictionary<string, object> { { "link", "client.js" } } }
            };
            return Header(new Dictionary<string, object> { { "files", files } }, member.Concat(binary).ToArray());
        }
        static byte[] Header(Dictionary<string, object> header, byte[] payload)
        {
            byte[] json = Encoding.UTF8.GetBytes(Json.Serializer.Serialize(header)); int size = (8 + json.Length + 3) & ~3;
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) {
                writer.Write(4); writer.Write(size); writer.Write(size - 4); writer.Write(json.Length); writer.Write(json); writer.Write(new byte[size - 8 - json.Length]); writer.Write(payload); return stream.ToArray();
            }
        }
        static PatchBundle Bundle(byte[] bytes)
        {
            byte[] member = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("alpha beta\n")).ToArray();
            Func<string, string, object> op = (find, replacement) => new Dictionary<string, object> { { "kind", "asarTextReplace" }, { "file", "app.asar" }, { "sha256", PatchEngine.Hash(bytes) }, { "entry", "client.js" }, { "entrySha256", PatchEngine.Hash(member) }, { "find", find }, { "replacement", replacement }, { "count", 1 } };
            var raw = new Dictionary<string, object> {
                { "schemaVersion", 1 }, { "id", "asar-test" }, { "appId", "asar-test" }, { "appName", "ASAR test" }, { "appVersion", "1" }, { "minimumPatcherVersion", "0.8.0" }, { "versionFile", "app.asar" }, { "versionSha256", PatchEngine.Hash(bytes) },
                { "patches", new object[] {
                    new Dictionary<string, object> { { "id", "first" }, { "name", "First" }, { "description", "first" }, { "operations", new object[] { op("alpha", "alphabetical-longer") } } },
                    new Dictionary<string, object> { { "id", "second" }, { "name", "Second" }, { "description", "second" }, { "operations", new object[] { op("beta", "B") } } }
                } }
            };
            return PatchBundle.Parse(Json.Pretty(raw));
        }
        public static void Transactions(string root)
        {
            string target = Path.Combine(root, "asar-target"); Directory.CreateDirectory(target);
            byte[] original = Archive("alpha beta\n", 9 * 1024 * 1024); File.WriteAllBytes(Path.Combine(target, "app.asar"), original);
            var bundle = Bundle(original); var engine = new PatchEngine(Path.Combine(root, "asar-data"));
            var plan = engine.Preview(bundle, target, new[] { "first", "second" });
            Assert(File.ReadAllBytes(Path.Combine(target, "app.asar")).SequenceEqual(original), "Preview mutated archive.");
            Assert(plan.Files[0].AfterText.Contains("alphabetical-longer B") && plan.Files[0].AfterText.Contains("Executable client code"), "Member preview or composition missing.");
            string beforeText, afterText;
            Assert(AsarPatches.Transform(original, bundle.Patches.SelectMany(x => x.Operations).Reverse().ToList(), out beforeText, out afterText).SequenceEqual(plan.Files[0].AfterBytes), "Selection order changed output.");
            var first = engine.Apply(plan); byte[] applied = File.ReadAllBytes(Path.Combine(target, "app.asar"));
            var update = engine.Preview(bundle, target, new[] { "second" });
            Assert(update.Files[0].BeforeText.Contains("alphabetical-longer B") && update.Files[0].AfterText.Contains("alpha B"), "Update member preview wrong.");
            engine.BeforeWriteForTest = index => { throw new IOException("Simulated ASAR update interruption"); };
            Reject(() => engine.Apply(update), "previous patch version was restored");
            Assert(File.ReadAllBytes(Path.Combine(target, "app.asar")).SequenceEqual(applied), "Failed update lost previous archive.");
            engine.BeforeWriteForTest = null;
            var latest = engine.Apply(engine.Preview(bundle, target, new[] { "second" }));
            latest.State = "Prepared"; File.WriteAllText(Path.Combine(latest.DirectoryPath, "journal.json"), Json.Pretty(latest));
            engine.Restore(latest);
            Assert(File.ReadAllBytes(Path.Combine(target, "app.asar")).SequenceEqual(original), "Interrupted archive did not restore exactly.");
            File.WriteAllBytes(Path.Combine(target, "large.txt"), original);
            Reject(() => PatchEngine.Read(Path.Combine(target, "large.txt")), "8 MB");
            var journal = engine.Apply(engine.Preview(bundle, target, new[] { "first" }));
            File.AppendAllText(Path.Combine(journal.DirectoryPath, journal.Files[0].BackupFile), "tamper");
            Reject(() => engine.Restore(journal), "Backup fingerprint mismatch");
        }
        public static void Boundaries(string root)
        {
            byte[] original = Archive("alpha beta\n", 8); var bundle = Bundle(original); var op = bundle.Patches[0].Operations[0];
            string before, after;
            Action transform = () => AsarPatches.Transform(original, new List<PatchOperation> { op }, out before, out after);
            op.Count = 2; Reject(transform, "text matches"); op.Count = 1;
            op.EntrySha256 = new string('0', 64); Reject(transform, "entry fingerprint"); op.EntrySha256 = PatchEngine.Hash(new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("alpha beta\n")).ToArray());
            op.Entry = "native.node"; Reject(transform, "unpacked"); op.Entry = "alias.js"; Reject(transform, "linked"); op.Entry = "client.js";
            var duplicate = Bundle(original).Patches[0].Operations[0]; duplicate.Replacement = "conflict";
            Reject(() => AsarPatches.Transform(original, new List<PatchOperation> { op, duplicate }, out before, out after), "Conflicting");
            byte[] changed = AsarPatches.Transform(original, new List<PatchOperation> { op }, out before, out after);
            byte[] corrupt = (byte[])changed.Clone(); corrupt[8 + BitConverter.ToInt32(corrupt, 4)] ^= 1;
            Reject(() => AsarPatches.Compare(corrupt, changed, out before, out after), "integrity mismatch");
            byte[] truncated = original.Take(15).ToArray(); Reject(() => AsarPatches.Compare(truncated, original, out before, out after), "size");
            byte[] invalidHeader = (byte[])original.Clone(); invalidHeader[4] = 0xff;
            Reject(() => AsarPatches.Compare(invalidHeader, original, out before, out after), "header");
            byte[] traversal = Header(new Dictionary<string, object> { { "files", new Dictionary<string, object> { { "..", new Dictionary<string, object> { { "size", 0 }, { "offset", "0" } } } } } }, new byte[0]);
            Reject(() => AsarPatches.Compare(traversal, original, out before, out after), "Unsafe");
            Reject(() => PatchBundle.Parse(bundle.Content.Replace("client.js", "../client.js")), "Unsafe");
            Reject(() => PatchBundle.Parse(bundle.Content.Replace("client.js", "client.exe")), "extension");
            byte[] tooLarge = new byte[AsarPatches.MaximumSize + 1];
            Reject(() => AsarPatches.Compare(tooLarge, original, out before, out after), "size");
            string target = Path.Combine(root, "asar-fingerprints"); Directory.CreateDirectory(target); File.WriteAllBytes(Path.Combine(target, "app.asar"), original.Concat(new byte[] { 0 }).ToArray());
            Reject(() => new PatchEngine(Path.Combine(root, "asar-fingerprint-data")).Preview(bundle, target, new[] { "first" }), "Target fingerprint");
        }
        public static void RunningClient(string root)
        {
            string target = Path.Combine(root, "asar-running"); Directory.CreateDirectory(target);
            string helper = Path.Combine(target, "Blitz.exe");
            File.Copy(System.Reflection.Assembly.GetExecutingAssembly().Location, helper);
            string framework = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe";
            string source = Path.Combine(target, "holder.cs"); File.WriteAllText(source, "class Holder { static void Main() { System.Threading.Thread.Sleep(15000); } }");
            using (var compiler = Process.Start(new ProcessStartInfo(framework, "/nologo /out:\"" + helper + "\" \"" + source + "\"") { UseShellExecute = false, CreateNoWindow = true })) { compiler.WaitForExit(); Assert(compiler.ExitCode == 0, "Holder compilation failed."); }
            using (var child = Process.Start(new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true }))
            {
                try { Reject(() => Worker.CheckClientClosed(target), "Close Blitz"); } finally { if (!child.HasExited) child.Kill(); child.WaitForExit(); }
            }
        }
    }
}
#endif
