// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DailyWallpaperPicker
{
    /// <summary>A presentation-only window. Applying a wallpaper requires the explicit Use button.</summary>
    public sealed class PickerWindow : Window
    {
        private static readonly Brush CanvasBrush = Brush("#17181C");
        private static readonly Brush CardBrush = Brush("#23252B");
        private static readonly Brush BorderBrushToken = Brush("#31343C");
        private static readonly Brush TextBrush = Brush("#F2F3F5");
        private static readonly Brush MutedBrush = Brush("#A4A8B4");
        private static readonly Brush AccentBrush = Brush("#C5CAFF");
        private static readonly Brush AccentTextBrush = Brush("#20243A");
        private static readonly Brush ErrorBrush = Brush("#FFB4AB");
        private const string PreviewOpenedMessage = "独立预览已打开；桌面保持不变。";

        private readonly PickerController _controller;
        private readonly Grid _layoutRoot;
        private readonly CardView[] _cards = new CardView[3];
        private readonly TextBlock _batchLabel;
        private readonly TextBlock _statusLabel;
        private readonly TextBlock _pauseLabel;
        private readonly Button _retryButton;
        private readonly Forms.NotifyIcon _tray;
        private readonly Forms.ToolStripMenuItem _pauseMenu;
        private string _lastAppliedId;
        private string _lastBatchId;
        private string _interactionMessage;
        private bool _interactionFailed;
        private bool _actionRunning;
        private bool _disposed;
        private int _renderQueued;
        private CardView _hoverCard;
        private Stopwatch _hoverClock;
        private readonly Dictionary<CardView, PreparedVideo> _preparedVideos = new Dictionary<CardView, PreparedVideo>();
        private readonly HashSet<string> _preparationFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer _hoverWatch;
        private int _preparedGeneration;
        private bool _suppressPreparation;

        public bool AllowClose { get; set; }
        public event Action ExitRequested;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

        public PickerWindow(PickerController controller)
        {
            if (controller == null) throw new ArgumentNullException("controller");
            _controller = controller;
            _hoverWatch = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _hoverWatch.Interval = TimeSpan.FromMilliseconds(500);
            _hoverWatch.Tick += delegate { CheckPreparedHover(); };
            _lastAppliedId = controller.State == null ? null : controller.State.AppliedId;
            _lastBatchId = controller.State == null ? null : controller.State.BatchId;
            Title = "每日壁纸 · 三选一";
            Width = Math.Min(960, Math.Max(320, SystemParameters.WorkArea.Width - 32));
            Height = Math.Min(584, Math.Max(240, SystemParameters.WorkArea.Height - 32));
            MinWidth = Math.Min(740, Width);
            MinHeight = Math.Min(440, Height);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            ShowActivated = false;
            Topmost = false;
            Background = CanvasBrush;
            Foreground = TextBrush;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 13;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            AutomationProperties.SetName(this, "每日壁纸三选一");
            AutomationProperties.SetAutomationId(this, "DailyWallpaperPickerWindow");
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
            Resources.Add(typeof(Button), CreateButtonStyle());
            SourceInitialized += delegate { ApplyNativeFrame(); };

            var root = new Grid { Margin = new Thickness(24, 23, 24, 20) };
            _layoutRoot = root;
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            var header = new Grid { Margin = new Thickness(0, 0, 0, 24) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var heading = new StackPanel();
            heading.Children.Add(new TextBlock { Text = "今日精选", FontSize = 28, FontWeight = FontWeights.SemiBold });
            heading.Children.Add(new TextBlock
            {
                Text = "三张壁纸，选你喜欢的。",
                Foreground = MutedBrush, Margin = new Thickness(0, 7, 8, 0), TextWrapping = TextWrapping.Wrap
            });
            header.Children.Add(heading);
            var headerRight = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            _batchLabel = new TextBlock { Foreground = MutedBrush, FontSize = 12, TextAlignment = TextAlignment.Center };
            _pauseLabel = new TextBlock { Foreground = AccentBrush, FontSize = 12, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 7, 0, 0), Visibility = Visibility.Collapsed };
            headerRight.Children.Add(new Border
            {
                Background = CardBrush, BorderBrush = BorderBrushToken, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 7, 12, 7), Child = _batchLabel
            });
            headerRight.Children.Add(_pauseLabel);
            Grid.SetColumn(headerRight, 1);
            header.Children.Add(headerRight);
            root.Children.Add(header);

            var cardScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(3, 3, 3, 3),
                Margin = new Thickness(-3, -3, -3, -3),
                Focusable = false
            };
            cardScroll.Resources.Add(typeof(ScrollBar), CreateScrollBarStyle());
            Grid.SetRow(cardScroll, 1);
            root.Children.Add(cardScroll);
            var cardGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < 5; i++) cardGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = i % 2 == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(16)
            });
            cardScroll.Content = cardGrid;
            for (int i = 0; i < _cards.Length; i++)
            {
                _cards[i] = new CardView(this, i);
                Grid.SetColumn(_cards[i].Root, i * 2);
                cardGrid.Children.Add(_cards[i].Root);
            }

            var footer = new Grid { Margin = new Thickness(0, 23, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var footerCopy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            _statusLabel = new TextBlock { Foreground = MutedBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 38, Margin = new Thickness(0, 0, 0, 5), Visibility = Visibility.Collapsed };
            AutomationProperties.SetName(_statusLabel, "推荐状态");
            footerCopy.Children.Add(_statusLabel);
            footerCopy.Children.Add(new TextBlock
            {
                Text = "每天 23:00 更新 · 不选就保持原样",
                Foreground = MutedBrush, FontSize = 12
            });
            footer.Children.Add(footerCopy);
            var footerActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _retryButton = MakeButton("重试", "重试今日壁纸", false);
            AutomationProperties.SetAutomationId(_retryButton, "RetryToday");
            KeyboardNavigation.SetTabIndex(_retryButton, 12);
            _retryButton.MinWidth = 62;
            _retryButton.Margin = new Thickness(0, 0, 8, 0);
            _retryButton.Visibility = Visibility.Collapsed;
            _retryButton.Click += async delegate { await RunAction(_controller.RetryAsync, "正在重新连接，请稍等…"); };
            var laterButton = MakeButton("暂时不选", "暂时不选并收起到托盘", false);
            AutomationProperties.SetAutomationId(laterButton, "HideToTray");
            KeyboardNavigation.SetTabIndex(laterButton, 13);
            laterButton.MinWidth = 94;
            laterButton.Background = Brushes.Transparent;
            laterButton.BorderBrush = Brushes.Transparent;
            laterButton.Foreground = MutedBrush;
            laterButton.Click += delegate { HideToTray(); };
            footerActions.Children.Add(_retryButton);
            footerActions.Children.Add(laterButton);
            Grid.SetColumn(footerActions, 1);
            footer.Children.Add(footerActions);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            var trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Items.Add("打开今日候选", null, delegate { OnUi(delegate { ShowPicker(true); }); });
            _pauseMenu = new Forms.ToolStripMenuItem("暂停每日推荐");
            _pauseMenu.Click += delegate
            {
                OnUi(delegate
                {
                    try
                    {
                        _controller.SetPaused(!_controller.State.Paused);
                        Render();
                    }
                    catch (Exception ex) { ShowFailure("暂时无法修改提醒状态", ex); }
                });
            };
            trayMenu.Items.Add(_pauseMenu);
            trayMenu.Items.Add(new Forms.ToolStripSeparator());
            trayMenu.Items.Add("退出", null, delegate { OnUi(RequestExit); });
            trayMenu.Opening += delegate { if (_controller.State != null) _pauseMenu.Checked = _controller.State.Paused; };
            _tray = new Forms.NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location),
                Text = "每日壁纸 · 三选一",
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            _tray.MouseClick += delegate(object sender, Forms.MouseEventArgs args)
            {
                if (args.Button == Forms.MouseButtons.Left) OnUi(delegate { ShowPicker(true); });
            };

            _controller.Changed += ControllerChanged;
            Closing += WindowClosing;
            Closed += WindowClosed;
            Deactivated += delegate { StopHover("window-deactivated"); };
            IsVisibleChanged += delegate { if (!IsVisible) ClosePreparedVideos("window-hidden"); else PrepareHoverPreviews(); };
            StateChanged += delegate { if (WindowState == WindowState.Minimized) ClosePreparedVideos("window-minimized"); else PrepareHoverPreviews(); };
            PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Escape)
                {
                    args.Handled = true;
                    HideToTray();
                }
            };
            Render();
        }

        /// <summary>
        /// Debug-only content scaling. This does not change Windows DPI and is not proof of
        /// operating-system DPI behavior; the title makes the test condition visible.
        /// </summary>
        public void SetTestScale(double scale)
        {
            if (Double.IsNaN(scale) || Double.IsInfinity(scale) || scale < 1.0 || scale > 2.0)
                throw new ArgumentOutOfRangeException("scale", "布局测试缩放必须在 1.0 到 2.0 之间。");
            if (!Dispatcher.CheckAccess()) { OnUi(delegate { SetTestScale(scale); }); return; }
            if (_disposed) return;
            Rect area = SystemParameters.WorkArea;
            double availableWidth = Math.Max(320, area.Width - 32);
            double availableHeight = Math.Max(240, area.Height - 32);
            MinWidth = Math.Min(740 * scale, availableWidth);
            MinHeight = Math.Min(440 * scale, availableHeight);
            Width = Math.Min(960 * scale, availableWidth);
            Height = Math.Min(584 * scale, availableHeight);
            var transform = new ScaleTransform(scale, scale);
            transform.Freeze();
            _layoutRoot.LayoutTransform = transform;
            Title = "每日壁纸 · 三选一 · 内容布局测试 " + (scale * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
            Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
            Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
            _layoutRoot.InvalidateMeasure();
            UpdateLayout();
        }

        public void ShowPicker(bool userInitiated)
        {
            if (!Dispatcher.CheckAccess()) { OnUi(delegate { ShowPicker(userInitiated); }); return; }
            if (_disposed) return;
            IntPtr foregroundBefore = GetForegroundWindow();
            Render();
            ShowActivated = userInitiated;
            ShowInTaskbar = true;
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized)
            {
                if (userInitiated) WindowState = WindowState.Normal;
                else ShowWindow(new WindowInteropHelper(this).Handle, 4); // SW_SHOWNOACTIVATE.
            }
            if (userInitiated) Activate();
            _suppressPreparation = false;
            PrepareHoverPreviews();
            _controller.Log("WINDOW SHOW manual=" + userInitiated + " foregroundBefore=" + foregroundBefore.ToInt64() + " foregroundAfter=" + GetForegroundWindow().ToInt64() + " topmost=" + Topmost);
        }

        private void HideToTray()
        {
            if (_disposed) return;
            ClosePreparedVideos("hide-to-tray");
            try { _controller.HidePreview(); }
            catch (Exception ex) { _controller.Log("Hide preview: " + ex.Message); }
            Hide();
            ShowInTaskbar = false;
            _controller.Log("WINDOW HIDE to tray; preview close requested");
        }

        private void RequestExit()
        {
            if (_disposed) return;
            ClosePreparedVideos("exit-requested");
            try { _controller.HidePreview(); }
            catch (Exception ex) { _controller.Log("Close preview on exit: " + ex.Message); }
            AllowClose = true;
            var handler = ExitRequested;
            if (handler != null) handler();
            else
            {
                Close();
                if (Application.Current != null) Application.Current.Shutdown();
            }
        }

        private void WindowClosing(object sender, CancelEventArgs args)
        {
            ClosePreparedVideos("window-closing");
            if (!AllowClose)
            {
                args.Cancel = true;
                HideToTray();
            }
        }

        private void WindowClosed(object sender, EventArgs args)
        {
            ClosePreparedVideos("window-closed");
            _disposed = true;
            _controller.Changed -= ControllerChanged;
            _tray.Visible = false;
            _tray.Dispose();
        }

        private void ControllerChanged()
        {
            if (_disposed || Interlocked.Exchange(ref _renderQueued, 1) != 0) return;
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                Interlocked.Exchange(ref _renderQueued, 0);
                if (_disposed) return;
                string applied = _controller.State == null ? null : _controller.State.AppliedId;
                bool newlyApplied = !String.IsNullOrEmpty(applied) && !String.Equals(applied, _lastAppliedId, StringComparison.Ordinal);
                _lastAppliedId = applied;
                Render();
                if (newlyApplied) HideToTray();
            }));
        }

        private void Render()
        {
            AppState state = _controller.State;
            if (state == null) return;
            if (!String.Equals(_lastBatchId, state.BatchId, StringComparison.Ordinal))
            {
                ClosePreparedVideos("batch-changed");
                _lastBatchId = state.BatchId;
                _interactionMessage = null;
                _interactionFailed = false;
            }
            DateTime day;
            _batchLabel.Text = DateTime.TryParse(state.BatchId, CultureInfo.InvariantCulture, DateTimeStyles.None, out day)
                ? day.ToString("MM 月 dd 日", CultureInfo.InvariantCulture) : "每天 23:00";
            _pauseLabel.Text = "每日推荐已暂停";
            _pauseLabel.Visibility = state.Paused ? Visibility.Visible : Visibility.Collapsed;
            _pauseMenu.Checked = state.Paused;
            var items = state.Candidates == null ? new List<Candidate>() : state.Candidates;
            bool unavailable = _controller.Busy || _actionRunning;
            for (int i = 0; i < _cards.Length; i++) _cards[i].Update(i < items.Count ? items[i] : null, unavailable);
            string message = String.IsNullOrWhiteSpace(_interactionMessage) ? state.Message : _interactionMessage;
            bool failed = _interactionFailed || IsFailureMessage(message);
            bool allReady = items.Count == 3;
            foreach (Candidate item in items) if (!item.Ready || !String.IsNullOrEmpty(item.Error)) allReady = false;
            bool previewSuccess = String.Equals(message, PreviewOpenedMessage, StringComparison.Ordinal);
            bool showMessage = !previewSuccess && !String.IsNullOrWhiteSpace(message) && (unavailable || failed || !IsRoutineMessage(message));
            _statusLabel.Text = message;
            _statusLabel.ToolTip = message;
            _statusLabel.Visibility = showMessage ? Visibility.Visible : Visibility.Collapsed;
            _statusLabel.Foreground = failed ? ErrorBrush : MutedBrush;
            _retryButton.IsEnabled = !unavailable;
            _retryButton.Visibility = !allReady || failed ? Visibility.Visible : Visibility.Collapsed;
            PrepareHoverPreviews();
        }

        private static bool IsFailureMessage(string message)
        {
            if (String.IsNullOrWhiteSpace(message)) return false;
            string[] indicators = { "失败", "未能", "无法", "未完成", "未打开", "暂未", "错误", "异常" };
            foreach (string indicator in indicators) if (message.IndexOf(indicator, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // Only known informational/success messages are hidden. Unknown messages remain visible.
        private static bool IsRoutineMessage(string message)
        {
            if (String.IsNullOrWhiteSpace(message)) return true;
            if (IsFailureMessage(message)) return false;
            return message == "选一张喜欢的，或暂时不选。"
                || message == PreviewOpenedMessage
                || message == "三张都已准备好。选一张喜欢的，或保持现在。"
                || message == "每日推荐已暂停；当前壁纸不会改变。"
                || message == "每天 23:00 提醒，22:50 提前准备。"
                || message.StartsWith("已换上：", StringComparison.Ordinal);
        }

        private void ApplyNativeFrame()
        {
            // Native chrome keeps Windows resizing, snapping and caption-button accessibility.
            // Unsupported DWM attributes are harmless on older Windows versions.
            try
            {
                IntPtr window = new WindowInteropHelper(this).Handle;
                int enabled = 1, round = 2, captionColor = 0x001C1817, textColor = 0x00F5F3F2;
                if (DwmSetWindowAttribute(window, 20, ref enabled, sizeof(int)) < 0)
                    DwmSetWindowAttribute(window, 19, ref enabled, sizeof(int));
                DwmSetWindowAttribute(window, 33, ref round, sizeof(int));
                DwmSetWindowAttribute(window, 35, ref captionColor, sizeof(int));
                DwmSetWindowAttribute(window, 36, ref textColor, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        private async Task RunAction(Func<Task> action, string workingText)
        {
            if (_actionRunning || _controller.Busy) return;
            _suppressPreparation = true;
            ClosePreparedVideos("explicit-action");
            _actionRunning = true;
            _interactionMessage = workingText;
            _interactionFailed = false;
            Render();
            try
            {
                await action();
                _interactionMessage = null;
            }
            catch (Exception ex) { ShowFailure("操作未完成，请查看提示", ex); }
            finally
            {
                _actionRunning = false;
                if (!_disposed) Render();
            }
        }

        private void ShowFailure(string text, Exception ex)
        {
            _interactionMessage = text + "。" + ex.Message;
            _interactionFailed = true;
            _controller.Log(ex.ToString());
            if (!_disposed) Render();
        }

        private void OnUi(Action action)
        {
            if (_disposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.BeginInvoke(action);
        }

        private sealed class PreparedVideo
        {
            public CardView Card;
            public string ItemId;
            public string Key;
            public int Generation;
            public HoverPreviewLease Lease;
            public MediaElement Player;
            public bool Closed;
            public bool Opened;
            public bool Playing;
            public bool ProgressLogged;
            public double ResumePosition;
            public double PausedPosition;
            public DateTime OpenDeadline;
            public DateTime AuditDue;
            public bool PauseAuditPending;
            public int PauseRetries;
            public Stopwatch PlayClock;
        }

        private bool CanPrepareHover()
        {
            return !_disposed && IsVisible && WindowState != WindowState.Minimized && !_actionRunning && !_controller.Busy;
        }

        private bool CanContinueHover(CardView card)
        {
            return CanPrepareHover() && card != null && card.CanHover;
        }

        private string PreparationKey(CardView card)
        {
            return (_controller.State == null ? "" : _controller.State.BatchId) + "|" + card.ItemId + "|" + card.ProjectPath;
        }

        private bool IsPreparedCurrent(PreparedVideo prepared)
        {
            PreparedVideo current;
            return prepared != null && !prepared.Closed && CanPrepareHover() && prepared.Card.CanPrepare
                && _preparedVideos.TryGetValue(prepared.Card, out current) && Object.ReferenceEquals(current, prepared)
                && String.Equals(prepared.Key, PreparationKey(prepared.Card), StringComparison.OrdinalIgnoreCase)
                && (prepared.Lease == null || _controller.IsHoverPreviewCurrent(prepared.Lease));
        }

        private void HoverLog(string message)
        {
            try { _controller.LogHover(message); } catch { }
        }

        private void PrepareHoverPreviews()
        {
            if (_suppressPreparation || !CanPrepareHover()) return;
            // At most the three visible cards are prepared. Preparing uses Pause from the
            // outset, never Play followed by Pause, so non-hovered media does not advance.
            foreach (CardView card in _cards)
                if (card != null && card.CanPrepare && card.IsKnownVideoOnly && !_preparedVideos.ContainsKey(card)
                    && !_preparationFailures.Contains(PreparationKey(card))) PrepareVideo(card);
        }

        private async void PrepareVideo(CardView card)
        {
            if (!CanPrepareHover() || !card.CanPrepare || card.IsKnownSceneOnly || _preparedVideos.ContainsKey(card)) return;
            var prepared = new PreparedVideo
            {
                Card = card, ItemId = card.ItemId, Key = PreparationKey(card),
                Generation = ++_preparedGeneration, OpenDeadline = DateTime.MaxValue
            };
            _preparedVideos.Add(card, prepared);
            card.SetHoverState("preparing; static-cover");
            HoverLog("prepare id=" + prepared.ItemId + " generation=" + prepared.Generation + " requestedState=Pause");
            AdjustHoverWatch();
            HoverPreviewLease pendingLease = null;
            try
            {
                pendingLease = await _controller.AcquireHoverPreviewAsync(prepared.ItemId);
                if (!IsPreparedCurrent(prepared) || !_controller.IsHoverPreviewCurrent(pendingLease)) return;
                if (!pendingLease.IsVideo)
                {
                    HoverLog("fallback id=" + prepared.ItemId + " type=Scene reason=independent-preview-required");
                    ClosePreparedVideo(prepared, "scene-fallback");
                    card.SetHoverState("static; scene-fallback");
                    card.SetHoverTip("Scene 场景请点击「预览」查看动态效果。");
                    return;
                }
                prepared.Lease = pendingLease;
                pendingLease = null;
                var player = new MediaElement
                {
                    LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
                    Volume = 0, IsMuted = true, ScrubbingEnabled = true,
                    Stretch = Stretch.UniformToFill, Focusable = false, IsHitTestVisible = false, Opacity = 0
                };
                prepared.Player = player;
                prepared.OpenDeadline = DateTime.UtcNow.AddSeconds(15);
                AutomationProperties.SetName(player, "候选视频静音悬停预览");
                AutomationProperties.SetAutomationId(player, "PreparedHoverVideo" + prepared.Generation.ToString(CultureInfo.InvariantCulture));
                player.MediaOpened += delegate
                {
                    if (!IsPreparedCurrent(prepared)) { ClosePreparedVideo(prepared, "stale-media-opened"); return; }
                    try
                    {
                        prepared.Opened = true;
                        player.Pause();
                        player.Opacity = 0;
                        AuditPausedLater(prepared);
                        card.SetHoverState("prepared; paused; position=" + PositionText(player));
                        HoverLog("open id=" + prepared.ItemId + " generation=" + prepared.Generation + " state=paused muted=" + player.IsMuted
                            + " size=" + player.NaturalVideoWidth + "x" + player.NaturalVideoHeight + " position=" + PositionText(player));
                        if (Object.ReferenceEquals(_hoverCard, card) && CanContinueHover(card)) ResumePreparedVideo(prepared);
                    }
                    catch (Exception ex) { PreparedFailed(prepared, ex); }
                    AdjustHoverWatch();
                };
                player.MediaEnded += delegate
                {
                    if (!IsPreparedCurrent(prepared)) { ClosePreparedVideo(prepared, "stale-media-ended"); return; }
                    try
                    {
                        player.Position = TimeSpan.Zero;
                        if (prepared.Playing && Object.ReferenceEquals(_hoverCard, card) && CanContinueHover(card))
                        {
                            if (!prepared.ProgressLogged) prepared.ResumePosition = 0;
                            player.Play();
                        }
                        else
                        {
                            prepared.Playing = false;
                            player.Pause();
                            player.Opacity = 0;
                            AuditPausedLater(prepared);
                        }
                    }
                    catch (Exception ex) { PreparedFailed(prepared, ex); }
                };
                player.MediaFailed += delegate(object sender, ExceptionRoutedEventArgs args) { PreparedFailed(prepared, args.ErrorException); };
                card.AttachPlayer(player);
                player.Source = new Uri(prepared.Lease.MediaPath, UriKind.Absolute);
                player.Pause();
                card.SetHoverState("preparing; paused; static-cover");
            }
            catch (Exception ex) { PreparedFailed(prepared, ex); }
            finally
            {
                if (pendingLease != null) pendingLease.Dispose();
                if (!IsPreparedCurrent(prepared)) ClosePreparedVideo(prepared, "preparation-no-longer-current");
            }
        }

        private void BeginHover(CardView card)
        {
            if (!CanContinueHover(card) || Object.ReferenceEquals(_hoverCard, card)) return;
            _suppressPreparation = false;
            var clock = Stopwatch.StartNew();
            StopHover("another-card");
            if (card.IsKnownSceneOnly)
            {
                HoverLog("fallback id=" + card.ItemId + " type=Scene reason=known-scene-static-cover");
                card.SetHoverState("static; scene-fallback");
                card.SetHoverTip("Scene 场景请点击「预览」查看动态效果。");
                return;
            }
            _hoverCard = card;
            _hoverClock = clock;
            card.SetHoverTip(null);
            PreparedVideo prepared;
            if (_preparedVideos.TryGetValue(card, out prepared))
            {
                if (!IsPreparedCurrent(prepared))
                {
                    ClosePreparedVideo(prepared, "stale-hover");
                    _hoverCard = card;
                    _hoverClock = clock;
                    PrepareVideo(card);
                }
                else if (prepared.Opened) ResumePreparedVideo(prepared);
                else card.SetHoverState("preparing; hover-waiting");
            }
            else
            {
                _preparationFailures.Remove(PreparationKey(card));
                PrepareVideo(card);
            }
            AdjustHoverWatch();
        }

        private void ResumePreparedVideo(PreparedVideo prepared)
        {
            if (!IsPreparedCurrent(prepared) || !prepared.Opened || !Object.ReferenceEquals(_hoverCard, prepared.Card)
                || !CanContinueHover(prepared.Card) || prepared.Playing) return;
            try
            {
                // Every other prepared element stays paused, including one whose MouseLeave
                // is queued behind this MouseEnter. The explicit large preview is closed here.
                foreach (PreparedVideo other in new List<PreparedVideo>(_preparedVideos.Values))
                    if (!Object.ReferenceEquals(other, prepared) && other.Playing) PausePreparedVideo(other, "another-card");
                _controller.HidePreview();
                prepared.ResumePosition = prepared.Player.Position.TotalSeconds;
                prepared.PlayClock = _hoverClock ?? Stopwatch.StartNew();
                prepared.ProgressLogged = false;
                prepared.PauseAuditPending = false;
                prepared.Playing = true;
                prepared.Player.Opacity = 1;
                prepared.Player.Play();
                prepared.Card.SetHoverState("playing; muted=true; position=" + PositionText(prepared.Player));
                HoverLog("resume id=" + prepared.ItemId + " generation=" + prepared.Generation + " hoverToPlayMs="
                    + prepared.PlayClock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
                    + " metric=Play-request-not-first-frame position=" + PositionText(prepared.Player) + " muted=" + prepared.Player.IsMuted);
            }
            catch (Exception ex) { PreparedFailed(prepared, ex); }
            AdjustHoverWatch();
        }

        private void EndHover(CardView card, string reason)
        {
            if (Object.ReferenceEquals(card, _hoverCard)) StopHover(reason);
        }

        private void StopHover(string reason)
        {
            CardView card = _hoverCard;
            _hoverCard = null;
            _hoverClock = null;
            PreparedVideo prepared;
            if (card != null && _preparedVideos.TryGetValue(card, out prepared)) PausePreparedVideo(prepared, reason);
            AdjustHoverWatch();
        }

        private void PausePreparedVideo(PreparedVideo prepared, string reason)
        {
            if (prepared.Closed || prepared.Player == null) return;
            bool wasPlaying = prepared.Playing;
            prepared.Playing = false;
            try
            {
                prepared.Player.Opacity = 0;
                prepared.Player.Pause();
                if (prepared.Opened) AuditPausedLater(prepared);
                prepared.Card.SetHoverState(prepared.Opened ? "prepared; paused; position=" + PositionText(prepared.Player) : "preparing; paused; static-cover");
                if (wasPlaying) HoverLog("stop id=" + prepared.ItemId + " generation=" + prepared.Generation + " reason=" + reason
                    + " state=paused position=" + PositionText(prepared.Player) + " leaseRetained=true");
            }
            catch (Exception ex) { PreparedFailed(prepared, ex); }
        }

        private void AuditPausedLater(PreparedVideo prepared)
        {
            prepared.PausedPosition = prepared.Player.Position.TotalSeconds;
            prepared.AuditDue = DateTime.UtcNow.AddMilliseconds(200);
            prepared.PauseAuditPending = true;
            prepared.PauseRetries = 0;
        }

        private static string PositionText(MediaElement player)
        {
            return player.Position.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);
        }

        private void CheckPreparedHover()
        {
            if (!CanPrepareHover()) { ClosePreparedVideos("window-or-controller-unavailable"); return; }
            if (_hoverCard != null && !CanContinueHover(_hoverCard)) StopHover("pointer-or-window-unavailable");
            DateTime now = DateTime.UtcNow;
            foreach (PreparedVideo prepared in new List<PreparedVideo>(_preparedVideos.Values))
            {
                if (!IsPreparedCurrent(prepared)) { ClosePreparedVideo(prepared, "lease-or-candidate-expired"); continue; }
                if (prepared.Player == null) continue;
                if (!prepared.Opened)
                {
                    if (now >= prepared.OpenDeadline) PreparedFailed(prepared, new IOException("视频未能及时打开。"));
                    continue;
                }
                try
                {
                    double position = prepared.Player.Position.TotalSeconds;
                    if (prepared.Playing)
                    {
                        if (!Object.ReferenceEquals(_hoverCard, prepared.Card) || !CanContinueHover(prepared.Card))
                        {
                            PausePreparedVideo(prepared, "no-longer-hovered");
                            continue;
                        }
                        prepared.Card.SetHoverState("playing; muted=true; position=" + position.ToString("0.000", CultureInfo.InvariantCulture));
                        // Some codecs reset their paused preroll clock when Play starts.
                        // A backward clock correction is not evidence of forward playback.
                        if (!prepared.ProgressLogged && position - prepared.ResumePosition > 0.02)
                        {
                            prepared.ProgressLogged = true;
                            HoverLog("playing id=" + prepared.ItemId + " generation=" + prepared.Generation + " hoverToProgressMs="
                                + prepared.PlayClock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
                                + " metric=media-position-progress-not-first-frame position=" + position.ToString("0.000", CultureInfo.InvariantCulture)
                                + " from=" + prepared.ResumePosition.ToString("0.000", CultureInfo.InvariantCulture) + " muted=" + prepared.Player.IsMuted);
                        }
                    }
                    else if (now >= prepared.AuditDue)
                    {
                        double signedDifference = position - prepared.PausedPosition;
                        double difference = Math.Abs(signedDifference);
                        bool stable = difference <= 0.12;
                        bool clockRebased = signedDifference < -0.12;
                        HoverLog((prepared.PauseAuditPending ? "paused-audit" : "idle-audit") + " id=" + prepared.ItemId + " generation=" + prepared.Generation
                            + " stable=" + stable + " position=" + position.ToString("0.000", CultureInfo.InvariantCulture)
                            + " delta=" + difference.ToString("0.000", CultureInfo.InvariantCulture)
                            + " clockRebased=" + clockRebased + " toleranceSeconds=0.120");
                        prepared.PausedPosition = position;
                        if (clockRebased)
                        {
                            // Pause may correct a decoder's preroll clock backwards.
                            // This is not continued playback, nor proof of a stable position;
                            // sample the new baseline again without counting a forward retry.
                            prepared.PauseRetries = 0;
                            prepared.PauseAuditPending = true;
                            prepared.AuditDue = now.AddMilliseconds(200);
                        }
                        else if (!stable)
                        {
                            prepared.Player.Opacity = 0;
                            prepared.Player.Pause();
                            prepared.PauseRetries++;
                            if (prepared.PauseRetries >= 2) { ClosePreparedVideo(prepared, "pause-did-not-settle"); continue; }
                            prepared.PauseAuditPending = true;
                            prepared.AuditDue = now.AddMilliseconds(200);
                        }
                        else
                        {
                            prepared.PauseAuditPending = false;
                            prepared.PauseRetries = 0;
                            prepared.AuditDue = now.AddSeconds(5);
                        }
                    }
                }
                catch (Exception ex) { PreparedFailed(prepared, ex); }
            }
            AdjustHoverWatch();
        }

        private void AdjustHoverWatch()
        {
            if (_preparedVideos.Count == 0) { _hoverWatch.Stop(); return; }
            int interval = 500;
            foreach (PreparedVideo prepared in _preparedVideos.Values)
            {
                if (prepared.Playing && !prepared.ProgressLogged) { interval = 40; break; }
                if (prepared.PauseAuditPending) interval = 200;
            }
            _hoverWatch.Interval = TimeSpan.FromMilliseconds(interval);
            _hoverWatch.Start();
        }

        private void PreparedFailed(PreparedVideo prepared, Exception error)
        {
            PreparedVideo current;
            if (prepared.Closed || !_preparedVideos.TryGetValue(prepared.Card, out current) || !Object.ReferenceEquals(current, prepared)) return;
            HoverLog("fail id=" + prepared.ItemId + " generation=" + prepared.Generation + " reason=" + (error == null ? "media-failed" : error.Message));
            _preparationFailures.Add(prepared.Key);
            ClosePreparedVideo(prepared, "media-failed");
            prepared.Card.SetHoverState("static; media-failed");
            prepared.Card.SetHoverTip("悬停播放暂不可用，可点击「预览」查看。");
        }

        private void ClosePreparedVideo(PreparedVideo prepared, string reason)
        {
            if (prepared == null || prepared.Closed) return;
            prepared.Closed = true;
            PreparedVideo current;
            bool ownsCard = _preparedVideos.TryGetValue(prepared.Card, out current) && Object.ReferenceEquals(current, prepared);
            if (ownsCard) _preparedVideos.Remove(prepared.Card);
            if (ownsCard && Object.ReferenceEquals(_hoverCard, prepared.Card)) { _hoverCard = null; _hoverClock = null; }
            try
            {
                if (prepared.Player != null)
                {
                    prepared.Player.Opacity = 0;
                    try { prepared.Player.Close(); } finally { prepared.Player.Source = null; }
                }
            }
            catch (Exception ex) { HoverLog("close-error id=" + prepared.ItemId + " reason=" + ex.Message); }
            finally
            {
                try
                {
                    prepared.Card.DetachPlayer(prepared.Player);
                    if (ownsCard) prepared.Card.SetHoverState("static");
                }
                finally
                {
                    if (prepared.Lease != null) prepared.Lease.Dispose();
                }
            }
            HoverLog("prepared-close id=" + prepared.ItemId + " generation=" + prepared.Generation + " reason=" + reason
                + " playerClosed=" + (prepared.Player != null) + " leaseReleased=" + (prepared.Lease != null));
            AdjustHoverWatch();
        }

        private void ClosePreparedVideos(string reason)
        {
            _hoverCard = null;
            _hoverClock = null;
            foreach (PreparedVideo prepared in new List<PreparedVideo>(_preparedVideos.Values)) ClosePreparedVideo(prepared, reason);
            _preparationFailures.Clear();
            _hoverWatch.Stop();
        }

        private static Brush Brush(string color)
        {
            var value = (SolidColorBrush)new BrushConverter().ConvertFromString(color);
            value.Freeze();
            return value;
        }

        private static Style CreateButtonStyle()
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.ForegroundProperty, TextBrush));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#2D3038")));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, BorderBrushToken));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 7, 12, 7)));
            style.Setters.Add(new Setter(Control.MinHeightProperty, 40.0));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Microsoft YaHei UI")));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));

            var template = new ControlTemplate(typeof(Button));
            var templateRoot = new FrameworkElementFactory(typeof(Grid));
            var border = new FrameworkElementFactory(typeof(Border), "ButtonBorder");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            border.AppendChild(presenter);
            templateRoot.AppendChild(border);
            var feedback = new FrameworkElementFactory(typeof(Border), "ButtonFeedback");
            feedback.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            feedback.SetValue(Border.BackgroundProperty, Brushes.White);
            feedback.SetValue(UIElement.OpacityProperty, 0.0);
            feedback.SetValue(UIElement.IsHitTestVisibleProperty, false);
            templateRoot.AppendChild(feedback);
            var focusRing = new FrameworkElementFactory(typeof(Border), "FocusRing");
            focusRing.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            focusRing.SetValue(Border.BorderBrushProperty, Brush("#E6E8FF"));
            focusRing.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            focusRing.SetValue(FrameworkElement.MarginProperty, new Thickness(-3));
            focusRing.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            focusRing.SetValue(UIElement.IsHitTestVisibleProperty, false);
            templateRoot.AppendChild(focusRing);
            template.VisualTree = templateRoot;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.07, "ButtonFeedback"));
            template.Triggers.Add(hover);
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "FocusRing"));
            template.Triggers.Add(focus);
            var down = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            down.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Black, "ButtonFeedback"));
            down.Setters.Add(new Setter(UIElement.OpacityProperty, 0.17, "ButtonFeedback"));
            template.Triggers.Add(down);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.42, "ButtonBorder"));
            template.Triggers.Add(disabled);
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        private static Style CreateScrollBarStyle()
        {
            // A local template preserves the native Track/Thumb and page-scroll commands,
            // while removing the bright system arrows and trough from this dark window.
            const string xaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='{x:Type ScrollBar}'>
  <Setter Property='Width' Value='10'/>
  <Setter Property='Background' Value='Transparent'/>
  <Setter Property='Focusable' Value='False'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='{x:Type ScrollBar}'>
        <Grid Background='Transparent'>
          <Track x:Name='PART_Track' IsDirectionReversed='True'
                 Orientation='{TemplateBinding Orientation}'
                 Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}'
                 Value='{TemplateBinding Value}' ViewportSize='{TemplateBinding ViewportSize}'>
            <Track.DecreaseRepeatButton>
              <RepeatButton Command='{x:Static ScrollBar.PageUpCommand}' Focusable='False' IsTabStop='False'>
                <RepeatButton.Template>
                  <ControlTemplate TargetType='{x:Type RepeatButton}'><Border Background='Transparent'/></ControlTemplate>
                </RepeatButton.Template>
              </RepeatButton>
            </Track.DecreaseRepeatButton>
            <Track.Thumb>
              <Thumb MinHeight='24' Background='#575C69' Focusable='False' IsTabStop='False'>
                <Thumb.Template>
                  <ControlTemplate TargetType='{x:Type Thumb}'>
                    <Border x:Name='Handle' Background='{TemplateBinding Background}' CornerRadius='4' Margin='2,0,2,0'/>
                    <ControlTemplate.Triggers>
                      <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Handle' Property='Background' Value='#818795'/></Trigger>
                      <Trigger Property='IsDragging' Value='True'><Setter TargetName='Handle' Property='Background' Value='#A4A8B4'/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command='{x:Static ScrollBar.PageDownCommand}' Focusable='False' IsTabStop='False'>
                <RepeatButton.Template>
                  <ControlTemplate TargetType='{x:Type RepeatButton}'><Border Background='Transparent'/></ControlTemplate>
                </RepeatButton.Template>
              </RepeatButton>
            </Track.IncreaseRepeatButton>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
            return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        private static Button MakeButton(string text, string name, bool primary)
        {
            var button = new Button { Content = text, Focusable = true };
            if (primary)
            {
                button.Background = AccentBrush;
                button.Foreground = AccentTextBrush;
                button.BorderBrush = AccentBrush;
                button.FontWeight = FontWeights.SemiBold;
            }
            AutomationProperties.SetName(button, name);
            return button;
        }

        private sealed class CardView
        {
            private readonly PickerWindow _owner;
            private readonly int _index;
            private readonly Image _image;
            private readonly Grid _previewPanel;
            private readonly Grid _imagePlaceholder;
            private readonly TextBlock _placeholderText;
            private readonly TextBlock _pool;
            private readonly TextBlock _title;
            private readonly TextBlock _status;
            private readonly Border _statusPanel;
            private readonly ProgressBar _progress;
            private readonly Button _preview;
            private readonly Button _apply;
            private readonly Button _dislike;
            private readonly Button _details;
            private bool _dislikeRunning;
            private string _itemId;
            private string _loadedImagePath;
            private string _projectPath;
            private bool _hoverReady;
            private bool _knownSceneOnly;
            private bool _knownVideoOnly;
            public Border Root { get; private set; }
            public string ItemId { get { return _itemId; } }
            public string ProjectPath { get { return _projectPath; } }
            public bool IsKnownSceneOnly { get { return _knownSceneOnly; } }
            public bool IsKnownVideoOnly { get { return _knownVideoOnly; } }
            public bool CanPrepare { get { return _hoverReady && !String.IsNullOrEmpty(_itemId); } }
            public bool CanHover { get { return CanPrepare && _previewPanel.IsMouseOver; } }

            public CardView(PickerWindow owner, int index)
            {
                _owner = owner;
                _index = index;
                Root = new Border
                {
                    Background = CardBrush,
                    BorderBrush = BorderBrushToken,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(16),
                    Padding = new Thickness(12),
                    VerticalAlignment = VerticalAlignment.Top
                };
                AutomationProperties.SetName(Root, "候选壁纸 " + (index + 1).ToString(CultureInfo.InvariantCulture));
                var content = new StackPanel();
                Root.Child = content;
                var previewPanel = new Grid { Height = 184, Background = Brush("#1B1D23"), ClipToBounds = true };
                _previewPanel = previewPanel;
                previewPanel.MouseEnter += delegate { _owner.BeginHover(this); };
                previewPanel.MouseLeave += delegate { _owner.EndHover(this, "pointer-left"); };
                previewPanel.SizeChanged += delegate
                {
                    double desiredHeight = Math.Max(120, Math.Min(220, previewPanel.ActualWidth * 0.68));
                    if (Math.Abs(previewPanel.Height - desiredHeight) > 0.5) previewPanel.Height = desiredHeight;
                    previewPanel.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, previewPanel.ActualWidth), Math.Max(0, previewPanel.ActualHeight)), 12, 12);
                };
                _image = new Image { Stretch = Stretch.UniformToFill, SnapsToDevicePixels = true };
                AutomationProperties.SetAutomationId(_image, "Candidate" + (index + 1).ToString(CultureInfo.InvariantCulture) + "Cover");
                SetHoverState("static");
                RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
                _imagePlaceholder = new Grid();
                var placeholderStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                placeholderStack.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M 3,27 L 16,12 L 25,22 L 33,14 L 45,27 M 30,5 A 3,3 0 1 1 30,11 A 3,3 0 1 1 30,5"),
                    Stroke = Brush("#515563"), StrokeThickness = 1.2, Width = 48, Height = 32,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                _placeholderText = new TextBlock
                {
                    Text = "等待候选", Foreground = MutedBrush, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 10, 8, 0)
                };
                placeholderStack.Children.Add(_placeholderText);
                _imagePlaceholder.Children.Add(placeholderStack);
                previewPanel.Children.Add(_image);
                previewPanel.Children.Add(_imagePlaceholder);
                _pool = new TextBlock { Foreground = TextBrush, FontSize = 11, FontWeight = FontWeights.Medium };
                var poolBadge = new Border
                {
                    Background = Brush("#B515171C"), CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 5, 9, 5),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(10), Child = _pool, IsHitTestVisible = false
                };
                previewPanel.Children.Add(poolBadge);
                _details = MakeButton(null, "打开候选 " + (index + 1).ToString(CultureInfo.InvariantCulture) + " 创意工坊详情", false);
                _details.Content = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M 8,2 L 14,2 L 14,8 M 14,2 L 6,10 M 5,3 L 2,3 L 2,14 L 13,14 L 13,11"),
                    Stroke = TextBrush, StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round, Width = 16, Height = 16
                };
                _details.Width = 32;
                _details.Height = 32;
                _details.MinHeight = 32;
                _details.Padding = new Thickness(7);
                _details.Background = Brush("#C5181A20");
                _details.BorderBrush = Brush("#30FFFFFF");
                _details.Margin = new Thickness(9);
                _details.HorizontalAlignment = HorizontalAlignment.Right;
                _details.VerticalAlignment = VerticalAlignment.Top;
                _details.ToolTip = "查看创意工坊详情";
                KeyboardNavigation.SetTabIndex(_details, index * 4);
                AutomationProperties.SetAutomationId(_details, "Candidate" + (index + 1).ToString(CultureInfo.InvariantCulture) + "Details");
                _details.Click += delegate
                {
                    _owner.EndHover(this, "open-details");
                    ulong numeric;
                    if (!UInt64.TryParse(_itemId, NumberStyles.None, CultureInfo.InvariantCulture, out numeric)) return;
                    try { Process.Start(new ProcessStartInfo("https://steamcommunity.com/sharedfiles/filedetails/?id=" + numeric.ToString(CultureInfo.InvariantCulture)) { UseShellExecute = true }); }
                    catch (Exception ex) { _owner.ShowFailure("无法打开创意工坊详情", ex); }
                };
                previewPanel.Children.Add(_details);

                _status = new TextBlock
                {
                    Foreground = TextBrush, FontSize = 11, MaxHeight = 34, LineHeight = 17,
                    TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis
                };
                AutomationProperties.SetName(_status, "候选 " + (index + 1).ToString(CultureInfo.InvariantCulture) + " 下载状态");
                _progress = new ProgressBar
                {
                    Minimum = 0, Maximum = 100, Height = 3, Margin = new Thickness(0, 7, 0, 0),
                    Background = Brush("#554B5061"), Foreground = AccentBrush, BorderThickness = new Thickness(0)
                };
                var statusContent = new StackPanel();
                statusContent.Children.Add(_status);
                statusContent.Children.Add(_progress);
                _statusPanel = new Border
                {
                    Background = Brush("#E01B1D23"), Padding = new Thickness(10, 8, 10, 8),
                    VerticalAlignment = VerticalAlignment.Bottom, Child = statusContent
                };
                previewPanel.Children.Add(_statusPanel);
                content.Children.Add(previewPanel);
                _title = new TextBlock
                {
                    FontSize = 14, FontWeight = FontWeights.Medium, Margin = new Thickness(1, 15, 1, 14), Height = 42,
                    LineHeight = 21, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis
                };
                content.Children.Add(_title);
                var actions = new Grid();
                actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.95, GridUnitType.Star) });
                actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) });
                actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
                _dislike = MakeButton("不喜欢", "不喜欢候选 " + (index + 1).ToString(CultureInfo.InvariantCulture) + "，换一张", false);
                AutomationProperties.SetAutomationId(_dislike, "Candidate" + (index + 1).ToString(CultureInfo.InvariantCulture) + "Dislike");
                AutomationProperties.SetHelpText(_dislike, "只更换这张候选，以后减少相似推荐；桌面保持不变。");
                KeyboardNavigation.SetTabIndex(_dislike, index * 4 + 1);
                _dislike.Background = Brushes.Transparent;
                _dislike.Foreground = MutedBrush;
                _dislike.Padding = new Thickness(4, 7, 4, 7);
                _dislike.ToolTip = "换一张，以后少推荐类似的";
                _dislike.Click += async delegate
                {
                    string id = _itemId;
                    if (String.IsNullOrEmpty(id) || _dislikeRunning || _owner._actionRunning || _owner._controller.Busy) return;
                    bool restoreFocus = _dislike.IsKeyboardFocusWithin;
                    _dislikeRunning = true;
                    _owner.EndHover(this, "dislike");
                    try { await _owner.RunAction(delegate { return _owner._controller.DislikeAsync(id); }, "正在换一张…"); }
                    finally
                    {
                        _dislikeRunning = false;
                        if (!_owner._disposed)
                        {
                            _owner.Render();
                            IInputElement focused = Keyboard.FocusedElement;
                            if (restoreFocus && _owner.IsVisible && _owner.IsActive
                                && (focused == null || focused == _dislike || focused == _owner))
                            {
                                if (!String.Equals(id, _itemId, StringComparison.Ordinal) && _preview.IsEnabled) _preview.Focus();
                                else if (_dislike.IsEnabled) _dislike.Focus();
                            }
                        }
                    }
                };
                _apply = MakeButton("使用这张", "使用候选 " + (index + 1).ToString(CultureInfo.InvariantCulture), true);
                AutomationProperties.SetAutomationId(_apply, "Candidate" + (index + 1).ToString(CultureInfo.InvariantCulture) + "Use");
                KeyboardNavigation.SetTabIndex(_apply, index * 4 + 3);
                _apply.Padding = new Thickness(6, 7, 6, 7);
                _apply.Click += async delegate
                {
                    string id = _itemId;
                    if (String.IsNullOrEmpty(id)) return;
                    await _owner.RunAction(delegate { return _owner._controller.ApplyAsync(id); }, "正在切换并确认壁纸…");
                };
                _preview = MakeButton("预览", "预览候选 " + (index + 1).ToString(CultureInfo.InvariantCulture), false);
                AutomationProperties.SetAutomationId(_preview, "Candidate" + (index + 1).ToString(CultureInfo.InvariantCulture) + "Preview");
                KeyboardNavigation.SetTabIndex(_preview, index * 4 + 2);
                _preview.Padding = new Thickness(6, 7, 6, 7);
                _preview.Click += async delegate
                {
                    string id = _itemId;
                    if (String.IsNullOrEmpty(id)) return;
                    await _owner.RunAction(delegate { return _owner._controller.PreviewAsync(id); }, "正在打开独立预览，桌面保持不变…");
                };
                Grid.SetColumn(_preview, 2);
                Grid.SetColumn(_apply, 4);
                actions.Children.Add(_dislike);
                actions.Children.Add(_preview);
                actions.Children.Add(_apply);
                content.Children.Add(actions);
            }

            public void Update(Candidate item, bool busy)
            {
                string nextId = item == null ? null : item.Id;
                string nextProject = item == null ? null : item.ProjectPath;
                bool nextReady = item != null && item.Ready && String.IsNullOrEmpty(item.Error) && !busy;
                bool identityChanged = !String.Equals(_itemId, nextId, StringComparison.Ordinal)
                    || !String.Equals(_projectPath, nextProject, StringComparison.OrdinalIgnoreCase);
                if (identityChanged || (_hoverReady && !nextReady)) _owner.ClosePreparedVideos(identityChanged ? "candidate-changed" : "candidate-unavailable");
                if (identityChanged) { SetHoverState("static"); SetHoverTip(null); }
                _hoverReady = nextReady;
                _projectPath = nextProject;
                string[] tags = item == null || item.Tags == null ? new string[0] : item.Tags;
                bool scene = Array.Exists(tags, delegate(string tag) { return String.Equals(tag, "Scene", StringComparison.OrdinalIgnoreCase); });
                bool video = Array.Exists(tags, delegate(string tag) { return String.Equals(tag, "Video", StringComparison.OrdinalIgnoreCase); });
                _knownSceneOnly = scene && !video;
                _knownVideoOnly = video && !scene;
                _itemId = item == null ? null : item.Id;
                _pool.Text = PoolName(item == null ? null : item.Pool, _index);
                _title.Text = item == null ? "正在寻找新壁纸" : (String.IsNullOrWhiteSpace(item.Title) ? "未命名壁纸" : item.Title);
                _title.ToolTip = _title.Text;
                _apply.IsEnabled = item != null && item.Ready && !busy;
                _preview.IsEnabled = item != null && item.Ready && !busy;
                _dislike.IsEnabled = item != null && !String.IsNullOrEmpty(item.Id) && !busy && !_dislikeRunning;
                _dislike.Content = _dislikeRunning ? "更换中" : "不喜欢";
                AutomationProperties.SetItemStatus(_dislike, _dislikeRunning ? "正在换一张" : "");
                _details.IsEnabled = item != null && !String.IsNullOrEmpty(item.Id);
                if (item != null)
                {
                    AutomationProperties.SetName(_apply, "使用这张：" + _title.Text);
                    AutomationProperties.SetName(_preview, "预览：" + _title.Text);
                    AutomationProperties.SetName(_dislike, "不喜欢：" + _title.Text + "，换一张");
                    AutomationProperties.SetName(_details, "创意工坊详情：" + _title.Text);
                }
                bool failed = item != null && !String.IsNullOrEmpty(item.Error);
                string status = item == null ? "连接 Steam 后准备候选" : StatusText(item);
                _status.Text = status;
                _status.ToolTip = status;
                _status.Foreground = failed ? ErrorBrush : TextBrush;
                _statusPanel.Visibility = item != null && item.Ready && !failed ? Visibility.Collapsed : Visibility.Visible;
                _statusPanel.ToolTip = status;
                _progress.IsIndeterminate = item != null && !item.Ready && !failed && item.Total <= 0 && String.Equals(item.Status, "downloading", StringComparison.OrdinalIgnoreCase);
                _progress.Value = item != null && item.Total > 0 ? Math.Min(100, Math.Max(0, (double)item.Downloaded / item.Total * 100)) : 0;
                _progress.Visibility = item != null && !item.Ready && !failed && (item.Total > 0 || _progress.IsIndeterminate) ? Visibility.Visible : Visibility.Collapsed;
                LoadImage(item == null ? null : item.ThumbnailPath);
                AutomationProperties.SetName(_image, item == null ? "候选封面待加载" : _title.Text + "，创意工坊封面");
            }

            public void AttachPlayer(MediaElement player)
            {
                // Keep badges and the detail button above the video, and the static cover below it.
                _previewPanel.Children.Insert(2, player);
            }

            public void DetachPlayer(MediaElement player)
            {
                if (player != null) _previewPanel.Children.Remove(player);
            }

            public void SetHoverState(string state)
            {
                AutomationProperties.SetItemStatus(_image, state);
                AutomationProperties.SetHelpText(_image, "悬停预览：" + state);
            }

            public void SetHoverTip(string text)
            {
                _previewPanel.ToolTip = null;
                AutomationProperties.SetHelpText(_previewPanel, text ?? String.Empty);
            }

            private void LoadImage(string path)
            {
                if (String.Equals(path, _loadedImagePath, StringComparison.OrdinalIgnoreCase) && _image.Source != null) return;
                _loadedImagePath = path;
                _image.Source = null;
                _imagePlaceholder.Visibility = Visibility.Visible;
                _placeholderText.Text = String.IsNullOrEmpty(_itemId) ? "等待候选" : "封面加载中";
                if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bitmap.DecodePixelWidth = 640;
                    bitmap.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    _image.Source = bitmap;
                    _imagePlaceholder.Visibility = Visibility.Collapsed;
                }
                catch (Exception)
                {
                    _placeholderText.Text = "封面暂不可用";
                }
            }

            private static string PoolName(string value, int index)
            {
                if (String.Equals(value, "popular", StringComparison.OrdinalIgnoreCase)) return "热门";
                if (String.Equals(value, "newest", StringComparison.OrdinalIgnoreCase)) return "新作";
                if (String.Equals(value, "classic", StringComparison.OrdinalIgnoreCase)) return "经典";
                if (!String.IsNullOrWhiteSpace(value)) return value;
                return new[] { "热门", "新作", "经典" }[index];
            }

            private static string StatusText(Candidate item)
            {
                if (!String.IsNullOrWhiteSpace(item.Error)) return item.Error;
                if (item.Ready) return "";
                if (item.Total > 0)
                {
                    return "下载中 " + Megabytes(item.Downloaded) + " / " + Megabytes(item.Total) + " MB";
                }
                string raw = item.Status ?? "";
                switch (raw.ToLowerInvariant())
                {
                    case "querying": return "正在寻找适合你的壁纸";
                    case "queued": return "等待下载";
                    case "pending": return "等待下载";
                    case "downloading": return "正在等待 Steam 下载…";
                    case "verifying": return "下载完成，正在核验文件";
                    case "copying": return "正在保存预览副本";
                    case "failed": return "尚未准备好，可稍后重试";
                    case "error": return "尚未准备好，可稍后重试";
                    case "": return "准备中";
                    default: return raw;
                }
            }

            private static string Megabytes(long value)
            {
                return Math.Max(0, value / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
            }
        }
    }
}
