// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
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

namespace DailyWallpaperPicker
{
    /// <summary>A presentation-only window. Applying a wallpaper requires the explicit Use button.</summary>
    public sealed class PickerWindow : Window, ICardActions
    {

        private readonly PickerController _controller;
        private readonly Grid _layoutRoot;
        private readonly CardView[] _cards = new CardView[3];
        private readonly TextBlock _batchLabel;
        private readonly TextBlock _statusLabel;
        private readonly TextBlock _pauseLabel;
        private readonly Button _retryButton;
        private readonly TrayManager _tray;
        private readonly HoverPreviewManager _hover;
        private string _lastAppliedId;
        private string _lastBatchId;
        private string _interactionMessage;
        private bool _interactionFailed;
        private bool _actionRunning;
        private bool _disposed;
        private int _renderQueued;
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
            _hover = new HoverPreviewManager(controller, Dispatcher, _cards, delegate
            {
                return !_disposed && IsVisible && WindowState != WindowState.Minimized && !_actionRunning;
            });
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
            Background = PickerStyles.CanvasBrush;
            Foreground = PickerStyles.TextBrush;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 13;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            AutomationProperties.SetName(this, "每日壁纸三选一");
            AutomationProperties.SetAutomationId(this, "DailyWallpaperPickerWindow");
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
            Resources.Add(typeof(Button), PickerStyles.CreateButtonStyle());
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
                Foreground = PickerStyles.MutedBrush, Margin = new Thickness(0, 7, 8, 0), TextWrapping = TextWrapping.Wrap
            });
            header.Children.Add(heading);
            var headerRight = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            _batchLabel = new TextBlock { Foreground = PickerStyles.MutedBrush, FontSize = 12, TextAlignment = TextAlignment.Center };
            _pauseLabel = new TextBlock { Foreground = PickerStyles.AccentBrush, FontSize = 12, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 7, 0, 0), Visibility = Visibility.Collapsed };
            headerRight.Children.Add(new Border
            {
                Background = PickerStyles.CardBrush, BorderBrush = PickerStyles.BorderBrushToken, BorderThickness = new Thickness(1),
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
            cardScroll.Resources.Add(typeof(ScrollBar), PickerStyles.CreateScrollBarStyle());
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
            _statusLabel = new TextBlock { Foreground = PickerStyles.MutedBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 38, Margin = new Thickness(0, 0, 0, 5), Visibility = Visibility.Collapsed };
            AutomationProperties.SetName(_statusLabel, "推荐状态");
            footerCopy.Children.Add(_statusLabel);
            footerCopy.Children.Add(new TextBlock
            {
                Text = "每天 23:00 更新 · 不选就保持原样",
                Foreground = PickerStyles.MutedBrush, FontSize = 12
            });
            footer.Children.Add(footerCopy);
            var footerActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _retryButton = PickerStyles.MakeButton("重试", "重试今日壁纸", false);
            AutomationProperties.SetAutomationId(_retryButton, "RetryToday");
            KeyboardNavigation.SetTabIndex(_retryButton, 9);
            _retryButton.MinWidth = 62;
            _retryButton.Margin = new Thickness(0, 0, 8, 0);
            _retryButton.Visibility = Visibility.Collapsed;
            _retryButton.Click += async delegate { await RunAction(_controller.RetryAsync, "正在重新连接，请稍等…"); };
            var laterButton = PickerStyles.MakeButton("暂时不选", "暂时不选并收起到托盘", false);
            AutomationProperties.SetAutomationId(laterButton, "HideToTray");
            KeyboardNavigation.SetTabIndex(laterButton, 10);
            laterButton.MinWidth = 94;
            laterButton.Background = Brushes.Transparent;
            laterButton.BorderBrush = Brushes.Transparent;
            laterButton.Foreground = PickerStyles.MutedBrush;
            laterButton.Click += delegate { HideToTray(); };
            footerActions.Children.Add(_retryButton);
            footerActions.Children.Add(laterButton);
            Grid.SetColumn(footerActions, 1);
            footer.Children.Add(footerActions);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            _tray = new TrayManager(
                delegate { OnUi(delegate { ShowPicker(true); }); },
                delegate { OnUi(TogglePause); },
                delegate { OnUi(RequestExit); },
                delegate { return _controller.State != null && _controller.State.Paused; });
            _controller.Changed += ControllerChanged;
            Closing += WindowClosing;
            Closed += WindowClosed;
            Deactivated += delegate { _hover.StopHover("window-deactivated"); };
            IsVisibleChanged += delegate { if (!IsVisible) _hover.ClosePreparedVideos("window-hidden"); else _hover.PrepareHoverPreviews(); };
            StateChanged += delegate { if (WindowState == WindowState.Minimized) _hover.ClosePreparedVideos("window-minimized"); else _hover.PrepareHoverPreviews(); };
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
            _hover.SuppressPreparation = false;
            _hover.PrepareHoverPreviews();
            _controller.Log("WINDOW SHOW manual=" + userInitiated + " foregroundBefore=" + foregroundBefore.ToInt64() + " foregroundAfter=" + GetForegroundWindow().ToInt64() + " topmost=" + Topmost);
        }

        private void HideToTray()
        {
            if (_disposed) return;
            _hover.ClosePreparedVideos("hide-to-tray");
            try { _controller.HidePreview(); }
            catch (Exception ex) { _controller.Log("Hide preview: " + ex.Message); }
            Hide();
            ShowInTaskbar = false;
            _controller.Log("WINDOW HIDE to tray; preview close requested");
        }

        private void RequestExit()
        {
            if (_disposed) return;
            _hover.ClosePreparedVideos("exit-requested");
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
            _hover.ClosePreparedVideos("window-closing");
            if (!AllowClose)
            {
                args.Cancel = true;
                HideToTray();
            }
        }

        private void WindowClosed(object sender, EventArgs args)
        {
            _hover.ClosePreparedVideos("window-closed");
            _disposed = true;
            _controller.Changed -= ControllerChanged;
            _hover.Dispose();
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
                _hover.ClosePreparedVideos("batch-changed");
                _lastBatchId = state.BatchId;
                _interactionMessage = null;
                _interactionFailed = false;
            }
            DateTime day;
            _batchLabel.Text = DateTime.TryParse(state.BatchId, CultureInfo.InvariantCulture, DateTimeStyles.None, out day)
                ? day.ToString("MM 月 dd 日", CultureInfo.InvariantCulture) : "每天 23:00";
            _pauseLabel.Text = "每日推荐已暂停";
            _pauseLabel.Visibility = state.Paused ? Visibility.Visible : Visibility.Collapsed;
            _tray.SetPaused(state.Paused);
            var items = state.Candidates == null ? new List<Candidate>() : state.Candidates;
            bool unavailable = _controller.Busy || _actionRunning;
            for (int i = 0; i < _cards.Length; i++) _cards[i].Update(i < items.Count ? items[i] : null, unavailable);
            string message = String.IsNullOrWhiteSpace(_interactionMessage) ? state.Message : _interactionMessage;
            MessageSeverity severity = String.IsNullOrWhiteSpace(_interactionMessage)
                ? state.MessageSeverity : (_interactionFailed ? MessageSeverity.Error : MessageSeverity.Info);
            bool failed = severity == MessageSeverity.Error;
            bool allReady = items.Count == 3;
            foreach (Candidate item in items) if (!item.Ready || !String.IsNullOrEmpty(item.Error)) allReady = false;
            bool showMessage = !String.IsNullOrWhiteSpace(message) && (unavailable || failed || severity == MessageSeverity.Info);
            _statusLabel.Text = message;
            _statusLabel.ToolTip = message;
            _statusLabel.Visibility = showMessage ? Visibility.Visible : Visibility.Collapsed;
            _statusLabel.Foreground = failed ? PickerStyles.ErrorBrush : PickerStyles.MutedBrush;
            _retryButton.IsEnabled = !unavailable;
            _retryButton.Visibility = !allReady || failed ? Visibility.Visible : Visibility.Collapsed;
            _hover.PrepareHoverPreviews();
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
            _hover.SuppressPreparation = true;
            _hover.ClosePreparedVideos("explicit-action");
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

        private void TogglePause()
        {
            try
            {
                _controller.SetPaused(!_controller.State.Paused);
                Render();
            }
            catch (Exception ex) { ShowFailure("暂时无法修改提醒状态", ex); }
        }

        bool ICardActions.ActionsUnavailable { get { return _actionRunning || _controller.Busy; } }
        bool ICardActions.IsDisposed { get { return _disposed; } }
        void ICardActions.BeginHover(CardView card) { _hover.BeginHover(card); }
        void ICardActions.EndHover(CardView card, string reason) { _hover.EndHover(card, reason); }
        void ICardActions.InvalidatePrepared(string reason) { _hover.ClosePreparedVideos(reason); }
        void ICardActions.RefreshCards() { Render(); }
        bool ICardActions.CanRestoreFocus(IInputElement focused, IInputElement original)
        {
            return IsVisible && IsActive && (focused == null || focused == original || focused == this);
        }
        Task ICardActions.DislikeAsync(string id)
        {
            return RunAction(delegate { return _controller.DislikeAsync(id); }, "正在换一张…");
        }
        Task ICardActions.ApplyAsync(string id)
        {
            return RunAction(delegate { return _controller.ApplyAsync(id); }, "正在切换并确认壁纸…");
        }
        Task ICardActions.PreviewAsync(string id)
        {
            return RunAction(delegate { return _controller.PreviewAsync(id); }, "正在打开独立预览，桌面保持不变…");
        }
    }
}
