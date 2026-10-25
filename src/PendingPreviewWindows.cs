// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DailyWallpaperPicker
{
    // IPC can return before its window exists. Keep its unique title until observed.
    internal sealed class PendingPreviewWindows
    {
        readonly object gate = new object();
        readonly HashSet<string> titles = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, GuardSession> guards = new Dictionary<string, GuardSession>(StringComparer.Ordinal);
        internal int[] GuardProcessIds { get { lock (gate) return guards.Values.Select(g => g.ProcessId).ToArray(); } }
        internal void Track(string title) { lock (gate) titles.Add(title); }
        internal void Complete(string title) { lock (gate) titles.Remove(title); }
        internal void Arm(string title, string engine)
        {
            var guard = GuardSession.Start(title, engine);
            lock (gate) { titles.Add(title); guards.Add(title, guard); }
        }
        internal void Cancel(string title)
        {
            GuardSession guard;
            lock (gate)
            {
                if (!guards.TryGetValue(title, out guard)) return;
                guards.Remove(title);
                titles.Remove(title);
            }
            guard.Cancel();
        }
        internal void KeepOnOwnerExit()
        {
            lock (gate) foreach (var guard in guards.Values) guard.Detach();
        }
        internal void Drain(Action<string> close)
        {
            Drain(close, () => DateTime.UtcNow, milliseconds => Thread.Sleep(milliseconds));
        }
        internal void Drain(Action<string> close, Func<DateTime> clock, Action<int> wait)
        {
            string[] guarded;
            lock (gate) guarded = guards.Keys.ToArray();
            foreach (string title in guarded) Cancel(title);
            string[] pending;
            // Unarmed test/legacy entries stay tracked until positively completed.
            // A bounded drain must not forget a title merely because its HWND is still absent.
            lock (gate) pending = titles.ToArray();
            if (pending.Length == 0) return;
            DateTime deadline = clock().AddSeconds(3);
            do
            {
                foreach (string title in pending) close(title);
                wait(50);
            } while (clock() < deadline);
            foreach (string title in pending) close(title);
        }

        sealed class GuardSession
        {
            readonly EventWaitHandle cancel;
            readonly EventWaitHandle detach;
            internal readonly int ProcessId;
            GuardSession(EventWaitHandle c, EventWaitHandle d, int pid) { cancel = c; detach = d; ProcessId = pid; }
            internal static GuardSession Start(string title, string engine)
            {
                if (!PreviewGuardHost.ValidTitle(title)) throw new IOException("无效的独立预览标识。");
                string helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WallpaperReadback.exe");
                if (!File.Exists(helper)) throw new IOException("独立预览保护组件缺失。");
                string nonce = Guid.NewGuid().ToString("N"), prefix = PreviewGuardHost.EventPrefix + nonce;
                var cancel = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".cancel");
                var detach = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".detach");
                try
                {
                    int helperPid;
                    using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".ready"))
                    using (var owner = Process.GetCurrentProcess())
                    {
                        string args = "--preview-guard " + Processes.Quote(Path.GetFullPath(engine)) + " " + Processes.Quote(title)
                            + " " + owner.Id.ToString(CultureInfo.InvariantCulture) + " " + owner.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) + " " + nonce;
                        var boundary = Processes.BeforeStart;
                        if (boundary != null) boundary(helper, args);
                        // Deliberately not in Processes.Children: it must outlive a picker shutdown.
                        using (var helperProcess = DetachedGuardProcess.Start(helper, args))
                        {
                            helperPid = helperProcess.Id;
                            if (!ready.WaitOne(5000) || helperProcess.HasExited)
                            {
                                helperProcess.Stop();
                                throw new IOException("独立预览保护组件未能准备完成，已取消打开预览。");
                            }
                        }
                    }
                    return new GuardSession(cancel, detach, helperPid);
                }
                catch { cancel.Dispose(); detach.Dispose(); throw; }
            }
            internal void Cancel() { try { cancel.Set(); } finally { cancel.Dispose(); detach.Dispose(); } }
            internal void Detach() { detach.Set(); }
        }

        // Process.Start on .NET Framework inherits *all* inheritable handles even when
        // new standard streams are redirected. A surviving guard must inherit none:
        // otherwise it can keep a headless caller's original stdout/stderr pipe alive.
        sealed class DetachedGuardProcess : IDisposable
        {
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct StartupInfo
            {
                public int cb; public string reserved, desktop, title;
                public int x, y, xsize, ysize, xcount, ycount, fill, flags;
                public short show, reserved2; public IntPtr reservedPtr, input, output, error;
            }
            [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr process, thread; public int pid, tid; }
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
                bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
            [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
            [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
            [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr handle, uint exitCode);
            IntPtr processHandle;
            internal readonly int Id;
            DetachedGuardProcess(IntPtr handle, int pid) { processHandle = handle; Id = pid; }
            internal static DetachedGuardProcess Start(string executable, string arguments)
            {
                var startup = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)), flags = 1, show = 0 };
                ProcessInfo process;
                if (!CreateProcess(executable, new StringBuilder(Processes.Quote(executable) + " " + arguments),
                    IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out process))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                CloseHandle(process.thread);
                return new DetachedGuardProcess(process.process, process.pid);
            }
            internal bool HasExited { get { return WaitForSingleObject(processHandle, 0) == 0; } }
            internal void Stop() { if (!HasExited) TerminateProcess(processHandle, 1); }
            public void Dispose() { if (processHandle != IntPtr.Zero) { CloseHandle(processHandle); processHandle = IntPtr.Zero; } }
        }
    }
}
