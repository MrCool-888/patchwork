using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Patchwork
{
    public static class JarPatches
    {
        public const int MaximumSize = 128 * 1024 * 1024;
        const int MaximumEntrySize = 8 * 1024 * 1024;
        public static void ValidateEntry(string name)
        {
            PatchBundle.ValidateRelative(name);
            if (name.Contains("\\") || name.Length > 2048) throw new InvalidDataException("Use bounded forward-slash JAR member paths.");
        }
        public static void ValidateClass(byte[] data)
        {
            if (data.Length < 10 || data[0] != 0xca || data[1] != 0xfe || data[2] != 0xba || data[3] != 0xbe) throw new InvalidDataException("Invalid Java class payload.");
        }
        static Dictionary<string, ZipArchiveEntry> Entries(ZipArchive archive)
        {
            if (archive.Entries.Count > 50000) throw new InvalidDataException("JAR member count exceeds limit.");
            var members = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                ValidateEntry(entry.FullName.TrimEnd('/'));
                if (members.ContainsKey(entry.FullName)) throw new InvalidDataException("Duplicate JAR member.");
                string ext = Path.GetExtension(entry.FullName);
                if (new[] { ".SF", ".RSA", ".DSA", ".EC" }.Contains(ext.ToUpperInvariant())) throw new InvalidDataException("Signed JARs cannot be modified.");
                total += entry.Length;
                if (entry.Length > MaximumSize || total > 1024L * 1024 * 1024) throw new InvalidDataException("JAR expanded size exceeds limit.");
                members.Add(entry.FullName, entry);
            }
            return members;
        }
        static byte[] ReadEntry(ZipArchiveEntry entry)
        {
            if (entry.Length > MaximumEntrySize) throw new InvalidDataException("Edited JAR member exceeds 8 MiB.");
            using (var input = entry.Open())
            using (var output = new MemoryStream())
            {
                byte[] buffer = new byte[65536]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (output.Length + count > MaximumEntrySize) throw new InvalidDataException("JAR member exceeds bounded size.");
                    output.Write(buffer, 0, count);
                }
                if (output.Length != entry.Length) throw new InvalidDataException("JAR member length mismatch.");
                return output.ToArray();
            }
        }
        public static byte[] Transform(byte[] original, List<PatchOperation> operations, out string beforeText, out string afterText)
        {
            if (original.Length > MaximumSize) throw new InvalidDataException("JAR exceeds 128 MiB.");
            var edits = operations.GroupBy(x => Json.Serializer.Serialize(x)).Select(x => x.First()).ToList();
            if (edits.GroupBy(x => x.Entry, StringComparer.Ordinal).Any(x => x.Count() != 1)) throw new InvalidDataException("Conflicting JAR member edits.");
            var changes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var before = new StringBuilder(); var after = new StringBuilder("Executable Java client code; runs inside the game. Patchwork does not execute it.\n");
            using (var input = new MemoryStream(original, false))
            using (var archive = new ZipArchive(input, ZipArchiveMode.Read))
            {
                var members = Entries(archive);
                foreach (var op in edits)
                {
                    ZipArchiveEntry entry; members.TryGetValue(op.Entry, out entry);
                    byte[] data;
                    if (op.Kind == "jarEntryAdd")
                    {
                        if (entry != null) throw new InvalidDataException("Added JAR member already exists: " + op.Entry);
                        data = BinaryPatches.Data(op.BinaryData);
                        if (PatchEngine.Hash(data) != op.EntrySha256.ToLowerInvariant()) throw new InvalidDataException("Added JAR payload hash mismatch.");
                        before.AppendLine(op.Entry + " · absent");
                    }
                    else
                    {
                        if (entry == null) throw new InvalidDataException("Missing JAR member: " + op.Entry);
                        byte[] bytes = ReadEntry(entry);
                        if (PatchEngine.Hash(bytes) != op.EntrySha256.ToLowerInvariant()) throw new InvalidDataException("JAR member fingerprint mismatch.");
                        before.AppendLine(op.Entry + " · SHA-256 " + PatchEngine.Hash(bytes));
                        data = BinaryPatches.Splice(bytes, op.BinaryEdits, MaximumEntrySize);
                        if (PatchEngine.Hash(data) != op.ResultSha256.ToLowerInvariant()) throw new InvalidDataException("Patched JAR member fingerprint mismatch.");
                    }
                    ValidateClass(data); changes.Add(op.Entry, data);
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
                if (output.Length > MaximumSize) throw new InvalidDataException("Patched JAR exceeds 128 MiB.");
                result = output.ToArray();
            }
            using (var output = new MemoryStream(result, false))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Read))
            {
                var members = Entries(archive);
                foreach (var pair in changes)
                    if (!members.ContainsKey(pair.Key) || !ReadEntry(members[pair.Key]).SequenceEqual(pair.Value)) throw new IOException("Patched JAR verification failed.");
            }
            beforeText = before.ToString(); afterText = after.ToString(); return result;
        }
    }
}
