using System;
using System.Collections.Generic;
using System.IO;

namespace Patchwork
{
    public static class AsarChecksum
    {
        public const int MaximumSize = 16 * 1024 * 1024;
        public static void Parse(PatchOperation op, Dictionary<string, object> raw)
        {
            if (!op.File.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("asarChecksum requires a .dat companion file.");
            op.Archive = Json.String(raw, "archive"); op.ArchiveSha256 = Json.String(raw, "archiveSha256");
            PatchBundle.ValidateRelative(op.Archive); PatchBundle.ValidateHash(op.ArchiveSha256);
            if (!op.Archive.EndsWith(".asar", StringComparison.OrdinalIgnoreCase) || Json.String(raw, "algorithm") != "xxhash32") throw new InvalidDataException("asarChecksum requires an ASAR and xxhash32 (seed zero).");
            object offset;
            if (!raw.TryGetValue("offset", out offset) || !(offset is int) || (int)offset < 0 || (int)offset > MaximumSize - 4) throw new InvalidDataException("Invalid checksum offset.");
            op.Offset = (int)offset;
        }
        static uint Rotate(uint value, int bits) { return (value << bits) | (value >> (32 - bits)); }
        static uint Round(uint value, uint lane) { return unchecked(Rotate(value + lane * 0x85ebca77U, 13) * 0x9e3779b1U); }
        // XXH32, seed zero, per the published xxHash specification.
        public static uint Hash(byte[] data)
        {
            unchecked
            {
                int offset = 0; uint hash;
                if (data.Length >= 16)
                {
                    uint a = 0x24234428U, b = 0x85ebca77U, c = 0, d = 0x61c8864fU;
                    do
                    {
                        a = Round(a, BitConverter.ToUInt32(data, offset)); b = Round(b, BitConverter.ToUInt32(data, offset + 4));
                        c = Round(c, BitConverter.ToUInt32(data, offset + 8)); d = Round(d, BitConverter.ToUInt32(data, offset + 12)); offset += 16;
                    } while (offset <= data.Length - 16);
                    hash = Rotate(a, 1) + Rotate(b, 7) + Rotate(c, 12) + Rotate(d, 18);
                }
                else hash = 0x165667b1U;
                hash += (uint)data.Length;
                while (offset <= data.Length - 4) { hash = Rotate(hash + BitConverter.ToUInt32(data, offset) * 0xc2b2ae3dU, 17) * 0x27d4eb2fU; offset += 4; }
                while (offset < data.Length) hash = Rotate(hash + data[offset++] * 0x165667b1U, 11) * 0x9e3779b1U;
                hash ^= hash >> 15; hash *= 0x85ebca77U; hash ^= hash >> 13; hash *= 0xc2b2ae3dU; return hash ^ (hash >> 16);
            }
        }
        public static string Describe(byte[] bytes)
        {
            if (bytes.Length < 4) throw new InvalidDataException("Missing checksum footer.");
            return "Binary companion file: " + bytes.Length + " bytes\nXXH32 at offset " + (bytes.Length - 4) + ": 0x" + BitConverter.ToUInt32(bytes, bytes.Length - 4).ToString("x8") + " (little endian)\nOnly the final four bytes change; all preceding bytes are preserved.\n";
        }
        public static byte[] Transform(byte[] original, FileChange archive, PatchOperation op)
        {
            if (original.Length > MaximumSize || op.Offset != original.Length - 4) throw new InvalidDataException("Checksum must occupy the companion file's final four bytes.");
            if (!PatchEngine.Hash(archive.BeforeBytes).Equals(op.ArchiveSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Checksum archive fingerprint mismatch.");
            if (BitConverter.ToUInt32(original, op.Offset) != Hash(archive.BeforeBytes)) throw new InvalidDataException("Original ASAR companion checksum mismatch.");
            var result = (byte[])original.Clone(); Buffer.BlockCopy(BitConverter.GetBytes(Hash(archive.AfterBytes)), 0, result, op.Offset, 4); return result;
        }
    }
}
