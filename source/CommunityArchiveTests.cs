#if TEST_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Patchwork
{
    public static class CommunityArchiveTests
    {
        static void Assert(bool value, string message) { if (!value) throw new Exception("Community archive assertion failed: " + message); }
        static void Reject(Action action)
        {
            try { action(); } catch (Exception) { return; }
            throw new Exception("Expected malformed or conflicting archive data to be rejected.");
        }
        static byte[] Zip(params KeyValuePair<string, byte[]>[] entries)
        {
            using (var memory = new MemoryStream())
            {
                using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
                    foreach (var pair in entries) { using (var stream = archive.CreateEntry(pair.Key).Open()) stream.Write(pair.Value, 0, pair.Value.Length); }
                return memory.ToArray();
            }
        }
        static PatchOperation Op(string kind, string entry)
        {
            return new PatchOperation { Kind = kind, File = "sample.zip", Entry = entry };
        }
        static byte[] Asar(Dictionary<string, object> files, byte[] payload)
        {
            byte[] json = Encoding.UTF8.GetBytes(Json.Serializer.Serialize(new Dictionary<string, object> { { "files", files } }));
            int headerSize = (8 + json.Length + 3) & ~3;
            using (var output = new MemoryStream()) using (var writer = new BinaryWriter(output))
            {
                writer.Write(4); writer.Write(headerSize); writer.Write(headerSize - 4); writer.Write(json.Length); writer.Write(json); writer.Write(new byte[headerSize - 8 - json.Length]); writer.Write(payload); return output.ToArray();
            }
        }
        static Dictionary<string, object> File(string name, int size, int offset)
        {
            return new Dictionary<string, object> { { "size", size }, { "offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
        }

        public static void Run(string root)
        {
            Assert(JarPatches.IsOperation("zipEntryAdd") && JarPatches.IsOperation("jarEntrySplice") && !JarPatches.IsOperation("binarySplice"), "ZIP operation detection");
            Assert(AsarPatches.IsOperation("asarEntryAdd") && AsarPatches.IsOperation("asarEntryReplace") && !AsarPatches.IsOperation("asarChecksum"), "ASAR operation detection");
            string before, after;
            byte[] note = Encoding.UTF8.GetBytes("before\n"), binary = new byte[] { 1, 2, 255 }, keep = new byte[] { 42, 43 };
            byte[] zip = Zip(new KeyValuePair<string, byte[]>("note.txt", note), new KeyValuePair<string, byte[]>("image.bin", binary), new KeyValuePair<string, byte[]>("keep.bin", keep));
            byte[] unusualZip = Zip(new KeyValuePair<string, byte[]>("CON.txt", new byte[] { 1 }), new KeyValuePair<string, byte[]>("name?.txt", new byte[] { 2 }));
            JarPatches.Compare(unusualZip, unusualZip, out before, out after);
            byte[] changedBinary = new byte[] { 1, 9, 255 }, addition = new byte[] { 255, 7 };
            var text = Op("zipTextReplace", "note.txt"); text.EntrySha256 = PatchEngine.Hash(note); text.Find = "before"; text.Replacement = "after"; text.Count = 1;
            var splice = Op("zipEntrySplice", "image.bin"); splice.EntrySha256 = PatchEngine.Hash(binary); splice.ResultSha256 = PatchEngine.Hash(changedBinary); splice.BinaryEdits.Add(new BinaryEdit { Offset = 1, Length = 1, Data = Convert.ToBase64String(new byte[] { 9 }) });
            var add = Op("zipEntryAdd", "new.bin"); add.BinaryData = Convert.ToBase64String(addition); add.ResultSha256 = PatchEngine.Hash(addition);
            byte[] patched = JarPatches.Transform(zip, new List<PatchOperation> { text, splice, add }, out before, out after);
            JarPatches.Compare(zip, patched, out before, out after);
            Assert(after.Contains("ADDED new.bin") && after.Contains("image.bin") && after.Contains("SHA-256") && after.Contains("after"), "ZIP additions and binary preview");
            byte[] repeatedLargeMember = new byte[7 * 1024 * 1024];
            var bulkBefore = Enumerable.Range(0, 8).Select(i => new KeyValuePair<string, byte[]>("large/" + i + ".bin", repeatedLargeMember)).Concat(new[] { new KeyValuePair<string, byte[]>("status.txt", Encoding.UTF8.GetBytes("before")) }).ToArray();
            var bulkAfter = Enumerable.Range(0, 8).Select(i => new KeyValuePair<string, byte[]>("large/" + i + ".bin", repeatedLargeMember)).Concat(new[] { new KeyValuePair<string, byte[]>("status.txt", Encoding.UTF8.GetBytes("after")) }).ToArray();
            byte[] bulkLeft = Zip(bulkBefore), bulkRight = Zip(bulkAfter);
            JarPatches.Compare(bulkLeft, bulkRight, out before, out after);
            Assert(before.Contains("status.txt") && after.Contains("after") && !after.Contains("large/0.bin"), "ZIP comparison streams unchanged members without retaining their payloads");
            using (var memory = new MemoryStream(patched)) using (var archive = new ZipArchive(memory, ZipArchiveMode.Read))
            {
                Assert(Read(archive, "keep.bin").SequenceEqual(keep), "unchanged ZIP member");
                Assert(Read(archive, "image.bin").SequenceEqual(changedBinary), "ZIP splice hash");
                Assert(Read(archive, "new.bin").SequenceEqual(addition), "ZIP addition hash");
            }
            var conflict = Op("zipEntryAdd", "image.bin"); conflict.BinaryData = add.BinaryData; conflict.ResultSha256 = add.ResultSha256;
            Reject(() => JarPatches.Transform(zip, new List<PatchOperation> { splice, conflict }, out before, out after));
            Reject(() => JarPatches.Compare(new byte[] { 1, 2 }, zip, out before, out after));
            var unsafeRaw = new Dictionary<string, object> { { "entry", "../escape" }, { "dataBase64", "AQ==" }, { "resultSha256", PatchEngine.Hash(new byte[] { 1 }) } };
            Reject(() => JarPatches.Parse(new PatchOperation { Kind = "zipEntryAdd", File = "sample.zip" }, unsafeRaw));
            byte[] signed = Zip(new KeyValuePair<string, byte[]>("META-INF/TEST.SF", new byte[] { 1 }));
            Reject(() => JarPatches.Transform(signed, new List<PatchOperation> { add }, out before, out after));
            var addDirectoryName = Op("zipEntryAdd", "folder"); addDirectoryName.BinaryData = add.BinaryData; addDirectoryName.ResultSha256 = add.ResultSha256;
            byte[] explicitDirectory = Zip(new KeyValuePair<string, byte[]>("folder/", new byte[0]));
            Reject(() => JarPatches.Transform(explicitDirectory, new List<PatchOperation> { addDirectoryName }, out before, out after));
            var addUnderFile = Op("zipEntryAdd", "parent/new.bin"); addUnderFile.BinaryData = add.BinaryData; addUnderFile.ResultSha256 = add.ResultSha256;
            byte[] parentFile = Zip(new KeyValuePair<string, byte[]>("parent", new byte[] { 1 }));
            Reject(() => JarPatches.Transform(parentFile, new List<PatchOperation> { addUnderFile }, out before, out after));
            var addFileOverDirectory = Op("zipEntryAdd", "group"); addFileOverDirectory.BinaryData = add.BinaryData; addFileOverDirectory.ResultSha256 = add.ResultSha256;
            byte[] existingChild = Zip(new KeyValuePair<string, byte[]>("group/child.bin", new byte[] { 1 }));
            Reject(() => JarPatches.Transform(existingChild, new List<PatchOperation> { addFileOverDirectory }, out before, out after));
            var addParent = Op("zipEntryAdd", "nested"); addParent.BinaryData = add.BinaryData; addParent.ResultSha256 = add.ResultSha256;
            var addChild = Op("zipEntryAdd", "nested/child.bin"); addChild.BinaryData = add.BinaryData; addChild.ResultSha256 = add.ResultSha256;
            Reject(() => JarPatches.Transform(zip, new List<PatchOperation> { addParent, addChild }, out before, out after));

            byte[] source = Encoding.UTF8.GetBytes("hello"), keepAsar = new byte[] { 3, 4 }, replaceData = new byte[] { 0, 255, 1 }, addData = new byte[] { 7, 8, 9 };
            var asarFiles = new Dictionary<string, object> {
                { "client.js", File("client.js", source.Length, 0) },
                { "keep.bin", File("keep.bin", keepAsar.Length, source.Length) }
            };
            var unusualAsar = Asar(new Dictionary<string, object> { { "CON.txt", File("CON.txt", 1, 0) }, { "name?.txt", File("name?.txt", 1, 1) } }, new byte[] { 1, 2 });
            AsarPatches.Compare(unusualAsar, unusualAsar, out before, out after);
            byte[] asar = Asar(asarFiles, source.Concat(keepAsar).ToArray());
            var replace = new PatchOperation { Kind = "asarEntryReplace", File = "app.asar", Entry = "client.js", EntrySha256 = PatchEngine.Hash(source), BinaryData = Convert.ToBase64String(replaceData), ResultSha256 = PatchEngine.Hash(replaceData) };
            var asarAdd = new PatchOperation { Kind = "asarEntryAdd", File = "app.asar", Entry = "added/data.bin", BinaryData = Convert.ToBase64String(addData), ResultSha256 = PatchEngine.Hash(addData) };
            byte[] changed = AsarPatches.Transform(asar, new List<PatchOperation> { replace, asarAdd }, out before, out after);
            AsarPatches.Compare(asar, changed, out before, out after);
            Assert(after.Contains("ADDED added/data.bin") && after.Contains("SHA-256") && after.Contains("client.js"), "ASAR add and binary preview");
            var read = AsarPatches.Transform(changed, new List<PatchOperation>(), out before, out after);
            Assert(read.SequenceEqual(changed), "ASAR unchanged transform bytes");
            var textConflict = new PatchOperation { Kind = "asarTextReplace", File = "app.asar", Entry = "client.js", EntrySha256 = PatchEngine.Hash(source), Find = "hello", Replacement = "world", Count = 1 };
            Reject(() => AsarPatches.Transform(asar, new List<PatchOperation> { replace, textConflict }, out before, out after));
            var linkedFiles = new Dictionary<string, object> { { "alias", new Dictionary<string, object> { { "link", "client.js" } } } };
            byte[] linked = Asar(linkedFiles, new byte[0]);
            var beneathLink = new PatchOperation { Kind = "asarEntryAdd", File = "app.asar", Entry = "alias/new.bin", BinaryData = Convert.ToBase64String(addData), ResultSha256 = PatchEngine.Hash(addData) };
            Reject(() => AsarPatches.Transform(linked, new List<PatchOperation> { beneathLink }, out before, out after));
            var unpackedLeaf = new Dictionary<string, object> { { "size", 1 }, { "unpacked", true } };
            var unpackedDirectoryFiles = new Dictionary<string, object> { { "native.node", unpackedLeaf } };
            var unpackedDirectory = new Dictionary<string, object> { { "unpacked", true }, { "files", unpackedDirectoryFiles } };
            var nodeModulesFiles = new Dictionary<string, object> { { "lzma-native", unpackedDirectory } };
            var nodeModules = new Dictionary<string, object> { { "files", nodeModulesFiles } };
            var unpackedRootFiles = new Dictionary<string, object> {
                { "client.js", File("client.js", source.Length, 0) }, { "keep.bin", File("keep.bin", keepAsar.Length, source.Length) }, { "node_modules", nodeModules }
            };
            byte[] unpackedArchive = Asar(unpackedRootFiles, source.Concat(keepAsar).ToArray());
            var legacyText = new PatchOperation { Kind = "asarTextReplace", File = "app.asar", Entry = "client.js", EntrySha256 = PatchEngine.Hash(source), Find = "hello", Replacement = "world", Count = 1 };
            byte[] unpackedPatched = AsarPatches.Transform(unpackedArchive, new List<PatchOperation> { legacyText }, out before, out after);
            using (var unpackedResult = new MemoryStream(unpackedPatched))
            using (var reader = new BinaryReader(unpackedResult))
            {
                reader.ReadUInt32(); int headerSize = reader.ReadInt32(); reader.ReadInt32(); int jsonSize = reader.ReadInt32();
                var updatedHeader = Json.Parse(new UTF8Encoding(false, true).GetString(reader.ReadBytes(jsonSize)));
                var rootFiles = Json.Object(Json.Object(updatedHeader["files"])["node_modules"])["files"];
                var lzma = Json.Object(Json.Object(rootFiles)["lzma-native"]);
                Assert((bool)lzma["unpacked"] && Json.Object(lzma["files"]).ContainsKey("native.node"), "unpacked directory metadata preserved through legacy text edit");
            }
            var beneathUnpacked = new PatchOperation { Kind = "asarEntryAdd", File = "app.asar", Entry = "node_modules/lzma-native/new.bin", BinaryData = Convert.ToBase64String(addData), ResultSha256 = PatchEngine.Hash(addData) };
            Reject(() => AsarPatches.Transform(unpackedArchive, new List<PatchOperation> { beneathUnpacked }, out before, out after));
            var packedChildDirectory = new Dictionary<string, object> { { "unpacked", true }, { "files", new Dictionary<string, object> { { "child.bin", File("child.bin", 1, 0) } } } };
            byte[] packedUnderUnpacked = Asar(new Dictionary<string, object> { { "module", packedChildDirectory } }, new byte[] { 1 });
            var replaceUnderUnpacked = new PatchOperation { Kind = "asarEntryReplace", File = "app.asar", Entry = "module/child.bin", EntrySha256 = PatchEngine.Hash(new byte[] { 1 }), BinaryData = Convert.ToBase64String(new byte[] { 2 }), ResultSha256 = PatchEngine.Hash(new byte[] { 2 }) };
            Reject(() => AsarPatches.Transform(packedUnderUnpacked, new List<PatchOperation> { replaceUnderUnpacked }, out before, out after));
            var badRaw = new Dictionary<string, object> { { "entry", "../escape.bin" }, { "dataBase64", "AQ==" }, { "resultSha256", PatchEngine.Hash(new byte[] { 1 }) } };
            Reject(() => AsarPatches.Parse(new PatchOperation { Kind = "asarEntryAdd", File = "app.asar" }, badRaw));
            Reject(() => AsarPatches.Compare(new byte[] { 4 }, asar, out before, out after));
        }

        static byte[] Read(ZipArchive archive, string name)
        {
            using (var stream = archive.GetEntry(name).Open()) using (var output = new MemoryStream()) { stream.CopyTo(output); return output.ToArray(); }
        }
    }
}
#endif
