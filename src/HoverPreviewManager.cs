// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DailyWallpaperPicker
{
    /// <summary>
    /// Owns the three preloaded media players and their cache leases. Only the hovered
    /// card may play; hiding, changing candidates or disposing releases every player.
    /// </summary>
    internal sealed class HoverPreviewManager : IDisposable
    {
        private readonly PickerController _controller;
        private readonly IEnumerable<CardView> _cards;
        private readonly Func<bool> _available;
        private bool _disposed;
        public bool SuppressPreparation { get { return _suppressPreparation; } set { _suppressPreparation = value; } }

        public HoverPreviewManager(PickerController controller, Dispatcher dispatcher, IEnumerable<CardView> cards, Func<bool> available)
        {
            _controller = controller;
            _cards = cards;
            _available = available;
            _hoverWatch = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            _hoverWatch.Interval = TimeSpan.FromMilliseconds(500);
            _hoverWatch.Tick += delegate { CheckPreparedHover(); };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ClosePreparedVideos("manager-disposed");
        }
        private CardView _hoverCard;
        private Stopwatch _hoverClock;
        private readonly Dictionary<CardView, PreparedVideo> _preparedVideos = new Dictionary<CardView, PreparedVideo>();
        private readonly HashSet<string> _preparationFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer _hoverWatch;
        private int _preparedGeneration;
        private bool _suppressPreparation;

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
            return !_disposed && _available() && !_controller.Busy;
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

        public void PrepareHoverPreviews()
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

        public void BeginHover(CardView card)
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

        public void EndHover(CardView card, string reason)
        {
            if (Object.ReferenceEquals(card, _hoverCard)) StopHover(reason);
        }

        public void StopHover(string reason)
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

        public void ClosePreparedVideos(string reason)
        {
            _hoverCard = null;
            _hoverClock = null;
            foreach (PreparedVideo prepared in new List<PreparedVideo>(_preparedVideos.Values)) ClosePreparedVideo(prepared, reason);
            _preparationFailures.Clear();
            _hoverWatch.Stop();
        }

    }
}
