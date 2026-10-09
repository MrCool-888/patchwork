using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Patchwork
{
    public static class PatchUpdateTests
    {
        static void Assert(bool value) { if (!value) throw new Exception("Applied patch update check failed."); }
        static void Reject(Action action, string fragment)
        {
            try { action(); } catch (Exception error) { if (error.Message.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return; throw; }
            throw new Exception("Unsafe patch update was accepted.");
        }
        static void Save(Journal journal) { File.WriteAllText(Path.Combine(journal.DirectoryPath, "journal.json"), Json.Pretty(journal)); }
        static PatchBundle Multi(string target, string version, bool old)
        {
            string text = old ? "legacy.txt" : "new.txt";
            var raw = Json.Parse(SourceTests.Bundle(version, old ? "midnight" : "dark").Content);
            var patch = Json.Object(Json.Array(raw, "patches")[0]);
            patch["operations"] = new object[] { Json.Array(patch, "operations")[0], new Dictionary<string, object> { { "kind", "textReplace" }, { "file", text }, { "sha256", PatchEngine.Hash(Encoding.UTF8.GetBytes("original")) }, { "find", "original" }, { "replacement", old ? "old patch" : "new patch" }, { "count", 1 } } };
            return PatchBundle.Parse(Json.Pretty(raw));
        }
        public static void UpdateRoundTrip(string root)
        {
            string data = Path.Combine(root, "applied-update"); var engine = new PatchEngine(data); string target = TestFixtures.CreateDemo(data);
            foreach (string file in new[] { "legacy.txt", "new.txt" }) File.WriteAllText(Path.Combine(target, file), "original", new UTF8Encoding(false));
            var old = Multi(target, "1.0.0", true); var first = engine.Apply(engine.Preview(old, target, new[] { "midnight-theme" }));
            var newer = Multi(target, "1.1.0", false); var plan = engine.Preview(newer, target, new[] { "midnight-theme" });
            Assert(plan.PreviousJournalId == first.Id && File.ReadAllText(Path.Combine(target, "legacy.txt")) == "old patch");
            Assert(plan.Files.Single(x => x.RelativePath == "settings.json").BeforeText.Contains("midnight") && plan.Files.Single(x => x.RelativePath == "settings.json").AfterText.Contains("dark"));
            var second = engine.Apply(plan);
            Assert(File.ReadAllText(Path.Combine(target, "legacy.txt")) == "original" && File.ReadAllText(Path.Combine(target, "new.txt")) == "new patch");
            Assert(engine.History().Single(x => x.Id == first.Id).State == "Superseded" && engine.ActiveSession(target).Id == second.Id);
            Assert(second.PreviousJournalId == first.Id && second.PackVersion == "1.1.0" && second.PatchIds.Single() == "midnight-theme");
            Reject(() => engine.Restore(first), "newer patch session");
            var third = engine.Apply(engine.Preview(SourceTests.Bundle("1.2.0"), target, new[] { "violet-accent" }));
            Assert(File.ReadAllText(Path.Combine(target, "settings.json")).Contains("system") && File.ReadAllText(Path.Combine(target, "new.txt")) == "original");
            engine.Restore(third);
            Assert(File.ReadAllText(Path.Combine(target, "settings.json")) == TestFixtures.DemoConfig && engine.ActiveSession(target) == null);
            Assert(new[] { "legacy.txt", "new.txt" }.All(x => File.ReadAllText(Path.Combine(target, x)) == "original"));
        }
        public static void UpdateRejections(string root)
        {
            string data = Path.Combine(root, "update-rejections"); var engine = new PatchEngine(data); string target = TestFixtures.CreateDemo(data);
            var old = SourceTests.Bundle("1.0.0"); var first = engine.Apply(engine.Preview(old, target, new[] { "midnight-theme" }));
            var newer = SourceTests.Bundle("1.1.0", "dark"); var plan = engine.Preview(newer, target, new[] { "midnight-theme" });
            string saved = File.ReadAllText(Path.Combine(target, "settings.json"));
            File.AppendAllText(Path.Combine(target, "settings.json"), " ");
            Reject(() => engine.Preview(newer, target, new[] { "midnight-theme" }), "outside Patchwork");
            Reject(() => engine.Apply(plan), "outside Patchwork");
            Assert(File.ReadAllText(Path.Combine(target, "settings.json")) == saved + " "); File.WriteAllText(Path.Combine(target, "settings.json"), saved, new UTF8Encoding(false));
            string backup = Path.Combine(first.DirectoryPath, first.Files[0].BackupFile); string original = File.ReadAllText(backup); File.AppendAllText(backup, " ");
            Reject(() => engine.Preview(newer, target, new[] { "midnight-theme" }), "Backup fingerprint"); File.WriteAllText(backup, original, new UTF8Encoding(false));
            var different = PatchBundle.Parse(newer.Content.Replace("sandbox-v1", "other-pack")); Reject(() => engine.Preview(different, target, new[] { "midnight-theme" }), "different patch pack");
            File.AppendAllText(Path.Combine(target, "app.version"), "updated upstream"); Reject(() => engine.Preview(newer, target, new[] { "midnight-theme" }), "Target fingerprint");
            File.WriteAllText(Path.Combine(target, "app.version"), TestFixtures.DemoVersion, new UTF8Encoding(false));
            engine.Restore(first); Reject(() => engine.Apply(plan), "session changed");
        }
        public static void RollbackAndRecovery(string root)
        {
            string data = Path.Combine(root, "update-rollback"); var engine = new PatchEngine(data); string target = TestFixtures.CreateDemo(data);
            foreach (string file in new[] { "legacy.txt", "new.txt" }) File.WriteAllText(Path.Combine(target, file), "original", new UTF8Encoding(false));
            var old = Multi(target, "1.0.0", true); var first = engine.Apply(engine.Preview(old, target, new[] { "midnight-theme" }));
            string working = File.ReadAllText(Path.Combine(target, "settings.json"));
            var newer = Multi(target, "1.1.0", false); var plan = engine.Preview(newer, target, new[] { "midnight-theme" });
            engine.BeforeWriteForTest = index => { if (index == 1) throw new IOException("Simulated write failure"); };
            Reject(() => engine.Apply(plan), "previous patch version was restored"); engine.BeforeWriteForTest = null;
            Assert(engine.ActiveSession(target).Id == first.Id && File.ReadAllText(Path.Combine(target, "settings.json")) == working && File.ReadAllText(Path.Combine(target, "legacy.txt")) == "old patch" && File.ReadAllText(Path.Combine(target, "new.txt")) == "original");
            var second = engine.Apply(engine.Preview(newer, target, new[] { "midnight-theme" }));
            // Recreate a process stopping after writes, before committing the update journal.
            second.State = "Prepared"; second.RestoreToPrevious = true; Save(second); first.State = "Applied"; first.SupersededBy = null; Save(first);
            Reject(() => engine.Preview(newer, target, new[] { "midnight-theme" }), "unfinished transaction");
            Reject(() => engine.Restore(first), "unfinished transaction");
            engine.Restore(second);
            Assert(engine.ActiveSession(target).Id == first.Id && engine.History().Single(x => x.Id == second.Id).State == "RolledBack" && File.ReadAllText(Path.Combine(target, "settings.json")) == working);
            var final = engine.Apply(engine.Preview(newer, target, new[] { "midnight-theme" }));
            // Recreate a process stopping after committing the update, before labeling its predecessor.
            first.State = "Applied"; first.SupersededBy = null; Save(first);
            Assert(engine.History().Single(x => x.Id == first.Id).State == "Superseded" && engine.ActiveSession(target).Id == final.Id);
            engine.Restore(final); Assert(File.ReadAllText(Path.Combine(target, "settings.json")) == TestFixtures.DemoConfig);
        }
        public static void PatchedVersionFileAndWorker(string root)
        {
            string data = Path.Combine(root, "update-worker"); var engine = new PatchEngine(data); string target = TestFixtures.CreateDemo(data);
            Func<string, string, PatchBundle> create = (version, theme) => {
                var raw = Json.Parse(SourceTests.Bundle(version, theme).Content); raw["versionFile"] = "settings.json"; raw["versionSha256"] = PatchEngine.Hash(Encoding.UTF8.GetBytes(TestFixtures.DemoConfig)); return PatchBundle.Parse(Json.Pretty(raw));
            };
            var first = engine.Apply(engine.Preview(create("1.0.0", "midnight"), target, new[] { "midnight-theme" }));
            // Old journals without patch IDs remain eligible for a verified update.
            first.PatchIds = null; Save(first);
            var newer = create("1.1.0", "dark"); var plan = engine.Preview(newer, target, new[] { "midnight-theme" });
            Assert(plan.VersionCurrentSha256 != plan.VersionSha256);
            string job = Worker.CreateJob(engine, newer, plan, null); Assert(Worker.Execute(job, data) == 0);
            var second = engine.ActiveSession(target); Assert(second.PreviousJournalId == first.Id);
            string restore = Worker.CreateJob(engine, null, null, second); Assert(Worker.Execute(restore, data) == 0);
            Assert(File.ReadAllText(Path.Combine(target, "settings.json")) == TestFixtures.DemoConfig);
            Assert(Worker.Execute(job, data) == 1);
        }
        public static void MetadataUpdate(string root)
        {
            string data = Path.Combine(root, "update-metadata"); var engine = new PatchEngine(data); string target = TestFixtures.CreateDemo(data);
            var first = engine.Apply(engine.Preview(SourceTests.Bundle("1.0.0"), target, new[] { "midnight-theme" }));
            var plan = engine.Preview(SourceTests.Bundle("1.1.0"), target, new[] { "midnight-theme" });
            Assert(plan.Files.All(x => x.BeforeHash == x.AfterHash));
            var second = engine.Apply(plan); Assert(second.PackVersion == "1.1.0" && second.PreviousJournalId == first.Id);
            engine.Restore(second); Assert(File.ReadAllText(Path.Combine(target, "settings.json")) == TestFixtures.DemoConfig);
        }
    }
}
