using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Patchwork
{
    public partial class PatchEngine
    {
        public static int VersionLimit(PatchBundle bundle) { return BinaryPatches.MaximumSize; }
        static byte[] ReadState(string file, int maximum)
        {
            if (maximum <= 0) throw new InvalidDataException("A bounded file size is required.");
            if (Directory.Exists(file)) throw new InvalidDataException("A regular file is required: " + file);
            return File.Exists(file) ? Read(file, maximum) : null;
        }
        static byte[] ReadOptionalOriginal(string file, Dictionary<string, byte[]> originals, int maximum)
        {
            byte[] bytes;
            return originals != null && originals.TryGetValue(file, out bytes) ? bytes : ReadState(file, maximum);
        }
        static void VerifyJournalState(bool exists, string hash)
        {
            if (exists) PatchBundle.ValidateHash(hash);
            else if (hash != null) throw new InvalidDataException("Absent files must not have a fingerprint.");
        }
        static void VerifyBytes(byte[] bytes, string hash, bool exists)
        {
            VerifyJournalState(exists, hash);
            if (exists != (bytes != null) || Hash(bytes) != hash) throw new InvalidDataException("Preview bytes failed verification.");
        }
        static byte[] ReadJournalBackup(Journal journal, JournalFile entry, bool previous)
        {
            bool exists = previous ? entry.PreviousExists : entry.BeforeExists;
            string hash = previous ? entry.PreviousHash : entry.BeforeHash;
            string backup = previous ? entry.PreviousBackupFile : entry.BackupFile;
            VerifyJournalState(exists, hash);
            if (!exists)
            {
                if (backup != null) throw new InvalidDataException("Absent original has an unexpected backup.");
                return null;
            }
            byte[] bytes = Read(Resolve(journal.DirectoryPath, backup), FileFormats.TrackedLimit(entry));
            if (Hash(bytes) != hash) throw new InvalidOperationException((previous ? "Previous-version backup" : "Backup") + " fingerprint mismatch: " + entry.RelativePath);
            return bytes;
        }
        static void WriteState(string destination, byte[] bytes, string expectedHash, int maximum)
        {
            if (bytes != null && bytes.Length > maximum) throw new InvalidDataException("File exceeds its tracked size limit.");
            if (Hash(ReadState(destination, maximum)) != expectedHash) throw new IOException("File changed before replacement: " + destination);
            if (bytes == null) { if (expectedHash != null) File.Delete(destination); return; }
            string temporary = destination + ".patchwork-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                DurableWrite(temporary, bytes);
                if (Hash(ReadState(destination, maximum)) != expectedHash) throw new IOException("File changed before replacement: " + destination);
                if (expectedHash == null) File.Move(temporary, destination);
                else File.Replace(temporary, destination, null);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        void EnsureParentDirectories(string root, string destination, Journal journal)
        {
            var missing = new List<string>(); string current = Path.GetDirectoryName(destination);
            string boundary = Path.GetFullPath(root).TrimEnd('\\');
            while (!Directory.Exists(current))
            {
                if (String.IsNullOrEmpty(current) || !current.StartsWith(boundary + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Parent folder escapes the target.");
                missing.Add(current); current = Path.GetDirectoryName(current);
            }
            if (missing.Count == 0) return;
            if (journal.CreatedDirectories == null) journal.CreatedDirectories = new List<string>();
            foreach (string directory in missing.AsEnumerable().Reverse())
            {
                string relative = directory.Substring(boundary.Length + 1); Resolve(boundary, relative);
                if (!journal.CreatedDirectories.Contains(relative, StringComparer.OrdinalIgnoreCase)) journal.CreatedDirectories.Add(relative);
            }
            Save(journal);
            foreach (string directory in missing.AsEnumerable().Reverse()) Directory.CreateDirectory(directory);
        }
        static void CleanupDirectories(Journal journal)
        {
            if (journal.CreatedDirectories == null) return;
            foreach (string relative in journal.CreatedDirectories.OrderByDescending(x => x.Length))
            {
                string directory = Resolve(journal.TargetRoot, relative);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
        }
    }
}
