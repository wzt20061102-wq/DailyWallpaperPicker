// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DailyWallpaperPicker
{
    public partial class PickerController
    {
        readonly Dictionary<string,DateTime> thumbnailRetryAfter=new Dictionary<string,DateTime>(StringComparer.Ordinal);
        readonly Dictionary<string,string> validatedThumbnails=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);

        void ResetThumbnailRetry()
        {
            lock(sync)thumbnailRetryAfter.Clear();
        }

        // Full wallpapers may already be ready when an optional cover failed. Run
        // this independently of the full-download gate, including after restart.
        async Task RecoverThumbnailsAsync(string batch,bool currentBatch=false)
        {
            List<Candidate> items;
            lock(sync)
            {
                var existing=ForBatch(batch);
                if(existing==null)return;
                items=existing.ToList();
            }
            foreach(var candidate in items)
            {
                if(!IsPreparationCurrent(batch,currentBatch))return;
                await Thumbnail(candidate,batch,currentBatch).ConfigureAwait(false);
            }
        }

        Task Thumbnail(Candidate c,string batch,bool currentBatch)
        {
            return Thumbnail(c,batch,TimeSpan.FromSeconds(15),currentBatch);
        }

        static string ThumbnailFingerprint(string file)
        {
            var info=new FileInfo(file);
            return info.Exists?info.Length+"|"+info.LastWriteTimeUtc.Ticks:null;
        }

        bool IsKnownThumbnail(string file)
        {
            string fingerprint=ThumbnailFingerprint(file);
            lock(sync)
            {
                string known;
                return fingerprint!=null&&validatedThumbnails.TryGetValue(file,out known)&&known==fingerprint;
            }
        }

        void RememberThumbnail(string file)
        {
            string fingerprint=ThumbnailFingerprint(file);
            lock(sync)
            {
                // These are hints only; eviction simply causes safe revalidation.
                if(validatedThumbnails.Count>=64)validatedThumbnails.Clear();
                validatedThumbnails[file]=fingerprint;
            }
        }

        // Failure remains optional and never changes wallpaper readiness. Retry
        // periodically; an explicit Retry bypasses this small network backoff.
        internal async Task Thumbnail(Candidate c,string batch,TimeSpan timeout,bool currentBatch=false)
        {
            string temporary=null;
            string retryKey=batch+"|"+c.Id+"|"+c.PreviewUrl;
            try
            {
                if(!IsPreparationCurrent(batch,currentBatch))return;
                lock(sync)
                {
                    var existing=ForBatch(batch);
                    if(existing==null||!existing.Any(x=>x.Id==c.Id))return;
                    DateTime next;
                    if(thumbnailRetryAfter.TryGetValue(retryKey,out next)&&UtcNow<next)return;
                }
                if(String.IsNullOrWhiteSpace(c.Id)||!c.Id.All(Char.IsDigit))throw new IOException("无效封面编号。");
                string dir=Path.Combine(data,"thumbnails");
                Directory.CreateDirectory(dir);
                string file=Path.Combine(dir,c.Id+".img");
                bool valid=IsKnownThumbnail(file);
                if(!valid&&File.Exists(file))
                {
                    try
                    {
                        await Task.Run(()=>ThumbnailDownloader.ValidateFile(file),cancellation).ConfigureAwait(false);
                        RememberThumbnail(file);
                        valid=true;
                    }
                    catch(InvalidDataException)
                    {
                        // Do not reuse legacy HTTP-success HTML/error bytes. Keep
                        // the invalid file until a validated replacement is ready.
                    }
                }
                if(!valid&&!String.IsNullOrEmpty(c.PreviewUrl))
                {
                    byte[] bytes=await ThumbnailDownloader.DownloadAsync(c.PreviewUrl,timeout,cancellation).ConfigureAwait(false);
                    if(!IsPreparationCurrent(batch,currentBatch))return;
                    temporary=Path.Combine(dir,c.Id+"-"+Guid.NewGuid().ToString("N")+".tmp");
                    File.WriteAllBytes(temporary,bytes);
                    await Task.Run(()=>ThumbnailDownloader.ValidateFile(temporary),cancellation).ConfigureAwait(false);
                    if(!IsPreparationCurrent(batch,currentBatch))return;
                    if(File.Exists(file))File.Replace(temporary,file,null);
                    else File.Move(temporary,file);
                    RememberThumbnail(file);
                    valid=true;
                }
                if(valid)
                {
                    lock(sync)thumbnailRetryAfter.Remove(retryKey);
                    if(!String.Equals(c.ThumbnailPath,file,StringComparison.OrdinalIgnoreCase))
                        Update(batch,c.Id,x=>x.ThumbnailPath=file);
                }
            }
            catch(OperationCanceledException)
            {
                throw;
            }
            catch(Exception ex)
            {
                lock(sync)
                {
                    if(thumbnailRetryAfter.Count>=64)thumbnailRetryAfter.Clear();
                    thumbnailRetryAfter[retryKey]=UtcNow.AddMinutes(5);
                }
                LogSafely("THUMBNAIL SKIPPED batch="+batch+" id="+c.Id+" "+Friendly(ex));
            }
            finally
            {
                if(temporary!=null)try
                {
                    if(File.Exists(temporary))File.Delete(temporary);
                }
                catch(IOException) { }
                catch(UnauthorizedAccessException) { }
            }
        }

        internal static Task<byte[]> DownloadThumbnail(string url,TimeSpan timeout)
        {
            return ThumbnailDownloader.DownloadAsync(url,timeout,CancellationToken.None);
        }
    }
}
