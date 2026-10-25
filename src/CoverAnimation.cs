// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DailyWallpaperPicker
{
    /// <summary>Detached, frozen cover pixels. Decoding never opens a wallpaper.</summary>
    internal sealed class CoverAnimation
    {
        private const int MaximumInputBytes = 10 * 1024 * 1024;
        private const int MaximumDimension = 4096;
        private const int MaximumCanvasPixels = 4 * 1024 * 1024;
        private const int MaximumFrames = 240;
        private const long MaximumDecodedBytes = 64L * 1024 * 1024;

        public BitmapSource Poster { get; private set; }
        public IReadOnlyList<BitmapSource> Frames { get; private set; }
        public IReadOnlyList<int> DelaysMilliseconds { get; private set; }
        public int PosterFrameIndex { get; private set; }
        public bool IsAnimated { get { return Frames.Count > 1; } }
        public string FallbackReason { get; private set; }

        private CoverAnimation(List<BitmapSource> frames, List<int> delays, int posterIndex, string reason)
        {
            Frames = new ReadOnlyCollection<BitmapSource>(frames);
            DelaysMilliseconds = new ReadOnlyCollection<int>(delays);
            PosterFrameIndex = posterIndex;
            Poster = frames[posterIndex];
            FallbackReason = reason;
        }

        public static CoverAnimation Load(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Cover path is empty.", "path");
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length == 0 || stream.Length > MaximumInputBytes)
                    throw new InvalidDataException("Cover must contain between 1 byte and 10 MiB.");
                bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int count = stream.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) throw new InvalidDataException("Cover ended while being read.");
                    offset += count;
                }
            }
            try
            {
                bool gif = bytes.Length >= 6 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F'
                    && bytes[3] == '8' && (bytes[4] == '7' || bytes[4] == '9') && bytes[5] == 'a';
                return gif ? LoadGif(bytes) : LoadStill(bytes);
            }
            catch (InvalidDataException) { throw; }
            catch (NotSupportedException ex) { throw new InvalidDataException("Cover format is not supported.", ex); }
            catch (FileFormatException ex) { throw new InvalidDataException("Cover pixels could not be decoded.", ex); }
            catch (ArgumentException ex) { throw new InvalidDataException("Cover data is invalid.", ex); }
        }

        private static CoverAnimation LoadStill(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes, false))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
                if (decoder.Frames.Count == 0) throw new InvalidDataException("Cover contains no image.");
                int width = decoder.Frames[0].PixelWidth;
                int height = decoder.Frames[0].PixelHeight;
                CheckCanvas(width, height);
                stream.Position = 0;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                // A fresh StreamSource has no URI cache entry; IgnoreImageCache would
                // call ImagingCache.RemoveFromCache(null) on .NET Framework WPF.
                bitmap.CreateOptions = BitmapCreateOptions.None;
                // Retain the previous 640-pixel card quality without enlarging small covers.
                bitmap.DecodePixelWidth = Math.Min(640, width);
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return Single(bitmap, null);
            }
        }

        private static CoverAnimation Single(BitmapSource poster, string reason)
        {
            return new CoverAnimation(new List<BitmapSource> { poster }, new List<int> { 100 }, 0, reason);
        }

        private sealed class GifFrame
        {
            public int Start, End, Left, Top, Width, Height, Disposal, Delay, TransparentIndex;
            public bool Transparent;
        }

        private sealed class GifData
        {
            public int Width, Height, HeaderEnd;
            public byte BackgroundBlue, BackgroundGreen, BackgroundRed;
            public bool HasBackground;
            public string FallbackReason;
            public readonly List<GifFrame> Frames = new List<GifFrame>();
        }

        private static CoverAnimation LoadGif(byte[] bytes)
        {
            GifData gif = ReadGif(bytes);
            int stride = checked(gif.Width * 4);
            int canvasLength = checked(stride * gif.Height);
            // Include the canvas, restore-previous buffer and decoded patch in the budget.
            if ((long)canvasLength * (gif.Frames.Count + 3) > MaximumDecodedBytes)
                gif.FallbackReason = "Animated cover exceeds the 64 MiB decoded-pixel budget; showing a still cover.";
            var canvas = new byte[canvasLength];
            FillBackground(canvas, gif.Width, gif, gif.Frames[0], true);
            var images = new List<BitmapSource>();
            var delays = new List<int>();
            int posterIndex = 0;
            bool needsPoster = true;
            int fallbackScanLimit = (int)Math.Max(1, MaximumDecodedBytes / canvasLength - 3);
            int decodeCount = gif.FallbackReason == null ? gif.Frames.Count : Math.Min(gif.Frames.Count, Math.Min(MaximumFrames, fallbackScanLimit));
            BitmapSource fallbackPoster = null;

            for (int index = 0; index < decodeCount; index++)
            {
                GifFrame frame = gif.Frames[index];
                byte[] previous = frame.Disposal == 3 ? (byte[])canvas.Clone() : null;
                byte[] pixels;
                try { pixels = DecodePatch(bytes, gif, frame); }
                catch (Exception ex)
                {
                    // A complete earlier frame is safer than a partial/corrupt animation.
                    if (!(ex is InvalidDataException || ex is NotSupportedException || ex is FileFormatException || ex is ArgumentException)) throw;
                    if (images.Count > 0) return Single(images[posterIndex], "A GIF frame could not be decoded; showing a still cover.");
                    if (fallbackPoster != null) return Single(fallbackPoster, "A GIF frame could not be decoded; showing a still cover.");
                    throw new InvalidDataException("GIF contains no decodable frame.", ex);
                }
                Composite(canvas, stride, pixels, frame);
                var image = BitmapSource.Create(gif.Width, gif.Height, 96, 96, PixelFormats.Bgra32, null, canvas, stride);
                image.Freeze();
                bool blank = IsNearBlack(canvas);
                if (gif.FallbackReason != null)
                {
                    // Do not retain animation frames after a limit is reached. A black lead-in
                    // may still be replaced by the first usable frame within the scan budget.
                    fallbackPoster = image;
                    if (!blank || index == decodeCount - 1) return Single(image, gif.FallbackReason);
                }
                else
                {
                    images.Add(image);
                    delays.Add(frame.Delay);
                    if (needsPoster && !blank) { posterIndex = index; needsPoster = false; }
                }
                if (frame.Disposal == 2) FillBackground(canvas, gif.Width, gif, frame, false);
                else if (frame.Disposal == 3) canvas = previous;
            }
            return new CoverAnimation(images, delays, posterIndex, null);
        }

        private static bool IsNearBlack(byte[] pixels)
        {
            // Only a truly blank lead-in is skipped. Ordinary dark artwork stays unchanged.
            for (int i = 0; i < pixels.Length; i += 4)
                if (pixels[i + 3] != 0 && (pixels[i] > 2 || pixels[i + 1] > 2 || pixels[i + 2] > 2)) return false;
            return true;
        }

        private static byte[] DecodePatch(byte[] source, GifData gif, GifFrame frame)
        {
            // WIC receives one standalone frame. Its decoder and stream cannot retain the
            // complete animation, and offsets/disposal are applied explicitly below.
            int blockLength = frame.End - frame.Start;
            var data = new byte[gif.HeaderEnd + 8 + blockLength + 1];
            Buffer.BlockCopy(source, 0, data, 0, gif.HeaderEnd);
            WriteWord(data, 6, frame.Width);
            WriteWord(data, 8, frame.Height);
            int control = gif.HeaderEnd;
            data[control] = 0x21; data[control + 1] = 0xF9; data[control + 2] = 4;
            data[control + 3] = (byte)(frame.Transparent ? 1 : 0);
            data[control + 6] = (byte)frame.TransparentIndex;
            int imageOffset = control + 8;
            Buffer.BlockCopy(source, frame.Start, data, imageOffset, blockLength);
            WriteWord(data, imageOffset + 1, 0);
            WriteWord(data, imageOffset + 3, 0);
            data[data.Length - 1] = 0x3B;
            using (var stream = new MemoryStream(data, false))
            {
                var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
                if (converted.PixelWidth != frame.Width || converted.PixelHeight != frame.Height)
                    throw new InvalidDataException("GIF decoder returned an unexpected frame size.");
                int stride = checked(frame.Width * 4);
                var pixels = new byte[checked(stride * frame.Height)];
                converted.CopyPixels(pixels, stride, 0);
                return pixels;
            }
        }

        private static void Composite(byte[] canvas, int canvasStride, byte[] patch, GifFrame frame)
        {
            for (int y = 0; y < frame.Height; y++)
            {
                int source = y * frame.Width * 4;
                int destination = (frame.Top + y) * canvasStride + frame.Left * 4;
                for (int x = 0; x < frame.Width; x++, source += 4, destination += 4)
                {
                    // GIF palette alpha is binary. Transparent pixels leave prior content intact.
                    if (patch[source + 3] == 0) continue;
                    canvas[destination] = patch[source];
                    canvas[destination + 1] = patch[source + 1];
                    canvas[destination + 2] = patch[source + 2];
                    canvas[destination + 3] = 255;
                }
            }
        }

        private static void FillBackground(byte[] canvas, int width, GifData gif, GifFrame frame, bool wholeCanvas)
        {
            int left = wholeCanvas ? 0 : frame.Left;
            int top = wholeCanvas ? 0 : frame.Top;
            int right = wholeCanvas ? gif.Width : frame.Left + frame.Width;
            int bottom = wholeCanvas ? gif.Height : frame.Top + frame.Height;
            bool opaque = gif.HasBackground && !frame.Transparent;
            for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                {
                    int i = (y * width + x) * 4;
                    canvas[i] = opaque ? gif.BackgroundBlue : (byte)0;
                    canvas[i + 1] = opaque ? gif.BackgroundGreen : (byte)0;
                    canvas[i + 2] = opaque ? gif.BackgroundRed : (byte)0;
                    canvas[i + 3] = opaque ? (byte)255 : (byte)0;
                }
        }

        private static GifData ReadGif(byte[] bytes)
        {
            Require(bytes, 0, 13);
            var gif = new GifData { Width = Word(bytes, 6), Height = Word(bytes, 8) };
            CheckCanvas(gif.Width, gif.Height);
            int offset = 13;
            if ((bytes[10] & 128) != 0)
            {
                int count = 1 << ((bytes[10] & 7) + 1);
                Require(bytes, offset, count * 3);
                int background = bytes[11];
                if (background < count)
                {
                    gif.HasBackground = true;
                    gif.BackgroundRed = bytes[offset + background * 3];
                    gif.BackgroundGreen = bytes[offset + background * 3 + 1];
                    gif.BackgroundBlue = bytes[offset + background * 3 + 2];
                }
                offset += count * 3;
            }
            gif.HeaderEnd = offset;
            int disposal = 0, delay = 100, transparentIndex = 0;
            bool transparent = false;
            bool trailer = false;
            while (offset < bytes.Length)
            {
                int marker = bytes[offset++];
                if (marker == 0x3B) { trailer = true; break; }
                if (marker == 0x21)
                {
                    Require(bytes, offset, 1);
                    int label = bytes[offset++];
                    if (label == 0xF9)
                    {
                        Require(bytes, offset, 6);
                        if (bytes[offset] != 4 || bytes[offset + 5] != 0) throw new InvalidDataException("Invalid GIF control block.");
                        int flags = bytes[offset + 1];
                        disposal = (flags >> 2) & 7;
                        transparent = (flags & 1) != 0;
                        transparentIndex = bytes[offset + 4];
                        delay = Word(bytes, offset + 2) * 10;
                        if (delay == 0) delay = 100;
                        if (disposal > 3 || (flags & 2) != 0)
                            gif.FallbackReason = "GIF uses unsupported disposal or user-input timing; showing a still cover.";
                        offset += 6;
                    }
                    else
                    {
                        if (label == 0x01) gif.FallbackReason = "GIF contains plain-text rendering; showing a still cover.";
                        SkipBlocks(bytes, ref offset);
                    }
                    continue;
                }
                if (marker != 0x2C) throw new InvalidDataException("Invalid GIF block marker.");
                Require(bytes, offset, 9);
                var frame = new GifFrame {
                    Start = offset - 1, Left = Word(bytes, offset), Top = Word(bytes, offset + 2),
                    Width = Word(bytes, offset + 4), Height = Word(bytes, offset + 6),
                    Disposal = disposal, Delay = delay, Transparent = transparent, TransparentIndex = transparentIndex
                };
                if (frame.Width < 1 || frame.Height < 1 || frame.Left + frame.Width > gif.Width || frame.Top + frame.Height > gif.Height)
                    throw new InvalidDataException("GIF frame falls outside its logical canvas.");
                int flagsImage = bytes[offset + 8];
                offset += 9;
                if ((flagsImage & 128) != 0)
                {
                    int paletteLength = 3 * (1 << ((flagsImage & 7) + 1));
                    Require(bytes, offset, paletteLength);
                    offset += paletteLength;
                }
                Require(bytes, offset, 1);
                offset++; // WIC validates and decodes the LZW stream.
                SkipBlocks(bytes, ref offset);
                frame.End = offset;
                gif.Frames.Add(frame);
                if (gif.Frames.Count > MaximumFrames)
                {
                    gif.FallbackReason = "GIF exceeds the 240-frame limit; showing a still cover.";
                    break;
                }
                disposal = 0; delay = 100; transparent = false; transparentIndex = 0;
            }
            if (gif.Frames.Count == 0) throw new InvalidDataException("GIF contains no image frame.");
            if (!trailer && gif.Frames.Count <= MaximumFrames) throw new InvalidDataException("GIF is truncated before its trailer.");
            return gif;
        }

        private static void CheckCanvas(int width, int height)
        {
            if (width < 1 || height < 1 || width > MaximumDimension || height > MaximumDimension || (long)width * height > MaximumCanvasPixels)
                throw new InvalidDataException("Cover canvas exceeds 4096 pixels per side or 4,194,304 pixels total.");
        }

        private static void SkipBlocks(byte[] bytes, ref int offset)
        {
            while (true)
            {
                Require(bytes, offset, 1);
                int count = bytes[offset++];
                if (count == 0) return;
                Require(bytes, offset, count);
                offset += count;
            }
        }

        private static int Word(byte[] bytes, int offset) { return bytes[offset] | (bytes[offset + 1] << 8); }
        private static void WriteWord(byte[] bytes, int offset, int value) { bytes[offset] = (byte)value; bytes[offset + 1] = (byte)(value >> 8); }
        private static void Require(byte[] bytes, int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new InvalidDataException("GIF data is truncated.");
        }
    }
}
