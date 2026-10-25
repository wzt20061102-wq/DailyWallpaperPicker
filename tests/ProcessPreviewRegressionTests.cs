// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DailyWallpaperPicker
{
    public static partial class SelfTests
    {
        private static void ProcessPreviewRegressionTests(List<TestResult> results, string fixtureRoot)
        {
            Check(results, "process_timeout_includes_inherited_stdout_and_stderr", delegate { return PipeDeadlineCase(fixtureRoot, false); });
            Check(results, "process_cancellation_during_inherited_pipe_read_is_not_success", delegate { return PipeDeadlineCase(fixtureRoot, true); });
            Check(results, "process_normal_exit_preserves_output", delegate
            {
                string root = Path.Combine(fixtureRoot, "process-normal"); Directory.CreateDirectory(root);
                var result = Processes.Run(Assembly.GetExecutingAssembly().Location,
                    "--fixture-process-preview " + Processes.Quote(root) + " output", root, 5).GetAwaiter().GetResult();
                Require(result.ExitCode == 0 && result.Output.Trim() == "fixture stdout" && result.Error.Trim() == "fixture stderr", "Normal output or exit code regressed.");
                return new { output = result.Output.Trim(), error = result.Error.Trim(), exitCode = result.ExitCode };
            });
            Check(results, "preview_guard_rejects_non_owned_location_names", delegate
            {
                Require(!PreviewGuardHost.ValidTitle("Monitor0") && !PreviewGuardHost.ValidTitle("")
                    && !PreviewGuardHost.ValidTitle("DailyWallpaperPreview-ffffffff-1\" -monitor 0"), "Guard accepted an arbitrary/desktop location.");
                return new { arbitraryLocationRejected = true };
            });
            Check(results, "preview_guard_closes_8_second_late_window_after_owner_shutdown", delegate { return GuardWindowCase(fixtureRoot, "cancel", 8500); });
            Check(results, "preview_guard_survives_owner_exit_and_preserves_other_process_window", delegate { return GuardWindowCase(fixtureRoot, "exit", 1000); });
            Check(results, "successful_headless_preview_can_outlive_picker", delegate { return GuardWindowCase(fixtureRoot, "detach", 250); });
        }

        private static object PipeDeadlineCase(string fixtureRoot, bool cancel)
        {
            string root = Path.Combine(fixtureRoot, cancel ? "pipe-cancel" : "pipe-timeout"); Directory.CreateDirectory(root);
            int descendant = 0;
            var watch = Stopwatch.StartNew();
            bool expected = false;
            try
            {
                using (var source = new CancellationTokenSource())
                {
                    if (cancel) source.CancelAfter(1000);
                    try
                    {
                        Processes.Run(Assembly.GetExecutingAssembly().Location,
                            "--fixture-process-preview " + Processes.Quote(root) + " pipe-parent", root,
                            cancel ? 15 : 1, source.Token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException) { expected = cancel; }
                    catch (TimeoutException) { expected = !cancel; }
                }
                string pid = Path.Combine(root, "pipe.pid");
                Require(File.Exists(pid), "Inherited-pipe descendant never started.");
                descendant = Int32.Parse(File.ReadAllText(pid));
                Require(expected && watch.ElapsedMilliseconds < 4500 && !ProcessHasExited(descendant), "Run waited for inherited EOF or returned success after cancellation.");
                Require(Processes.ActiveCount == 0, "Runner leaked its direct child registration.");
                return new { cancelled = cancel, elapsedMilliseconds = watch.ElapsedMilliseconds, descendantWasNotKilled = true };
            }
            finally
            {
                if (descendant == 0 && File.Exists(Path.Combine(root, "pipe.pid"))) descendant = Int32.Parse(File.ReadAllText(Path.Combine(root, "pipe.pid")));
                ProcessPreviewFixtures.KillFixture(descendant, Assembly.GetExecutingAssembly().Location);
            }
        }

        private static object GuardWindowCase(string fixtureRoot, string mode, int delay)
        {
            string root = Path.Combine(fixtureRoot, "guard-" + mode); Directory.CreateDirectory(root);
            string runner = Assembly.GetExecutingAssembly().Location;
            string engine = Path.Combine(root, "FixtureWallpaperEngine.exe");
            File.Copy(runner, engine);
            File.Copy(typeof(Processes).Assembly.Location, Path.Combine(root, "DailyWallpaperPicker.exe"));
            string title = "DailyWallpaperPreview-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-1";
            IntPtr unrelated = ProcessPreviewFixtures.CreateHiddenWindow(title);
            int enginePid = 0, guardPid = 0;
            try
            {
                var watch = Stopwatch.StartNew();
                var ownerTask = Processes.Run(runner, "--fixture-process-preview " + Processes.Quote(root)
                    + " owner " + title + " " + mode + " " + delay, root, 15);
                string ownerPidFile = Path.Combine(root, "owner.pid");
                Require(SpinWait.SpinUntil(() => File.Exists(ownerPidFile) && new FileInfo(ownerPidFile).Length > 0, 5000), "Owner fixture did not start.");
                int ownerPid = Int32.Parse(File.ReadAllText(ownerPidFile));
                Require(SpinWait.SpinUntil(() => ProcessHasExited(ownerPid), 10000), "Owner process did not exit.");
                long ownerExitedAt = watch.ElapsedMilliseconds;
                var result = ownerTask.GetAwaiter().GetResult();
                Json.Write(Path.Combine(root, "owner-observation.json"), new { actualOwnerExitedMilliseconds = ownerExitedAt, runnerReturnedMilliseconds = watch.ElapsedMilliseconds });
                Require(result.ExitCode == 0, "Fixture owner failed: " + result.Error);
                enginePid = Int32.Parse(File.ReadAllText(Path.Combine(root, "engine.pid")));
                guardPid = Int32.Parse(File.ReadAllText(Path.Combine(root, "guard.pid")));
                Require(watch.ElapsedMilliseconds < 7000, "Owner waited for the delayed preview while shutting down.");
                string finished = Path.Combine(root, "window-result.json");
                Require(SpinWait.SpinUntil(() => File.Exists(finished), 18000), "Delayed window fixture did not finish.");
                var state = Json.Read<Dictionary<string, object>>(finished);
                bool closed = Convert.ToBoolean(state["closedByGuard"]);
                Require(closed == (mode != "detach"), "Guard failed to close a canceled window, or closed a deliberate headless preview.");
                Require(ProcessPreviewFixtures.WindowExists(unrelated), "Guard closed the same-title window from another executable/PID.");
                Require(SpinWait.SpinUntil(() => ProcessHasExited(guardPid), 5000), "Guard did not exit after its owned window/engine closed.");
                return new { mode = mode, delayedMilliseconds = delay, closedByGuard = closed, unrelatedSameTitlePreserved = true, helperExited = true, realEngineUsed = false };
            }
            finally
            {
                ProcessPreviewFixtures.DestroyHiddenWindow(unrelated);
                if (enginePid == 0 && File.Exists(Path.Combine(root, "engine.pid"))) enginePid = Int32.Parse(File.ReadAllText(Path.Combine(root, "engine.pid")));
                if (guardPid == 0 && File.Exists(Path.Combine(root, "guard.pid"))) guardPid = Int32.Parse(File.ReadAllText(Path.Combine(root, "guard.pid")));
                ProcessPreviewFixtures.KillFixture(enginePid, engine);
                ProcessPreviewFixtures.KillFixture(guardPid, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WallpaperReadback.exe"));
            }
        }
    }

    internal static class ProcessPreviewFixtures
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct StartupInfo { public int cb; public string reserved, desktop, title; public int x, y, xsize, ysize, xcount, ycount, fill, flags; public short show, reserved2; public IntPtr reservedPtr, input, output, error; }
        [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr process, thread; public int pid, tid; }
        [StructLayout(LayoutKind.Sequential)] struct NativeMessage { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x, y; public uint privateValue; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcess(string app, StringBuilder command, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref StartupInfo startup, out ProcessInfo result);
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int kind);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr value);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint min, uint max, uint remove);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref NativeMessage message);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref NativeMessage message);

        internal static IntPtr CreateHiddenWindow(string title)
        {
            IntPtr window = CreateWindowEx(0, "STATIC", title, 0, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (window == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return window;
        }
        internal static void DestroyHiddenWindow(IntPtr window) { if (window != IntPtr.Zero) DestroyWindow(window); }
        internal static bool WindowExists(IntPtr window) { return IsWindow(window); }
        internal static void KillFixture(int pid, string executable)
        {
            if (pid <= 0) return;
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    if (process.HasExited || !String.Equals(Path.GetFullPath(process.MainModule.FileName), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) return;
                    process.Kill(); process.WaitForExit(3000);
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        internal static bool TryRun(string[] args, out int exit)
        {
            exit = 0;
            // Only the copied fake engine receives these. It intentionally ignores the
            // close command so HWND cleanup itself must handle the late arrival.
            if (args.Length == 4 && args[0] == "-control" && args[1] == "closeWallpaper" && args[2] == "-location" && PreviewGuardHost.ValidTitle(args[3])) return true;
            if (args.Length < 3 || args[0] != "--fixture-process-preview") return false;
            try
            {
                string root = Path.GetFullPath(args[1]);
                if (!Directory.Exists(root)) throw new IOException("Fixture directory is missing.");
                string self = Assembly.GetExecutingAssembly().Location;
                if (args[2] == "run-guard-test")
                {
                    ProcessBoundary.Begin(root);
                    var method = typeof(SelfTests).GetMethod("GuardWindowCase", BindingFlags.Static | BindingFlags.NonPublic);
                    try { Json.Write(Path.Combine(root, "targeted-result.json"), method.Invoke(null, new object[] { root, args[3], Int32.Parse(args[4]) })); }
                    catch (TargetInvocationException error) { throw error.InnerException; }
                    return true;
                }
                if (args[2] == "output") { Console.WriteLine("fixture stdout"); Console.Error.WriteLine("fixture stderr"); return true; }
                if (args[2] == "pipe-hold") { Thread.Sleep(15000); return true; }
                if (args[2] == "pipe-parent")
                {
                    var startup = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)), flags = 0x100, input = GetStdHandle(-10), output = GetStdHandle(-11), error = GetStdHandle(-12) };
                    ProcessInfo process;
                    string command = Processes.Quote(self) + " --fixture-process-preview " + Processes.Quote(root) + " pipe-hold";
                    if (!CreateProcess(null, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, true, 0x08000000, IntPtr.Zero, root, ref startup, out process)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    CloseHandle(process.thread); CloseHandle(process.process);
                    File.WriteAllText(Path.Combine(root, "pipe.pid"), process.pid.ToString()); return true;
                }
                if (args[2] == "owner")
                {
                    File.WriteAllText(Path.Combine(root, "owner.pid"), Process.GetCurrentProcess().Id.ToString());
                    var ownerWatch = Stopwatch.StartNew();
                    string title = args[3], mode = args[4];
                    string engine = Path.Combine(root, "FixtureWallpaperEngine.exe");
                    var startup = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)), flags = 1, show = 0 };
                    ProcessInfo process;
                    string command = Processes.Quote(engine) + " --fixture-process-preview " + Processes.Quote(root) + " engine " + title + " " + args[5] + " " + mode;
                    if (!CreateProcess(engine, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, root, ref startup, out process))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    CloseHandle(process.thread);
                    try
                    {
                        File.WriteAllText(Path.Combine(root, "engine.pid"), process.pid.ToString());
                        if (!SpinWait.SpinUntil(() => File.Exists(Path.Combine(root, "engine.ready")), 5000)) throw new IOException("Engine fixture did not start.");
                        var pending = new PendingPreviewWindows(); pending.Arm(title, engine);
                        long armedAt = ownerWatch.ElapsedMilliseconds;
                        File.WriteAllText(Path.Combine(root, "guard.pid"), pending.GuardProcessIds.Single().ToString());
                        if (mode == "cancel") pending.Drain(t => { });
                        if (mode == "detach") pending.KeepOnOwnerExit();
                        Json.Write(Path.Combine(root, "owner-timing.json"), new { armedMilliseconds = armedAt, drainAndDetachFinishedMilliseconds = ownerWatch.ElapsedMilliseconds });
                    }
                    finally { CloseHandle(process.process); }
                    return true;
                }
                if (args[2] == "engine")
                {
                    File.WriteAllText(Path.Combine(root, "engine.ready"), "ready");
                    Thread.Sleep(Int32.Parse(args[4]));
                    IntPtr window = CreateHiddenWindow(args[3]);
                    var elapsed = Stopwatch.StartNew();
                    int duration = args[5] == "detach" ? 2000 : 6000;
                    while (IsWindow(window) && elapsed.ElapsedMilliseconds < duration)
                    {
                        NativeMessage message;
                        while (PeekMessage(out message, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessage(ref message); }
                        Thread.Sleep(10);
                    }
                    bool closed = !IsWindow(window);
                    DestroyHiddenWindow(window);
                    Json.Write(Path.Combine(root, "window-result.json"), new { closedByGuard = closed, hiddenWindow = true });
                    return true;
                }
                throw new ArgumentException("Unknown process fixture operation.");
            }
            catch (Exception error) { Console.Error.WriteLine(error); exit = 2; return true; }
        }
    }
}
