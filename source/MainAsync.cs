using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Patchwork
{
    public partial class MainController
    {
        sealed class Inspection
        {
            internal PatchBundle Bundle;
            internal string Target;
            internal int Revision;
        }
        sealed class TargetResult
        {
            internal string Text;
            internal bool Match;
        }
        int sessionRevision, historyRevision;
        int hintDetectionRevision;
        bool inspectingSession;
        Inspection pendingInspection;
        PatchBundle selectionBundle;
        int selectionRevision;
        Task sessionTask = Task.FromResult(0), historyTask = Task.FromResult(0), selectionTask = Task.FromResult(0);
#if TEST_BUILD
        internal Action InspectionGateForTest, PreviewGateForTest, HistoryGateForTest, RestoreGateForTest;
        internal Func<string, bool> ConfirmDialogForTest;
#endif
        void InitializeAsync()
        {
            window.Loaded += async delegate {
                StartAppliedSelection();
                StartSessionInspection();
                try
                {
                    int count = await Task.Run(() => engine.History().Count(x => x.State == "Prepared" || x.State == "Restoring" || x.State == "RecoveryRequired"));
                    if (!closed && !busy && count > 0) Notify(count + " unfinished transaction(s). Open History & restore to recover.", true);
                }
                catch (Exception error) { if (!closed && !busy) Notify("Could not inspect recovery history: " + error.Message, true); }
            };
            window.Closed += delegate { sessionRevision++; historyRevision++; pendingInspection = null; };
        }
        void QueueAppliedSelection(PatchBundle bundle)
        {
            selectionRevision++; selectionBundle = bundle;
            StartAppliedSelection();
        }
        void StartAppliedSelection()
        {
            if (closed || !window.IsLoaded || selectionBundle == null) return;
            var bundle = selectionBundle; selectionBundle = null;
            string target = targets[bundle.Id]; int revision = selectionRevision;
            selectionTask = RestoreAppliedSelection(bundle, target, revision);
        }
        async Task RestoreAppliedSelection(PatchBundle bundle, string target, int revision)
        {
            try
            {
                var active = await Task.Run(() => String.IsNullOrEmpty(target) ? null : engine.ActiveSession(target));
                if (closed || busy || revision != selectionRevision || current != bundle || targets[bundle.Id] != target || selected.Count != 0 || active == null || active.BundleId != bundle.Id || active.PatchIds == null) return;
                foreach (string id in active.PatchIds.Where(x => bundle.Patches.Any(p => p.Id == x && p.Ready))) selected.Add(id);
                RenderPatches(); UpdateSession();
            }
            catch { /* Saved selection is optional; fingerprint inspection reports target errors. */ }
        }
        void QueueSessionInspection(PatchBundle bundle, string target)
        {
            pendingInspection = new Inspection { Bundle = bundle, Target = target, Revision = ++sessionRevision };
            Control<TextBlock>("TargetStatus").Text = "Checking target fingerprints…";
            StartSessionInspection();
        }
        void StartSessionInspection()
        {
            if (closed || !window.IsLoaded || busy || inspectingSession || pendingInspection == null) return;
            sessionTask = InspectPendingSessions();
        }
        async Task InspectPendingSessions()
        {
            inspectingSession = true;
            try
            {
                while (!closed && !busy && pendingInspection != null)
                {
                    var snapshot = pendingInspection; pendingInspection = null;
                    TargetResult result = await Task.Run(() => {
                        try
                        {
#if TEST_BUILD
                            if (InspectionGateForTest != null) InspectionGateForTest();
#endif
                            return InspectTarget(snapshot.Bundle, snapshot.Target);
                        }
                        catch (Exception error) { return new TargetResult { Text = error.Message }; }
                    });
                    if (closed || busy || snapshot.Revision != sessionRevision || current != snapshot.Bundle || targets[current.Id] != snapshot.Target) continue;
                    var status = Control<TextBlock>("TargetStatus");
                    status.Text = result.Text; status.Foreground = Color(result.Match ? "#85DCC0" : "#E9C985");
                }
            }
            catch (Exception error) { if (!closed && !busy) { Control<TextBlock>("TargetStatus").Text = error.Message; Control<TextBlock>("TargetStatus").Foreground = Color("#E9C985"); } }
            finally { inspectingSession = false; }
        }
        TargetResult InspectTarget(PatchBundle bundle, string target)
        {
            try
            {
                if (!bundle.Patches.Any(x => x.Ready))
                {
                    string detected = ProtonVersion(target);
                    return new TargetResult { Text = detected.Length == 0 ? "Proton executable not found. Import a compatible patch file." : "Detected v" + detected + ". Import a compatible Proton patch file." };
                }
                var applied = engine.VerifiedAppliedSession(bundle, target);
                if (applied != null) return new TargetResult { Match = true, Text = "Applied pack " + VersionLabel(applied.PackVersion) + ". Preview to update from verified originals; no manual restore needed." };
                bool match = PatchEngine.Hash(PatchEngine.Read(PatchEngine.Resolve(target, bundle.VersionFile), PatchEngine.VersionLimit(bundle))).Equals(bundle.VersionSha256, StringComparison.OrdinalIgnoreCase);
                bool changed = bundle.Patches.SelectMany(x => x.Operations).GroupBy(x => x.File).Any(group => {
                    string path = PatchEngine.Resolve(target, group.Key);
                    bool added = group.Any(x => x.Kind == "fileAdd");
                    return added ? File.Exists(path) || Directory.Exists(path) : !File.Exists(path) || !PatchEngine.Hash(PatchEngine.Read(path, FileFormats.Limit(FileFormats.Kind(group.First()), group.Key))).Equals(group.First().Sha256, StringComparison.OrdinalIgnoreCase);
                });
                return new TargetResult { Match = match && !changed, Text = !match ? "Version fingerprint mismatch. Choose a compatible folder." : changed ? "Files differ and no matching applied session was found. Choose a compatible folder or recover its original files." : "Target fingerprints match. Ready to preview." };
            }
            catch (Exception error) { return new TargetResult { Text = error.Message }; }
        }
        void QueueHintDetection(PatchBundle bundle)
        {
            int revision = ++hintDetectionRevision;
            Task.Run(() => Targeting.FindTargets(bundle, engine)).ContinueWith(task => window.Dispatcher.BeginInvoke(new Action(() => {
                if (closed || revision != hintDetectionRevision || current != bundle || !String.IsNullOrEmpty(targets[bundle.Id])) return;
                if (task.IsFaulted) { Control<TextBlock>("TargetStatus").Text = "Target hints could not be checked. Choose a folder manually."; return; }
                var found = task.Result;
                if (found.Count == 1)
                {
                    targets[bundle.Id] = found[0]; preview = null; SaveSettings(); UpdateSession();
                }
                else Control<TextBlock>("TargetStatus").Text = found.Count == 0 ? "No hinted folder matched this app version. Choose a folder manually." : "More than one hinted folder matched. Choose a folder manually.";
            })));
        }
        void RenderHistory()
        {
            if (closed) return;
            int revision = ++historyRevision;
            Control<StackPanel>("HistoryList").Children.Clear();
            Control<StackPanel>("HistoryList").Children.Add(Text("Loading patch history…", 14, "#929DB1"));
            historyTask = RefreshHistory(revision);
        }
        async Task RefreshHistory(int revision)
        {
            try
            {
                var journals = await Task.Run(() => {
#if TEST_BUILD
                    if (HistoryGateForTest != null) HistoryGateForTest();
#endif
                    return engine.History();
                });
                if (!closed && revision == historyRevision && page == "history") RenderHistory(journals);
            }
            catch (Exception error) { if (!closed && revision == historyRevision && page == "history") Notify("Could not load patch history: " + error.Message, true); }
        }
    }
}
