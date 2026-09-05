using Paladin.Core.Logging;
using Paladin.Core.Model;
using Paladin.Core.Shield;
using static Paladin.Tests.TestHarness;

namespace Paladin.Tests;

public static class ShieldTests
{
    private static readonly string[] RealisticPatterns =
    {
        "configuration_system.lua",
        "local.ini",
        "keyBindingProfiles/**",
        "Users/*/configuration_user.lua",
        "Users/*/cloud/configuration_user.lua",
    };

    private static readonly string[] RealisticExclusions =
    {
        "**/*.log", "LogFiles/**", "Cache/**", "playback/**", "matchhistory/**",
    };

    private static PaladinLog Quiet() => new(echoToConsole: false);

    public static void Register()
    {
        Suite("PathGlob");

        Test("exact file name matches", () =>
            True(PathGlob.IsMatch("configuration_system.lua", "configuration_system.lua"), "same name"));

        Test("matching is case-insensitive, as Windows is", () =>
            True(PathGlob.IsMatch("Configuration_System.LUA", "configuration_system.lua"), "case folded"));

        Test("a bare file name does not match a nested file", () =>
            False(PathGlob.IsMatch("Users/123/configuration_system.lua", "configuration_system.lua"), "must be at the root"));

        Test("single star stays inside one segment", () =>
        {
            True(PathGlob.IsMatch("Users/2535400000000001/configuration_user.lua", "Users/*/configuration_user.lua"), "one segment");
            False(PathGlob.IsMatch("Users/2535400000000001/cloud/configuration_user.lua", "Users/*/configuration_user.lua"), "star must not span '/'");
        });

        Test("double star spans any number of segments, including none", () =>
        {
            True(PathGlob.IsMatch("keyBindingProfiles/oMc.rkp", "keyBindingProfiles/**"), "one level");
            True(PathGlob.IsMatch("keyBindingProfiles/a/b/c.rkp", "keyBindingProfiles/**"), "many levels");
            False(PathGlob.IsMatch("keyBindingProfiles", "keyBindingProfiles/**"), "the directory itself is not a file match");
        });

        Test("extension wildcards work", () =>
        {
            True(PathGlob.IsMatch("warnings.log", "**/*.log"), "root-level log");
            True(PathGlob.IsMatch("LogFiles/deep/x.log", "**/*.log"), "nested log");
            False(PathGlob.IsMatch("local.ini", "**/*.log"), "not a log");
        });

        Test("backslash paths are normalised", () =>
            True(PathGlob.IsMatch(@"Users\123\configuration_user.lua", "Users/*/configuration_user.lua"), "windows separators"));

        // ---------------------------------------------------------------------------

        Suite("ProtectionPolicy");

        Test("protects exactly the configured settings files", () =>
        {
            var policy = new ProtectionPolicy(RealisticPatterns, RealisticExclusions);
            True(policy.IsProtected("configuration_system.lua"), "system config");
            True(policy.IsProtected("local.ini"), "local ini");
            True(policy.IsProtected("keyBindingProfiles/Remappable.rkp"), "keybinds");
            True(policy.IsProtected("Users/76561198000000001/configuration_user.lua"), "per-profile config");
            True(policy.IsProtected("Users/76561198000000001/cloud/configuration_user.lua"), "cloud copy");
        });

        Test("leaves replays, logs, saves and progression alone", () =>
        {
            var policy = new ProtectionPolicy(RealisticPatterns, RealisticExclusions);
            False(policy.IsProtected("playback/AgeIV_Replay_244989270"), "replays are not settings");
            False(policy.IsProtected("playback/replays/20251025_204405_2vs2_lipany.rec"), "recorded replays");
            False(policy.IsProtected("warnings.log"), "logs");
            False(policy.IsProtected("Users/123/datastore/campaign_progress.rlt"), "progression is not in the default set");
            False(policy.IsProtected("Users/123/Savegames/skirmish/generated_map - save0.sav"), "savegames");
            False(policy.IsProtected("mods_meta_data.lua"), "not listed");
        });

        Test("exclusions beat inclusions", () =>
        {
            var policy = new ProtectionPolicy(new[] { "keyBindingProfiles/**" }, new[] { "**/*.log" });
            True(policy.IsProtected("keyBindingProfiles/x.rkp"), "normal file");
            False(policy.IsProtected("keyBindingProfiles/x.log"), "excluded even though included");
        });

        Test("directory pruning skips trees that cannot hold protected files", () =>
        {
            var policy = new ProtectionPolicy(RealisticPatterns, RealisticExclusions);
            False(policy.CouldContainProtected("playback"), "playback is excluded outright");
            False(policy.CouldContainProtected("Cache"), "cache is excluded outright");
            True(policy.CouldContainProtected("Users"), "profiles live here");
            True(policy.CouldContainProtected("Users/2535400000000001"), "and one level deeper");
            True(policy.CouldContainProtected("keyBindingProfiles"), "keybinds live here");
            False(policy.CouldContainProtected("screenshots"), "nothing protected below");
        });

        // ---------------------------------------------------------------------------

        Suite("FileHasher");

        Test("identical bytes hash identically, different bytes do not", () =>
        {
            using var dir = new TempDir("hash");
            var a = dir.File("a.txt", "settings");
            var b = dir.File("b.txt", "settings");
            var c = dir.File("c.txt", "settings!");
            Equal(FileHasher.HashFile(a), FileHasher.HashFile(b), "same content");
            True(FileHasher.HashFile(a) != FileHasher.HashFile(c), "different content");
        });

        Test("hash is lowercase hex of the expected length", () =>
        {
            var hash = FileHasher.HashBytes("paladin"u8);
            Equal(64, hash.Length, "sha256 hex length");
            Equal(hash.ToLowerInvariant(), hash, "lowercase");
        });

        // ---------------------------------------------------------------------------

        Suite("ChangeDetector");

        Test("reports modified, created, removed and unchanged correctly", () =>
        {
            var before = new List<FileSnapshot>
            {
                new() { RelativePath = "local.ini", Sha256 = "aaa", SizeBytes = 10 },
                new() { RelativePath = "configuration_system.lua", Sha256 = "bbb", SizeBytes = 20 },
                new() { RelativePath = "keyBindingProfiles/x.rkp", Sha256 = "ccc", SizeBytes = 30 },
            };
            var after = new List<FileSnapshot>
            {
                new() { RelativePath = "local.ini", Sha256 = "zzz", SizeBytes = 11 },       // modified
                new() { RelativePath = "configuration_system.lua", Sha256 = "bbb", SizeBytes = 20 }, // unchanged
                                                                                             // keyBindingProfiles/x.rkp removed
                new() { RelativePath = "Users/1/configuration_user.lua", Sha256 = "ddd", SizeBytes = 40 }, // created
            };

            var changes = ChangeDetector.Compare(before, after);
            Equal(ChangeKind.Modified, changes.First(c => c.RelativePath == "local.ini").Kind);
            Equal(ChangeKind.Unchanged, changes.First(c => c.RelativePath == "configuration_system.lua").Kind);
            Equal(ChangeKind.Removed, changes.First(c => c.RelativePath == "keyBindingProfiles/x.rkp").Kind);
            Equal(ChangeKind.Created, changes.First(c => c.RelativePath == "Users/1/configuration_user.lua").Kind);
            True(ChangeDetector.AnythingChanged(changes), "something changed");
        });

        Test("two identical snapshots report no change at all", () =>
        {
            var snapshot = new List<FileSnapshot> { new() { RelativePath = "local.ini", Sha256 = "aaa" } };
            var changes = ChangeDetector.Compare(snapshot, snapshot);
            False(ChangeDetector.AnythingChanged(changes), "nothing changed");
        });

        // ---------------------------------------------------------------------------

        Suite("Snapshot + backup + restore round trip");

        Test("a settings file mangled by the game is restored byte-for-byte", () =>
        {
            using var docs = new TempDir("docs");
            using var backup = new TempDir("backup");
            using var quarantine = new TempDir("quarantine");

            docs.File("configuration_system.lua", "-- original settings\nresolution = 1440\n");
            docs.File("local.ini", "[settings]\nvolume=50\n");
            docs.File("keyBindingProfiles/oMc.rkp", "keybinds-original");
            docs.File("playback/AgeIV_Replay_1", "not settings, must be ignored");
            docs.File("warnings.log", "noise");

            var policy = new ProtectionPolicy(RealisticPatterns, RealisticExclusions);
            var log = Quiet();
            var snapshots = new SnapshotService(policy, log, 64 * 1024 * 1024);

            var before = snapshots.Capture(docs.Path);
            Equal(3, before.Count, "three protected files, replay and log excluded");
            True(snapshots.WriteBackup(docs.Path, backup.Path, before), "backup verified");
            True(before.All(s => s.BackupVerified), "every entry verified");

            // Simulate AoE4 in -dev mode trashing the settings.
            File.WriteAllText(docs.Full("configuration_system.lua"), "-- RESET BY DEV MODE\n");
            File.Delete(docs.Full("keyBindingProfiles/oMc.rkp"));
            docs.File("keyBindingProfiles/dev_generated.rkp", "junk the replay created");

            var after = snapshots.Capture(docs.Path);
            var changes = ChangeDetector.Compare(before, after);

            var restorer = new RestoreService(policy, log, quarantineCreatedFiles: true);
            var results = restorer.Restore(docs.Path, backup.Path, quarantine.Path, before, changes);

            Equal("-- original settings\nresolution = 1440\n", docs.Read("configuration_system.lua"), "overwritten file restored");
            Equal("keybinds-original", docs.Read("keyBindingProfiles/oMc.rkp"), "deleted file recreated");
            False(docs.Exists("keyBindingProfiles/dev_generated.rkp"), "created file removed from the live folder");
            True(File.Exists(Path.Combine(quarantine.Path, "keyBindingProfiles", "dev_generated.rkp")), "created file kept in quarantine");
            True(results.All(r => r.Action != RestoreAction.Failed), "no failures");
            True(results.Any(r => r.Action == RestoreAction.Overwritten && r.Verified), "an overwrite was verified");
            True(results.Any(r => r.Action == RestoreAction.Recreated && r.Verified), "a recreate was verified");
        });

        Test("untouched settings are not rewritten", () =>
        {
            using var docs = new TempDir("docs-noop");
            using var backup = new TempDir("backup-noop");
            using var quarantine = new TempDir("quarantine-noop");

            docs.File("local.ini", "[settings]\nvolume=50\n");
            var policy = new ProtectionPolicy(RealisticPatterns, RealisticExclusions);
            var log = Quiet();
            var snapshots = new SnapshotService(policy, log, 1024 * 1024);

            var before = snapshots.Capture(docs.Path);
            snapshots.WriteBackup(docs.Path, backup.Path, before);
            var stamp = File.GetLastWriteTimeUtc(docs.Full("local.ini"));

            var changes = ChangeDetector.Compare(before, snapshots.Capture(docs.Path));
            var results = new RestoreService(policy, log, true)
                .Restore(docs.Path, backup.Path, quarantine.Path, before, changes);

            Equal(0, results.Count, "nothing to do");
            Equal(stamp, File.GetLastWriteTimeUtc(docs.Full("local.ini")), "file not touched");
        });

        Test("restore refuses to act on a path outside the protected set", () =>
        {
            using var docs = new TempDir("docs-guard");
            using var backup = new TempDir("backup-guard");
            using var quarantine = new TempDir("quarantine-guard");

            docs.File("playback/AgeIV_Replay_1", "a user's own replay");

            var policy = new ProtectionPolicy(RealisticPatterns, RealisticExclusions);
            // Hand the restorer a change it should never have been given.
            var rogue = new List<FileChange>
            {
                new() { RelativePath = "playback/AgeIV_Replay_1", Kind = ChangeKind.Created },
            };

            var results = new RestoreService(policy, Quiet(), true)
                .Restore(docs.Path, backup.Path, quarantine.Path, new List<FileSnapshot>(), rogue);

            Equal(RestoreAction.Failed, results[0].Action, "refused");
            True(docs.Exists("playback/AgeIV_Replay_1"), "the user's replay is untouched");
        });

        Test("restore refuses a backup that was never verified", () =>
        {
            using var docs = new TempDir("docs-unverified");
            using var backup = new TempDir("backup-unverified");
            using var quarantine = new TempDir("q-unverified");

            docs.File("local.ini", "damaged");
            var pre = new List<FileSnapshot>
            {
                new() { RelativePath = "local.ini", Sha256 = "aaa", BackupVerified = false },
            };
            var changes = new List<FileChange>
            {
                new() { RelativePath = "local.ini", Kind = ChangeKind.Modified, BeforeSha256 = "aaa", AfterSha256 = "bbb" },
            };

            var policy = new ProtectionPolicy(RealisticPatterns, RealisticExclusions);
            var results = new RestoreService(policy, Quiet(), true)
                .Restore(docs.Path, backup.Path, quarantine.Path, pre, changes);

            Equal(RestoreAction.Failed, results[0].Action, "refused an unverified backup");
            Equal("damaged", docs.Read("local.ini"), "live file left alone rather than corrupted further");
        });

        Test("restore refuses a backup whose bytes changed on disk since the snapshot", () =>
        {
            using var docs = new TempDir("docs-rot");
            using var backup = new TempDir("backup-rot");
            using var quarantine = new TempDir("q-rot");

            docs.File("local.ini", "original");
            var policy = new ProtectionPolicy(RealisticPatterns, RealisticExclusions);
            var snapshots = new SnapshotService(policy, Quiet(), 1024 * 1024);
            var before = snapshots.Capture(docs.Path);
            snapshots.WriteBackup(docs.Path, backup.Path, before);

            // Something corrupts the backup, then the game changes the live file.
            File.WriteAllText(Path.Combine(backup.Path, "local.ini"), "CORRUPTED BACKUP");
            File.WriteAllText(docs.Full("local.ini"), "changed by the game");

            var changes = ChangeDetector.Compare(before, snapshots.Capture(docs.Path));
            var results = new RestoreService(policy, Quiet(), true)
                .Restore(docs.Path, backup.Path, quarantine.Path, before, changes);

            Equal(RestoreAction.Failed, results[0].Action, "rotten backup rejected");
            Equal("changed by the game", docs.Read("local.ini"), "never wrote the corrupt bytes over live settings");
        });

        // ---------------------------------------------------------------------------

        Suite("VolatileContent (bookkeeping vs real settings)");

        // These are the exact lines that differed on a live install after an ORDINARY
        // Age of Empires IV launch — no replay, no -dev. Nothing else changed.
        const string SystemConfigBefore = "resolution = 2560\nfullscreen = true\nlast_modified = 1787925696\nvsync = false\n";
        const string SystemConfigAfter = "resolution = 2560\nfullscreen = true\nlast_modified = 1787925967\nvsync = false\n";
        const string LocalIniBefore = "[settings]\napp_runs_count=1000\nmost_recently_viewed_profile_id=18017137\nvolume=42\n";
        const string LocalIniAfter = "[settings]\napp_runs_count=1001\nmost_recently_viewed_profile_id=12294160\nvolume=42\n";

        Test("a last_modified bump alone is not a settings change", () =>
        {
            var a = VolatileContent.StripVolatileLines(SystemConfigBefore, VolatileContent.DefaultVolatileKeys);
            var b = VolatileContent.StripVolatileLines(SystemConfigAfter, VolatileContent.DefaultVolatileKeys);
            Equal(a, b, "only the timestamp differed");
        });

        Test("a run-counter bump alone is not a settings change", () =>
        {
            var a = VolatileContent.StripVolatileLines(LocalIniBefore, VolatileContent.DefaultVolatileKeys);
            var b = VolatileContent.StripVolatileLines(LocalIniAfter, VolatileContent.DefaultVolatileKeys);
            Equal(a, b, "only counters and last-viewed profile differed");
        });

        Test("a REAL setting change is still caught, timestamp bump or not", () =>
        {
            var damaged = "resolution = 800\nfullscreen = true\nlast_modified = 1787925967\nvsync = false\n";
            var a = VolatileContent.StripVolatileLines(SystemConfigBefore, VolatileContent.DefaultVolatileKeys);
            var b = VolatileContent.StripVolatileLines(damaged, VolatileContent.DefaultVolatileKeys);
            True(a != b, "resolution changed and must not be masked by the timestamp filter");
        });

        Test("volatile keys are matched whatever the spacing or quoting", () =>
        {
            var stripped = VolatileContent.StripVolatileLines(
                "  last_modified   =  1\n[\"last_modified\"] = 2\nlast_modified=3\nkeep_me = 4\n",
                VolatileContent.DefaultVolatileKeys);
            False(stripped.Contains("last_modified"), "every form removed");
            True(stripped.Contains("keep_me"), "unrelated keys survive");
        });

        Test("only text formats get a semantic hash; binary keybinds stay strict", () =>
        {
            True(VolatileContent.LooksLikeText("configuration_system.lua"), "lua");
            True(VolatileContent.LooksLikeText("local.ini"), "ini");
            False(VolatileContent.LooksLikeText("keyBindingProfiles/oMc.rkp"), "binary keybinds are compared byte-for-byte");
        });

        Test("a normal launch reports bookkeeping, not a settings change", () =>
        {
            using var docs = new TempDir("bookkeeping");
            docs.File("configuration_system.lua", SystemConfigBefore);
            docs.File("local.ini", LocalIniBefore);

            var policy = new ProtectionPolicy(RealisticPatterns, RealisticExclusions);
            var snapshots = new SnapshotService(policy, Quiet(), 1024 * 1024, VolatileContent.DefaultVolatileKeys);
            var before = snapshots.Capture(docs.Path);

            File.WriteAllText(docs.Full("configuration_system.lua"), SystemConfigAfter);
            File.WriteAllText(docs.Full("local.ini"), LocalIniAfter);

            var changes = ChangeDetector.Compare(before, snapshots.Capture(docs.Path));

            True(ChangeDetector.AnythingChanged(changes), "the bytes really did change");
            False(ChangeDetector.AnythingMeaningfullyChanged(changes), "but no setting did");
            Equal(2, changes.Count(c => c.Kind == ChangeKind.BookkeepingOnly), "both classified as bookkeeping");
        });

        Test("bookkeeping-only changes are never restored", () =>
        {
            using var docs = new TempDir("bk-restore");
            using var backup = new TempDir("bk-backup");
            using var quarantine = new TempDir("bk-quarantine");

            docs.File("local.ini", LocalIniBefore);
            var policy = new ProtectionPolicy(RealisticPatterns, RealisticExclusions);
            var snapshots = new SnapshotService(policy, Quiet(), 1024 * 1024, VolatileContent.DefaultVolatileKeys);
            var before = snapshots.Capture(docs.Path);
            snapshots.WriteBackup(docs.Path, backup.Path, before);

            File.WriteAllText(docs.Full("local.ini"), LocalIniAfter);
            var changes = ChangeDetector.Compare(before, snapshots.Capture(docs.Path));

            var results = new RestoreService(policy, Quiet(), true)
                .Restore(docs.Path, backup.Path, quarantine.Path, before, changes);

            Equal(0, results.Count, "nothing to do");
            Equal(LocalIniAfter, docs.Read("local.ini"), "the game's own counter is left exactly as written");
        });

        Test("a real settings change IS still restored when bookkeeping also moved", () =>
        {
            using var docs = new TempDir("real-restore");
            using var backup = new TempDir("real-backup");
            using var quarantine = new TempDir("real-quarantine");

            docs.File("configuration_system.lua", SystemConfigBefore);
            var policy = new ProtectionPolicy(RealisticPatterns, RealisticExclusions);
            var snapshots = new SnapshotService(policy, Quiet(), 1024 * 1024, VolatileContent.DefaultVolatileKeys);
            var before = snapshots.Capture(docs.Path);
            snapshots.WriteBackup(docs.Path, backup.Path, before);

            // Resolution wrecked AND the timestamp bumped, which is what real damage looks like.
            File.WriteAllText(docs.Full("configuration_system.lua"),
                "resolution = 800\nfullscreen = true\nlast_modified = 1787925967\nvsync = false\n");

            var changes = ChangeDetector.Compare(before, snapshots.Capture(docs.Path));
            Equal(ChangeKind.Modified, changes.First(c => c.RelativePath == "configuration_system.lua").Kind);

            var results = new RestoreService(policy, Quiet(), true)
                .Restore(docs.Path, backup.Path, quarantine.Path, before, changes);

            Equal(SystemConfigBefore, docs.Read("configuration_system.lua"), "real damage restored");
            True(results.Any(r => r.Verified), "and verified");
        });

        Test("a binary file with no semantic hash is always treated as a real change", () =>
        {
            var before = new List<FileSnapshot>
            {
                new() { RelativePath = "keyBindingProfiles/oMc.rkp", Sha256 = "aaa", SemanticSha256 = null },
            };
            var after = new List<FileSnapshot>
            {
                new() { RelativePath = "keyBindingProfiles/oMc.rkp", Sha256 = "bbb", SemanticSha256 = null },
            };
            Equal(ChangeKind.Modified, ChangeDetector.Compare(before, after)[0].Kind,
                "no semantic hash means we must assume the worst");
        });

        // ---------------------------------------------------------------------------

        Suite("RecoveryTriage");

        Test("a file changed during the session is restored", () =>
        {
            var heartbeat = new DateTime(2026, 8, 27, 20, 0, 0, DateTimeKind.Utc);
            var current = new List<FileSnapshot>
            {
                new() { RelativePath = "local.ini", Sha256 = "bbb", ModifiedUtc = heartbeat.AddMinutes(10) },
            };
            var changes = new List<FileChange>
            {
                new() { RelativePath = "local.ini", Kind = ChangeKind.Modified },
            };

            var result = RecoveryTriage.Triage(changes, current, heartbeat);
            Equal(1, result.Actionable.Count, "restored");
            Equal(0, result.Deferred.Count, "nothing deferred");
        });

        Test("a file the user edited long afterwards is left alone", () =>
        {
            var heartbeat = new DateTime(2026, 8, 27, 20, 0, 0, DateTimeKind.Utc);
            var current = new List<FileSnapshot>
            {
                new() { RelativePath = "local.ini", Sha256 = "bbb", ModifiedUtc = heartbeat.AddDays(3) },
            };
            var changes = new List<FileChange>
            {
                new() { RelativePath = "local.ini", Kind = ChangeKind.Modified },
            };

            var result = RecoveryTriage.Triage(changes, current, heartbeat);
            Equal(0, result.Actionable.Count, "not touched");
            Equal(1, result.Deferred.Count, "deferred to the user");
        });

        Test("a deleted settings file is always actionable", () =>
        {
            var heartbeat = new DateTime(2026, 8, 27, 20, 0, 0, DateTimeKind.Utc);
            var changes = new List<FileChange>
            {
                new() { RelativePath = "keyBindingProfiles/oMc.rkp", Kind = ChangeKind.Removed },
            };

            var result = RecoveryTriage.Triage(changes, new List<FileSnapshot>(), heartbeat);
            Equal(1, result.Actionable.Count, "a missing file has no timestamp to defer on");
        });

        Test("unchanged files never reach either list", () =>
        {
            var changes = new List<FileChange>
            {
                new() { RelativePath = "local.ini", Kind = ChangeKind.Unchanged },
            };
            var result = RecoveryTriage.Triage(changes, new List<FileSnapshot>(), DateTime.UtcNow);
            Equal(0, result.Actionable.Count + result.Deferred.Count, "nothing to do");
        });

        Test("bookkeeping-only differences are reported apart and never restored", () =>
        {
            // Timestamps and run counters the game rewrites on every launch, changed
            // inside the grace window like a real session change would be.
            var heartbeat = new DateTime(2026, 9, 5, 14, 31, 0, DateTimeKind.Utc);
            var current = new List<FileSnapshot>
            {
                new() { RelativePath = "local.ini", Sha256 = "bbb", ModifiedUtc = heartbeat.AddMinutes(10) },
                new() { RelativePath = "configuration_system.lua", Sha256 = "ccc", ModifiedUtc = heartbeat.AddMinutes(10) },
            };
            var changes = new List<FileChange>
            {
                new() { RelativePath = "local.ini", Kind = ChangeKind.BookkeepingOnly },
                new() { RelativePath = "configuration_system.lua", Kind = ChangeKind.BookkeepingOnly },
                new() { RelativePath = "keyBindingProfiles/oMc.rkp", Kind = ChangeKind.Modified },
            };

            var result = RecoveryTriage.Triage(changes, current, heartbeat);
            Equal(2, result.Bookkeeping.Count, "the two bookkeeping files are set apart");
            Equal(1, result.Actionable.Count, "the real change is still restored");
            Equal(0, result.Deferred.Count, "nothing deferred");
            True(result.Actionable[0].RelativePath == "keyBindingProfiles/oMc.rkp", "and it is the right one");
        });
    }
}
