using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Patchwork
{
    // Archive edits are executable client code; Patchwork never evaluates their contents.
    public static class AsarPatches
    {
        public const int MaximumSize = 64 * 1024 * 1024;
        const int MaximumEntrySize = 8 * 1024 * 1024;
        sealed class Member
        {
            public string Path;
            public Dictionary<string, object> Metadata;
            public int Offset, Size;
            public byte[] Bytes;
        }
        sealed class Archive
        {
            public Dictionary<string, object> Header;
            public List<Member> Members = new List<Member>();
            public long PackedSize;
        }
        sealed class Edit { public int Start, Length; public string Replacement; }

        public static bool IsOperation(string kind) { return kind == "asarTextReplace" || kind == "asarEntryAdd" || kind == "asarEntryReplace"; }

        public static void Parse(PatchOperation op, Dictionary<string, object> raw)
        {
            if (!op.File.EndsWith(".asar", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(op.Kind + " requires an .asar file.");
            op.Entry = Json.String(raw, "entry"); ValidateEntry(op.Entry);
            if (op.Kind == "asarEntryAdd" || op.Kind == "asarEntryReplace")
            {
                if (op.Kind == "asarEntryReplace") { op.EntrySha256 = Json.String(raw, "entrySha256"); PatchBundle.ValidateHash(op.EntrySha256); }
                op.BinaryData = Json.String(raw, "dataBase64"); byte[] data = BinaryPatches.Data(op.BinaryData);
                op.ResultSha256 = Json.String(raw, "resultSha256"); PatchBundle.ValidateHash(op.ResultSha256);
                if (!PatchEngine.Hash(data).Equals(op.ResultSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ASAR payload fingerprint mismatch.");
                return;
            }
            op.EntrySha256 = Json.String(raw, "entrySha256"); PatchBundle.ValidateHash(op.EntrySha256);
            if (!new[] { ".js", ".mjs", ".json", ".css", ".html", ".txt" }.Contains(Path.GetExtension(op.Entry).ToLowerInvariant())) throw new InvalidDataException("Unsupported ASAR text entry extension.");
            op.Find = Json.String(raw, "find"); op.Replacement = Json.String(raw, "replacement");
            object count;
            if (!raw.TryGetValue("count", out count) || !(count is int) || (int)count < 1 || (int)count > 10000 || op.Find.Length == 0) throw new InvalidDataException("asarTextReplace requires nonempty find and an exact integer count.");
            op.Count = (int)count;
        }
        static void ValidateEntry(string path)
        {
            PatchBundle.ValidateArchiveRelative(path);
            if (path.Contains("\\") || path.Length > 2048) throw new InvalidDataException("Use forward slashes in ASAR entry paths.");
        }
        static int Number(object value, string name, int maximum)
        {
            if (!(value is int) || (int)value < 0 || (int)value > maximum) throw new InvalidDataException("Invalid ASAR " + name + ".");
            return (int)value;
        }
        static Archive Read(byte[] bytes)
        {
            if (bytes.Length < 16 || bytes.Length > MaximumSize) throw new InvalidDataException("Invalid ASAR archive size.");
            uint headerSize = BitConverter.ToUInt32(bytes, 4), textSize = BitConverter.ToUInt32(bytes, 12);
            if (BitConverter.ToUInt32(bytes, 0) != 4 || headerSize < 8 || headerSize > 4 * 1024 * 1024 || 8L + headerSize > bytes.Length || BitConverter.ToUInt32(bytes, 8) != headerSize - 4 || textSize == 0 || ((8L + textSize + 3) & ~3L) != headerSize) throw new InvalidDataException("Invalid ASAR Pickle header.");
            for (long i = 16L + textSize; i < 8L + headerSize; i++) if (bytes[(int)i] != 0) throw new InvalidDataException("Invalid ASAR header padding.");
            var archive = new Archive { Header = Json.Parse(new UTF8Encoding(false, true).GetString(bytes, 16, (int)textSize)) };
            int dataStart = 8 + (int)headerSize, nodes = 0;
            Walk(archive, archive.Header, "", bytes, dataStart, 0, ref nodes);
            var packed = archive.Members.Where(x => x.Bytes != null).OrderBy(x => x.Offset).ThenBy(x => x.Size).ToList();
            for (int i = 1; i < packed.Count; i++)
            {
                var previous = packed[i - 1]; var current = packed[i];
                if (current.Offset < previous.Offset + previous.Size && (current.Offset != previous.Offset || current.Size != previous.Size)) throw new InvalidDataException("Overlapping ASAR entries.");
            }
            return archive;
        }
        static void Walk(Archive archive, Dictionary<string, object> directory, string prefix, byte[] bytes, int dataStart, int depth, ref int nodes)
        {
            if (depth > 32 || !directory.ContainsKey("files")) throw new InvalidDataException("Invalid ASAR directory.");
            foreach (var pair in Json.Object(directory["files"]))
            {
                if (++nodes > 50000 || pair.Key.Contains("/") || pair.Key.Contains("\\")) throw new InvalidDataException("Invalid ASAR entry tree.");
                string path = prefix + pair.Key; ValidateEntry(path);
                var metadata = Json.Object(pair.Value);
                if (metadata.ContainsKey("files"))
                {
                    if (metadata.ContainsKey("link")) throw new InvalidDataException("Invalid ASAR directory metadata.");
                    object directoryUnpacked;
                    if (metadata.TryGetValue("unpacked", out directoryUnpacked) && !(directoryUnpacked is bool)) throw new InvalidDataException("Invalid ASAR unpacked flag.");
                    Walk(archive, metadata, path + "/", bytes, dataStart, depth + 1, ref nodes); continue;
                }
                var member = new Member { Path = path, Metadata = metadata }; archive.Members.Add(member);
                if (metadata.ContainsKey("link")) { ValidateEntry(Json.String(metadata, "link")); continue; }
                object unpacked;
                if (metadata.TryGetValue("unpacked", out unpacked) && !(unpacked is bool)) throw new InvalidDataException("Invalid ASAR unpacked flag.");
                object size;
                if (!metadata.TryGetValue("size", out size)) throw new InvalidDataException("Missing ASAR entry size: " + path);
                member.Size = Number(size, "entry size", MaximumSize);
                if (unpacked is bool && (bool)unpacked) continue;
                long offset;
                if (!Int64.TryParse(Json.String(metadata, "offset"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out offset) || offset < 0 || offset + member.Size > bytes.Length - dataStart) throw new InvalidDataException("ASAR entry exceeds archive bounds.");
                archive.PackedSize += member.Size;
                if (archive.PackedSize > MaximumSize) throw new InvalidDataException("ASAR packed member contents exceed 64 MB.");
                member.Offset = (int)offset; member.Bytes = new byte[member.Size];
                Buffer.BlockCopy(bytes, dataStart + member.Offset, member.Bytes, 0, member.Size);
                if (metadata.ContainsKey("integrity")) VerifyIntegrity(member.Bytes, Json.Object(metadata["integrity"]));
            }
        }
        static Dictionary<string, object> Integrity(byte[] bytes, int blockSize)
        {
            if (blockSize <= 0 || (bytes.Length + (long)blockSize - 1) / blockSize > 65536) throw new InvalidDataException("ASAR integrity block count exceeds bounded size.");
            var blocks = new List<string>();
            for (int offset = 0; offset < bytes.Length; offset += blockSize)
            {
                var block = new byte[Math.Min(blockSize, bytes.Length - offset)]; Buffer.BlockCopy(bytes, offset, block, 0, block.Length); blocks.Add(PatchEngine.Hash(block));
            }
            if (bytes.Length == 0) blocks.Add(PatchEngine.Hash(bytes));
            return new Dictionary<string, object> { { "algorithm", "SHA256" }, { "hash", PatchEngine.Hash(bytes) }, { "blockSize", blockSize }, { "blocks", blocks } };
        }
        static void VerifyIntegrity(byte[] bytes, Dictionary<string, object> integrity)
        {
            object blockSize;
            if (Json.String(integrity, "algorithm") != "SHA256" || !integrity.TryGetValue("blockSize", out blockSize)) throw new InvalidDataException("Unsupported ASAR integrity metadata.");
            int size = Number(blockSize, "integrity block size", MaximumSize);
            if (size == 0) throw new InvalidDataException("Invalid ASAR integrity block size.");
            var expected = Integrity(bytes, size);
            var blocks = Json.Array(integrity, "blocks").Cast<string>().ToList();
            // Earlier Patchwork builds emitted no blocks for empty members; retain those histories.
            bool validBlocks = bytes.Length == 0 && blocks.Count == 0 || blocks.SequenceEqual((List<string>)expected["blocks"], StringComparer.OrdinalIgnoreCase);
            if (!Json.String(integrity, "hash").Equals((string)expected["hash"], StringComparison.OrdinalIgnoreCase) || !validBlocks) throw new InvalidDataException("ASAR member integrity mismatch.");
        }
        static byte[] Write(Archive archive)
        {
            using (var payload = new MemoryStream())
            {
                foreach (var member in archive.Members.Where(x => x.Bytes != null).OrderBy(x => x.Offset).ThenBy(x => x.Path, StringComparer.Ordinal))
                {
                    member.Metadata["offset"] = payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    member.Metadata["size"] = member.Bytes.Length;
                    payload.Write(member.Bytes, 0, member.Bytes.Length);
                    if (payload.Length > MaximumSize) throw new InvalidDataException("Patched ASAR exceeds 64 MB.");
                }
                byte[] header = Encoding.UTF8.GetBytes(Json.Serializer.Serialize(archive.Header));
                int headerSize = (8 + header.Length + 3) & ~3;
                if (headerSize > 4 * 1024 * 1024 || 8L + headerSize + payload.Length > MaximumSize) throw new InvalidDataException("Patched ASAR exceeds bounded size.");
                using (var output = new MemoryStream())
                using (var writer = new BinaryWriter(output))
                {
                    writer.Write(4); writer.Write(headerSize); writer.Write(headerSize - 4); writer.Write(header.Length); writer.Write(header);
                    writer.Write(new byte[headerSize - 8 - header.Length]); payload.Position = 0; payload.CopyTo(output);
                    return output.ToArray();
                }
            }
        }
        static void AddMember(Archive archive, string path, byte[] bytes)
        {
            string[] parts = path.Split('/'); var directory = archive.Header;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var files = Json.Object(directory["files"]); object value;
                if (!files.TryGetValue(parts[i], out value))
                {
                    var child = new Dictionary<string, object> { { "files", new Dictionary<string, object>() } }; files.Add(parts[i], child); directory = child; continue;
                }
                var metadata = Json.Object(value);
                object unpacked;
                if (metadata.ContainsKey("link") || !metadata.ContainsKey("files") || (metadata.TryGetValue("unpacked", out unpacked) && (!(unpacked is bool) || (bool)unpacked))) throw new InvalidDataException("Cannot add an ASAR member beneath a linked, unpacked or file entry: " + path);
                directory = metadata;
            }
            var parentFiles = Json.Object(directory["files"]);
            if (parentFiles.ContainsKey(parts[parts.Length - 1])) throw new InvalidDataException("Added ASAR member already exists: " + path);
            var memberMetadata = new Dictionary<string, object> { { "size", bytes.Length }, { "offset", "0" }, { "integrity", Integrity(bytes, 4 * 1024 * 1024) } };
            parentFiles.Add(parts[parts.Length - 1], memberMetadata);
            int offset = archive.Members.Where(x => x.Bytes != null).Select(x => x.Offset + x.Size).DefaultIfEmpty(0).Max();
            archive.Members.Add(new Member { Path = path, Metadata = memberMetadata, Offset = offset, Size = bytes.Length, Bytes = bytes });
        }

        static void ValidatePackedAncestors(Archive archive, string path)
        {
            string[] parts = path.Split('/'); var directory = archive.Header;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                object value;
                if (!Json.Object(directory["files"]).TryGetValue(parts[i], out value)) throw new InvalidDataException("ASAR entry parent is missing: " + path);
                var metadata = Json.Object(value);
                object unpacked;
                if (metadata.ContainsKey("link") || !metadata.ContainsKey("files") ||
                    (metadata.TryGetValue("unpacked", out unpacked) && (!(unpacked is bool) || (bool)unpacked)))
                    throw new InvalidDataException("Cannot replace an ASAR member beneath a linked, unpacked or file entry: " + path);
                directory = metadata;
            }
        }

        public static byte[] Transform(byte[] original, List<PatchOperation> operations, out string beforeText, out string afterText)
        {
            operations = operations.GroupBy(x => Json.Serializer.Serialize(x)).Select(x => x.First()).ToList();
            var archive = Read(original);
            foreach (var group in operations.GroupBy(x => x.Entry, StringComparer.Ordinal))
            {
                var edits = group.ToList();
                var member = archive.Members.SingleOrDefault(x => x.Path == group.Key);
                bool add = edits[0].Kind == "asarEntryAdd";
                if (add)
                {
                    if (edits.Count != 1 || member != null) throw new InvalidDataException("Added ASAR member already exists or conflicts: " + group.Key);
                    byte[] data = BinaryPatches.Data(edits[0].BinaryData);
                    if (!PatchEngine.Hash(data).Equals(edits[0].ResultSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ASAR payload fingerprint mismatch.");
                    AddMember(archive, group.Key, data); continue;
                }
                if (member == null || member.Bytes == null) throw new InvalidDataException("ASAR entry is missing, linked or unpacked: " + group.Key);
                if (edits.Any(x => x.Kind == "asarEntryReplace"))
                {
                    if (edits.Count != 1 || edits[0].Kind != "asarEntryReplace") throw new InvalidDataException("Conflicting ASAR entry edits: " + group.Key);
                    ValidatePackedAncestors(archive, group.Key);
                    if (member.Bytes.Length > MaximumEntrySize) throw new InvalidDataException("ASAR entry exceeds 8 MB.");
                    if (!PatchEngine.Hash(member.Bytes).Equals(edits[0].EntrySha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ASAR entry fingerprint mismatch: " + group.Key);
                    byte[] replacement = BinaryPatches.Data(edits[0].BinaryData);
                    if (!PatchEngine.Hash(replacement).Equals(edits[0].ResultSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ASAR payload fingerprint mismatch.");
                    member.Bytes = replacement; member.Size = replacement.Length;
                    int replacementBlockSize = member.Metadata.ContainsKey("integrity") ? (int)Json.Object(member.Metadata["integrity"])["blockSize"] : 4 * 1024 * 1024;
                    member.Metadata["integrity"] = Integrity(replacement, replacementBlockSize); continue;
                }
                if (member.Bytes.Length > MaximumEntrySize) throw new InvalidDataException("ASAR text entries must not exceed 8 MB.");
                string text = PatchEngine.Decode(member.Bytes); var textEdits = new List<Edit>();
                foreach (var op in edits)
                {
                    if (!PatchEngine.Hash(member.Bytes).Equals(op.EntrySha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("ASAR entry fingerprint mismatch: " + group.Key);
                    int count = 0, offset = 0;
                    while ((offset = text.IndexOf(op.Find, offset, StringComparison.Ordinal)) >= 0) { textEdits.Add(new Edit { Start = offset, Length = op.Find.Length, Replacement = op.Replacement }); count++; offset += op.Find.Length; }
                    if (count != op.Count) throw new InvalidOperationException("Expected " + op.Count + " text matches in ASAR entry " + group.Key + "; found " + count + ".");
                }
                var ordered = textEdits.OrderBy(x => x.Start).ToList();
                for (int i = 1; i < ordered.Count; i++) if (ordered[i].Start < ordered[i - 1].Start + ordered[i - 1].Length) throw new InvalidOperationException("Conflicting ASAR entry edits: " + group.Key);
                if (text.Length + ordered.Sum(x => (long)x.Replacement.Length - x.Length) > MaximumEntrySize) throw new InvalidDataException("Patched ASAR entry exceeds 8 MB.");
                var changed = new StringBuilder(text);
                foreach (var edit in ordered.AsEnumerable().Reverse()) { changed.Remove(edit.Start, edit.Length); changed.Insert(edit.Start, edit.Replacement); }
                byte[] encoded = Encoding.UTF8.GetBytes(changed.ToString());
                if (member.Bytes.Length >= 3 && member.Bytes[0] == 0xef && member.Bytes[1] == 0xbb && member.Bytes[2] == 0xbf) encoded = new byte[] { 0xef, 0xbb, 0xbf }.Concat(encoded).ToArray();
                if (encoded.Length > MaximumEntrySize) throw new InvalidDataException("Patched ASAR entry exceeds 8 MB.");
                member.Bytes = encoded;
                int blockSize = member.Metadata.ContainsKey("integrity") ? (int)Json.Object(member.Metadata["integrity"])["blockSize"] : 4 * 1024 * 1024;
                member.Metadata["integrity"] = Integrity(encoded, blockSize);
            }
            byte[] result = Write(archive); var verified = Read(result).Members.ToDictionary(x => x.Path, StringComparer.Ordinal);
            foreach (var member in archive.Members.Where(x => x.Bytes != null)) if (!verified.ContainsKey(member.Path) || verified[member.Path].Bytes == null || !member.Bytes.SequenceEqual(verified[member.Path].Bytes)) throw new IOException("Rebuilt ASAR verification failed.");
            Compare(original, result, out beforeText, out afterText); return result;
        }
        public static void Compare(byte[] before, byte[] after, out string beforeText, out string afterText)
        {
            var left = Read(before).Members.ToDictionary(x => x.Path, StringComparer.Ordinal); var right = Read(after).Members.ToDictionary(x => x.Path, StringComparer.Ordinal); var oldText = new StringBuilder(); var newText = new StringBuilder();
            newText.AppendLine("Executable client code · runs inside the target app. Patchwork does not execute this code.");
            foreach (string path in left.Keys.Union(right.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            {
                Member member, other; left.TryGetValue(path, out member); right.TryGetValue(path, out other);
                if (member != null && other != null && member.Bytes != null && other.Bytes != null && member.Bytes.SequenceEqual(other.Bytes)) continue;
                if (member == null) { newText.AppendLine("ADDED " + path); AppendPreview(newText, other); continue; }
                if (other == null) { oldText.AppendLine("REMOVED " + path); AppendPreview(oldText, member); continue; }
                if (member.Bytes == null && other.Bytes == null) continue;
                if (member.Bytes == null || other.Bytes == null)
                {
                    oldText.AppendLine("CHANGED " + path); newText.AppendLine("CHANGED " + path);
                    AppendPreview(oldText, member); AppendPreview(newText, other); continue;
                }
                if (member.Bytes.Length > MaximumEntrySize || other.Bytes.Length > MaximumEntrySize) throw new InvalidDataException("ASAR preview entry exceeds 8 MB.");
                oldText.AppendLine("ENTRY " + path + " · SHA-256 " + PatchEngine.Hash(member.Bytes)); AppendPreview(oldText, member);
                newText.AppendLine("ENTRY " + path + " · SHA-256 " + PatchEngine.Hash(other.Bytes)); AppendPreview(newText, other);
            }
            beforeText = oldText.Length == 0 ? "Archive member contents are unchanged; this file remains tracked for restoration." : oldText.ToString();
            afterText = newText.ToString();
        }

        static void AppendPreview(StringBuilder output, Member member)
        {
            if (member == null || member.Bytes == null) { output.AppendLine("Linked or unpacked member; payload is not embedded in this archive."); return; }
            if (member.Bytes.Length > MaximumEntrySize) throw new InvalidDataException("ASAR preview entry exceeds 8 MB.");
            try { output.AppendLine(PatchEngine.Decode(member.Bytes)); }
            catch (DecoderFallbackException) { output.AppendLine(BinaryPatches.Describe(member.Bytes)); }
            catch (InvalidDataException) { output.AppendLine(BinaryPatches.Describe(member.Bytes)); }
        }
    }
}
