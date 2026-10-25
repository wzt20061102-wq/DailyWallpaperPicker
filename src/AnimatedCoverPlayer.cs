// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.
using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DailyWallpaperPicker
{
    // Plays the author's cover frames only. This never invokes Wallpaper Engine.
    internal sealed class AnimatedCoverPlayer : IDisposable
    {
        readonly Image image;
        readonly Action<string> report;
        readonly DispatcherTimer timer;
        CoverAnimation animation;
        int frame;
        bool playing;
        bool disposed;

        internal AnimatedCoverPlayer(Image image, Action<string> report)
        {
            this.image = image;
            this.report = report;
            timer = new DispatcherTimer(DispatcherPriority.Render, image.Dispatcher);
            timer.Tick += Advance;
        }
        internal bool CanPlay { get { return animation != null && animation.IsAnimated; } }
        internal bool IsPlaying { get { return playing; } }
        internal int FrameIndex { get { return frame; } }

        internal void SetAnimation(CoverAnimation value)
        {
            Stop();
            animation = value;
            ShowPoster();
        }
        internal void Play()
        {
            if (disposed || playing || !CanPlay) return;
            playing = true;
            frame = animation.PosterFrameIndex;
            RenderFrame();
            timer.Start();
        }
        internal void Stop()
        {
            playing = false;
            timer.Stop();
            ShowPoster();
        }
        void ShowPoster()
        {
            frame = animation == null ? 0 : animation.PosterFrameIndex;
            image.Source = animation == null ? null : animation.Poster;
            if (CanPlay) report("cover-ready; paused; frame=" + frame.ToString(CultureInfo.InvariantCulture));
        }
        void Advance(object sender, EventArgs args)
        {
            if (disposed || !playing || !CanPlay) return;
            frame++;
            // Do not flash a blank initialization frame on every loop.
            if (frame >= animation.Frames.Count) frame = animation.PosterFrameIndex;
            RenderFrame();
        }
        void RenderFrame()
        {
            image.Source = animation.Frames[frame];
            timer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, animation.DelaysMilliseconds[frame]));
            report("cover-playing; muted=true; frame=" + frame.ToString(CultureInfo.InvariantCulture));
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Stop();
            timer.Tick -= Advance;
            animation = null;
            image.Source = null;
        }
    }
}
