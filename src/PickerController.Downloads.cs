// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Net;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;
namespace DailyWallpaperPicker
{
    public partial class PickerController
    {
        async Task DownloadCandidatesAsync(List<Candidate> items,string batch,bool currentBatch)
        {
            foreach(var c in items)
            {
                if(!IsPreparationCurrent(batch,currentBatch))return;
                lock(sync)
                {
                    if(!feedbackStore.Read().Allows(c))continue;
                }
                if(c.Ready&&File.Exists(c.ProjectPath))continue;
                try
                {
                    await Thumbnail(c,batch,currentBatch).ConfigureAwait(false);
                    if(!IsPreparationCurrent(batch,currentBatch))return;
                    string cached=Path.Combine(cache,c.Id);
                    string project;
                    project=null;
                    bool corrupt=false;
                    if(Directory.Exists(cached)&&File.Exists(Path.Combine(cached,".verified.json")))try
                    {
                        project=await Task.Run(()=>CacheStore.Verify(cached)).ConfigureAwait(false);
                    }
                    catch(IOException)
                    {
                        corrupt=true;
                    }
                    if(corrupt)
                    {
                        string current=await wallpaper.GetCurrent().ConfigureAwait(false);
                        if(!IsPreparationCurrent(batch,currentBatch))return;
                        await QuarantineCorruptCacheAsync(cached,c.Id,current).ConfigureAwait(false);
                    }
                    if(project==null)
                    {
                        Update(batch,c.Id,x=>
                        {
                            x.Ready=false;
                            x.Stage=DownloadStage.Downloading;
                            x.Status="正在下载完整壁纸";
                            x.Error=null;
                        });
                        var reply=await callSteam("download",new
                        {
                            id=c.Id
                        },p=>Update(batch,c.Id,x=>
                        {
                            x.Downloaded=p.downloaded;
                            x.Total=p.total;
                            x.Stage=DownloadStage.Downloading;
                            x.Status="正在下载完整壁纸";
                        })).ConfigureAwait(false);
                        ValidateDownloadReply(reply,c.Id);
                        if(!IsPreparationCurrent(batch,currentBatch))return;
                        Update(batch,c.Id,x=>
                        {
                            x.Stage=DownloadStage.Verifying;
                            x.Status="正在校验文件";
                        });
                        project=await ImportCacheAsync(reply.installPath,c.Id).ConfigureAwait(false);
                    }
                    if(!IsPreparationCurrent(batch,currentBatch))return;
                    Update(batch,c.Id,x=>
                    {
                        x.ProjectPath=project;
                        x.CachePath=Path.GetDirectoryName(project);
                        x.Ready=true;
                        x.Stage=DownloadStage.Ready;
                        x.Status="已就绪";
                        x.Error=null;
                        x.Downloaded=x.Total;
                    });
                    LogSafely("READY batch="+batch+" id="+c.Id+" "+project);
                }
                catch(OperationCanceledException)
                {
                    throw;
                }
                catch(Exception ex)
                {
                    Update(batch,c.Id,x=>
                    {
                        x.Ready=false;
                        x.Stage=DownloadStage.Failed;
                        x.Status="暂未下载完成";
                        x.Error=Friendly(ex);
                    });
                    LogSafely("CANDIDATE ERROR "+c.Id+" "+ex.Message);
                }
            }
            if(!IsPreparationCurrent(batch,currentBatch))return;
            lock(sync)
            {
                PromoteIfDue();
                if(state.BatchId==batch)
                {
                    state.MessageSeverity=state.Candidates.All(x=>x.Ready)?MessageSeverity.Routine:MessageSeverity.Error;
                    state.Message=state.Candidates.All(x=>x.Ready)?"三张都已准备好。选一张喜欢的，或保持现在。":"部分壁纸仍待下载，稍后会重试。";
                }
                Save();
            }
            if(items.All(x=>x.Ready))
            {
                try
                {
                    await Cleanup().ConfigureAwait(false);
                }
                catch(Exception ex)
                {
                    DeferCleanup(ex);
                }
            }
            else nextTry=UtcNow.AddMinutes(5);
            Notify();
        }
        internal static void ValidateDownloadReply(WorkerReply reply,string expectedId)
        {
            if(reply==null||!reply.ok||reply.id!=expectedId||!reply.appId.HasValue||reply.appId.Value!=431960)throw new IOException("Steam 下载结果与本次壁纸不一致，已停止。");
            if(!reply.subscribedBefore.HasValue||!reply.subscribedAfter.HasValue)throw new IOException("Steam 未返回完整订阅校验结果，已停止。");
            if(reply.subscribedBefore.Value!=reply.subscribedAfter.Value)throw new IOException("Steam 订阅状态发生异常变化，已停止。");
            if(String.IsNullOrWhiteSpace(reply.installPath))throw new IOException("Steam 未返回完整壁纸位置，已停止。");
        }
        Task Thumbnail(Candidate c,string batch,bool currentBatch)
        {
            return Thumbnail(c,batch,TimeSpan.FromSeconds(15),currentBatch);
        }
        // A cover is optional: failure must not stop the full wallpaper download or
        // leave the daily preparation job waiting indefinitely on an image server.
        internal async Task Thumbnail(Candidate c,string batch,TimeSpan timeout,bool currentBatch=false)
        {
            string temporary=null;
            try
            {
                if(String.IsNullOrWhiteSpace(c.Id)||!c.Id.All(Char.IsDigit))throw new IOException("无效封面编号。");
                string dir=Path.Combine(data,"thumbnails");
                Directory.CreateDirectory(dir);
                string file=Path.Combine(dir,c.Id+".img");
                if(!File.Exists(file)&&!String.IsNullOrEmpty(c.PreviewUrl))
                {
                    byte[] bytes=await ThumbnailDownloader.DownloadAsync(c.PreviewUrl,timeout,cancellation).ConfigureAwait(false);
                    if(!IsPreparationCurrent(batch,currentBatch))return;
                    temporary=Path.Combine(dir,c.Id+"-"+Guid.NewGuid().ToString("N")+".tmp");
                    File.WriteAllBytes(temporary,bytes);
                    if(!File.Exists(file))File.Move(temporary,file);
                }
                if(File.Exists(file))Update(batch,c.Id,x=>x.ThumbnailPath=file);
            }
            catch(OperationCanceledException)
            {
                throw;
            }
            catch(Exception ex)
            {
                LogSafely("THUMBNAIL SKIPPED batch="+batch+" id="+c.Id+" "+Friendly(ex));
            }
            finally
            {
                if(temporary!=null)try
                {
                    if(File.Exists(temporary))File.Delete(temporary);
                }
                catch(IOException)
                {
                }
                catch(UnauthorizedAccessException)
                {
                }
            }
        }
        internal static Task<byte[]> DownloadThumbnail(string url,TimeSpan timeout)
        {
            return ThumbnailDownloader.DownloadAsync(url,timeout,CancellationToken.None);
        }
    }
}
