// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DailyWallpaperPicker
{
    public sealed class RunResult
    {
        public int ExitCode;
        public string Output;
        public string Error;
    }

    // Tracks only child processes started by this application, never Steam itself.
    public static class Processes
    {
        static readonly object Gate = new object();
        static readonly HashSet<Process> Children = new HashSet<Process>();
        static readonly CancellationTokenSource Lifetime = new CancellationTokenSource();
        internal static Action<string, string> BeforeStart;
        internal static int ActiveCount { get { lock (Gate) return Children.Count; } }

        public static string Quote(string value)
        {
            if (value.IndexOf('"') >= 0) throw new ArgumentException("参数不能含引号");
            return "\"" + value + "\"";
        }

        public static Task<RunResult> Run(string exe, string args, string cwd, int seconds)
        {
            return Run(exe, args, cwd, seconds, CancellationToken.None, true);
        }

        public static async Task<RunResult> Run(string exe, string args, string cwd, int seconds,
            CancellationToken cancellationToken, bool redirectOutput = true)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Lifetime.Token))
            using (var process = new Process())
            {
                linked.Token.ThrowIfCancellationRequested();
                process.StartInfo = new ProcessStartInfo(exe, args)
                {
                    WorkingDirectory = cwd,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = redirectOutput,
                    RedirectStandardError = redirectOutput
                };
                if (redirectOutput)
                {
                    process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                    process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
                }
                var guard = BeforeStart;
                if (guard != null) guard(exe, args);
                lock (Gate)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    process.Start();
                    Children.Add(process);
                }
                try
                {
                    var stdout = redirectOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
                    var stderr = redirectOutput ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
                    using (linked.Token.Register(() => KillOwned(process)))
                    {
                        bool exited = await Task.Run(() => process.WaitForExit(checked(seconds * 1000))).ConfigureAwait(false);
                        linked.Token.ThrowIfCancellationRequested();
                        if (!exited)
                        {
                            KillOwned(process);
                            throw new TimeoutException("操作超时，稍后可以重试。");
                        }
                        return new RunResult
                        {
                            ExitCode = process.ExitCode,
                            Output = await stdout.ConfigureAwait(false),
                            Error = await stderr.ConfigureAwait(false)
                        };
                    }
                }
                finally
                {
                    KillOwned(process);
                    lock (Gate) Children.Remove(process);
                }
            }
        }

        static void KillOwned(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            try { process.WaitForExit(2000); }
            catch (InvalidOperationException) { }
        }

        public static void Shutdown()
        {
            Lifetime.Cancel();
            Process[] children;
            lock (Gate) children = Children.ToArray();
            foreach (var child in children) KillOwned(child);
        }
    }
}
