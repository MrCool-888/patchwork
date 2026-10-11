using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Patchwork
{
    public static class JarPatches
    {
        public const int MaximumSize = 128 * 1024 * 1024;
        const int MaximumEntrySize = 8 * 1024 * 1024;
        static readonly string[] ArchiveExtensions = { ".zip", ".jar", ".war", ".ear" };

        public static bool IsOperation(string kind)
        {
            return kind == "jarEntrySplice" || kind == "jarEntryAdd" || kind == "zipEntrySplice" || kind == "zipEntryAdd" || kind == "zipTextReplace";
        }

        public static void ValidateEntry(string name)
        {
            PatchBundle.ValidateArchiveRelative(name);
            if (name.Contains("\\") || name.Length > 2048) throw new InvalidDataException("Use bounded forward-slash JAR member paths.");
        }

        public static void ValidateClass(byte[] data)
        {
            if (data.Length < 10 || data[0] != 0xca || data[1] != 0xfe || data[2] != 0xba || data[3] != 0xbe) throw new InvalidDataException("Invalid Java class payload.");
        }

        public static void Parse(PatchOperation op, Dictionary<string, object> raw)
        {
            bool legacy = op.Kind == "jarEntrySplice" || op.Kind == "jarEntryAdd";
            if (!(legacy ? op.File.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) : ArchiveExtensions.Contains(Path.GetExtension(op.File).ToLowerInvariant())))
                throw new InvalidDataException((legacy ? "JAR" : "ZIP") + " operation has an unsupported archive extension.");
            op.Entry = Json.String(raw, "entry"); ValidateEntry(op.Entry);
            if (legacy && !op.Entry.EndsWith(".class", StringComparison.Ordinal)) throw new InvalidDataException("JAR edits require a .class member.");
            if (op.Kind == "jarEntryAdd")
            {
                op.EntrySha256 = Json.String(raw, "entrySha256"); PatchBundle.ValidateHash(op.EntrySha256);
                op.BinaryData = Json.String(raw, "dataBase64");
                byte[] data = BinaryPatches.Data(op.BinaryData);
                if (!PatchEngine.Hash(data).Equals(op.EntrySha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Added class payload fingerprint mismatch.");
                ValidateClass(data); return;
            }
            if (op.Kind == "zipEntryAdd")
            {
                op.BinaryData = Json.String(raw, "dataBase64"); byte[] data = BinaryPatches.Data(op.BinaryData);
                op.ResultSha256 = Json.String(raw, "resultSha256"); PatchBundle.ValidateHash(op.ResultSha256);
                if (!PatchEngine.Hash(data).Equals(op.ResultSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Added archive member fingerprint mismatch.");
                return;
            }
            if (op.Kind == "jarEntrySplice" || op.Kind == "zipEntrySplice")
            {
                op.EntrySha256 = Json.String(raw, "entrySha256"); PatchBundle.ValidateHash(op.EntrySha256);
                op.ResultSha256 = Json.String(raw, "resultSha256"); PatchBundle.ValidateHash(op.ResultSha256);
                BinaryPatches.ParseEdits(op, raw); return;
            }
            op.EntrySha256 = Json.String(raw, "entrySha256"); PatchBundle.ValidateHash(op.EntrySha256);
            op.Find = Json.String(raw, "find"); op.Replacement = Json.String(raw, "replacement");
            object count;
            if (!raw.TryGetValue("count", out count) || !(count is int) || (int)count < 1 || (int)count > 10000 || op.Find.Length == 0)
                throw new InvalidDataException("zipTextReplace requires nonempty find and an exact integer count.");
            op.Count = (int)count;
        }

        static Dictionary<string, ZipArchiveEntry> Entries(ZipArchive archive)
        {
            if (archive.Entries.Count > 50000) throw new InvalidDataException("Archive member count exceeds limit.");
            var members = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                string memberPath = entry.FullName.EndsWith("/", StringComparison.Ordinal) ? entry.FullName.Substring(0, entry.FullName.Length - 1) : entry.FullName;
                ValidateEntry(memberPath);
                if (members.ContainsKey(entry.FullName)) throw new InvalidDataException("Duplicate archive member.");
                string ext = Path.GetExtension(entry.FullName);
                if (new[] { ".SF", ".RSA", ".DSA", ".EC" }.Contains(ext.ToUpperInvariant())) throw new InvalidDataException("Signed JARs cannot be modified.");
                total += entry.Length;
                if (entry.Length > MaximumSize || total > 1024L * 1024 * 1024) throw new InvalidDataException("Archive expanded size exceeds limit.");
                members.Add(entry.FullName, entry);
            }
            return members;
        }

        static byte[] ReadEntry(ZipArchiveEntry entry)
        {
            if (entry.Length > MaximumEntrySize) throw new InvalidDataException("Edited archive member exceeds 8 MiB.");
            using (var input = entry.Open())
            using (var output = new MemoryStream())
            {
                byte[] buffer = new byte[65536]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (output.Length + count > MaximumEntrySize) throw new InvalidDataException("Archive member exceeds bounded size.");
                    output.Write(buffer, 0, count);
                }
                if (output.Length != entry.Length) throw new InvalidDataException("Archive member length mismatch.");
                return output.ToArray();
            }
        }

        static bool Legacy(PatchOperation op) { return op.Kind == "jarEntryAdd" || op.Kind == "jarEntrySplice"; }

        static void ValidateZipAddition(string path, Dictionary<string, ZipArchiveEntry> members, IEnumerable<string> additions)
        {
            if (members.ContainsKey(path) || members.ContainsKey(path + "/") || members.Keys.Any(x => x.StartsWith(path + "/", StringComparison.Ordinal)))
                throw new InvalidDataException("Added archive member conflicts with an existing file or directory: " + path);
            string[] parts = path.Split('/');
            for (int i = 1; i < parts.Length; i++)
            {
                string parent = String.Join("/", parts.Take(i));
                if (members.ContainsKey(parent)) throw new InvalidDataException("Added archive member is beneath an existing file: " + path);
            }
            foreach (string other in additions)
                if (other != path && (other.StartsWith(path + "/", StringComparison.Ordinal) || path.StartsWith(other + "/", StringComparison.Ordinal)))
                    throw new InvalidDataException("Added archive members conflict by path: " + path);
        }

        public static byte[] Transform(byte[] original, List<PatchOperation> operations, out string beforeText, out string afterText)
        {
            if (original.Length > MaximumSize) throw new InvalidDataException("Archive exceeds 128 MiB.");
            var edits = operations.GroupBy(x => Json.Serializer.Serialize(x)).Select(x => x.First()).ToList();
            if (edits.GroupBy(x => x.Entry, StringComparer.Ordinal).Any(x => x.Count() != 1)) throw new InvalidDataException("Conflicting archive member edits.");
            var changes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            bool legacyOnly = edits.All(Legacy);
            var before = new StringBuilder(); var after = new StringBuilder(legacyOnly ? "Executable Java client code; runs inside the game. Patchwork does not execute it.\n" : "Executable client code; runs inside the target app. Patchwork does not execute it.\n");
            using (var input = new MemoryStream(original, false))
            using (var archive = new ZipArchive(input, ZipArchiveMode.Read))
            {
                var members = Entries(archive);
                foreach (var op in edits)
                {
                    ZipArchiveEntry entry; members.TryGetValue(op.Entry, out entry);
                    byte[] data;
                    if (op.Kind == "jarEntryAdd" || op.Kind == "zipEntryAdd")
                    {
                        if (entry != null) throw new InvalidDataException("Added archive member already exists: " + op.Entry);
                        if (op.Kind == "zipEntryAdd") ValidateZipAddition(op.Entry, members, edits.Where(x => x.Kind == "zipEntryAdd").Select(x => x.Entry));
                        data = BinaryPatches.Data(op.BinaryData);
                        if (op.Kind == "jarEntryAdd" && !PatchEngine.Hash(data).Equals(op.EntrySha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Added JAR payload hash mismatch.");
                        if (op.Kind == "zipEntryAdd" && !PatchEngine.Hash(data).Equals(op.ResultSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Added archive member fingerprint mismatch.");
                        before.AppendLine(op.Entry + " · absent");
                    }
                    else
                    {
                        if (entry == null) throw new InvalidDataException("Missing archive member: " + op.Entry);
                        byte[] bytes = ReadEntry(entry);
                        if (!PatchEngine.Hash(bytes).Equals(op.EntrySha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive member fingerprint mismatch.");
                        before.AppendLine(op.Entry + " · SHA-256 " + PatchEngine.Hash(bytes));
                        if (op.Kind == "zipTextReplace")
                        {
                            string text = DecodeText(bytes);
                            int count = CountMatches(text, op.Find);
                            if (count != op.Count) throw new InvalidOperationException("Expected " + op.Count + " text matches in archive member " + op.Entry + "; found " + count + ".");
                            if ((long)text.Length + (long)count * (op.Replacement.Length - op.Find.Length) > MaximumEntrySize) throw new InvalidDataException("Patched archive member exceeds 8 MiB.");
                            string changed = text.Replace(op.Find, op.Replacement);
                            byte[] encoded = new UTF8Encoding(false, true).GetBytes(changed);
                            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) encoded = new byte[] { 0xef, 0xbb, 0xbf }.Concat(encoded).ToArray();
                            data = encoded;
                        }
                        else data = BinaryPatches.Splice(bytes, op.BinaryEdits, MaximumEntrySize);
                        if (op.Kind != "zipTextReplace" && !PatchEngine.Hash(data).Equals(op.ResultSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Patched archive member fingerprint mismatch.");
                    }
                    if (Legacy(op)) ValidateClass(data);
                    if (data.Length > MaximumEntrySize) throw new InvalidDataException("Patched archive member exceeds 8 MiB.");
                    changes.Add(op.Entry, data);
                    after.AppendLine(op.Entry + " · " + data.Length + " bytes · SHA-256 " + PatchEngine.Hash(data));
                }
            }
            byte[] result;
            using (var output = new MemoryStream())
            {
                output.Write(original, 0, original.Length); output.Position = 0;
                using (var archive = new ZipArchive(output, ZipArchiveMode.Update, true))
                    foreach (var op in edits.OrderBy(x => x.Entry, StringComparer.Ordinal))
                    {
                        var entry = archive.GetEntry(op.Entry);
                        if (entry == null)
                        {
                            entry = archive.CreateEntry(op.Entry, CompressionLevel.Optimal);
                            entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        }
                        using (var stream = entry.Open()) { stream.SetLength(0); byte[] data = changes[op.Entry]; stream.Write(data, 0, data.Length); }
                    }
                if (output.Length > MaximumSize) throw new InvalidDataException("Patched archive exceeds 128 MiB.");
                result = output.ToArray();
            }
            using (var output = new MemoryStream(result, false))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Read))
            {
                var members = Entries(archive);
                foreach (var pair in changes)
                    if (!members.ContainsKey(pair.Key) || !ReadEntry(members[pair.Key]).SequenceEqual(pair.Value)) throw new IOException("Patched archive verification failed.");
            }
            beforeText = before.ToString(); afterText = after.ToString(); return result;
        }

        static int CountMatches(string text, string find)
        {
            int count = 0, offset = 0;
            while ((offset = text.IndexOf(find, offset, StringComparison.Ordinal)) >= 0) { count++; offset += find.Length; }
            return count;
        }

        static string DecodeText(byte[] bytes)
        {
            int offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            string text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
            if (text.IndexOf('\0') >= 0) throw new InvalidDataException("Archive member is not UTF-8 text.");
            return text;
        }

        static bool TryText(byte[] bytes, out string text)
        {
            try { text = DecodeText(bytes); return true; } catch (DecoderFallbackException) { text = null; return false; } catch (InvalidDataException) { text = null; return false; }
        }

        sealed class PreviewMember { public long Size; public string Hash; public ZipArchiveEntry Entry; }
        static Dictionary<string, PreviewMember> PreviewMembers(ZipArchive archive)
        {
            var result = new Dictionary<string, PreviewMember>(StringComparer.Ordinal);
            foreach (var pair in Entries(archive).Where(x => !x.Key.EndsWith("/", StringComparison.Ordinal)))
            {
                using (var stream = pair.Value.Open()) using (var hash = SHA256.Create())
                {
                    byte[] buffer = new byte[65536]; int read; long length = 0;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        length += read;
                        if (length > pair.Value.Length || length > MaximumSize) throw new InvalidDataException("Archive member exceeds declared bounds.");
                        hash.TransformBlock(buffer, 0, read, buffer, 0);
                    }
                    hash.TransformFinalBlock(new byte[0], 0, 0);
                    if (length != pair.Value.Length) throw new InvalidDataException("Archive member length mismatch.");
                    result.Add(pair.Key, new PreviewMember { Size = length, Hash = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant(), Entry = pair.Value });
                }
            }
            return result;
        }

        public static void Compare(byte[] before, byte[] after, out string beforeText, out string afterText)
        {
            if (before.Length > MaximumSize || after.Length > MaximumSize) throw new InvalidDataException("Archive exceeds 128 MiB.");
            using (var oldInput = new MemoryStream(before, false)) using (var oldArchive = new ZipArchive(oldInput, ZipArchiveMode.Read))
            using (var newInput = new MemoryStream(after, false)) using (var newArchive = new ZipArchive(newInput, ZipArchiveMode.Read))
            {
                var left = PreviewMembers(oldArchive); var right = PreviewMembers(newArchive);
                var oldText = new StringBuilder(); var newText = new StringBuilder("Archive member changes; executable contents run in the target application. Patchwork does not execute them.\n");
                foreach (string path in left.Keys.Union(right.Keys).OrderBy(x => x, StringComparer.Ordinal))
                {
                    PreviewMember oldMember, newMember; bool hasOld = left.TryGetValue(path, out oldMember), hasNew = right.TryGetValue(path, out newMember);
                    if (hasOld && hasNew && oldMember.Size == newMember.Size && oldMember.Hash == newMember.Hash) continue;
                    if (!hasOld) { newText.AppendLine("ADDED " + path); AppendMember(newText, newMember); continue; }
                    if (!hasNew) { oldText.AppendLine("REMOVED " + path); AppendMember(oldText, oldMember); continue; }
                    oldText.AppendLine("CHANGED " + path); newText.AppendLine("CHANGED " + path);
                    if (oldMember.Size <= MaximumEntrySize && newMember.Size <= MaximumEntrySize)
                    {
                        byte[] oldBytes = ReadEntry(oldMember.Entry), newBytes = ReadEntry(newMember.Entry);
                        string oldContent, newContent;
                        if (TryText(oldBytes, out oldContent) && TryText(newBytes, out newContent))
                        { oldText.AppendLine(oldContent); newText.AppendLine(newContent); }
                        else { oldText.AppendLine(Describe(oldMember)); newText.AppendLine(Describe(newMember)); }
                    }
                    else { oldText.AppendLine(Describe(oldMember)); newText.AppendLine(Describe(newMember)); }
                }
                beforeText = oldText.Length == 0 ? "Archive members are unchanged; this file remains tracked for restoration." : oldText.ToString();
                afterText = newText.ToString();
            }
        }

        static string Describe(PreviewMember member) { return "Binary or oversized member · " + member.Size + " bytes · SHA-256 " + member.Hash; }
        static void AppendMember(StringBuilder output, PreviewMember member)
        {
            string text;
            if (member.Size <= MaximumEntrySize && TryText(ReadEntry(member.Entry), out text)) output.AppendLine(text);
            else output.AppendLine(Describe(member));
        }
    }
}
