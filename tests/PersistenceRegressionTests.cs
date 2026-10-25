// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace DailyWallpaperPicker
{
    public static partial class SelfTests
    {
        private static byte[] ValidHttpCover()
        {
            return Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
        }

        private static void PersistenceRegressionTests(List<TestResult> results, string fixtureRoot)
        {
            Check(results, "single_instance_trailing_separator_alias_uses_same_named_mutex", delegate
            {
                string root = Path.Combine(fixtureRoot, "instance-alias");
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "sentinel.txt"), "same directory");
                string canonical = Program.ResolveDataDirectory(new[] { "--data-dir", root }, fixtureRoot);
                string alias = Program.ResolveDataDirectory(new[] { "--data-dir", root + "\\" }, fixtureRoot);
                Require(canonical == alias && File.ReadAllText(Path.Combine(alias, "sentinel.txt")) == "same directory", "Trailing separator was not normalized.");
                bool firstNew, secondNew;
                using (var first = new Mutex(false, ProfilePaths.InstanceMutexName(canonical), out firstNew))
                using (var second = new Mutex(false, ProfilePaths.InstanceMutexName(alias), out secondNew))
                    Require(firstNew && !secondNew, "Same profile still creates two independent mutexes.");
                Require(ProfilePaths.InstanceMutexName(root.ToUpperInvariant() + "/") == ProfilePaths.InstanceMutexName(root), "Case/separator alias changed the mutex.");
                return new { sameDirectory = true, duplicateMutexRejected = true, realUiProcessesStarted = 0 };
            });
            Check(results, "profile_canonicalization_preserves_drive_and_unc_roots_and_install_profile", delegate
            {
                string driveRoot = Path.GetPathRoot(fixtureRoot);
                Require(ProfilePaths.Canonicalize(driveRoot) == driveRoot, "Drive root became drive-relative.");
                string unc = ProfilePaths.Canonicalize(@"\\server\share\");
                Require(Path.GetPathRoot(unc) == Path.GetPathRoot(@"\\server\share\") && Path.IsPathRooted(unc), "UNC root became relative.");
                Require(ProfilePaths.InstanceMutexName(@"\\server\share\") == ProfilePaths.InstanceMutexName(@"\\server\share"), "UNC root aliases created different mutexes.");
                string root = Path.Combine(fixtureRoot, "canonical-install-profile");
                Directory.CreateDirectory(root);
                string data = Path.Combine(root, "data");
                Json.Write(Path.Combine(root, "install-profile.json"), new InstallProfile { DataDirectory = data + "\\" });
                Require(Program.ResolveDataDirectory(new string[0], root) == data, "Install-profile alias not normalized.");
                Require(ProfilePaths.Canonicalize(Path.Combine(data, ".")) == data, "Dot segment changed identity.");
                return new { rootsPreserved = true, uncTestWasStringOnly = true, installProfileNormalized = true };
            });
            Check(results, "reminder_save_failure_retries_same_controller_then_stays_presented_after_restart", delegate
            {
                DateTime now = new DateTime(2026, 10, 25, 15, 5, 0, DateTimeKind.Utc);
                var initial = new AppState { BatchId = Schedule.Due(now), PresentedBatch = "2026-10-24", SeedImported = true };
                var controller = FixtureController(fixtureRoot, "reminder-save-retry", initial, () => now);
                string path = Path.Combine(controller.DataDirectory, "state.json");
                string before = CacheStore.Hash(path);
                bool failed = false;
                using (var fileLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { controller.ShouldPresent(); }
                    catch (IOException) { failed = true; }
                    Require(failed, "Fixture did not block the real atomic state replace.");
                    Require(controller.State.PresentedBatch == initial.PresentedBatch, "Failed save consumed today's reminder in memory.");
                }
                Require(CacheStore.Hash(path) == before, "Failed save changed the original state.");
                Require(controller.ShouldPresent(), "Unlocked same controller did not retry the missed reminder.");
                Require(!controller.ShouldPresent(), "Successful retry caused duplicate reminder.");
                var fresh = new PickerController(controller.DataDirectory, () => now);
                Require(!fresh.ShouldPresent(), "Restart forgot the successful reminder.");
                return new { failureObserved = true, sameProcessRetry = true, oneReminderOnly = true, restartPreservesMarker = true };
            });
            Check(results, "json_failed_replace_removes_only_own_temp_and_preserves_previous_version", delegate
            {
                string root = Path.Combine(fixtureRoot, "json-failed-replace");
                Directory.CreateDirectory(root);
                string path = Path.Combine(root, "state.json");
                Json.Write(path, new { value = "original" });
                string before = CacheStore.Hash(path);
                string unrelated = Path.Combine(root, "state.json.unowned.tmp");
                File.WriteAllText(unrelated, "leave this alone");
                int failures = 0;
                using (var fileLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    for (int n = 0; n < 5; n++)
                    {
                        try { Json.Write(path, new { value = "failed" }); }
                        catch (IOException) { failures++; }
                        Require(Directory.GetFiles(root, "*.tmp").SequenceEqual(new[] { unrelated }), "Failed replace leaked its temp or removed unowned data.");
                    }
                Require(failures == 5 && CacheStore.Hash(path) == before, "Failed writes did not preserve original.");
                Json.Write(path, new { value = "success" });
                Require(Json.Read<Dictionary<string, object>>(path)["value"].ToString() == "success", "Write did not recover after unlock.");
                Require(CacheStore.Hash(path + ".previous") == before, "Previous saved version was not retained.");
                Require(Directory.GetFiles(root, "*.tmp").SequenceEqual(new[] { unrelated }), "Successful retry left owned temp files.");
                return new { failedWrites = failures, leakedFiles = 0, previousVersionPreserved = true, unownedTempPreserved = true };
            });
        }
    }
}
