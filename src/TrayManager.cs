// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.

using System;
using System.Reflection;
using Forms = System.Windows.Forms;

namespace DailyWallpaperPicker
{
    /// <summary>Owns the notification icon and its native menu, including disposal.</summary>
    internal sealed class TrayManager : IDisposable
    {
        private readonly Forms.NotifyIcon _icon;
        private readonly Forms.ContextMenuStrip _menu;
        private readonly Forms.ToolStripMenuItem _pause;
        private bool _disposed;

        public TrayManager(Action open, Action togglePause, Action exit, Func<bool> isPaused)
        {
            _menu = new Forms.ContextMenuStrip();
            _menu.Items.Add("打开今日候选", null, delegate { open(); });
            _pause = new Forms.ToolStripMenuItem("暂停每日推荐");
            _pause.Click += delegate { togglePause(); };
            _menu.Items.Add(_pause);
            _menu.Items.Add(new Forms.ToolStripSeparator());
            _menu.Items.Add("退出", null, delegate { exit(); });
            _menu.Opening += delegate { _pause.Checked = isPaused(); };
            _icon = new Forms.NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location),
                Text = "每日壁纸 · 三选一",
                ContextMenuStrip = _menu,
                Visible = true
            };
            _icon.MouseClick += delegate(object sender, Forms.MouseEventArgs args)
            {
                if (args.Button == Forms.MouseButtons.Left) open();
            };
        }

        public void SetPaused(bool paused)
        {
            if (!_disposed) _pause.Checked = paused;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _icon.Visible = false;
            var icon = _icon.Icon;
            _icon.Dispose();
            _menu.Dispose();
            if (icon != null) icon.Dispose();
        }
    }
}