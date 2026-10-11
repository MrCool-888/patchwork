using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Patchwork
{
    public partial class PatchEngine
    {
        static byte[] ReadOriginal(string full, Dictionary<string, byte[]> originals, int maximum = 0)
        {
            byte[] bytes;
            return originals != null && originals.TryGetValue(full, out bytes) ? bytes : maximum == 0 ? Read(full) : Read(full, maximum);
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
                VerifyJournalState(entry.AfterExists, entry.AfterHash);
                byte[] original = ReadJournalBackup(journal, entry, false);
                if (Hash(ReadState(full, FileFormats.TrackedLimit(entry))) != entry.AfterHash) throw new InvalidOperationException("Update blocked: " + entry.RelativePath + " was changed outside Patchwork. No files were updated.");
                result.Add(full, original);
            }
            return result;
        }
        public Journal VerifiedAppliedSession(PatchBundle bundle, string root)
        {
            lock (transactionLock)
            {
                EnsureNoRecoveryPending(root);
                var journal = ActiveSession(root);
                if (journal == null) return null;
                if (journal.BundleId != bundle.Id) throw new InvalidOperationException("A different patch pack is applied to this folder.");
                var originals = VerifyOriginals(journal);
                if (Hash(ReadOriginal(Resolve(root, bundle.VersionFile), originals, VersionLimit(bundle))) != bundle.VersionSha256) throw new InvalidOperationException("The saved originals do not match this app version.");
                return journal;
            }
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
                var current = previous.Files.ToDictionary(x => Resolve(root, x.RelativePath), x => ReadState(Resolve(root, x.RelativePath), FileFormats.TrackedLimit(x)), StringComparer.OrdinalIgnoreCase);
                var plan = PreviewOriginal(bundle, root, selection, originals);
                var desired = plan.Files.ToDictionary(x => Resolve(root, x.RelativePath), StringComparer.OrdinalIgnoreCase);
                foreach (var entry in previous.Files)
                {
                    string full = Resolve(root, entry.RelativePath);
                    if (!desired.ContainsKey(full))
                    {
                        byte[] original = originals[full];
                        desired.Add(full, new FileChange { RelativePath = entry.RelativePath, TransformKind = FileFormats.TrackedKind(entry), SizeLimit = FileFormats.TrackedLimit(entry), BeforeBytes = original, BeforeExists = original != null, BeforeHash = Hash(original), AfterBytes = original, AfterExists = original != null, AfterHash = Hash(original), Details = new List<string> { "Remove patches no longer selected; retain verified originals." } });
                    }
                }
                plan.Files.Clear();
                foreach (var pair in desired)
                {
                    var change = pair.Value;
                    change.OriginalBeforeBytes = change.BeforeBytes;
                    change.OriginalBeforeHash = change.BeforeHash; change.OriginalBeforeExists = change.BeforeExists;
                    byte[] live;
                    if (current.TryGetValue(pair.Key, out live)) { change.BeforeBytes = live; change.BeforeHash = Hash(live); change.BeforeExists = live != null; }
                    FileFormats.Compare(change, root);
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
                VerifyJournalState(entry.AfterExists, entry.AfterHash); ReadJournalBackup(journal, entry, true);
                string actual = Hash(ReadState(full, FileFormats.TrackedLimit(entry)));
                if (actual != entry.PreviousHash && actual != entry.AfterHash)
                    throw new InvalidOperationException("Recovery blocked: " + entry.RelativePath + " was changed outside Patchwork.");
            }
            journal.State = "Restoring"; journal.RestoreToPrevious = true; Save(journal);
            try
            {
                foreach (var entry in journal.Files)
                {
                    string full = Resolve(journal.TargetRoot, entry.RelativePath);
                    int limit = FileFormats.TrackedLimit(entry);
                    if (Hash(ReadState(full, limit)) == entry.PreviousHash) continue;
                    WriteState(full, ReadJournalBackup(journal, entry, true), entry.AfterHash, limit);
                    if (Hash(ReadState(full, limit)) != entry.PreviousHash) throw new IOException("Previous-version recovery failed: " + entry.RelativePath);
                }
                CleanupDirectories(journal);
                parent.State = "Applied"; parent.SupersededBy = null; Save(parent);
                journal.State = "RolledBack"; Save(journal);
            }
            catch (Exception error) { journal.State = "RecoveryRequired"; journal.Error = error.Message; Save(journal); throw; }
        }
    }
}
