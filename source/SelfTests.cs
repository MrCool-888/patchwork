using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Patchwork
{
    public static class SelfTests
    {
        static int passed, failed;
        public static int Run(string dataRoot)
        {
            Directory.CreateDirectory(dataRoot);
            string root = Path.Combine(dataRoot, "test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Test("All demo patches preview, apply, and restore byte-for-byte", delegate {
                string data = Path.Combine(root, "roundtrip"); var engine = new PatchEngine(data);
                string target = TestFixtures.CreateDemo(data); var bundle = PatchBundle.Parse(TestFixtures.DemoRecipe());
                byte[] before = File.ReadAllBytes(Path.Combine(target, "settings.json"));
                var plan = engine.Preview(bundle, target, bundle.Patches.Select(x => x.Id));
                Assert(plan.Files.Count == 1 && plan.PatchIds.Count == 4, "Unexpected demo plan.");
                Assert(File.ReadAllBytes(Path.Combine(target, "settings.json")).SequenceEqual(before), "Preview wrote target files.");
                var journal = engine.Apply(plan);
                var config = Json.Parse(File.ReadAllText(Path.Combine(target, "settings.json")));
                Assert((string)Json.Object(config["appearance"])["theme"] == "midnight", "Theme not applied.");
                Assert((bool)Json.Object(config["privacy"])["telemetry"] == false, "Telemetry not changed.");
                Assert((bool)Json.Object(config["interface"])["showPromotions"] == false, "Promotions not changed.");
                Assert(journal.State == "Applied" && engine.History().Count == 1, "History missing.");
                engine.Restore(engine.History()[0]);
                Assert(File.ReadAllBytes(Path.Combine(target, "settings.json")).SequenceEqual(before), "Original bytes not restored.");
                Assert(engine.History()[0].State == "Restored", "Restore not journaled.");
            });
            Test("Unsupported file fingerprints refuse preview", delegate {
                string data = Path.Combine(root, "unsupported"); var engine = new PatchEngine(data); string target = TestFixtures.CreateDemo(data);
                File.AppendAllText(Path.Combine(target, "settings.json"), " ");
                ExpectFailure(() => engine.Preview(PatchBundle.Parse(TestFixtures.DemoRecipe()), target, new[] { "midnight-theme" }), "modified");
                Assert(engine.History().Count == 0, "Invalid preview created a transaction.");
            });
            Test("Files changed after preview refuse apply", delegate {
                string data = Path.Combine(root, "stale"); var engine = new PatchEngine(data); string target = TestFixtures.CreateDemo(data);
                var plan = engine.Preview(PatchBundle.Parse(TestFixtures.DemoRecipe()), target, new[] { "midnight-theme" });
                File.AppendAllText(Path.Combine(target, "settings.json"), " ");
                byte[] changed = File.ReadAllBytes(Path.Combine(target, "settings.json"));
                ExpectFailure(() => engine.Apply(plan), "after preview");
                Assert(File.ReadAllBytes(Path.Combine(target, "settings.json")).SequenceEqual(changed), "Stale apply wrote files.");
            });
            Test("Restore refuses to overwrite outside edits", delegate {
                string data = Path.Combine(root, "outside"); var engine = new PatchEngine(data); string target = TestFixtures.CreateDemo(data);
                var journal = engine.Apply(engine.Preview(PatchBundle.Parse(TestFixtures.DemoRecipe()), target, new[] { "midnight-theme" }));
                File.AppendAllText(Path.Combine(target, "settings.json"), " "); byte[] changed = File.ReadAllBytes(Path.Combine(target, "settings.json"));
                ExpectFailure(() => engine.Restore(journal), "changed outside");
                Assert(File.ReadAllBytes(Path.Combine(target, "settings.json")).SequenceEqual(changed), "External edits were overwritten.");
                Assert(engine.History()[0].State == "Applied", "Blocked restore changed transaction state.");
            });
            Test("Dependencies, conflicts, and overlapping mutations are rejected", delegate {
                string data = Path.Combine(root, "conflicts"); var engine = new PatchEngine(data); string target = TestFixtures.CreateDemo(data);
                var bundle = PatchBundle.Parse(TestFixtures.DemoRecipe()); bundle.Patches[0].Dependencies.Add("violet-accent");
                ExpectFailure(() => engine.Preview(bundle, target, new[] { "midnight-theme" }), "requires");
                bundle.Patches[0].Dependencies.Clear(); bundle.Patches[0].Conflicts.Add("violet-accent");
                ExpectFailure(() => engine.Preview(bundle, target, new[] { "midnight-theme", "violet-accent" }), "Conflicting");
                bundle.Patches[0].Conflicts.Clear(); bundle.Patches[1].Operations[0].Path = bundle.Patches[0].Operations[0].Path;
                ExpectFailure(() => engine.Preview(bundle, target, new[] { "midnight-theme", "violet-accent" }), "same JSON");
            });
            Test("Path traversal, invalid text targets, scripts, and false Proton claims are rejected", delegate {
                ExpectFailure(() => PatchEngine.Resolve(root, "../escape.json"), "Unsafe");
                ExpectFailure(() => PatchEngine.Resolve(root, "C:\\escape.json"), "relative");
                ExpectFailure(() => PatchEngine.Resolve(root, "settings.json:stream"), "relative");
                string recipe = TestFixtures.DemoRecipe();
                ExpectFailure(() => PatchBundle.Parse(recipe.Replace("\"settings.json\"", "\"app.exe\"")), "Only UTF-8");
                ExpectFailure(() => PatchBundle.Parse(recipe.Replace("\"patchwork-sandbox\"", "\"proton-vpn\"")), "fingerprint ProtonVPN.Client.exe");
                ExpectFailure(() => PatchBundle.Parse(recipe.Replace("\"jsonSet\"", "\"runCommand\"")), "Unknown operation");
            });
            Test("Multi-file failure rolls back already-written files", delegate {
                string data = Path.Combine(root, "rollback"); var engine = new PatchEngine(data); string target = TestFixtures.CreateDemo(data);
                string css = "body { color: red; }\r\n"; File.WriteAllText(Path.Combine(target, "theme.css"), css, new UTF8Encoding(false));
                var bundle = PatchBundle.Parse(TestFixtures.DemoRecipe());
                bundle.Patches[0].Operations.Add(new PatchOperation { Kind = "textReplace", File = "theme.css", Sha256 = PatchEngine.Hash(Encoding.UTF8.GetBytes(css)), Find = "red", Replacement = "violet", Count = 1 });
                var plan = engine.Preview(bundle, target, new[] { "midnight-theme" });
                engine.BeforeWriteForTest = index => { if (index == 1) throw new IOException("Simulated interrupted write"); };
                ExpectFailure(() => engine.Apply(plan), "Original files were restored");
                Assert(File.ReadAllText(Path.Combine(target, "settings.json")) == TestFixtures.DemoConfig, "First file was not rolled back.");
                Assert(File.ReadAllText(Path.Combine(target, "theme.css")) == css, "Second file changed.");
                Assert(engine.History()[0].State == "RolledBack", "Rollback state missing.");
            });
            Test("Prepared journals recover without overwriting unrelated files", delegate {
                string data = Path.Combine(root, "recovery"); var engine = new PatchEngine(data); string target = TestFixtures.CreateDemo(data);
                var journal = engine.Apply(engine.Preview(PatchBundle.Parse(TestFixtures.DemoRecipe()), target, new[] { "midnight-theme" }));
                journal.State = "Prepared"; File.WriteAllText(Path.Combine(journal.DirectoryPath, "journal.json"), Json.Pretty(journal));
                engine.Restore(engine.History()[0]);
                Assert(File.ReadAllText(Path.Combine(target, "settings.json")) == TestFixtures.DemoConfig, "Recovery failed.");
            });
            Test("Tampered backups refuse restoration", delegate {
                string data = Path.Combine(root, "backup-corrupt"); var engine = new PatchEngine(data); string target = TestFixtures.CreateDemo(data);
                var journal = engine.Apply(engine.Preview(PatchBundle.Parse(TestFixtures.DemoRecipe()), target, new[] { "midnight-theme" }));
                File.AppendAllText(Path.Combine(journal.DirectoryPath, journal.Files[0].BackupFile), " ");
                ExpectFailure(() => engine.Restore(journal), "Backup fingerprint mismatch");
            });
            Test("UTF-8 BOM and exact text replacement round-trip", delegate {
                string data = Path.Combine(root, "bom"); var engine = new PatchEngine(data); string target = TestFixtures.CreateDemo(data);
                byte[] original = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("hello world\r\n")).ToArray();
                File.WriteAllBytes(Path.Combine(target, "hello.txt"), original);
                var bundle = PatchBundle.Parse(TestFixtures.DemoRecipe()); bundle.Patches[0].Operations.Clear();
                var operation = new PatchOperation { Kind = "textReplace", File = "hello.txt", Sha256 = PatchEngine.Hash(original), Find = "world", Replacement = "Patchwork", Count = 1 }; bundle.Patches[0].Operations.Add(operation);
                var plan = engine.Preview(bundle, target, new[] { "midnight-theme" });
                Assert(plan.Files[0].AfterBytes[0] == 0xef, "BOM not retained.");
                operation.Count = 2; ExpectFailure(() => engine.Preview(bundle, target, new[] { "midnight-theme" }), "text matches");
                var journal = engine.Apply(plan); engine.Restore(journal);
                Assert(File.ReadAllBytes(Path.Combine(target, "hello.txt")).SequenceEqual(original), "BOM round-trip changed bytes.");
            });
            Test("Already-patched targets and repeated restores are refused", delegate {
                string data = Path.Combine(root, "idempotency"); var engine = new PatchEngine(data); string target = TestFixtures.CreateDemo(data); var bundle = PatchBundle.Parse(TestFixtures.DemoRecipe());
                var journal = engine.Apply(engine.Preview(bundle, target, new[] { "midnight-theme" }));
                ExpectFailure(() => engine.Preview(bundle, target, new[] { "midnight-theme" }), "already modified");
                engine.Restore(journal); ExpectFailure(() => engine.Restore(journal), "already been restored");
            });
            Test("Concurrent app instance cannot start a transaction", delegate {
                string data = Path.Combine(root, "lock"); var engine = new PatchEngine(data); string target = TestFixtures.CreateDemo(data);
                var plan = engine.Preview(PatchBundle.Parse(TestFixtures.DemoRecipe()), target, new[] { "midnight-theme" });
                using (var stream = new FileStream(Path.Combine(data, "transaction.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) ExpectFailure(() => engine.Apply(plan), "Another Patchwork");
            });
            Test("Managed operations execute correctly and DLLs restore exactly", () => ManagedTests.RuntimeRoundTrip(root));
            Test("Managed signatures, call counts, value types and conflicts are enforced", () => ManagedTests.Rejections(root));
            Test("Worker apply/restore, stale preview and request boundaries", () => ManagedTests.WorkerRequests(root));
            Test("Updates compare versions and exclude draft/prerelease releases", () => UpdaterTests.Versions());
            Test("Updates reject unexpected repositories, assets, digests and sizes", () => UpdaterTests.UntrustedAssets());
            Test("Installer download verification rejects tampering and non-executables", () => UpdaterTests.Tampering());
            Test("Windows GitHub transport rejects unsafe hosts and invalid download limits", () => UpdaterTests.TransportBoundaries());
            Test("Update setup releases its working folder and passes the old process identity", () => UpdaterTests.SetupHandoff(root));
            Test("Fresh library is empty; separate imports persist without duplication", () => UpdaterTests.ImportOnly(root));
            Test("Conditional selectors, projections and generic inherited overrides execute and restore", () => SelectorTests.Runtime(root));
            Test("Selectors reject invalid conditions, signatures, call counts and mappings", () => SelectorTests.Rejections(root));
            Test("Pack and patch versions validate, display and persist in history", () => UpdaterTests.VersionMetadata(root));
            Test("GitHub source links, preferences, persistence and hourly scheduling", () => SourceTests.Registration(root));
            Test("Source release channels, asset locations, bounds and downloaded pack integrity", () => SourceTests.ReleasesAndIntegrity());
            Test("Source upgrades preserve history, ownership and versions without downgrade", () => SourceTests.CatalogAndHistory(root));
            Test("Source downloads cache unchanged assets and reject invalid batches atomically", () => SourceTests.DownloadCacheAndAtomicValidation(root));
            Test("Source management interface displays repositories and versions", () => SourceTests.SourcesInterface(root));
            Test("Applied patches update and remove old edits without manual restore", () => PatchUpdateTests.UpdateRoundTrip(root));
            Test("Patch updates reject outside edits, corrupt originals, upstream changes and stale sessions", () => PatchUpdateTests.UpdateRejections(root));
            Test("Failed and interrupted updates recover previous patches; committed history stays consistent", () => PatchUpdateTests.RollbackAndRecovery(root));
            Test("Patched version files, old journals and worker update requests are supported", () => PatchUpdateTests.PatchedVersionFileAndWorker(root));
            Test("Pack metadata updates retain exact original backups", () => PatchUpdateTests.MetadataUpdate(root));
            Test("Theme resources compose, execute, save color choices and update without restore", () => ThemeTests.RuntimeAndUpdates(root));
            Test("Theme options reject XAML injection, unknown choices, resource types and conflicts", () => ThemeTests.Validation(root));
            Test("Worker freezes color choices and theme controls save preferences", () => ThemeTests.WorkerAndInterface(root));
            Test("UI hiding and enum filtering execute with branches, nulls and paid preservation", () => PresentationTests.Runtime(root));
            Test("Presentation operations refuse wrong UI receivers, parameters and enum values", () => PresentationTests.Validation(root));
            Test("Embedded client fallback preserves original behavior and after hooks", () => ModuleTests.Runtime(root));
            Test("Embedded client modules refuse invalid hashes, entries and signatures", () => ModuleTests.Validation(root));
            Test("ASAR previews compose, update, recover and restore archives larger than 8 MB", () => AsarTests.Transactions(root));
            Test("ASAR parsing, entry integrity, counts, conflicts and paths are bounded", () => AsarTests.Boundaries(root));
            Test("Running Blitz processes block archive transactions", () => AsarTests.RunningClient(root));
            Test("ASAR companion checksums compose, upgrade old sessions, roll back and restore both files", () => AsarTests.Checksums(root));
            Test("Blitz folder detection requires both executable and archive and respects candidate order", delegate {
                string missing = Path.Combine(root, "blitz-missing"), executableOnly = Path.Combine(root, "blitz-exe-only"), archiveOnly = Path.Combine(root, "blitz-asar-only");
                string local = Path.Combine(root, "blitz-local"), system = Path.Combine(root, "blitz-system");
                foreach (string folder in new[] { executableOnly, archiveOnly, local, system }) Directory.CreateDirectory(Path.Combine(folder, "resources"));
                foreach (string folder in new[] { executableOnly, local, system }) File.WriteAllText(Path.Combine(folder, "Blitz.exe"), "fixture");
                foreach (string folder in new[] { archiveOnly, local, system }) File.WriteAllText(Path.Combine(folder, "resources", "app.asar"), "fixture");
                Assert(MainController.FindBlitzFolder(new[] { missing, executableOnly, archiveOnly }) == null, "Incomplete installation was selected.");
                Assert(MainController.FindBlitzFolder(new[] { missing, executableOnly, archiveOnly, local, system }) == local, "Per-user installation was not selected first.");
                Assert(MainController.FindBlitzFolder(new[] { missing, system }) == system, "System installation was not detected.");
            });
            string result = passed + " passed; " + failed + " failed.\r\n";
            Console.WriteLine(result); File.WriteAllText(Path.Combine(dataRoot, "test-results.txt"), result);
            return failed == 0 ? 0 : 1;
        }
        static void Test(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
        }
        static void Assert(bool okay, string message) { if (!okay) throw new Exception(message); }
        static void ExpectFailure(Action action, string expected)
        {
            try { action(); }
            catch (Exception error) { if (error.Message.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0) return; throw new Exception("Expected '" + expected + "', got: " + error.Message); }
            throw new Exception("Expected rejection: " + expected);
        }
    }
}
