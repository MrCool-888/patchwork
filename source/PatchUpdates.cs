using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Patchwork
{
    public partial class PatchEngine
    {
        static byte[] ReadOriginal(string full, Dictionary<string, byte[]> originals)
        {
            byte[] bytes;
            return originals != null && originals.TryGetValue(full, out bytes) ? bytes : Read(full);
        }
        static bool SameRoot(string left, string right)
        {
            return Path.GetFullPath(left).TrimEnd('\\').Equals(Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        public Journal ActiveSession(string root)
        {
            var active = History().Where(x => SameRoot(x.TargetRoot, root) && x.State == "Applied").ToList();
            if (active.Count > 1) throw new InvalidOperationException("This folder has multiple applied sessions. Restore them before updating patches.");
            return active.SingleOrDefault();
        }
        Dictionary<string, byte[]> VerifyOriginals(Journal journal)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in journal.Files)
            {
                string full = Resolve(journal.TargetRoot, entry.RelativePath);
                if (result.ContainsKey(full)) throw new InvalidDataException("Duplicate backup file entry.");
                PatchBundle.ValidateHash(entry.BeforeHash); PatchBundle.ValidateHash(entry.AfterHash);
                byte[] original = Read(Resolve(journal.DirectoryPath, entry.BackupFile));
                if (Hash(original) != entry.BeforeHash) throw new InvalidOperationException("Backup fingerprint mismatch: " + entry.RelativePath);
                if (Hash(Read(full)) != entry.AfterHash) throw new InvalidOperationException("Update blocked: " + entry.RelativePath + " was changed outside Patchwork. No files were updated.");
                result.Add(full, original);
            }
            return result;
        }
        public Journal VerifiedAppliedSession(PatchBundle bundle, string root)
        {
            EnsureNoRecoveryPending(root);
            var journal = ActiveSession(root);
            if (journal == null) return null;
            if (journal.BundleId != bundle.Id) throw new InvalidOperationException("A different patch pack is applied to this folder.");
            var originals = VerifyOriginals(journal);
            if (Hash(ReadOriginal(Resolve(root, bundle.VersionFile), originals)) != bundle.VersionSha256) throw new InvalidOperationException("The saved originals do not match this app version.");
            return journal;
        }
        public PatchPlan Preview(PatchBundle bundle, string root, IEnumerable<string> selection)
        {
            return Preview(bundle, root, selection, null);
        }
        public PatchPlan Preview(PatchBundle bundle, string root, IEnumerable<string> selection, Dictionary<string, string> options)
        {
            var ids = selection.ToList(); Dictionary<string, string> choices;
            var resolved = ThemeOptions.Resolve(bundle, ids, options, out choices);
            var plan = PreviewResolved(resolved, root, ids); plan.Options = choices; return plan;
        }
        PatchPlan PreviewResolved(PatchBundle bundle, string root, IEnumerable<string> selection)
        {
            lock (transactionLock)
            {
                if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Choose an existing target folder.");
                root = Path.GetFullPath(root).TrimEnd('\\');
                EnsureNoRecoveryPending(root);
                var previous = ActiveSession(root);
                if (previous == null) return PreviewOriginal(bundle, root, selection, null);
                if (previous.BundleId != bundle.Id) throw new InvalidOperationException("A different patch pack is applied to this folder. Restore it before changing sources or packs.");
                var originals = VerifyOriginals(previous);
                var current = originals.ToDictionary(x => x.Key, x => Read(x.Key), StringComparer.OrdinalIgnoreCase);
                var plan = PreviewOriginal(bundle, root, selection, originals);
                var desired = plan.Files.ToDictionary(x => Resolve(root, x.RelativePath), StringComparer.OrdinalIgnoreCase);
                foreach (var entry in previous.Files)
                {
                    string full = Resolve(root, entry.RelativePath);
                    if (!desired.ContainsKey(full))
                    {
                        byte[] original = originals[full];
                        desired.Add(full, new FileChange { RelativePath = entry.RelativePath, BeforeBytes = original, BeforeHash = Hash(original), AfterBytes = original, AfterHash = Hash(original), Details = new List<string> { "Remove patches no longer selected; retain verified originals." } });
                    }
                }
                plan.Files.Clear();
                foreach (var pair in desired)
                {
                    var change = pair.Value;
                    change.OriginalBeforeBytes = change.BeforeBytes;
                    change.OriginalBeforeHash = change.BeforeHash;
                    byte[] live;
                    if (current.TryGetValue(pair.Key, out live)) { change.BeforeBytes = live; change.BeforeHash = Hash(live); }
                    if (change.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        string beforeText, afterText;
                        ManagedPatches.Compare(change.BeforeBytes, change.AfterBytes, root, out beforeText, out afterText);
                        change.BeforeText = beforeText; change.AfterText = afterText;
                    }
                    else { change.BeforeText = Decode(change.BeforeBytes); change.AfterText = Decode(change.AfterBytes); }
                    plan.Files.Add(change);
                }
                if (plan.Files.All(x => x.BeforeHash == x.AfterHash) && previous.BundleSha256 == plan.BundleSha256)
                    throw new InvalidOperationException("These files are already modified by this patch selection. There are no new changes.");
                plan.PreviousJournalId = previous.Id;
                return plan;
            }
        }
        void RestorePreviousInternal(Journal journal)
        {
            if (String.IsNullOrEmpty(journal.PreviousJournalId)) throw new InvalidDataException("Missing previous patch session.");
            var parent = History().SingleOrDefault(x => x.Id == journal.PreviousJournalId);
            if (parent == null || !SameRoot(parent.TargetRoot, journal.TargetRoot) || parent.BundleId != journal.BundleId)
                throw new InvalidDataException("The previous session is missing or does not match this update.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in journal.Files)
            {
                string full = Resolve(journal.TargetRoot, entry.RelativePath);
                if (!paths.Add(full)) throw new InvalidDataException("Duplicate backup file entry.");
                PatchBundle.ValidateHash(entry.PreviousHash); PatchBundle.ValidateHash(entry.AfterHash);
                if (Hash(Read(Resolve(journal.DirectoryPath, entry.PreviousBackupFile))) != entry.PreviousHash)
                    throw new InvalidOperationException("Previous-version backup fingerprint mismatch: " + entry.RelativePath);
                string actual = Hash(Read(full));
                if (actual != entry.PreviousHash && actual != entry.AfterHash)
                    throw new InvalidOperationException("Recovery blocked: " + entry.RelativePath + " was changed outside Patchwork.");
            }
            journal.State = "Restoring"; journal.RestoreToPrevious = true; Save(journal);
            try
            {
                foreach (var entry in journal.Files)
                {
                    string full = Resolve(journal.TargetRoot, entry.RelativePath);
                    if (Hash(Read(full)) == entry.PreviousHash) continue;
                    AtomicReplace(full, Read(Resolve(journal.DirectoryPath, entry.PreviousBackupFile)), entry.AfterHash);
                    if (Hash(Read(full)) != entry.PreviousHash) throw new IOException("Previous-version recovery failed: " + entry.RelativePath);
                }
                parent.State = "Applied"; parent.SupersededBy = null; Save(parent);
                journal.State = "RolledBack"; Save(journal);
            }
            catch (Exception error) { journal.State = "RecoveryRequired"; journal.Error = error.Message; Save(journal); throw; }
        }
    }
}
