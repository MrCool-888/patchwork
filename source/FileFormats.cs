using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Patchwork
{
    public static class FileFormats
    {
        public static string Kind(PatchOperation op)
        {
            if (op.Kind.StartsWith("managed", StringComparison.Ordinal)) return "managed";
            if (AsarPatches.IsOperation(op.Kind)) return "asar";
            if (op.Kind == "asarChecksum") return "checksum";
            if (JarPatches.IsOperation(op.Kind)) return "zip";
            if (op.Kind == "binarySplice" || op.Kind == "fileAdd" || op.Kind == "fileReplace") return "binary";
            return "text";
        }
        public static string LegacyKind(string file)
        {
            string extension = Path.GetExtension(file).ToLowerInvariant();
            return extension == ".dll" ? "managed" : extension == ".asar" ? "asar" : extension == ".dat" ? "checksum" : extension == ".jar" ? "zip" : extension == ".exe" ? "binary" : "text";
        }
        public static int Limit(string kind, string file)
        {
            switch (kind)
            {
                case "managed": return 8 * 1024 * 1024;
                case "asar": return AsarPatches.MaximumSize;
                case "checksum": return AsarChecksum.MaximumSize;
                case "zip": return JarPatches.MaximumSize;
                case "binary": return BinaryPatches.MaximumSize;
                case "text": return file.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? 16 * 1024 * 1024 : 8 * 1024 * 1024;
                default: throw new InvalidDataException("Unknown tracked file format: " + kind);
            }
        }
        public static bool ContainsClientCode(PatchOperation operation)
        {
            string kind = Kind(operation);
            if (kind != "text" && kind != "checksum") return true;
            return kind == "text" && new[] { ".js", ".mjs", ".cjs", ".ps1", ".bat", ".cmd", ".vbs", ".html", ".cs", ".java", ".py", ".lua" }.Contains(Path.GetExtension(operation.File).ToLowerInvariant());
        }
        public static string TrackedKind(JournalFile file) { return file.TransformKind ?? LegacyKind(file.RelativePath); }
        public static int TrackedLimit(JournalFile file)
        {
            int maximum = Limit(TrackedKind(file), file.RelativePath);
            if (file.SizeLimit != 0 && file.SizeLimit != maximum) throw new InvalidDataException("Invalid tracked file size limit.");
            return maximum;
        }
        public static void Compare(FileChange change, string root)
        {
            if (!change.BeforeExists || !change.AfterExists || change.TransformKind == "binary")
            {
                change.BeforeText = change.BeforeExists ? BinaryPatches.Describe(change.BeforeBytes) : "File absent";
                change.AfterText = change.AfterExists ? BinaryPatches.Describe(change.AfterBytes) : "File absent · remove the verified added file";
                return;
            }
            string before, after;
            switch (change.TransformKind)
            {
                case "managed": ManagedPatches.Compare(change.BeforeBytes, change.AfterBytes, root, out before, out after); break;
                case "asar": AsarPatches.Compare(change.BeforeBytes, change.AfterBytes, out before, out after); break;
                case "zip": JarPatches.Compare(change.BeforeBytes, change.AfterBytes, out before, out after); break;
                case "checksum": before = AsarChecksum.Describe(change.BeforeBytes); after = AsarChecksum.Describe(change.AfterBytes); break;
                default: before = PatchEngine.Decode(change.BeforeBytes); after = PatchEngine.Decode(change.AfterBytes); break;
            }
            change.BeforeText = before; change.AfterText = after;
        }
    }
}
