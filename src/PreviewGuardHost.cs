// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;

namespace DailyWallpaperPicker
{
    // Runs in WallpaperReadback.exe, independently of the picker's process lifetime.
    // It can only close an explicitly named pop-out owned by the captured engine session.
    internal static class PreviewGuardHost
    {
        delegate bool EnumWindowProc(IntPtr window, IntPtr state);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowProc callback, IntPtr state);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder title, int capacity);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr handle, uint message, IntPtr w, IntPtr l);
        internal const string EventPrefix = "Local\\DailyWallpaperPicker.PreviewGuard.";
        internal const int CancellationWatchSeconds = 120;

        internal static bool ValidTitle(string title)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(title ?? "", @"\ADailyWallpaperPreview-[a-f0-9]{8}-[1-9][0-9]*\z");
        }

        sealed class Identity
        {
            internal int Pid;
            internal long Started;
            internal bool Alive(string executable, int session)
            {
                try
                {
                    using (var p = Process.GetProcessById(Pid))
                        return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == Started && p.SessionId == session
                            && String.Equals(Path.GetFullPath(p.MainModule.FileName), executable, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
        }

        internal static int Run(string[] args)
        {
            try
            {
                if (args.Length != 6 || args[0] != "--preview-guard" || !ValidTitle(args[2])) return 2;
                string executable = Path.GetFullPath(args[1]), title = args[2];
                int ownerPid = Int32.Parse(args[3], CultureInfo.InvariantCulture);
                long ownerStarted = Int64.Parse(args[4], CultureInfo.InvariantCulture);
                Guid nonce;
                if (!Guid.TryParseExact(args[5], "N", out nonce)) return 2;
                string prefix = EventPrefix + nonce.ToString("N");
                using (var ready = EventWaitHandle.OpenExisting(prefix + ".ready"))
                using (var cancel = EventWaitHandle.OpenExisting(prefix + ".cancel"))
                using (var detach = EventWaitHandle.OpenExisting(prefix + ".detach"))
                {
                    int session = Process.GetCurrentProcess().SessionId;
                    var engines = new List<Identity>();
                    foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
                    using (p)
                    {
                        try
                        {
                            var identity = new Identity { Pid = p.Id, Started = p.StartTime.ToUniversalTime().Ticks };
                            if (identity.Alive(executable, session)) engines.Add(identity);
                        }
                        catch { }
                    }
                    if (engines.Count == 0) return 3;
                    bool seen = false, cancelling = false;
                    var cancellationWatch = new Stopwatch();
                    int commandIndex = 0;
                    int[] commandSeconds = { 0, 5, 15, 30, 60 };
                    ready.Set(); // IPC is sent only after the engine identities have been captured.
                    while (engines.Any(p => p.Alive(executable, session)))
                    {
                        if (!cancelling && (cancel.WaitOne(0) || (!detach.WaitOne(0) && !OwnerAlive(ownerPid, ownerStarted))))
                        {
                            cancelling = true;
                            cancellationWatch.Start();
                        }
                        bool owned = false;
                        EnumWindows(delegate(IntPtr window, IntPtr ignored)
                        {
                            var text = new StringBuilder(256);
                            GetWindowText(window, text, text.Capacity);
                            if (!String.Equals(text.ToString(), title, StringComparison.Ordinal)) return true;
                            uint pid; GetWindowThreadProcessId(window, out pid);
                            if (!engines.Any(p => p.Pid == (int)pid && p.Alive(executable, session))) return true;
                            owned = true;
                            if (cancelling) PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero);
                            return true;
                        }, IntPtr.Zero);
                        if (owned)
                        {
                            seen = true;
                        }
                        else if (seen) return 0; // The one window we owned has now closed.
                        if (cancelling)
                        {
                            // Ask the engine to cancel the named loading item even before HWND creation.
                            // Never send closeWallpaper without a validated, exact -location argument.
                            if (commandIndex < commandSeconds.Length && cancellationWatch.Elapsed.TotalSeconds >= commandSeconds[commandIndex])
                            {
                                CloseLocation(executable, title);
                                commandIndex++;
                            }
                            if (cancellationWatch.Elapsed.TotalSeconds >= CancellationWatchSeconds) return 4;
                        }
                        Thread.Sleep(100);
                    }
                    return 0; // A stopped/restarted engine cannot own this queued request any longer.
                }
            }
            catch { return 1; }
        }

        static bool OwnerAlive(int pid, long started)
        {
            try { using (var owner = Process.GetProcessById(pid)) return !owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == started; }
            catch { return false; }
        }

        static void CloseLocation(string executable, string title)
        {
            if (!ValidTitle(title) || executable.IndexOf('"') >= 0) return;
            try
            {
                using (var launcher = Process.Start(new ProcessStartInfo(executable, "-control closeWallpaper -location \"" + title + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(executable)
                }))
                {
                    if (!launcher.WaitForExit(2000)) try { launcher.Kill(); } catch { }
                }
            }
            catch { } // The HWND watcher still covers engines that reject or delay the command.
        }
    }
}
