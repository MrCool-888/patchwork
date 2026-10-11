using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Patchwork
{
    public static class BinaryTests
    {
        static void Assert(bool ok) { if (!ok) throw new Exception("Binary/JAR assertion failed."); }
        static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid binary/JAR edit accepted."); }
        static byte[] Class(byte value) { return new byte[] { 0xca, 0xfe, 0xba, 0xbe, 0, 0, 0, 61, 0, 1, value }; }
        static byte[] Archive(bool signed = false)
        {
            using (var memory = new MemoryStream())
            {
                using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
                {
                    foreach (string name in new[] { "A.class", "keep.txt" }.Concat(signed ? new[] { "META-INF/X.SF" } : new string[0]))
                    {
                        var entry = archive.CreateEntry(name); entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        using (var stream = entry.Open()) { byte[] data = name == "A.class" ? Class(1) : new byte[] { 1, 2, 3 }; stream.Write(data, 0, data.Length); }
                    }
                }
                return memory.ToArray();
            }
        }
        static Dictionary<string, object> Operation(string kind, string file, byte[] original, byte[] changed)
        {
            return new Dictionary<string, object> { { "kind", kind }, { "file", file }, { "sha256", PatchEngine.Hash(original) }, { "resultSha256", PatchEngine.Hash(changed) },
                { "edits", new[] { new Dictionary<string, object> { { "offset", original.Length - 1 }, { "length", 1 }, { "dataBase64", Convert.ToBase64String(new[] { changed.Last() }) } } } } };
        }
        public static void Run(string root)
        {
            byte[] exe = new byte[9 * 1024 * 1024], editedExe = (byte[])exe.Clone(); exe[0] = editedExe[0] = (byte)'M'; exe[1] = editedExe[1] = (byte)'Z'; editedExe[editedExe.Length - 1] = 7;
            byte[] jar = Archive();
            var binary = Operation("binarySplice", "Client.exe", exe, editedExe);
            var member = Operation("jarEntrySplice", "game.jar", jar, Class(2));
            member["entry"] = "A.class"; member["entrySha256"] = PatchEngine.Hash(Class(1));
            member["edits"] = new[] { new Dictionary<string, object> { { "offset", 10 }, { "length", 1 }, { "dataBase64", "Ag==" } } };
            var addition = new Dictionary<string, object> { { "kind", "jarEntryAdd" }, { "file", "game.jar" }, { "sha256", PatchEngine.Hash(jar) }, { "entry", "local/Helper.class" }, { "entrySha256", PatchEngine.Hash(Class(3)) }, { "dataBase64", Convert.ToBase64String(Class(3)) } };
            var document = new Dictionary<string, object> { { "schemaVersion", 1 }, { "id", "binary-fixture" }, { "appId", "fixture" }, { "appName", "Fixture" }, { "appVersion", "1" }, { "packVersion", "1.0.0" }, { "minimumPatcherVersion", "0.9.0" }, { "versionFile", "Client.exe" }, { "versionSha256", PatchEngine.Hash(exe) },
                { "patches", new[] {
                    new Dictionary<string, object> { { "id", "exe" }, { "name", "Executable" }, { "description", "Fixture" }, { "operations", new[] { binary } } },
                    new Dictionary<string, object> { { "id", "jar" }, { "name", "Game" }, { "description", "Fixture" }, { "operations", new[] { member, addition } } }
                } } };
            var bundle = PatchBundle.Parse(Json.Pretty(document)); string target = Path.Combine(root, "binary-target"); Directory.CreateDirectory(target);
            File.WriteAllBytes(Path.Combine(target, "Client.exe"), exe); File.WriteAllBytes(Path.Combine(target, "game.jar"), jar);
            var engine = new PatchEngine(Path.Combine(root, "binary-data")); var plan = engine.Preview(bundle, target, new[] { "exe", "jar" });
            Assert(plan.Files.Count == 2 && File.ReadAllBytes(Path.Combine(target, "game.jar")).SequenceEqual(jar));
            byte[] changedJar = plan.Files.Single(x => x.RelativePath == "game.jar").AfterBytes;
            Assert(engine.Preview(bundle, target, new[] { "jar", "exe" }).Files.Single(x => x.RelativePath == "game.jar").AfterBytes.SequenceEqual(changedJar));
            using (var memory = new MemoryStream(changedJar))
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Read))
            using (var stream = archive.GetEntry("keep.txt").Open()) { Assert(stream.ReadByte() == 1 && stream.ReadByte() == 2 && stream.ReadByte() == 3 && stream.ReadByte() == -1); }
            var session = engine.Apply(plan);
            var request = Worker.CreateJob(engine, bundle, plan, null); Assert(File.ReadAllText(request).Contains("binarySplice"));
            var update = engine.Preview(bundle, target, new[] { "exe" }); Assert(update.Files.Any(x => x.RelativePath == "game.jar" && x.AfterBytes.SequenceEqual(jar)));
            engine.BeforeWriteForTest = i => { if (i == 1) throw new IOException("fixture failure"); };
            try { engine.Apply(update); throw new Exception("Rollback fixture did not fail."); } catch (IOException) { }
            Assert(File.ReadAllBytes(Path.Combine(target, "game.jar")).SequenceEqual(changedJar)); engine.BeforeWriteForTest = null;
            var next = engine.Apply(engine.Preview(bundle, target, new[] { "exe" })); Assert(File.ReadAllBytes(Path.Combine(target, "game.jar")).SequenceEqual(jar));
            engine.Restore(next); Assert(File.ReadAllBytes(Path.Combine(target, "Client.exe")).SequenceEqual(exe) && File.ReadAllBytes(Path.Combine(target, "game.jar")).SequenceEqual(jar));
            var added = bundle.Patches[1].Operations[1]; added.BinaryData = "AA==";
            Reject(() => { string a, b; JarPatches.Transform(jar, bundle.Patches[1].Operations, out a, out b); });
            added.BinaryData = Convert.ToBase64String(Class(3));
            Reject(() => { string a, b; JarPatches.Transform(Archive(true), bundle.Patches[1].Operations, out a, out b); });
            Reject(() => JarPatches.ValidateEntry("../outside.class"));
            Reject(() => BinaryPatches.Splice(Class(1), new List<BinaryEdit> { new BinaryEdit { Offset = 10, Length = 2, Data = "AA==" } }, 100));
            Reject(() => BinaryPatches.Splice(Class(1), new List<BinaryEdit> { new BinaryEdit { Offset = 1, Length = 2, Data = "AA==" }, new BinaryEdit { Offset = 2, Length = 1, Data = "AA==" } }, 100));
            member["entrySha256"] = new string('0', 64); var bad = PatchBundle.Parse(Json.Pretty(document));
            Reject(() => { string a, b; JarPatches.Transform(jar, bad.Patches[1].Operations, out a, out b); });
            binary["resultSha256"] = new string('0', 64); bad = PatchBundle.Parse(Json.Pretty(document));
            Reject(() => { string a, b; BinaryPatches.Transform(exe, bad.Patches[0].Operations, out a, out b); });
        }
    }
}
