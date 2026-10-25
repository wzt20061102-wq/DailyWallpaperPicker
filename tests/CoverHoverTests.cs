// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DailyWallpaperPicker
{
    public static partial class SelfTests
    {
        private static void CoverHoverTests(List<TestResult> results, string fixtureRoot)
        {
            string root = Path.Combine(fixtureRoot, "cover-hover");
            Directory.CreateDirectory(root);
            string blackIntro = Path.Combine(root, "black-intro.gif");
            WriteCoverGif(blackIntro, 8, 8, new[] {
                CoverFrame(8, 8, 0, 4), CoverFrame(8, 8, 2, 7), CoverFrame(8, 8, 3, 11)
            });

            Check(results, "cover_black_initial_frame_uses_first_visible_poster", delegate
            {
                CoverAnimation animation = CoverAnimation.Load(blackIntro);
                Require(animation.IsAnimated && animation.Frames.Count == 3, "GIF 应保留三个完整帧供悬停播放。");
                Require(animation.PosterFrameIndex == 1, "初始化黑帧不应作为封面。");
                Require(Object.ReferenceEquals(animation.Poster, animation.Frames[1]), "封面须对应实际后续帧。");
                Require(CoverPixel(animation.Poster, 0, 0) == 0xfff02820, "替代封面没有保留作者帧的颜色。");
                return new { fixture = blackIntro, frameCount = animation.Frames.Count, poster = animation.PosterFrameIndex };
            });
            Check(results, "cover_normal_dark_first_frame_is_not_skipped", delegate
            {
                string path = Path.Combine(root, "dark-intro.gif");
                WriteCoverGif(path, 8, 8, new[] { CoverFrame(8, 8, 1, 6), CoverFrame(8, 8, 2, 6) });
                CoverAnimation animation = CoverAnimation.Load(path);
                Require(animation.PosterFrameIndex == 0, "正常暗色封面不应被亮帧替代。");
                Require(CoverPixel(animation.Poster, 0, 0) == 0xff141820, "暗色封面被修改。");
                return new { fixture = path, poster = animation.PosterFrameIndex };
            });
            Check(results, "cover_gif_original_delays_and_zero_delay_default", delegate
            {
                CoverAnimation animation = CoverAnimation.Load(blackIntro);
                Require(animation.DelaysMilliseconds.SequenceEqual(new[] { 40, 70, 110 }), "GIF 每帧原始时长不一致。");
                string path = Path.Combine(root, "zero-delay.gif");
                WriteCoverGif(path, 2, 2, new[] { CoverFrame(2, 2, 2, 0), CoverFrame(2, 2, 3, 5) });
                CoverAnimation zero = CoverAnimation.Load(path);
                Require(zero.DelaysMilliseconds.SequenceEqual(new[] { 100, 50 }), "缺省零时长应使用 100 ms。");
                return new { delays = animation.DelaysMilliseconds.ToArray(), zeroDelay = zero.DelaysMilliseconds.ToArray() };
            });
            Check(results, "cover_frames_are_frozen_and_readable_across_threads", delegate
            {
                CoverAnimation animation = CoverAnimation.Load(blackIntro);
                Require(animation.Poster.IsFrozen && animation.Frames.All(f => f.IsFrozen), "后台解码结果必须冻结。");
                Exception error = null;
                uint pixel = 0;
                var thread = new Thread(delegate() { try { pixel = CoverPixel(animation.Poster, 1, 1); } catch (Exception ex) { error = ex; } });
                thread.Start();
                Require(thread.Join(2000), "跨线程帧读取超时。");
                Require(error == null && pixel == 0xfff02820, "冻结帧无法从另一线程安全读取。");
                return new { frozenFrames = animation.Frames.Count, crossThreadPixel = pixel };
            });
            Check(results, "cover_jpeg_remains_static", delegate
            {
                string path = Path.Combine(root, "static.jpg");
                byte[] pixels = Enumerable.Repeat((byte)120, 8 * 8 * 3).ToArray();
                BitmapSource bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Rgb24, null, pixels, 24);
                var encoder = new JpegBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(path)) encoder.Save(stream);
                CoverAnimation animation = CoverAnimation.Load(path);
                Require(!animation.IsAnimated && animation.Frames.Count == 1 && animation.PosterFrameIndex == 0, "JPEG 不应被识别为动画。");
                Require(animation.Poster.PixelWidth == 8 && animation.Poster.PixelHeight == 8 && animation.Poster.IsFrozen, "静态封面尺寸或冻结状态错误。");
                return new { fixture = path, animated = animation.IsAnimated };
            });
            Check(results, "cover_corrupt_data_is_rejected_without_partial_result", delegate
            {
                string path = Path.Combine(root, "corrupt.gif");
                File.WriteAllBytes(path, Encoding.ASCII.GetBytes("GIF89a-not-valid-image-data"));
                return new { rejection = RejectCover(path) };
            });
            Check(results, "cover_oversized_canvas_is_rejected_before_frame_allocation", delegate
            {
                string path = Path.Combine(root, "oversize-width.gif");
                WriteCoverGif(path, 4097, 1, new[] { CoverFrame(1, 1, 2, 5) });
                string width = RejectCover(path);
                string areaPath = Path.Combine(root, "oversize-area.gif");
                WriteCoverGif(areaPath, 2049, 2049, new[] { CoverFrame(1, 1, 2, 5) });
                string area = RejectCover(areaPath);
                return new { widthRejection = width, areaRejection = area, encodedBytes = new FileInfo(areaPath).Length };
            });
            Check(results, "cover_oversized_input_file_is_rejected", delegate
            {
                string path = Path.Combine(root, "oversize-input.gif");
                File.Copy(blackIntro, path);
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write)) stream.SetLength(10L * 1024 * 1024 + 1);
                return new { length = new FileInfo(path).Length, rejection = RejectCover(path) };
            });
            Check(results, "cover_excessive_frame_count_falls_back_to_static", delegate
            {
                string path = Path.Combine(root, "too-many-frames.gif");
                WriteCoverGif(path, 1, 1, Enumerable.Range(0, 241).Select(i => CoverFrame(1, 1, (byte)(i % 2 + 2), 5)).ToArray());
                CoverAnimation animation = CoverAnimation.Load(path);
                Require(!animation.IsAnimated && animation.Frames.Count == 1 && animation.Poster.IsFrozen, "帧数超限应安全保留静态封面。");
                return new { frameCount = animation.Frames.Count, poster = animation.PosterFrameIndex };
            });
            Check(results, "cover_frame_memory_budget_falls_back_to_static", delegate
            {
                string path = Path.Combine(root, "over-animation-memory.gif");
                WriteCoverGif(path, 1024, 1024, Enumerable.Range(0, 14).Select(i => CoverFrame(1, 1, (byte)(i % 2 + 2), 5)).ToArray());
                CoverAnimation animation = CoverAnimation.Load(path);
                Require(!animation.IsAnimated && animation.Frames.Count == 1 && animation.Poster.IsFrozen, "动画内存超限应安全保留静态封面。");
                return new { encodedBytes = new FileInfo(path).Length, animated = animation.IsAnimated };
            });
            Check(results, "cover_gif_offsets_restore_background_and_previous", delegate
            {
                var details = new List<object>();
                foreach (byte disposal in new byte[] { 2, 3 })
                {
                    CoverGifFrame overlay = CoverFrame(2, 2, 2, 5);
                    overlay.Left = 1; overlay.Top = 1; overlay.Disposal = disposal;
                    CoverGifFrame next = CoverFrame(2, 2, 3, 5);
                    next.Left = 4; next.Top = 4;
                    string path = Path.Combine(root, "disposal-" + disposal + ".gif");
                    WriteCoverGif(path, 8, 8, new[] { CoverFrame(8, 8, 1, 5), overlay, next });
                    CoverAnimation animation = CoverAnimation.Load(path);
                    Require(animation.Frames.Count == 3, "偏移帧数量错误。");
                    Require(CoverPixel(animation.Frames[1], 1, 1) == 0xfff02820 && CoverPixel(animation.Frames[1], 7, 7) == 0xff141820, "子帧偏移或保留背景错误。");
                    uint restored = CoverPixel(animation.Frames[2], 1, 1);
                    Require(restored == (disposal == 2 ? 0xff000000 : 0xff141820), "GIF disposal 未正确恢复背景/先前状态。");
                    Require(CoverPixel(animation.Frames[2], 4, 4) == 0xff2850f0, "后续子帧绘制位置错误。");
                    details.Add(new { disposal = disposal, restoredPixel = restored });
                }
                return details;
            });
            Check(results, "cover_gif_transparent_patch_preserves_then_clears_background", delegate
            {
                CoverGifFrame overlay = CoverFrame(2, 2, 0, 5);
                overlay.Left = 1; overlay.Top = 1; overlay.Disposal = 2; overlay.Transparent = true;
                overlay.Pixels[0] = 2;
                CoverGifFrame next = CoverFrame(1, 1, 3, 5);
                next.Left = 4; next.Top = 4;
                string path = Path.Combine(root, "transparent-overlay.gif");
                WriteCoverGif(path, 8, 8, new[] { CoverFrame(8, 8, 1, 5), overlay, next });
                CoverAnimation animation = CoverAnimation.Load(path);
                Require(CoverPixel(animation.Frames[1], 1, 1) == 0xfff02820, "不透明子帧像素未显示。");
                Require(CoverPixel(animation.Frames[1], 2, 1) == 0xff141820, "透明子帧像素未保留前帧。");
                Require(CoverPixel(animation.Frames[2], 1, 1) == 0 && CoverPixel(animation.Frames[2], 2, 1) == 0, "透明 disposal 2 区域未恢复为透明背景。");
                Require(CoverPixel(animation.Frames[2], 7, 7) == 0xff141820, "disposal 不应清除子帧外区域。");
                using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Require(exclusive.Length > 0, "解码完成后文件仍被占用。");
                return new { transparencyPreserved = true, disposalCleared = true, sourceReleased = true };
            });
            Check(results, "cover_player_dispatcher_advances_stops_and_disposes", delegate
            {
                CoverAnimation animation = CoverAnimation.Load(blackIntro);
                var image = new Image();
                var reports = new List<string>();
                var player = new AnimatedCoverPlayer(image, reports.Add);
                try
                {
                    player.SetAnimation(animation);
                    Require(player.CanPlay && !player.IsPlaying && Object.ReferenceEquals(image.Source, animation.Poster), "播放器初始状态须静止显示封面。");
                    player.Play();
                    player.Play();
                    Require(player.IsPlaying, "Play 未立即标记播放状态。");
                    Require(PumpCoverDispatcher(delegate { return player.FrameIndex != animation.PosterFrameIndex; }, 1000), "Dispatcher 实际计时未推进动画。");
                    player.Stop();
                    Require(!player.IsPlaying && player.FrameIndex == animation.PosterFrameIndex && Object.ReferenceEquals(image.Source, animation.Poster), "Stop 必须立即恢复封面。");
                    int stoppedReports = reports.Count;
                    PumpCoverDispatcher(delegate { return false; }, 180);
                    Require(reports.Count == stoppedReports && player.FrameIndex == animation.PosterFrameIndex, "停止后计时器仍在推进。");
                    player.Play();
                    Require(PumpCoverDispatcher(delegate { return player.FrameIndex != animation.PosterFrameIndex; }, 1000), "再次 Play 未恢复播放。");
                    player.Dispose();
                    Require(!player.IsPlaying && !player.CanPlay && image.Source == null, "释放后仍持有动画状态。");
                    int disposedReports = reports.Count;
                    player.Play();
                    PumpCoverDispatcher(delegate { return false; }, 180);
                    Require(reports.Count == disposedReports && image.Source == null, "释放后仍发生动画回调。");
                    return new { dispatcherAdvanceObserved = true, stoppedTimerObserved = true, pointerAutomationTest = false, reports = reports.Count };
                }
                finally { player.Dispose(); }
            });
        }

        private sealed class CoverGifFrame
        {
            internal int Left, Top, Width, Height, Delay;
            internal byte Disposal = 1;
            internal bool Transparent;
            internal byte[] Pixels;
        }
        private static CoverGifFrame CoverFrame(int width, int height, byte color, int centiseconds)
        {
            return new CoverGifFrame { Width = width, Height = height, Delay = centiseconds, Pixels = Enumerable.Repeat(color, width * height).ToArray() };
        }

        // Tiny technical fixtures, with a clear code before each pixel to keep all LZW codes at 3 bits.
        // This intentionally avoids external media, image libraries, or workshop artwork in the tests.
        private static void WriteCoverGif(string path, int width, int height, CoverGifFrame[] frames)
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(Encoding.ASCII.GetBytes("GIF89a"));
                writer.Write((ushort)width); writer.Write((ushort)height);
                writer.Write((byte)0x81); writer.Write((byte)0); writer.Write((byte)0);
                writer.Write(new byte[] { 0, 0, 0, 20, 24, 32, 240, 40, 32, 40, 80, 240 });
                foreach (CoverGifFrame frame in frames)
                {
                    writer.Write(new byte[] { 0x21, 0xf9, 4, (byte)((frame.Disposal << 2) | (frame.Transparent ? 1 : 0)) });
                    writer.Write((ushort)frame.Delay); writer.Write((byte)0); writer.Write((byte)0);
                    writer.Write((byte)0x2c);
                    writer.Write((ushort)frame.Left); writer.Write((ushort)frame.Top);
                    writer.Write((ushort)frame.Width); writer.Write((ushort)frame.Height); writer.Write((byte)0);
                    writer.Write((byte)2);
                    var codes = new List<int>();
                    foreach (byte pixel in frame.Pixels) { codes.Add(4); codes.Add(pixel); }
                    codes.Add(5);
                    var compressed = new List<byte>();
                    int bits = 0, count = 0;
                    foreach (int code in codes)
                    {
                        bits |= code << count; count += 3;
                        while (count >= 8) { compressed.Add((byte)bits); bits >>= 8; count -= 8; }
                    }
                    if (count > 0) compressed.Add((byte)bits);
                    for (int offset = 0; offset < compressed.Count; offset += 255)
                    {
                        int size = Math.Min(255, compressed.Count - offset);
                        writer.Write((byte)size); writer.Write(compressed.GetRange(offset, size).ToArray());
                    }
                    writer.Write((byte)0);
                }
                writer.Write((byte)0x3b);
            }
        }
        private static uint CoverPixel(BitmapSource source, int x, int y)
        {
            BitmapSource bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            byte[] pixel = new byte[4];
            bgra.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return ((uint)pixel[3] << 24) | ((uint)pixel[2] << 16) | ((uint)pixel[1] << 8) | pixel[0];
        }
        private static string RejectCover(string path)
        {
            try { CoverAnimation.Load(path); }
            catch (InvalidDataException ex) { return ex.Message; }
            throw new InvalidOperationException("不安全或损坏的封面没有返回预期的 InvalidDataException。");
        }
        private static bool PumpCoverDispatcher(Func<bool> completed, int timeoutMilliseconds)
        {
            var frame = new DispatcherFrame();
            var stopwatch = Stopwatch.StartNew();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += delegate { if (completed() || stopwatch.ElapsedMilliseconds >= timeoutMilliseconds) frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            return completed();
        }
    }
}
