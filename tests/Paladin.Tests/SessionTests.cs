using Paladin.Core.Config;
using Paladin.Core.Logging;
using Paladin.Core.Model;
using Paladin.Core.Shield;
using static Paladin.Tests.TestHarness;

namespace Paladin.Tests;

public static class SessionTests
{
    public static void Register()
    {
        Suite("SessionRecord");

        Test("round trips through JSON with every field intact", () =>
        {
            var original = new SessionRecord
            {
                SessionId = "20260827-224500-abc123",
                StartedUtc = new DateTime(2026, 8, 27, 22, 45, 0, DateTimeKind.Utc),
                OwnerSid = "S-1-5-21-1-2-3-1001",
                MachineName = "PALADIN-PC",
                Aoe4DocumentsPath = @"C:\Users\x\OneDrive\Docs\My Games\Age of Empires IV",
                BackupPath = @"C:\backup",
                ReplaySource = new ReplaySourceRecord { Kind = "url", Value = "https://example.com/a.rec" },
                RestorePending = true,
                Outcome = SessionOutcome.Started,
                PreLaunch =
                {
                    new FileSnapshot { RelativePath = "local.ini", Sha256 = "abc", SizeBytes = 5723, BackupVerified = true },
                },
                Changes = { new FileChange { RelativePath = "local.ini", Kind = ChangeKind.Modified } },
                Restored = { new RestoreResult { RelativePath = "local.ini", Action = RestoreAction.Overwritten, Verified = true } },
            };

            var revived = SessionRecord.FromJson(original.ToJson());

            Equal(original.SessionId, revived.SessionId);
            Equal(original.StartedUtc, revived.StartedUtc);
            Equal(original.OwnerSid, revived.OwnerSid);
            Equal(original.Aoe4DocumentsPath, revived.Aoe4DocumentsPath, "backslashes survive");
            Equal(true, revived.RestorePending, "the crash flag is the point of this file");
            Equal(SessionOutcome.Started, revived.Outcome);
            Equal("url", revived.ReplaySource.Kind);
            Equal(1, revived.PreLaunch.Count);
            Equal(true, revived.PreLaunch[0].BackupVerified);
            Equal(ChangeKind.Modified, revived.Changes[0].Kind);
            Equal(RestoreAction.Overwritten, revived.Restored[0].Action);
        });

        Test("enums serialise as readable names, not numbers", () =>
        {
            var json = new SessionRecord { Outcome = SessionOutcome.RestoredChanges }.ToJson();
            True(json.Contains("\"RestoredChanges\""), "outcome is human readable in the file");
        });

        // ---------------------------------------------------------------------------

        Suite("SessionStore");

        Test("session ids sort chronologically as plain strings", () =>
        {
            var earlier = SessionStore.NewSessionId(new DateTime(2026, 8, 27, 10, 0, 0, DateTimeKind.Utc));
            var later = SessionStore.NewSessionId(new DateTime(2026, 8, 27, 11, 0, 0, DateTimeKind.Utc));
            True(string.CompareOrdinal(earlier, later) < 0, "ordinal sort matches time order");
        });

        Test("a saved session can be found again and reports its pending flag", () =>
        {
            using var root = new TempDir("store");
            var store = new SessionStore(root.Path, new PaladinLog(false));

            var session = store.Create("s1", DateTime.UtcNow, "SID-A", "PC-A");
            session.RestorePending = true;
            store.Save(session);

            var pending = store.FindPendingRestores("SID-A", "PC-A");
            Equal(1, pending.Count, "found");
            Equal("s1", pending[0].SessionId);
        });

        Test("a session from another user is never offered for recovery", () =>
        {
            using var root = new TempDir("store-user");
            var store = new SessionStore(root.Path, new PaladinLog(false));

            var theirs = store.Create("theirs", DateTime.UtcNow, "SID-OTHER", "PC-A");
            theirs.RestorePending = true;
            store.Save(theirs);

            Equal(0, store.FindPendingRestores("SID-ME", "PC-A").Count, "different SID is skipped");
        });

        Test("a session from another machine is never offered for recovery", () =>
        {
            using var root = new TempDir("store-machine");
            var store = new SessionStore(root.Path, new PaladinLog(false));

            var elsewhere = store.Create("elsewhere", DateTime.UtcNow, "SID-A", "OTHER-PC");
            elsewhere.RestorePending = true;
            store.Save(elsewhere);

            Equal(0, store.FindPendingRestores("SID-A", "PC-A").Count, "different machine is skipped");
        });

        Test("a completed session is not offered for recovery", () =>
        {
            using var root = new TempDir("store-done");
            var store = new SessionStore(root.Path, new PaladinLog(false));

            var done = store.Create("done", DateTime.UtcNow, "SID-A", "PC-A");
            done.RestorePending = false;
            done.Outcome = SessionOutcome.RestoredClean;
            store.Save(done);

            Equal(0, store.FindPendingRestores("SID-A", "PC-A").Count, "nothing pending");
        });

        Test("saving is atomic — an existing record is replaced, never truncated", () =>
        {
            using var root = new TempDir("store-atomic");
            var store = new SessionStore(root.Path, new PaladinLog(false));

            var session = store.Create("s1", DateTime.UtcNow, "SID-A", "PC-A");
            session.Notes.Add("first");
            store.Save(session);
            session.Notes.Add("second");
            store.Save(session);

            var reloaded = store.TryLoad("s1");
            NotNull(reloaded);
            Equal(2, reloaded!.Notes.Count, "both writes landed");
            False(File.Exists(store.SessionFile("s1") + ".tmp"), "no temp file left behind");
        });

        Test("pruning keeps pending and failed sessions no matter what", () =>
        {
            using var root = new TempDir("store-prune");
            var store = new SessionStore(root.Path, new PaladinLog(false));

            for (var i = 0; i < 5; i++)
            {
                var s = store.Create($"clean{i}", DateTime.UtcNow.AddMinutes(i), "SID-A", "PC-A");
                s.Outcome = SessionOutcome.RestoredClean;
                store.Save(s);
            }
            var pending = store.Create("pending", DateTime.UtcNow, "SID-A", "PC-A");
            pending.RestorePending = true;
            store.Save(pending);

            var failed = store.Create("failed", DateTime.UtcNow, "SID-A", "PC-A");
            failed.Outcome = SessionOutcome.RestoreFailed;
            store.Save(failed);

            store.Prune(keepRecent: 2);

            NotNull(store.TryLoad("pending"));
            NotNull(store.TryLoad("failed"));
            Equal(2, store.AllSessions().Count(s => s.Outcome == SessionOutcome.RestoredClean), "only the newest clean ones kept");
        });

        Test("an unreadable session record is skipped rather than crashing the launcher", () =>
        {
            using var root = new TempDir("store-corrupt");
            var store = new SessionStore(root.Path, new PaladinLog(false));
            store.Create("good", DateTime.UtcNow, "SID-A", "PC-A");

            Directory.CreateDirectory(store.SessionDirectory("bad"));
            File.WriteAllText(store.SessionFile("bad"), "{ this is not json");

            Equal(1, store.AllSessions().Count(), "the good one still loads");
        });

        // ---------------------------------------------------------------------------

        Suite("LauncherConfig");

        Test("defaults protect settings but not progression", () =>
        {
            var config = new LauncherConfig();
            var patterns = config.EffectiveProtectedPatterns();
            True(patterns.Contains("configuration_system.lua"), "system config protected");
            True(patterns.Contains("keyBindingProfiles/**"), "keybinds protected");
            False(patterns.Any(p => p.Contains("datastore")), "progression is opt-in only");
        });

        Test("the extended set is merged only when asked for", () =>
        {
            var config = new LauncherConfig { ProtectExtended = true };
            True(config.EffectiveProtectedPatterns().Any(p => p.Contains("datastore")), "opted in");
        });

        Test("round trips through JSON", () =>
        {
            var config = new LauncherConfig { UseDevFlag = false, ProtectExtended = true, SteamAppId = 1466860 };
            var revived = LauncherConfig.FromJson(config.ToJson());
            Equal(false, revived.UseDevFlag);
            Equal(true, revived.ProtectExtended);
            Equal(1466860, revived.SteamAppId);
        });

        Test("a corrupt config falls back to defaults and keeps the bad file", () =>
        {
            using var dir = new TempDir("config");
            var path = dir.Full("config.json");
            File.WriteAllText(path, "{ not json at all");

            var config = LauncherConfig.LoadOrCreate(path);
            Equal(true, config.UseDevFlag, "defaults used");
            True(File.Exists(path + ".invalid"), "the bad file is preserved for inspection");
        });
    }
}
