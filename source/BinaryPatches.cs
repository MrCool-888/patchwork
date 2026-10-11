using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Patchwork
{
    public class BinaryEdit
    {
        public int Offset, Length;
        public string Data;
    }
    // These are data transformations. Payloads are never loaded or executed in Patchwork.
    public static class BinaryPatches
    {
        public const int MaximumSize = 256 * 1024 * 1024;
        public static bool IsOperation(string kind) { return kind == "binarySplice" || kind == "jarEntrySplice" || kind == "jarEntryAdd"; }
        public static int Integer(Dictionary<string, object> raw, string name, int maximum)
        {
            object value;
            if (!raw.TryGetValue(name, out value) || !(value is int) || (int)value < 0 || (int)value > maximum) throw new InvalidDataException("Invalid integer " + name + ".");
            return (int)value;
        }
        public static byte[] Data(string value)
        {
            if (value.Length > 350000) throw new InvalidDataException("Binary payload exceeds 256 KiB.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(value); } catch (FormatException) { throw new InvalidDataException("Invalid base64 binary payload."); }
            if (bytes.Length > 262144) throw new InvalidDataException("Binary payload exceeds 256 KiB.");
            return bytes;
        }
        public static void Parse(PatchOperation op, Dictionary<string, object> raw)
        {
            bool jar = op.Kind != "binarySplice";
            if (!op.File.EndsWith(jar ? ".jar" : ".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Binary operation has an unsupported target extension.");
            if (jar)
            {
                op.Entry = Json.String(raw, "entry"); JarPatches.ValidateEntry(op.Entry);
                if (!op.Entry.EndsWith(".class", StringComparison.Ordinal)) throw new InvalidDataException("JAR edits require a .class member.");
                op.EntrySha256 = Json.String(raw, "entrySha256"); PatchBundle.ValidateHash(op.EntrySha256);
            }
            if (op.Kind == "jarEntryAdd")
            {
                op.BinaryData = Json.String(raw, "dataBase64");
                byte[] data = Data(op.BinaryData);
                if (!PatchEngine.Hash(data).Equals(op.EntrySha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Added class payload fingerprint mismatch.");
                JarPatches.ValidateClass(data); return;
            }
            foreach (object item in Json.Array(raw, "edits"))
            {
                var edit = Json.Object(item);
                var value = new BinaryEdit { Offset = Integer(edit, "offset", MaximumSize), Length = Integer(edit, "length", MaximumSize), Data = Json.String(edit, "dataBase64") };
                if (value.Length == 0 && Data(value.Data).Length == 0) throw new InvalidDataException("Empty binary edit.");
                Data(value.Data); op.BinaryEdits.Add(value);
            }
            if (op.BinaryEdits.Count == 0 || op.BinaryEdits.Count > 256 || op.BinaryEdits.Sum(x => (long)Data(x.Data).Length) > 524288) throw new InvalidDataException("Binary edits exceed bounded size.");
            op.ResultSha256 = Json.String(raw, "resultSha256"); PatchBundle.ValidateHash(op.ResultSha256);
        }
        public static byte[] Splice(byte[] original, List<BinaryEdit> edits, int maximum)
        {
            var ordered = edits.OrderBy(x => x.Offset).ToList();
            long length = original.Length + ordered.Sum(x => (long)Data(x.Data).Length - x.Length);
            if (length < 0 || length > maximum) throw new InvalidDataException("Patched binary exceeds its size limit.");
            using (var output = new MemoryStream((int)length))
            {
                int cursor = 0, previous = -1;
                foreach (var edit in ordered)
                {
                    if (edit.Offset < cursor || edit.Offset == previous || (long)edit.Offset + edit.Length > original.Length) throw new InvalidDataException("Overlapping or out-of-bounds binary edits.");
                    output.Write(original, cursor, edit.Offset - cursor);
                    byte[] data = Data(edit.Data); output.Write(data, 0, data.Length);
                    cursor = edit.Offset + edit.Length; previous = edit.Offset;
                }
                output.Write(original, cursor, original.Length - cursor); return output.ToArray();
            }
        }
        public static byte[] Transform(byte[] original, List<PatchOperation> operations, out string beforeText, out string afterText)
        {
            var unique = operations.GroupBy(x => Json.Serializer.Serialize(x)).Select(x => x.First()).ToList();
            if (unique.Count != 1) throw new InvalidDataException("Conflicting binary file edits.");
            byte[] result = Splice(original, unique[0].BinaryEdits, MaximumSize);
            if (!PatchEngine.Hash(result).Equals(unique[0].ResultSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Patched binary fingerprint mismatch.");
            beforeText = Describe(original); afterText = Describe(result) + "\nExecutable modified; publisher signature may be invalid.\n";
            foreach (var edit in unique[0].BinaryEdits) afterText += "Offset " + edit.Offset + ": replace " + edit.Length + " byte(s) with " + Data(edit.Data).Length + " byte(s).\n";
            return result;
        }
        public static string Describe(byte[] data) { return "Binary file · " + data.Length + " bytes · SHA-256 " + PatchEngine.Hash(data); }
    }
}
