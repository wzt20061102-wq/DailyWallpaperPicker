// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DailyWallpaperPicker
{
    // IPC can return before its window exists. Keep its unique title until observed.
    internal sealed class PendingPreviewWindows
    {
        readonly object gate = new object();
        readonly HashSet<string> titles = new HashSet<string>(StringComparer.Ordinal);
        internal void Track(string title) { lock (gate) titles.Add(title); }
        internal void Complete(string title) { lock (gate) titles.Remove(title); }
        internal void Drain(Action<string> close)
        {
            Drain(close, () => DateTime.UtcNow, milliseconds => Thread.Sleep(milliseconds));
        }
        internal void Drain(Action<string> close, Func<DateTime> clock, Action<int> wait)
        {
            string[] pending;
            lock (gate) { pending = titles.ToArray(); titles.Clear(); }
            if (pending.Length == 0) return;
            DateTime deadline = clock().AddSeconds(3);
            do
            {
                foreach (string title in pending) close(title);
                wait(50);
            } while (clock() < deadline);
            foreach (string title in pending) close(title);
        }
    }
}
