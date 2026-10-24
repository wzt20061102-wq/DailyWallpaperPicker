// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.

using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DailyWallpaperPicker
{
    /// <summary>Card commands are explicit; rendering a candidate cannot apply a wallpaper.</summary>
    internal interface ICardActions
    {
        bool ActionsUnavailable { get; }
        bool IsDisposed { get; }
        void BeginHover(CardView card);
        void EndHover(CardView card, string reason);
        void InvalidatePrepared(string reason);
        Task DislikeAsync(string itemId);
        Task ApplyAsync(string itemId);
        Task PreviewAsync(string itemId);
        void RefreshCards();
        bool CanRestoreFocus(IInputElement focused, IInputElement original);
    }
    /// <summary>A single candidate card, with explicit commands and typed status rendering.</summary>
    internal sealed class CardView
    {
        private readonly ICardActions _owner;
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

        public CardView(ICardActions owner, int index)
        {
            _owner = owner;
            _index = index;
            Root = new Border
            {
                Background = PickerStyles.CardBrush,
                BorderBrush = PickerStyles.BorderBrushToken,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(12),
                VerticalAlignment = VerticalAlignment.Top
            };
            AutomationProperties.SetName(Root, "候选壁纸 " + (index + 1).ToString(CultureInfo.InvariantCulture));
            var content = new StackPanel();
            Root.Child = content;
            var previewPanel = new Grid { Height = 184, Background = PickerStyles.Brush("#1B1D23"), ClipToBounds = true };
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
                Stroke = PickerStyles.Brush("#515563"), StrokeThickness = 1.2, Width = 48, Height = 32,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            _placeholderText = new TextBlock
            {
                Text = "等待候选", Foreground = PickerStyles.MutedBrush, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 10, 8, 0)
            };
            placeholderStack.Children.Add(_placeholderText);
            _imagePlaceholder.Children.Add(placeholderStack);
            previewPanel.Children.Add(_image);
            previewPanel.Children.Add(_imagePlaceholder);
            _pool = new TextBlock { Foreground = PickerStyles.TextBrush, FontSize = 11, FontWeight = FontWeights.Medium };
            var poolBadge = new Border
            {
                Background = PickerStyles.Brush("#B515171C"), CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 5, 9, 5),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(10), Child = _pool, IsHitTestVisible = false
            };
            previewPanel.Children.Add(poolBadge);
            _status = new TextBlock
            {
                Foreground = PickerStyles.TextBrush, FontSize = 11, MaxHeight = 34, LineHeight = 17,
                TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis
            };
            AutomationProperties.SetName(_status, "候选 " + (index + 1).ToString(CultureInfo.InvariantCulture) + " 下载状态");
            _progress = new ProgressBar
            {
                Minimum = 0, Maximum = 100, Height = 3, Margin = new Thickness(0, 7, 0, 0),
                Background = PickerStyles.Brush("#554B5061"), Foreground = PickerStyles.AccentBrush, BorderThickness = new Thickness(0)
            };
            var statusContent = new StackPanel();
            statusContent.Children.Add(_status);
            statusContent.Children.Add(_progress);
            _statusPanel = new Border
            {
                Background = PickerStyles.Brush("#E01B1D23"), Padding = new Thickness(10, 8, 10, 8),
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
            _dislike = PickerStyles.MakeButton("不喜欢", "不喜欢候选 " + (index + 1).ToString(CultureInfo.InvariantCulture) + "，换一张", false);
            AutomationProperties.SetAutomationId(_dislike, "Candidate" + (index + 1).ToString(CultureInfo.InvariantCulture) + "Dislike");
            AutomationProperties.SetHelpText(_dislike, "只更换这张候选，以后减少相似推荐；桌面保持不变。");
            KeyboardNavigation.SetTabIndex(_dislike, index * 3);
            _dislike.Background = Brushes.Transparent;
            _dislike.Foreground = PickerStyles.MutedBrush;
            _dislike.Padding = new Thickness(4, 7, 4, 7);
            _dislike.ToolTip = "换一张，以后少推荐类似的";
            _dislike.Click += async delegate
            {
                string id = _itemId;
                if (String.IsNullOrEmpty(id) || _dislikeRunning || _owner.ActionsUnavailable) return;
                bool restoreFocus = _dislike.IsKeyboardFocusWithin;
                _dislikeRunning = true;
                _owner.EndHover(this, "dislike");
                try { await _owner.DislikeAsync(id); }
                finally
                {
                    _dislikeRunning = false;
                    if (!_owner.IsDisposed)
                    {
                        _owner.RefreshCards();
                        IInputElement focused = Keyboard.FocusedElement;
                        if (restoreFocus && _owner.CanRestoreFocus(focused, _dislike))
                        {
                            if (!String.Equals(id, _itemId, StringComparison.Ordinal) && _preview.IsEnabled) _preview.Focus();
                            else if (_dislike.IsEnabled) _dislike.Focus();
                        }
                    }
                }
            };
            _apply = PickerStyles.MakeButton("使用这张", "使用候选 " + (index + 1).ToString(CultureInfo.InvariantCulture), true);
            AutomationProperties.SetAutomationId(_apply, "Candidate" + (index + 1).ToString(CultureInfo.InvariantCulture) + "Use");
            KeyboardNavigation.SetTabIndex(_apply, index * 3 + 2);
            _apply.Padding = new Thickness(6, 7, 6, 7);
            _apply.Click += async delegate
            {
                string id = _itemId;
                if (String.IsNullOrEmpty(id)) return;
                await _owner.ApplyAsync(id);
            };
            _preview = PickerStyles.MakeButton("预览", "预览候选 " + (index + 1).ToString(CultureInfo.InvariantCulture), false);
            AutomationProperties.SetAutomationId(_preview, "Candidate" + (index + 1).ToString(CultureInfo.InvariantCulture) + "Preview");
            KeyboardNavigation.SetTabIndex(_preview, index * 3 + 1);
            _preview.Padding = new Thickness(6, 7, 6, 7);
            _preview.Click += async delegate
            {
                string id = _itemId;
                if (String.IsNullOrEmpty(id)) return;
                await _owner.PreviewAsync(id);
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
            if (identityChanged || (_hoverReady && !nextReady)) _owner.InvalidatePrepared(identityChanged ? "candidate-changed" : "candidate-unavailable");
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
            if (item != null)
            {
                AutomationProperties.SetName(_apply, "使用这张：" + _title.Text);
                AutomationProperties.SetName(_preview, "预览：" + _title.Text);
                AutomationProperties.SetName(_dislike, "不喜欢：" + _title.Text + "，换一张");
            }
            bool failed = item != null && (item.Stage == DownloadStage.Failed || !String.IsNullOrEmpty(item.Error));
            string status = item == null ? "连接 Steam 后准备候选" : StatusText(item);
            _status.Text = status;
            _status.ToolTip = status;
            _status.Foreground = failed ? PickerStyles.ErrorBrush : PickerStyles.TextBrush;
            _statusPanel.Visibility = item != null && item.Ready && !failed ? Visibility.Collapsed : Visibility.Visible;
            _statusPanel.ToolTip = status;
            _progress.IsIndeterminate = item != null && !item.Ready && !failed && item.Total <= 0 && item.Stage == DownloadStage.Downloading;
            _progress.Value = item != null && item.Total > 0 ? Math.Min(100, Math.Max(0, (double)item.Downloaded / item.Total * 100)) : 0;
            _progress.Visibility = item != null && !item.Ready && !failed && item.Stage == DownloadStage.Downloading ? Visibility.Visible : Visibility.Collapsed;
            LoadImage(item == null ? null : item.ThumbnailPath);
            AutomationProperties.SetName(_image, item == null ? "候选封面待加载" : _title.Text + "，创意工坊封面");
        }

        public void AttachPlayer(MediaElement player)
        {
            // Keep the pool badge above the video, and the static cover below it.
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

        internal static string StatusText(Candidate item)
        {
            if (!String.IsNullOrWhiteSpace(item.Error)) return item.Error;
            if (item.Ready || item.Stage == DownloadStage.Ready) return "";
            switch (item.Stage)
            {
                case DownloadStage.Downloading:
                    return item.Total > 0
                        ? "下载中 " + Megabytes(item.Downloaded) + " / " + Megabytes(item.Total) + " MB"
                        : "正在等待 Steam 下载…";
                case DownloadStage.Verifying: return "下载完成，正在核验文件";
                case DownloadStage.Failed: return "尚未准备好，可稍后重试";
                default: return "等待下载";
            }
        }
        private static string Megabytes(long value)
        {
            return Math.Max(0, value / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
        }
    }
}
