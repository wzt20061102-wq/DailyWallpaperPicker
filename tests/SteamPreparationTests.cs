// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DailyWallpaperPicker
{
    public static partial class SelfTests
    {
        private static void SteamPreparationRegressionTests(List<TestResult> results, string fixtureRoot)
        {
            Check(results, "steam_ready_22_49_is_idle_and_22_50_missing_batch_checks_connection", delegate
            {
                using (var fixture = new SteamPreparationFixture(fixtureRoot, "s01", SteamPreparationTime(14, 49, 0), (state, project) => state.Candidates = SteamPreparationCards(project, true)))
                {
                    string current = Json.Encode(fixture.Controller.State.Candidates);
                    fixture.Tick();
                    Require(fixture.ReadinessCalls == 0 && fixture.WorkerCalls == 0, "Ready 22:49 batch unexpectedly contacted Steam.");
                    fixture.Now = SteamPreparationTime(14, 50, 0);
                    fixture.Tick();
                    Require(fixture.ReadinessCalls == 1 && fixture.WorkerCalls == 0, "22:50 did not gate the missing next batch before querying.");
                    Require(fixture.ReadinessBatches.Single() == Schedule.Prepare(fixture.Now), "Connection gate received the wrong preparation date.");
                    Require(Json.Encode(fixture.Controller.State.Candidates) == current, "Preparing the next batch changed the displayed cards.");
                    fixture.AssertIsolated();
                    return new { idleBefore2250 = true, readinessCallsAt2250 = 1, realSteamCalls = 0, displayedCardsUnchanged = true };
                }
            });
            Check(results, "steam_offline_preparation_resumes_automatically_after_minimum_15_seconds", delegate
            {
                using (var fixture = new SteamPreparationFixture(fixtureRoot, "s02", SteamPreparationTime(15, 5, 0), null))
                {
                    fixture.RetryAfter = TimeSpan.Zero;
                    fixture.Tick();
                    Require(fixture.WorkerCalls == 0 && fixture.ReadinessCalls == 1, "Offline readiness allowed a Steam query.");
                    Require(fixture.Controller.State.MessageSeverity == MessageSeverity.Info && fixture.Controller.State.Message == "fixture waiting for Steam", "Offline readiness was presented as an error.");
                    fixture.Online = true;
                    fixture.Now = fixture.Now.AddSeconds(14);
                    fixture.Tick();
                    Require(fixture.ReadinessCalls == 1 && fixture.WorkerCalls == 0, "Minimum connection retry delay was bypassed.");
                    fixture.Now = fixture.Now.AddSeconds(1);
                    fixture.Tick();
                    Require(fixture.QueryCalls == 3 && fixture.DownloadCalls == 3, "Automatic tick did not resume all three query/download pairs.");
                    fixture.AssertReady();
                    fixture.AssertIsolated();
                    return new { blockedWorkerCallsWhileOffline = true, automaticRetrySeconds = 15, queryCalls = fixture.QueryCalls, downloadCalls = fixture.DownloadCalls, explicitRetryCalled = false };
                }
            });
            Check(results, "steam_disconnection_before_download_keeps_cards_pending_then_resumes", delegate
            {
                using (var fixture = new SteamPreparationFixture(fixtureRoot, "s03", SteamPreparationTime(15, 5, 0), null))
                {
                    fixture.ReadinessBehavior = batch => Task.FromResult(new SteamReadiness { Online = fixture.ReadinessCalls <= 3, Message = "fixture download waiting", RetryAfter = TimeSpan.FromSeconds(15) });
                    fixture.Tick();
                    var waiting = fixture.Controller.State;
                    Require(fixture.QueryCalls == 3 && fixture.DownloadCalls == 0 && fixture.ReadinessCalls == 4, "First download bypassed readiness after selection.");
                    Require(waiting.Candidates.Count == 3 && waiting.Candidates.All(c => !c.Ready && c.Stage == DownloadStage.Pending && c.Error == null), "Disconnected downloads were marked failed or lost selected cards.");
                    Require(waiting.MessageSeverity == MessageSeverity.Info, "Download connection wait was not informational.");
                    string ids = String.Join(",", waiting.Candidates.Select(c => c.Id));
                    fixture.ReadinessBehavior = null;
                    fixture.Online = true;
                    fixture.Now = fixture.Now.AddSeconds(15);
                    fixture.Tick();
                    Require(fixture.QueryCalls == 3 && fixture.DownloadCalls == 3 && ids == String.Join(",", fixture.Controller.State.Candidates.Select(c => c.Id)), "Reconnect repeated selection or failed to resume downloads.");
                    fixture.AssertReady();
                    fixture.AssertIsolated();
                    return new { queriesBeforeDisconnect = 3, downloadsWhileOffline = 0, resumedDownloads = 3, candidateIdsPreserved = true };
                }
            });
            Check(results, "steam_verified_local_cache_recovers_without_a_connection_probe", delegate
            {
                using (var fixture = new SteamPreparationFixture(fixtureRoot, "s04", SteamPreparationTime(15, 5, 0), (state, project) =>
                {
                    state.Candidates = SteamPreparationCards(null, false);
                    string source = Path.GetDirectoryName(project);
                    string cache = Path.Combine(Path.GetDirectoryName(source), "cache");
                    foreach (var card in state.Candidates) CacheStore.Import(source, cache, card.Id, 1024 * 1024);
                }))
                {
                    fixture.Tick();
                    Require(fixture.ReadinessCalls == 0 && fixture.WorkerCalls == 0, "Complete local cache unnecessarily checked or started Steam.");
                    fixture.AssertReady();
                    fixture.AssertIsolated();
                    return new { restoredVerifiedCopies = 3, readinessCalls = 0, workerCalls = 0 };
                }
            });
            Check(results, "steam_offline_missing_first_card_does_not_block_later_cached_cards", delegate
            {
                using (var fixture = new SteamPreparationFixture(fixtureRoot, "s14", SteamPreparationTime(15, 5, 0), (state, project) =>
                {
                    state.Candidates = SteamPreparationCards(null, false);
                    string source = Path.GetDirectoryName(project);
                    string cache = Path.Combine(Path.GetDirectoryName(source), "cache");
                    foreach (var card in state.Candidates.Skip(1)) CacheStore.Import(source, cache, card.Id, 1024 * 1024);
                }))
                {
                    fixture.Tick();
                    var waiting = fixture.Controller.State;
                    Require(fixture.ReadinessCalls == 1 && fixture.WorkerCalls == 0, "Mixed cache recovery repeated the connection probe or called Steam while offline.");
                    Require(waiting.Candidates.Count == 3 && !waiting.Candidates[0].Ready && waiting.Candidates[0].Stage == DownloadStage.Pending && waiting.Candidates[0].Error == null, "Missing first card was not left waiting for Steam.");
                    var restored = waiting.Candidates.Skip(1).ToList();
                    Require(restored.All(c => c.Ready && c.Stage == DownloadStage.Ready && File.Exists(c.ProjectPath)), "An earlier missing download blocked later complete local copies.");
                    foreach (var card in restored) Require(CacheStore.Verify(card.CachePath) == card.ProjectPath, "Recovered later cache did not pass verification.");
                    var saved = Json.Read<AppState>(Path.Combine(fixture.Controller.DataDirectory, "state.json"));
                    Require(!saved.Candidates[0].Ready && saved.Candidates.Skip(1).All(c => c.Ready), "Mixed local-cache recovery was not persisted.");
                    Require(waiting.MessageSeverity == MessageSeverity.Info, "Remaining connection wait was not informational.");
                    fixture.Online = true;
                    fixture.Now = fixture.Now.AddSeconds(15);
                    fixture.Tick();
                    Require(fixture.ReadinessCalls == 2 && fixture.QueryCalls == 0 && fixture.DownloadCalls == 1, "Reconnect reselected or redownloaded already recovered local copies.");
                    fixture.AssertReady();
                    fixture.AssertIsolated();
                    return new { restoredWhileOffline = 2, offlineReadinessCalls = 1, recoveredMissingDownloads = 1, queryCalls = fixture.QueryCalls, manualRetryRequired = false };
                }
            });
            Check(results, "steam_current_batch_repair_uses_current_launch_date_and_download_gate", delegate
            {
                using (var fixture = new SteamPreparationFixture(fixtureRoot, "s05", SteamPreparationTime(14, 55, 0), (state, project) =>
                {
                    state.Candidates = SteamPreparationCards(null, false);
                    state.PreparedBatchId = Schedule.Prepare(SteamPreparationTime(14, 55, 0));
                    state.PreparedCandidates = SteamPreparationCards(project, true);
                    foreach (var card in state.PreparedCandidates) card.Id = (Int32.Parse(card.Id) + 10).ToString();
                }))
                {
                    string prepared = Json.Encode(fixture.Controller.State.PreparedCandidates);
                    fixture.Tick();
                    Require(fixture.ReadinessCalls == 1 && fixture.WorkerCalls == 0, "Repairing the old visible batch bypassed the connection gate.");
                    Require(fixture.ReadinessBatches.Single() == Schedule.Prepare(fixture.Now), "Repair used yesterday as the daily Steam launch key.");
                    Require(fixture.Controller.State.Candidates[0].Stage == DownloadStage.Pending && fixture.Controller.State.Candidates[0].Error == null, "Offline repair marked a download as failed.");
                    Require(Json.Encode(fixture.Controller.State.PreparedCandidates) == prepared, "Old-batch repair changed tonight's prepared cards.");
                    fixture.AssertIsolated();
                    return new { currentBatch = fixture.Controller.State.BatchId, launchDate = fixture.ReadinessBatches.Single(), offlineDownloads = 0, nextBatchUnchanged = true };
                }
            });
            Check(results, "steam_seed_history_lookup_is_also_connection_gated", delegate
            {
                using (var fixture = new SteamPreparationFixture(fixtureRoot, "s06", SteamPreparationTime(15, 5, 0), (state, project) => state.SeedImported = false))
                {
                    fixture.Tick();
                    Require(fixture.ReadinessCalls == 1 && fixture.WorkerCalls == 0 && !fixture.Controller.State.SeedImported, "Offline preference import bypassed readiness or prematurely consumed history.");
                    fixture.AssertIsolated();
                    return new { historyQueryBlockedOffline = true, importStillPending = true };
                }
            });
            SteamPreparationPendingCase(results, fixtureRoot, "s07", "pause", false);
            SteamPreparationPendingCase(results, fixtureRoot, "s08", "pause", true);
            SteamPreparationPendingCase(results, fixtureRoot, "s09", "prepare_boundary", false);
            SteamPreparationPendingCase(results, fixtureRoot, "s10", "due_boundary", true);
            SteamPreparationPendingCase(results, fixtureRoot, "s11", "next_day", false);
            SteamPreparationPendingCase(results, fixtureRoot, "s12", "cancel", false);
            Check(results, "steam_online_query_failure_retains_five_minute_backoff", delegate
            {
                using (var fixture = new SteamPreparationFixture(fixtureRoot, "s13", SteamPreparationTime(15, 5, 0), null))
                {
                    fixture.Online = true;
                    fixture.WorkerBehavior = (verb, input, progress) => { throw new IOException("fixture query failure"); };
                    DateTime start = fixture.Now;
                    fixture.Tick();
                    Require(fixture.QueryCalls == 1 && fixture.Controller.State.MessageSeverity == MessageSeverity.Error, "Online query failure did not retain ordinary error handling.");
                    foreach (int seconds in new[] { 15, 299 })
                    {
                        fixture.Now = start.AddSeconds(seconds);
                        fixture.Tick();
                        Require(fixture.QueryCalls == 1 && fixture.ReadinessCalls == 1, "Query failure used the short connection retry loop.");
                    }
                    fixture.Now = start.AddMinutes(5);
                    fixture.Tick();
                    Require(fixture.QueryCalls == 2 && fixture.ReadinessCalls == 2 && fixture.DownloadCalls == 0, "Five-minute query retry did not occur at its boundary.");
                    fixture.AssertIsolated();
                    return new { retrySeconds = 300, queryAttempts = 2, prematureRetries = 0 };
                }
            });
        }

        private static void SteamPreparationPendingCase(List<TestResult> results, string fixtureRoot, string name, string transition, bool repairCurrent)
        {
            Check(results, "steam_pending_probe_rejects_" + transition + (repairCurrent ? "_current_repair" : "_preparation"), delegate
            {
                DateTime start = transition == "prepare_boundary" ? SteamPreparationTime(14, 49, 59) : repairCurrent ? SteamPreparationTime(14, 59, 59) : SteamPreparationTime(15, 5, 0);
                using (var fixture = new SteamPreparationFixture(fixtureRoot, name, start, null))
                {
                    var reply = new TaskCompletionSource<SteamReadiness>();
                    fixture.ReadinessBehavior = batch => reply.Task;
                    Task operation = fixture.Controller.TickAsync();
                    Require(fixture.ReadinessCalls == 1 && !operation.IsCompleted, "Fixture did not stop inside an actual pending readiness probe.");
                    if (transition == "pause") fixture.Controller.SetPaused(true);
                    else if (transition == "prepare_boundary" || transition == "due_boundary") fixture.Now = start.AddSeconds(1);
                    else if (transition == "next_day") fixture.Now = start.AddDays(1);
                    else if (transition == "cancel") fixture.Cancellation.Cancel();
                    reply.SetResult(new SteamReadiness { Online = true });
                    operation.GetAwaiter().GetResult();
                    var state = fixture.Controller.State;
                    Require(fixture.WorkerCalls == 0, "A stale or cancelled readiness result started Steam work.");
                    Require(state.Candidates.Count == 0 && state.PreparedCandidates.Count == 0 && state.Seen.Count == 0 && state.SeedTags.Count == 0, "An obsolete readiness result published candidates or preference changes.");
                    if (transition == "pause") Require(state.Paused, "Readiness completion cleared the user's pause.");
                    if (transition != "cancel")
                    {
                        if (transition == "pause") fixture.Controller.SetPaused(false);
                        fixture.ReadinessBehavior = null;
                        fixture.Tick();
                        Require(fixture.ReadinessCalls == 2 && fixture.WorkerCalls == 0, "Stale completion stranded the preparation lock or imposed an error backoff.");
                    }
                    fixture.AssertIsolated();
                    return new { transition = transition, currentBatchRepair = repairCurrent, staleWorkerCalls = 0, stalePublications = 0, nextTickRemainsUsable = transition != "cancel" };
                }
            });
        }

        private static DateTime SteamPreparationTime(int hour, int minute, int second)
        {
            return new DateTime(2026, 10, 28, hour, minute, second, DateTimeKind.Utc);
        }

        private static List<Candidate> SteamPreparationCards(string project, bool ready)
        {
            string[] pools = { "popular", "newest", "classic" };
            return pools.Select((pool, index) => new Candidate
            {
                Id = (981001 + index).ToString(), Pool = pool, Title = "offline fixture " + pool,
                Tags = new[] { "Everyone", "Scene", "Dynamic resolution", "Landscape" }, FileSize = 100,
                CreatedUtc = 100, ProjectPath = project, Ready = ready,
                Stage = ready ? DownloadStage.Ready : DownloadStage.Pending
            }).ToList();
        }

        private sealed class SteamPreparationFixture : IDisposable
        {
            internal DateTime Now;
            internal bool Online;
            internal TimeSpan RetryAfter = TimeSpan.FromSeconds(15);
            internal int ReadinessCalls, QueryCalls, DownloadCalls;
            internal readonly List<string> ReadinessBatches = new List<string>();
            internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            internal readonly PickerController Controller;
            internal Func<string, Task<SteamReadiness>> ReadinessBehavior;
            internal Func<string, object, Action<WorkerProgress>, Task<WorkerReply>> WorkerBehavior;
            readonly SteamPreparationBridge bridge = new SteamPreparationBridge();
            readonly string source, sourceHash;
            readonly int processStarts;
            internal int WorkerCalls { get { return QueryCalls + DownloadCalls; } }

            internal SteamPreparationFixture(string fixtureRoot, string name, DateTime now, Action<AppState, string> configure)
            {
                Now = now;
                string root = Path.Combine(fixtureRoot, "sp", name);
                source = Path.Combine(root, "src");
                Directory.CreateDirectory(source);
                Json.Write(Path.Combine(source, "project.json"), new { type = "scene", file = "scene.pkg" });
                File.WriteAllText(Path.Combine(source, "scene.pkg"), "inert scene fixture, not executable media", new UTF8Encoding(false));
                sourceHash = CacheStore.Hash(Path.Combine(source, "scene.pkg"));
                var state = new AppState { BatchId = Schedule.Due(now), SeedImported = true };
                if (configure != null) configure(state, Path.Combine(source, "project.json"));
                string engineRoot = Path.Combine(root, "we");
                Directory.CreateDirectory(engineRoot);
                Json.Write(Path.Combine(engineRoot, "config.json"), new { fixture = new { general = new { wallpaperconfigrecent = @"C:\fixture\431960\986001\project.json" } } });
                Json.Write(Path.Combine(root, "config.json"), new PickerConfig { WallpaperExe = Path.Combine(engineRoot, "forbidden-engine.exe"), SteamBin = Path.Combine(root, "forbidden-steam"), MaxSizeMB = 300, RetainCount = 10 });
                Json.Write(Path.Combine(root, "state.json"), state);
                processStarts = ProcessBoundary.FixtureStarts + ProcessBoundary.UnexpectedStarts;
                Controller = new PickerController(root, () => Now, Worker, Cancellation.Token, bridge, Readiness);
            }

            Task<SteamReadiness> Readiness(string batch)
            {
                ReadinessCalls++;
                ReadinessBatches.Add(batch);
                return ReadinessBehavior == null ? Task.FromResult(new SteamReadiness { Online = Online, Message = "fixture waiting for Steam", RetryAfter = RetryAfter }) : ReadinessBehavior(batch);
            }

            Task<WorkerReply> Worker(string verb, object input, Action<WorkerProgress> progress)
            {
                if (verb == "query") QueryCalls++;
                else if (verb == "download") DownloadCalls++;
                else throw new InvalidOperationException("Unexpected worker verb: " + verb);
                if (WorkerBehavior != null) return WorkerBehavior(verb, input, progress);
                var values = Json.Decode<Dictionary<string, object>>(Json.Encode(input));
                if (verb == "query")
                {
                    string pool = Convert.ToString(values["pool"]);
                    return Task.FromResult(new WorkerReply { ok = true, items = SteamPreparationCards(null, false).Where(c => c.Pool == pool).ToList() });
                }
                return Task.FromResult(new WorkerReply { ok = true, id = Convert.ToString(values["id"]), installPath = source, appId = 431960, subscribedBefore = false, subscribedAfter = false });
            }

            internal void Tick() { Controller.TickAsync().GetAwaiter().GetResult(); }

            internal void AssertReady()
            {
                var cards = Controller.State.Candidates;
                Require(cards.Count == 3 && cards.All(c => c.Ready && c.Stage == DownloadStage.Ready && File.Exists(c.ProjectPath)), "Three verified wallpapers were not recovered/prepared.");
                foreach (var card in cards) Require(CacheStore.Verify(card.CachePath) == card.ProjectPath, "Published wallpaper copy did not pass cache verification.");
            }

            internal void AssertIsolated()
            {
                Require(processStarts == ProcessBoundary.FixtureStarts + ProcessBoundary.UnexpectedStarts, "Preparation fixture attempted to launch an external process.");
                Require(bridge.ApplyCalls == 0 && bridge.PreviewCalls == 0 && Controller.State.Choices.Count == 0, "Preparing wallpapers changed/previewed the desktop or recorded a choice.");
                Require(Directory.GetFiles(Path.Combine(Controller.DataDirectory, "jobs")).Length == 0, "Preparation fixture created a real Steam job.");
                Require(CacheStore.Hash(Path.Combine(source, "scene.pkg")) == sourceHash, "Preparing copies modified the source fixture.");
            }

            public void Dispose() { Cancellation.Dispose(); }
        }

        private sealed class SteamPreparationBridge : WallpaperBridge
        {
            internal int ApplyCalls, PreviewCalls;
            internal SteamPreparationBridge() : base("forbidden-engine.exe", message => { }) { }
            public override bool CanSafelyClean { get { return true; } }
            public override Task<string> GetCurrent() { return Task.FromResult(Path.Combine(Path.GetTempPath(), "unrelated-current-wallpaper.json")); }
            public override Task Apply(string project) { ApplyCalls++; throw new InvalidOperationException("No wallpaper application permitted in preparation tests."); }
            public override Task Preview(string project) { PreviewCalls++; throw new InvalidOperationException("No wallpaper preview permitted in preparation tests."); }
        }
    }
}
