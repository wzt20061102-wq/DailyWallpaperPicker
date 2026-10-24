// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace DailyWallpaperPicker
{
    public static partial class SelfTests
    {
        private static void MaintenanceTests(List<TestResult> results, string fixtureRoot)
        {
            Check(results, "dislike_waiting_for_queue_persists_before_23_00_and_survives_promotion", delegate
            {
                DateTime now = new DateTime(2026, 10, 24, 14, 55, 0, DateTimeKind.Utc);
                const string name = "dislike-held-queue";
                AppState initial = DislikeFixtureState(fixtureRoot, name, now);
                int workerCalls = 0;
                var controller = FixtureController(fixtureRoot, name, initial, () => now,
                    (verb, input, progress) => { workerCalls++; throw new IOException("No stale replacement may query Steam"); });
                Task operation;
                using (controller.HoldRecommendationQueueAsync().GetAwaiter().GetResult())
                {
                    operation = controller.DislikeAsync("890002");
                    Require(!operation.IsCompleted, "Request must actually wait for the preparation queue.");
                    Require(PickerController.ReadFeedback(controller.DataDirectory).Dislikes.Count == 1,
                        "Explicit feedback must be durable before the long preparation wait.");
                    now = new DateTime(2026, 10, 24, 15, 0, 0, DateTimeKind.Utc);
                    Require(controller.ShouldPresent(), "The next batch must be promoted at 23:00.");
                }
                operation.GetAwaiter().GetResult();
                Require(controller.State.BatchId == "2026-10-24" && controller.State.Candidates.All(c => c.Id.StartsWith("891")), "Stale replacement changed the new batch.");
                Require(workerCalls == 0 && !controller.Busy, "Stale replacement must release Busy without worker calls.");
                Require(PickerController.ReadFeedback(controller.DataDirectory).Dislikes.Single().Id == "890002", "Accepted feedback was lost across the date boundary.");
                return new { persistedBeforeQueueWait = true, retainedAfterPromotion = true, workerCalls = workerCalls, busyReleased = !controller.Busy };
            });
            Check(results, "dislike_waiting_queue_rejects_duplicate_without_duplicate_feedback", delegate
            {
                DateTime now = new DateTime(2026, 10, 24, 14, 55, 0, DateTimeKind.Utc);
                const string name = "dislike-held-double";
                var controller = FixtureController(fixtureRoot, name, DislikeFixtureState(fixtureRoot, name, now), () => now,
                    (verb, input, progress) => { throw new IOException("Not called after promotion"); });
                Task first;
                using (controller.HoldRecommendationQueueAsync().GetAwaiter().GetResult())
                {
                    first = controller.DislikeAsync("890002");
                    RejectedOperation(() => controller.DislikeAsync("890002").GetAwaiter().GetResult());
                    Require(PickerController.ReadFeedback(controller.DataDirectory).Dislikes.Count == 1, "Duplicate click retrained feedback.");
                    now = now.AddMinutes(5);
                    controller.ShouldPresent();
                }
                first.GetAwaiter().GetResult();
                return new { feedbackRecords = 1, busyReleased = !controller.Busy };
            });
            Check(results, "in_flight_recommendation_reloads_feedback_before_committing_next_batch", delegate
            {
                DateTime now = new DateTime(2026, 10, 24, 14, 55, 0, DateTimeKind.Utc);
                const string name = "dislike-during-query";
                var initial = DislikeFixtureState(fixtureRoot, name, now);
                initial.PreparedCandidates.Clear();
                int queries = 0;
                var pending = new TaskCompletionSource<WorkerReply>();
                var downloaded = new List<string>();
                var controller = FixtureController(fixtureRoot, name, initial, () => now, (verb, input, progress) =>
                {
                    if (verb == "download")
                    {
                        downloaded.Add(Convert.ToString(Json.Decode<Dictionary<string, object>>(Json.Encode(input))["id"]));
                        throw new IOException("Isolated download stub");
                    }
                    queries++;
                    if (queries == 1) return pending.Task;
                    return Task.FromResult(new WorkerReply { ok = true, items = new List<Candidate> {
                        new Candidate { Id = (950000 + queries).ToString(), Tags = new[] { "Everyone", "Scene", "Nature", "Dynamic resolution" }, FileSize = 100 }
                    }});
                });
                Task preparation = controller.TickAsync();
                Require(queries == 1 && !preparation.IsCompleted, "Fixture did not suspend a real preparation query.");
                Task dislike = controller.DislikeAsync("890002");
                Require(!dislike.IsCompleted && PickerController.ReadFeedback(controller.DataDirectory).Dislikes.Count == 1, "Feedback was not accepted while the query held the queue.");
                pending.SetResult(new WorkerReply { ok = true, items = new List<Candidate> {
                    new Candidate { Id = "890002", Tags = new[] { "Everyone", "Scene", "Nature", "Dynamic resolution" }, FileSize = 100 },
                    new Candidate { Id = "950001", Tags = new[] { "Everyone", "Scene", "Nature", "Dynamic resolution" }, FileSize = 100 }
                }});
                preparation.GetAwaiter().GetResult();
                dislike.GetAwaiter().GetResult();
                Require(controller.State.PreparedCandidates.Count == 3 && !controller.State.PreparedCandidates.Any(c => c.Id == "890002"), "In-flight query committed a newly disliked wallpaper.");
                Require(!controller.State.Candidates.Any(c => c.Id == "890002") && !downloaded.Contains("890002"), "Newly disliked wallpaper was downloaded or retained after replacement.");
                return new { queryCount = queries, newlyDislikedIdExcluded = true, downloadedIds = downloaded.ToArray() };
            });
            Check(results, "state_legacy_migration_preserves_choices_and_derives_explicit_stages", delegate
            {
                var state = new AppState();
                state.Candidates.Add(new Candidate { Id = "940001", Ready = true });
                state.Candidates.Add(new Candidate { Id = "940002", Status = "正在校验", Total = 100 });
                state.Candidates.Add(new Candidate { Id = "940003", Error = "saved error" });
                string choices = Json.Encode(state.Choices);
                StateMigration.Upgrade(state);
                Require(state.SchemaVersion == StateMigration.CurrentVersion, "Schema version missing.");
                Require(state.Candidates.Select(c => c.Stage).SequenceEqual(new[] { DownloadStage.Ready, DownloadStage.Verifying, DownloadStage.Failed }), "Legacy candidate stages were not migrated.");
                Require(Json.Encode(state.Choices) == choices, "Migration changed choice history.");
                string once = Json.Encode(state);
                StateMigration.Upgrade(state);
                Require(Json.Encode(state) == once, "Migration is not idempotent.");
                return new { schemaVersion = state.SchemaVersion, idempotent = true, choicesPreserved = true };
            });
            Check(results, "state_future_schema_rejected_without_saving", delegate
            {
                string root = Path.Combine(fixtureRoot, "future-schema");
                Directory.CreateDirectory(root);
                string file = Path.Combine(root, "state.json");
                Json.Write(file, new AppState { SchemaVersion = StateMigration.CurrentVersion + 1 });
                string hash = CacheStore.Hash(file);
                Rejected(() => StateMigration.Upgrade(Json.Read<AppState>(file)));
                Require(CacheStore.Hash(file) == hash, "Future data was overwritten.");
                return new { rejected = true, originalHashRetained = hash };
            });
            Check(results, "ui_verification_stage_is_not_overridden_by_download_totals_or_text", delegate
            {
                var item = new Candidate { Stage = DownloadStage.Verifying, Total = 100, Downloaded = 100, Status = "arbitrary translated message" };
                string status = CardView.StatusText(item);
                Require(status.Contains("核验") && !status.Contains("MB"), "Old progress totals override the verification stage.");
                item.Status = "failed success 失败 已准备好";
                Require(CardView.StatusText(item) == status, "Presentation still classifies the free-form message.");
                return new { explicitStage = item.Stage.ToString(), displayed = status, freeTextDoesNotControlStage = true };
            });
            Check(results, "profile_explicit_argument_wins_over_install_profile_and_portable_default", delegate
            {
                string root = Path.Combine(fixtureRoot, "profile-paths");
                Directory.CreateDirectory(root);
                string portable = Program.ResolveDataDirectory(new string[0], root);
                Require(portable == Path.Combine(root, "data"), "Portable default moved.");
                string installed = Path.Combine(fixtureRoot, "existing-user-data");
                Json.Write(Path.Combine(root, "install-profile.json"), new InstallProfile { DataDirectory = installed });
                Require(Program.ResolveDataDirectory(new string[0], root) == installed, "Installed profile was ignored.");
                string explicitPath = Path.Combine(fixtureRoot, "explicit-test-data");
                Require(Program.ResolveDataDirectory(new[] { "--data-dir", explicitPath }, root) == explicitPath, "CLI must override installation metadata.");
                Require(!Directory.Exists(installed) && !Directory.Exists(explicitPath), "Path resolution unexpectedly created user folders.");
                return new { portableDefaultPreserved = true, installedPathPreserved = true, explicitArgumentWins = true };
            });
            Check(results, "profile_invalid_relative_install_metadata_does_not_fall_back_silently", delegate
            {
                string root = Path.Combine(fixtureRoot, "invalid-profile");
                Directory.CreateDirectory(root);
                string file = Path.Combine(root, "install-profile.json");
                Json.Write(file, new InstallProfile { DataDirectory = "relative-data" });
                string before = CacheStore.Hash(file);
                Rejected(() => Program.ResolveDataDirectory(new string[0], root));
                Require(CacheStore.Hash(file) == before, "Invalid installation metadata was rewritten.");
                return new { invalidPathRejected = true, metadataRetained = true };
            });
            Check(results, "failed_jobs_pruned_by_age_and_count_while_active_and_unowned_files_survive", delegate
            {
                string root = Path.Combine(fixtureRoot, "job-retention");
                Directory.CreateDirectory(root);
                DateTime now = DateTime.UtcNow;
                string activeId = new string('a', 32);
                string active = Path.Combine(root, activeId + ".request.json");
                File.WriteAllText(active, "active job");
                using (JobRetention.Acquire(root, activeId))
                {
                    File.SetLastWriteTimeUtc(active, now.AddDays(-20));
                    for (int index = 0; index < 35; index++)
                    {
                        string file = Path.Combine(root, index.ToString("x32") + ".request.json");
                        File.WriteAllText(file, "failed fixture");
                        File.SetLastWriteTimeUtc(file, now.AddMinutes(-index));
                    }
                    string expired = Path.Combine(root, new string('b', 32) + ".response.json");
                    File.WriteAllText(expired, "old failed fixture"); File.SetLastWriteTimeUtc(expired, now.AddDays(-8));
                    string unrelated = Path.Combine(root, "keep-user-notes.json"); File.WriteAllText(unrelated, "unowned");
                    JobRetention.Prune(root, now);
                    Require(File.Exists(active), "Active job was pruned.");
                    Require(!File.Exists(expired), "Expired failed job survived.");
                    Require(Directory.GetFiles(root, "*.request.json").Length == 31, "Failed job count was not bounded to 30 plus active job.");
                    Require(File.Exists(unrelated), "Unowned file was removed.");
                }
                Require(!File.Exists(active), "Expired job was not eligible after releasing its lease.");
                return new { retainedInactiveJobs = 30, activeJobProtected = true, unownedFilePreserved = true };
            });
            Check(results, "successful_job_cleanup_removes_only_its_owned_files", delegate
            {
                string root = Path.Combine(fixtureRoot, "job-success"); Directory.CreateDirectory(root);
                string id = new string('c', 32), other = new string('d', 32);
                foreach (string suffix in new[] { ".request.json", ".response.json", ".progress.json", ".progress.json.previous" }) File.WriteAllText(Path.Combine(root, id + suffix), "owned fixture");
                string sentinel = Path.Combine(root, other + ".request.json"); File.WriteAllText(sentinel, "other job");
                JobRetention.DeleteCompleted(root, id);
                Require(Directory.GetFiles(root).Length == 1 && File.Exists(sentinel), "Success cleanup crossed job identity.");
                return new { completedFilesRemoved = 4, otherJobPreserved = true };
            });
            Check(results, "logs_rotate_and_keep_five_recent_archives_without_touching_unowned_files", delegate
            {
                string root = Path.Combine(fixtureRoot, "log-retention"); Directory.CreateDirectory(root);
                DateTime now = DateTime.UtcNow;
                for (int index = 0; index < 8; index++)
                {
                    string file = Path.Combine(root, "runtime_output.txt." + (100 + index) + ".log");
                    File.WriteAllText(file, "archive fixture"); File.SetLastWriteTimeUtc(file, now.AddMinutes(-index));
                }
                string expired = Path.Combine(root, "runtime_output.txt.900.log"); File.WriteAllText(expired, "expired"); File.SetLastWriteTimeUtc(expired, now.AddDays(-15));
                string unrelated = Path.Combine(root, "runtime_output.txt.user.log"); File.WriteAllText(unrelated, "unowned");
                File.WriteAllBytes(Path.Combine(root, "runtime_output.txt"), new byte[4 * 1024 * 1024]);
                BoundedLog.Write(root, "fresh marker");
                var archives = Directory.GetFiles(root, "runtime_output.txt.*.log").Where(p => p != unrelated).ToArray();
                Require(archives.Length == 5 && !File.Exists(expired), "Log archive retention exceeded its limit.");
                Require(File.Exists(unrelated) && File.ReadAllText(Path.Combine(root, "runtime_output.txt")).Contains("fresh marker"), "Current log or unowned file was damaged.");
                return new { retainedArchives = archives.Length, activeLogWritten = true, unownedFileRetained = true };
            });
            Check(results, "linked_retention_directory_rejects_prune_delete_and_log_write", delegate
            {
                string root = Path.Combine(fixtureRoot, "linked-retention");
                string target = Path.Combine(root, "external-target");
                string link = Path.Combine(root, "linked-jobs");
                Directory.CreateDirectory(target);
                string id = new string('e', 32);
                string sentinel = Path.Combine(target, id + ".request.json");
                File.WriteAllText(sentinel, "outside sentinel"); File.SetLastWriteTimeUtc(sentinel, DateTime.UtcNow.AddDays(-30));
                string hash = CacheStore.Hash(sentinel);
                JunctionFixture.Create(link, target);
                Require((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "Fixture was not an actual junction.");
                Rejected(() => JobRetention.Prune(link, DateTime.UtcNow));
                Rejected(() => JobRetention.DeleteCompleted(link, id));
                Rejected(() => { using (JobRetention.Acquire(link, id)) { } });
                Rejected(() => BoundedLog.Write(link, "must not reach target"));
                Require(CacheStore.Hash(sentinel) == hash && !File.Exists(Path.Combine(target, "runtime_output.txt")), "Linked directory protection changed target content.");
                return new { realJunctionUsed = true, rejectedOperations = 4, targetSentinelPreserved = true };
            });
            Check(results, "pre_cancelled_wallpaper_apply_never_starts_ipc_process", delegate
            {
                int before = ProcessBoundary.FixtureStarts + ProcessBoundary.UnexpectedStarts;
                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel();
                    var bridge = new WallpaperBridge(Path.Combine(fixtureRoot, "forbidden-wallpaper.exe"), message => { }, cancellation.Token);
                    bool cancelled = false;
                    try { bridge.Apply(Path.Combine(fixtureRoot, "not-applied-project.json")).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Require(cancelled && before == ProcessBoundary.FixtureStarts + ProcessBoundary.UnexpectedStarts, "Cancelled apply sent an IPC command.");
                    return new { cancelled = true, processStartAttempts = 0 };
                }
            });
            Check(results, "progress_subscriber_failure_drains_actual_owned_worker_before_return", delegate
            {
                string root = Path.Combine(fixtureRoot, "callback-worker"); Directory.CreateDirectory(root);
                string pidFile = Path.Combine(root, "child.pid");
                var client = new SteamClient(new PickerConfig { SteamBin = root }, root, CancellationToken.None, Assembly.GetExecutingAssembly().Location);
                bool expectedFailure = false;
                try
                {
                    client.Call("download", new { id = "960001", fixturePidFile = pidFile }, progress => { throw new InvalidOperationException("expected fixture subscriber failure"); }).GetAwaiter().GetResult();
                }
                catch (InvalidOperationException ex) { expectedFailure = ex.Message == "expected fixture subscriber failure"; }
                int pid = WaitForFixturePid(pidFile);
                Require(expectedFailure && Processes.ActiveCount == 0 && ProcessHasExited(pid), "Callback failure abandoned its owned worker.");
                Require(Directory.GetFiles(Path.Combine(root, "jobs"), "*.request.json").Length == 1, "Failed job diagnostics were not retained.");
                return new { realFixturePid = pid, subscriberFailureSurfaced = expectedFailure, ownedWorkerExited = true, activeCount = Processes.ActiveCount };
            });
            Check(results, "cancelled_preview_closes_window_that_arrives_after_ipc_return", delegate
            {
                var pending = new PendingPreviewWindows();
                DateTime now = new DateTime(2026, 10, 24, 15, 0, 0, DateTimeKind.Utc);
                DateTime start = now;
                bool windowArrived = false, closed = false;
                int waits = 0, closeAttempts = 0;
                pending.Track("owned-delayed-fixture-preview");
                pending.Drain(title => { Require(title == "owned-delayed-fixture-preview", "Drain touched an unowned window title."); closeAttempts++; if (windowArrived) closed = true; },
                    () => now, milliseconds => { waits++; now = now.AddMilliseconds(milliseconds); if (now >= start.AddMilliseconds(200)) windowArrived = true; });
                Require(closed && waits == 60 && now == start.AddSeconds(3), "Late preview was not closed within the bounded drain interval.");
                return new { fakeClockUsed = true, delayedWindowClosed = closed, closeAttempts = closeAttempts, waitBudgetMilliseconds = 3000 };
            });
            Check(results, "completed_preview_title_does_not_delay_shutdown", delegate
            {
                var pending = new PendingPreviewWindows();
                pending.Track("completed-fixture"); pending.Complete("completed-fixture");
                int waits = 0, closes = 0;
                pending.Drain(title => closes++, () => DateTime.UtcNow, milliseconds => waits++);
                Require(closes == 0 && waits == 0, "No pending preview should cause no shutdown polling.");
                return new { closeAttempts = closes, waits = waits };
            });
            Check(results, "owned_process_cancellation_terminates_actual_fixture_child", delegate
            {
                string pidFile = Path.Combine(fixtureRoot, "cancellation-child.pid");
                using (var cancellation = new CancellationTokenSource())
                {
                    Task<RunResult> running = StartFixtureChild(pidFile, cancellation.Token);
                    int pid = WaitForFixturePid(pidFile);
                    Require(Processes.ActiveCount == 1, "Live child was not tracked.");
                    cancellation.Cancel();
                    bool cancelled = false;
                    try { running.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
                    Require(cancelled && Processes.ActiveCount == 0 && ProcessHasExited(pid), "Cancellation leaked a real owned child.");
                    return new { realFixturePid = pid, cancellationObserved = cancelled, childExited = true, activeCount = Processes.ActiveCount };
                }
            });
            Check(results, "shutdown_terminates_actual_owned_fixture_child", delegate
            {
                string pidFile = Path.Combine(fixtureRoot, "shutdown-child.pid");
                Task<RunResult> running = StartFixtureChild(pidFile, CancellationToken.None);
                int pid = WaitForFixturePid(pidFile);
                Processes.Shutdown();
                bool cancelled = false;
                try { running.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled && Processes.ActiveCount == 0 && ProcessHasExited(pid), "Shutdown leaked a real owned child.");
                return new { realFixturePid = pid, childExited = true, activeCount = Processes.ActiveCount };
            });
        }

        private static Task<RunResult> StartFixtureChild(string pidFile, CancellationToken cancellation)
        {
            return Processes.Run(Assembly.GetExecutingAssembly().Location, "--fixture-wait " + Processes.Quote(pidFile), Path.GetDirectoryName(pidFile), 30, cancellation);
        }
        private static int WaitForFixturePid(string path)
        {
            Require(SpinWait.SpinUntil(() => File.Exists(path) && new FileInfo(path).Length > 0, 10000), "Fixture child did not start.");
            return Int32.Parse(File.ReadAllText(path));
        }
        private static bool ProcessHasExited(int pid)
        {
            try { using (var process = Process.GetProcessById(pid)) return process.HasExited; }
            catch (ArgumentException) { return true; }
        }
    }
}
