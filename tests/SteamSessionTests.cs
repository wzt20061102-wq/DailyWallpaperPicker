// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DailyWallpaperPicker
{
    public static partial class SelfTests
    {
        private sealed class SessionFixture
        {
            internal DateTime Now = new DateTime(2026, 10, 29, 14, 50, 0, DateTimeKind.Utc);
            internal SteamProcessState ProcessState = SteamProcessState.NotRunning;
            internal string Executable = @"C:\Fixture Steam\steam.exe";
            internal int Starts, Probes;
            internal bool Online;
            internal Exception StartError;
            internal Func<Task<bool>> Probe;
            internal SteamSession Create(CancellationToken token)
            {
                return new SteamSession(path => ProcessState, () => Executable, path =>
                {
                    Starts++;
                    if (StartError != null) throw StartError;
                }, () =>
                {
                    Probes++;
                    return Probe == null ? Task.FromResult(Online) : Probe();
                }, () => Now, token, null);
            }
        }

        private static void SteamSessionRegressionTests(List<TestResult> results, string fixtureRoot)
        {
            Check(results, "steam_session_online_existing_client_is_not_relaunched_and_cache_expires", delegate
            {
                var f = new SessionFixture { ProcessState = SteamProcessState.Running, Online = true };
                var session = f.Create(CancellationToken.None);
                Require(session.EnsureOnlineAsync("2026-10-29").GetAwaiter().GetResult().Online, "Existing client was not accepted.");
                f.Now = f.Now.AddSeconds(4);
                Require(session.EnsureOnlineAsync("2026-10-29").GetAwaiter().GetResult().Online && f.Probes == 1, "Five-second cache was not reused.");
                f.Now = f.Now.AddSeconds(1); f.Online = false;
                Require(!session.EnsureOnlineAsync("2026-10-29").GetAwaiter().GetResult().Online && f.Probes == 2 && f.Starts == 0, "Expired online status hid disconnect or relaunched Steam.");
                return new { starts = f.Starts, probes = f.Probes, expiredStatusRechecked = true };
            });
            Check(results, "steam_session_starts_once_then_offline_to_online_without_manual_retry", delegate
            {
                var f = new SessionFixture(); var session = f.Create(CancellationToken.None);
                SteamReadiness waiting = session.EnsureOnlineAsync("2026-10-29").GetAwaiter().GetResult();
                Require(!waiting.Online && waiting.RetryAfter == TimeSpan.FromSeconds(15) && f.Starts == 1, "Client was not started with bounded waiting.");
                f.ProcessState = SteamProcessState.Running; f.Now = f.Now.AddSeconds(15);
                Require(!session.EnsureOnlineAsync("2026-10-29").GetAwaiter().GetResult().Online, "Offline client reported online.");
                f.Online = true; f.Now = f.Now.AddSeconds(15);
                Require(session.EnsureOnlineAsync("2026-10-29").GetAwaiter().GetResult().Online && f.Starts == 1 && f.Probes == 3, "Connection did not recover without a retry button.");
                return new { launches = f.Starts, automaticProbes = f.Probes, recovered = true };
            });
            Check(results, "steam_session_launch_throttle_budget_manual_recovery_and_new_day", delegate
            {
                var f = new SessionFixture(); var session = f.Create(CancellationToken.None);
                for (int n = 0; n < 13; n++)
                {
                    session.EnsureOnlineAsync("2026-10-29").GetAwaiter().GetResult();
                    f.Now = f.Now.AddSeconds(15);
                }
                Require(f.Starts == 3, "Launch limit or sixty-second spacing failed.");
                f.ProcessState = SteamProcessState.Running; f.Online = true;
                Require(session.EnsureOnlineAsync("2026-10-29").GetAwaiter().GetResult().Online, "Exhausted launch budget blocked a manually opened client.");
                f.Now = f.Now.AddDays(1); f.ProcessState = SteamProcessState.NotRunning; f.Online = false;
                session.EnsureOnlineAsync("2026-10-30").GetAwaiter().GetResult();
                Require(f.Starts == 4, "New date did not receive a new launch budget.");
                for (int n = 0; n < 2; n++) { f.Now = f.Now.AddSeconds(60); session.EnsureOnlineAsync("2026-10-30").GetAwaiter().GetResult(); }
                f.Now = f.Now.AddSeconds(60); session.EnsureOnlineAsync("2026-10-29").GetAwaiter().GetResult();
                Require(f.Starts == 6, "Late old batch reset the new batch launch limit.");
                return new { launchBudgetPerDate = 3, manualOpenAfterLimitWorks = true, olderBatchDoesNotResetBudget = true };
            });
            Check(results, "steam_session_start_failure_is_slow_and_does_not_probe_or_storm", delegate
            {
                var f = new SessionFixture { StartError = new IOException("fixture start refused") }; var session = f.Create(CancellationToken.None);
                var failed = session.EnsureOnlineAsync("2026-10-29").GetAwaiter().GetResult();
                Require(!failed.Online && failed.RetryAfter == TimeSpan.FromSeconds(60) && f.Starts == 1 && f.Probes == 0, "Launch failure was not bounded.");
                f.Now = f.Now.AddSeconds(15); session.EnsureOnlineAsync("2026-10-29").GetAwaiter().GetResult();
                Require(f.Starts == 1, "A failed launch was immediately repeated.");
                return new { starts = f.Starts, probes = f.Probes, friendlyMessage = !String.IsNullOrWhiteSpace(failed.Message) };
            });
            Check(results, "steam_session_missing_install_does_not_launch_and_unknown_identity_only_probes", delegate
            {
                var f = new SessionFixture { Executable = null }; var session = f.Create(CancellationToken.None);
                var missing = session.EnsureOnlineAsync("2026-10-29").GetAwaiter().GetResult();
                Require(!missing.Online && missing.RetryAfter == TimeSpan.FromSeconds(60) && f.Starts == 0 && f.Probes == 0, "Missing installation launched or probed a nonexistent client.");
                f.ProcessState = SteamProcessState.Unknown; f.Online = true;
                Require(session.EnsureOnlineAsync("2026-10-29").GetAwaiter().GetResult().Online && f.Starts == 0 && f.Probes == 1, "Unknown identity caused a duplicate launch or rejected a valid SDK session.");
                return new { starts = f.Starts, sdkResolvedUnknownIdentity = true };
            });
            Check(results, "steam_session_current_session_and_path_identity_fail_closed", delegate
            {
                string exe = @"C:\Steam\steam.exe";
                Require(SteamSession.ClassifyProcesses(1, exe, new[] { new SteamProcessObservation { SessionId = 2, ExecutablePath = exe } }) == SteamProcessState.NotRunning, "Another session hid the missing current-user client.");
                Require(SteamSession.ClassifyProcesses(1, exe, new[] { new SteamProcessObservation { SessionId = 1, ExecutablePath = @"C:\Other\steam.exe" } }) == SteamProcessState.Unknown, "A different path was treated as verified or absent.");
                Require(SteamSession.ClassifyProcesses(1, exe, new[] { new SteamProcessObservation { SessionId = 1 } }) == SteamProcessState.Unknown, "Unreadable path was treated as absent.");
                Require(SteamSession.ClassifyProcesses(1, exe, new[] { new SteamProcessObservation() }) == SteamProcessState.Unknown, "Unreadable session was treated as absent.");
                Require(SteamSession.ClassifyProcesses(1, exe, new[] { new SteamProcessObservation { SessionId = 1, ExecutablePath = @"c:\STEAM\steam.exe" } }) == SteamProcessState.Running, "Correct client path was not recognized.");
                return new { otherSessionIgnored = true, unknownIdentityNeverStartsDuplicate = true, matchingPathAccepted = true };
            });
            Check(results, "steam_session_probe_failure_does_not_restart_running_steam", delegate
            {
                var f = new SessionFixture { ProcessState = SteamProcessState.Running, Probe = () => { throw new IOException("Steam API unavailable"); } };
                var session = f.Create(CancellationToken.None);
                var waiting = session.EnsureOnlineAsync("2026-10-29").GetAwaiter().GetResult();
                Require(!waiting.Online && waiting.RetryAfter == TimeSpan.FromSeconds(15) && f.Starts == 0, "API failure restarted an existing Steam process.");
                f.Probe = null; f.Online = true;
                Require(session.EnsureOnlineAsync("2026-10-29").GetAwaiter().GetResult().Online && f.Starts == 0, "API recovery needed a manual retry.");
                Require(!SteamSession.IsOnlineReply(null) && !SteamSession.IsOnlineReply(new WorkerReply { ok = true, appId = 431960 }) &&
                    !SteamSession.IsOnlineReply(new WorkerReply { ok = true, appId = 1, loggedOn = true }) &&
                    !SteamSession.IsOnlineReply(new WorkerReply { ok = false, appId = 431960, loggedOn = true }) &&
                    SteamSession.IsOnlineReply(new WorkerReply { ok = true, appId = 431960, loggedOn = true }), "Unverified or wrong-app status was accepted.");
                return new { starts = f.Starts, statusProtocolChecked = true };
            });
            Check(results, "steam_session_cancellation_before_launch_and_during_probe_is_prompt", delegate
            {
                var f = new SessionFixture();
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel(); bool rejected = false;
                    try { f.Create(cancelled.Token).EnsureOnlineAsync("2026-10-29").GetAwaiter().GetResult(); } catch (OperationCanceledException) { rejected = true; }
                    Require(rejected && f.Starts == 0 && f.Probes == 0, "Pre-cancelled operation started Steam.");
                }
                using (var cancelled = new CancellationTokenSource())
                {
                    var pending = new TaskCompletionSource<bool>(); f.Probe = () => pending.Task;
                    Task<SteamReadiness> operation = f.Create(cancelled.Token).EnsureOnlineAsync("2026-10-29");
                    Require(f.Starts == 1 && f.Probes == 1, "Probe fixture was not entered.");
                    cancelled.Cancel();
                    bool rejected = false;
                    try { if (!((IAsyncResult)operation).AsyncWaitHandle.WaitOne(2000)) throw new Exception("Cancellation did not complete promptly."); operation.GetAwaiter().GetResult(); } catch (OperationCanceledException) { rejected = true; }
                    Require(rejected && !pending.Task.IsCompleted && f.Starts == 1, "Cancellation altered the detached application or waited for probe completion.");
                    pending.SetException(new IOException("late fixture status failure"));
                }
                return new { cancelledBeforeStart = true, cancelledDuringProbe = true, realProcessesStarted = 0 };
            });
            Check(results, "steam_session_concurrent_calls_share_one_probe_and_launch", delegate
            {
                var f = new SessionFixture(); var pending = new TaskCompletionSource<bool>(); f.Probe = () => pending.Task;
                var session = f.Create(CancellationToken.None);
                var first = session.EnsureOnlineAsync("2026-10-29");
                var second = session.EnsureOnlineAsync("2026-10-29");
                Require(f.Starts == 1 && f.Probes == 1 && !second.IsCompleted, "Concurrent request bypassed session coordination.");
                pending.SetResult(true);
                Require(first.GetAwaiter().GetResult().Online && second.GetAwaiter().GetResult().Online && f.Starts == 1 && f.Probes == 1, "Concurrent requests duplicated startup or probe.");
                return new { starts = f.Starts, probes = f.Probes };
            });
            Check(results, "steam_session_executable_discovery_requires_existing_absolute_steam_path", delegate
            {
                string directory = Path.Combine(fixtureRoot, "steam-path-with-spaces"); Directory.CreateDirectory(directory);
                string exe = Path.Combine(directory, "steam.exe"); File.WriteAllText(exe, "not executable; path verification fixture only");
                Require(SteamSession.VerifyExecutableCandidate(exe, false) == exe && SteamSession.VerifyExecutableCandidate(directory, true) == exe, "Verified installed path was not accepted.");
                Require(SteamSession.VerifyExecutableCandidate('"' + exe + '"', false) == exe, "Quoted registry path was not normalized.");
                Require(SteamSession.VerifyExecutableCandidate("steam.exe", false) == null && SteamSession.VerifyExecutableCandidate(@"C:steam.exe", false) == null &&
                    SteamSession.VerifyExecutableCandidate('"' + exe + "\" -silent", false) == null && SteamSession.VerifyExecutableCandidate(Path.Combine(directory, "missing", "steam.exe"), false) == null, "A relative, argument-bearing or missing path was accepted.");
                string wrong = Path.Combine(directory, "other.exe"); File.WriteAllText(wrong, "wrong-name fixture");
                Require(SteamSession.VerifyExecutableCandidate(wrong, false) == null, "Wrong executable name was accepted.");
                return new { absolutePathRequired = true, fileMustExist = true, noFixtureExecuted = true };
            });
        }
    }
}
