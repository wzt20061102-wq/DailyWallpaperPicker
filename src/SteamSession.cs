// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DailyWallpaperPicker
{
    internal sealed class SteamReadiness
    {
        public bool Online;
        public string Message;
        public TimeSpan RetryAfter;
        internal static SteamReadiness Ready() { return new SteamReadiness { Online = true, Message = "", RetryAfter = TimeSpan.Zero }; }
        internal static SteamReadiness Waiting(string message, TimeSpan retryAfter)
        {
            return new SteamReadiness { Online = false, Message = message, RetryAfter = retryAfter };
        }
    }

    internal enum SteamProcessState { NotRunning, Running, Unknown }

    internal sealed class SteamProcessObservation
    {
        public int? SessionId;
        public string ExecutablePath;
    }

    // Steam is a user-owned application. Its lifetime is never attached to this
    // program's worker-process collection, cancellation, or shutdown handler.
    internal sealed class SteamSession
    {
        readonly Func<string, SteamProcessState> inspectProcess;
        readonly Func<string> resolveExecutable;
        readonly Action<string> launch;
        readonly Func<Task<bool>> probeOnline;
        readonly Func<DateTime> clock;
        readonly CancellationToken cancellation;
        readonly Action<string> log;
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        string launchBatch;
        int launchAttempts;
        DateTime nextLaunchUtc = DateTime.MinValue;
        DateTime lastOnlineUtc = DateTime.MinValue;
        bool onlineCached;
        static readonly TimeSpan ShortRetry = TimeSpan.FromSeconds(15);
        static readonly TimeSpan SlowRetry = TimeSpan.FromSeconds(60);

        internal SteamSession(PickerConfig config, SteamClient client, Func<DateTime> clock, CancellationToken token, Action<string> log)
            : this(InspectProcesses, () => ResolveInstalledSteam(config), LaunchDetached,
                async () => IsOnlineReply(await client.Call("status", new { }, null).ConfigureAwait(false)), clock, token, log)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (client == null) throw new ArgumentNullException("client");
        }

        internal SteamSession(Func<string, SteamProcessState> inspectProcess, Func<string> resolveExecutable,
            Action<string> launch, Func<Task<bool>> probeOnline, Func<DateTime> clock, CancellationToken token, Action<string> log)
        {
            if (inspectProcess == null || resolveExecutable == null || launch == null || probeOnline == null || clock == null)
                throw new ArgumentNullException("Steam session delegates must not be null.");
            this.inspectProcess = inspectProcess;
            this.resolveExecutable = resolveExecutable;
            this.launch = launch;
            this.probeOnline = probeOnline;
            this.clock = clock;
            cancellation = token;
            this.log = log;
        }

        internal async Task<SteamReadiness> EnsureOnlineAsync(string batch)
        {
            DateTime parsedBatch;
            if (!DateTime.TryParseExact(batch, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedBatch))
                throw new ArgumentException("A recommendation date is required.", "batch");
            cancellation.ThrowIfCancellationRequested();
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                DateTime now = clock();
                if (onlineCached && now >= lastOnlineUtc && now - lastOnlineUtc < TimeSpan.FromSeconds(5)) return SteamReadiness.Ready();
                onlineCached = false;
                // An old in-flight batch must not reset the launch budget after a
                // newer day's operation has already claimed it.
                if (launchBatch == null || String.CompareOrdinal(batch, launchBatch) > 0)
                {
                    launchBatch = batch;
                    launchAttempts = 0;
                }
                string executable = null;
                try { executable = resolveExecutable(); }
                catch (Exception ex) { Log("STEAM discovery unavailable: " + ex.GetType().Name); }
                SteamProcessState processState;
                try { processState = inspectProcess(executable); }
                catch (Exception ex)
                {
                    processState = SteamProcessState.Unknown;
                    Log("STEAM process identity unavailable: " + ex.GetType().Name);
                }
                cancellation.ThrowIfCancellationRequested();
                if (processState == SteamProcessState.NotRunning)
                {
                    if (String.IsNullOrWhiteSpace(executable))
                        return SteamReadiness.Waiting("没有找到已安装的 Steam，请手动打开 Steam；连接后会自动准备壁纸。", SlowRetry);
                    if (launchAttempts >= 3)
                        return SteamReadiness.Waiting("Steam 尚未启动，请手动打开一次；连接后会自动准备壁纸。", SlowRetry);
                    if (now < nextLaunchUtc)
                        return SteamReadiness.Waiting("正在等待 Steam 启动，连接后会自动准备壁纸。", ShortRetry);
                    cancellation.ThrowIfCancellationRequested();
                    launchAttempts++;
                    nextLaunchUtc = now.AddSeconds(60);
                    try
                    {
                        launch(executable);
                        Log("STEAM launch requested batch=" + batch + " attempt=" + launchAttempts);
                    }
                    catch (Exception ex)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        Log("STEAM launch failed: " + ex.GetType().Name);
                        return SteamReadiness.Waiting("Steam 启动暂未成功，稍后会自动重试；也可以手动打开 Steam。", SlowRetry);
                    }
                }
                bool online;
                try { online = await AwaitProbeAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) { if (cancellation.IsCancellationRequested) throw; online = false; }
                catch (Exception ex)
                {
                    Log("STEAM connection not ready: " + ex.GetType().Name);
                    online = false;
                }
                cancellation.ThrowIfCancellationRequested();
                if (!online)
                {
                    Log("STEAM connection waiting batch=" + batch);
                    return SteamReadiness.Waiting("正在等待 Steam 连接或登录，连接后会自动准备壁纸。", ShortRetry);
                }
                lastOnlineUtc = clock();
                onlineCached = true;
                Log("STEAM connection ready batch=" + batch);
                return SteamReadiness.Ready();
            }
            finally { gate.Release(); }
        }

        async Task<bool> AwaitProbeAsync()
        {
            cancellation.ThrowIfCancellationRequested();
            Task<bool> pending = probeOnline();
            if (pending == null) throw new InvalidOperationException("Steam status returned no task.");
            var interrupted = new TaskCompletionSource<bool>();
            using (cancellation.Register(() => interrupted.TrySetResult(true)))
            {
                if (await Task.WhenAny(pending, interrupted.Task).ConfigureAwait(false) != pending)
                {
                    ObserveFailure(pending);
                    cancellation.ThrowIfCancellationRequested();
                }
                cancellation.ThrowIfCancellationRequested();
                return await pending.ConfigureAwait(false);
            }
        }

        internal static bool IsOnlineReply(WorkerReply reply)
        {
            return reply != null && reply.ok && reply.appId == 431960 && reply.loggedOn == true;
        }

        static void ObserveFailure(Task<bool> pending)
        {
            pending.ContinueWith(t => { var ignored = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        void Log(string message) { try { if (log != null) log(message); } catch { } }

        internal static string VerifyExecutableCandidate(string value, bool directory)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(value)) return null;
                value = Environment.ExpandEnvironmentVariables(value.Trim());
                if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"') value = value.Substring(1, value.Length - 2);
                if (value.IndexOf('"') >= 0 || value.IndexOf('\0') >= 0) return null;
                bool driveAbsolute = value.Length >= 3 && Char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/');
                if (!driveAbsolute && !value.StartsWith(@"\\", StringComparison.Ordinal)) return null;
                string path = Path.GetFullPath(directory ? Path.Combine(value, "steam.exe") : value);
                if (!String.Equals(Path.GetFileName(path), "steam.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
                return path;
            }
            catch (Exception) { return null; }
        }

        static string ResolveInstalledSteam(PickerConfig config)
        {
            var candidates = new List<Tuple<string, bool>>();
            AddRegistryCandidates(candidates, RegistryHive.CurrentUser, RegistryView.Registry64);
            AddRegistryCandidates(candidates, RegistryHive.CurrentUser, RegistryView.Registry32);
            AddRegistryCandidates(candidates, RegistryHive.LocalMachine, RegistryView.Registry64);
            AddRegistryCandidates(candidates, RegistryHive.LocalMachine, RegistryView.Registry32);
            candidates.Add(Tuple.Create(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"), true));
            candidates.Add(Tuple.Create(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"), true));
            // A secondary library is not necessarily the client install. Only use
            // this fallback when an actual steam.exe exists beside steamapps.
            if (config != null && !String.IsNullOrWhiteSpace(config.SteamBin)) try
            {
                DirectoryInfo current = new DirectoryInfo(Path.GetFullPath(config.SteamBin));
                while (current != null)
                {
                    if (String.Equals(current.Name, "steamapps", StringComparison.OrdinalIgnoreCase) && current.Parent != null)
                    { candidates.Add(Tuple.Create(current.Parent.FullName, true)); break; }
                    current = current.Parent;
                }
            }
            catch (Exception) { }
            foreach (var candidate in candidates)
            {
                string result = VerifyExecutableCandidate(candidate.Item1, candidate.Item2);
                if (result != null) return result;
            }
            return null;
        }

        static void AddRegistryCandidates(List<Tuple<string, bool>> candidates, RegistryHive hive, RegistryView view)
        {
            try
            {
                using (var root = RegistryKey.OpenBaseKey(hive, view))
                using (var key = root.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (key == null) return;
                    candidates.Add(Tuple.Create(Convert.ToString(key.GetValue("SteamExe")), false));
                    candidates.Add(Tuple.Create(Convert.ToString(key.GetValue("SteamPath")), true));
                    candidates.Add(Tuple.Create(Convert.ToString(key.GetValue("InstallPath")), true));
                }
            }
            catch (Exception) { }
        }

        static SteamProcessState InspectProcesses(string executable)
        {
            int session;
            using (var current = Process.GetCurrentProcess()) session = current.SessionId;
            var observations = new List<SteamProcessObservation>();
            foreach (var process in Process.GetProcessesByName("steam"))
            {
                using (process)
                {
                    var observation = new SteamProcessObservation();
                    try { observation.SessionId = process.SessionId; } catch (Exception) { }
                    if (observation.SessionId == session) try { observation.ExecutablePath = process.MainModule.FileName; } catch (Exception) { }
                    observations.Add(observation);
                }
            }
            return ClassifyProcesses(session, executable, observations);
        }

        internal static SteamProcessState ClassifyProcesses(int session, string executable, IEnumerable<SteamProcessObservation> observations)
        {
            bool unknown = false;
            foreach (var observation in observations)
            {
                if (observation == null || !observation.SessionId.HasValue) { unknown = true; continue; }
                if (observation.SessionId.Value != session) continue;
                try
                {
                    if (!String.IsNullOrWhiteSpace(executable) && !String.IsNullOrWhiteSpace(observation.ExecutablePath) &&
                        String.Equals(Path.GetFullPath(executable), Path.GetFullPath(observation.ExecutablePath), StringComparison.OrdinalIgnoreCase)) return SteamProcessState.Running;
                }
                catch (Exception) { }
                // An inaccessible or different same-name process must not cause
                // duplicate launch attempts. The SDK probe is the authority.
                unknown = true;
            }
            return unknown ? SteamProcessState.Unknown : SteamProcessState.NotRunning;
        }

        static void LaunchDetached(string executable)
        {
            string verified = VerifyExecutableCandidate(executable, false);
            if (verified == null) throw new IOException("The installed Steam executable is unavailable.");
            var guard = Processes.BeforeStart;
            if (guard != null) guard(verified, "-silent");
            var start = new ProcessStartInfo(verified, "-silent")
            {
                WorkingDirectory = Path.GetDirectoryName(verified),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                RedirectStandardInput = false
            };
            using (var process = Process.Start(start))
                if (process == null) throw new IOException("Steam did not start.");
        }
    }
}
