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
        readonly HashSet<HoverPreviewLease> hoverLeases=new HashSet<HoverPreviewLease>();
        // These parsed entries are process-local; disk metadata must match before every reuse.
        sealed class HoverVerifiedEntry
        {
            public string Fingerprint;
            public string Project;
            public string Media;
            public bool Video;
        }
        readonly Dictionary<string,HoverVerifiedEntry> hoverVerified=new Dictionary<string,HoverVerifiedEntry>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,Task> hoverWarmJobs=new Dictionary<string,Task>(StringComparer.OrdinalIgnoreCase);
        public async Task<HoverPreviewLease> AcquireHoverPreviewAsync(string itemId)
        {
            // Hold the same gate as deletion, quarantine and import while hashing. Once verified,
            // register a lease before releasing the gate so there is no unprotected hand-off.
            var elapsed=Stopwatch.StartNew();
            await cleanupGate.WaitAsync(cancellation).ConfigureAwait(false);
            long gateWaitMs=elapsed.ElapsedMilliseconds;
            try
            {
                Candidate candidate;
                string batch;
                lock(sync)
                {
                    if(acting||disliking)throw new InvalidOperationException("正在处理壁纸，请稍后预览。");
                    if(state.BatchId!=Schedule.Due(UtcNow))throw new IOException("候选已更新，请重新选择。");
                    var current=state.Candidates.FirstOrDefault(x=>x.Id==itemId&&x.Ready);
                    if(current==null)throw new IOException("这张壁纸尚未准备好。");
                    candidate=Json.Decode<Candidate>(Json.Encode(current));
                    batch=state.BatchId;
                }
                string directory=OwnedCachePath(candidate.Id,candidate.CachePath);
                if(!SamePath(candidate.ProjectPath,Path.Combine(directory,"project.json")))throw new IOException("壁纸项目文件与缓存不一致。");
                var lease=await Task.Run(()=>
                {
                    var verifyTimer=Stopwatch.StartNew();
                    string before=HoverPreviewVerifier.Fingerprint(directory);
                    HoverVerifiedEntry entry;
                    bool cacheHit=hoverVerified.TryGetValue(directory,out entry)&&entry.Fingerprint==before;
                    if(!cacheHit)
                    {
                        hoverVerified.Remove(directory);
                        string project=CacheStore.Verify(directory);
                        var document=Json.Read<Dictionary<string,object>>(project);
                        object typeValue,fileValue;
                        if(!document.TryGetValue("type",out typeValue)||!(typeValue is string))throw new IOException("壁纸类型无效。");
                        string type=((string)typeValue).ToLowerInvariant();
                        if(type!="video"&&type!="scene")throw new IOException("仅支持 Scene 或 Video 壁纸。");
                        if(!document.TryGetValue("file",out fileValue)||!(fileValue is string)||String.IsNullOrWhiteSpace((string)fileValue))throw new IOException("壁纸缺少主文件。");
                        string relative=(string)fileValue;
                        if(Path.IsPathRooted(relative)||relative.IndexOf(':')>=0)throw new IOException("壁纸主文件必须是缓存内的相对路径。");
                        string media=Path.GetFullPath(Path.Combine(directory,relative));
                        if(!CacheStore.Under(media,directory))throw new IOException("壁纸主文件路径越界。");
                        if(!File.Exists(media)&&type=="scene")media=Path.Combine(directory,"scene.pkg");
                        if(!File.Exists(media)||new FileInfo(media).Length==0)throw new IOException("壁纸主文件缺失或为空。");
                        var manifest=Json.Read<Dictionary<string,string>>(Path.Combine(directory,".verified.json"));
                        if(!manifest.Keys.Any(x=>SamePath(Path.Combine(directory,x),media)))throw new IOException("壁纸主文件没有经过副本校验。");
                        entry=new HoverVerifiedEntry
                        {
                            Project=project,Media=media,Video=type=="video",Fingerprint=before
                        };
                    }
                    string after=HoverPreviewVerifier.Fingerprint(directory);
                    if(before!=after)
                    {
                        hoverVerified.Remove(directory);
                        throw new IOException("校验期间壁纸文件发生变化，请稍后重试。");
                    }
                    hoverVerified[directory]=entry;
                    LogHover("verified id="+itemId+" cacheHit="+(cacheHit?"true":"false")+" verifyMs="+verifyTimer.ElapsedMilliseconds);
                    return new HoverPreviewLease(candidate.Id,batch,entry.Project,entry.Media,entry.Video,ReleaseHoverPreview);
                }).ConfigureAwait(false);
                lock(sync)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if(acting||disliking||!HoverCandidateMatches(lease))throw new IOException("候选已更新，本次悬停已取消。");
                    hoverLeases.Add(lease);
                }
                LogHover("lease-acquired batch="+batch+" id="+itemId+" type="+(lease.IsVideo?"video":"scene")+" gateWaitMs="+gateWaitMs+" totalMs="+elapsed.ElapsedMilliseconds);
                return lease;
            }
            catch(Exception ex)
            {
                LogHover("lease-failed id="+itemId+" gateWaitMs="+gateWaitMs+" totalMs="+elapsed.ElapsedMilliseconds+" "+ex.Message);
                if(ex is ArgumentException||ex is NotSupportedException)throw new IOException("壁纸内容或文件路径格式无效。",ex);
                throw;
            }
            finally
            {
                cleanupGate.Release();
            }
        }
        public Task WarmHoverPreviewsAsync()
        {
            var jobs=new List<Task>();
            lock(sync)
            {
                string batch=state.BatchId;
                if(acting||disliking||batch!=Schedule.Due(UtcNow))return Task.FromResult(0);
                foreach(string key in hoverWarmJobs.Keys.Where(x=>!x.StartsWith(batch+"|",StringComparison.Ordinal)).ToArray())hoverWarmJobs.Remove(key);
                foreach(var candidate in state.Candidates.Where(x=>x.Ready&&(x.Tags??new string[0]).Contains("Video",StringComparer.OrdinalIgnoreCase)&&!(x.Tags??new string[0]).Contains("Scene",StringComparer.OrdinalIgnoreCase)))
                {
                    string id=candidate.Id,key=batch+"|"+id+"|"+candidate.ProjectPath;
                    Task job;
                    if(!hoverWarmJobs.TryGetValue(key,out job))
                    {
                        job=Task.Run(async()=>
                        {
                            try
                            {
                                lock(sync)
                                {
                                    if(state.BatchId!=batch||batch!=Schedule.Due(UtcNow)||acting)
                                    {
                                        hoverWarmJobs.Remove(key);
                                        return;
                                    }
                                }
                                using(var lease=await AcquireHoverPreviewAsync(id).ConfigureAwait(false))
                                {
                                    LogHover("warm-ready batch="+batch+" id="+id);
                                }
                            }
                            catch(Exception ex)
                            {
                                lock(sync)hoverWarmJobs.Remove(key);
                                LogHover("warm-failed batch="+batch+" id="+id+" "+ex.Message);
                            }
                        });
                        hoverWarmJobs.Add(key,job);
                    }
                    jobs.Add(job);
                }
            }
            return Task.WhenAll(jobs);
        }
        bool HoverCandidateMatches(HoverPreviewLease lease)
        {
            return lease.BatchId==state.BatchId&&lease.BatchId==Schedule.Due(UtcNow)&&state.Candidates.Any(x=>x.Id==lease.ItemId&&x.Ready&&SamePath(x.ProjectPath,lease.ProjectPath)&&SamePath(x.CachePath,lease.CachePath));
        }
        public bool IsHoverPreviewCurrent(HoverPreviewLease lease)
        {
            lock(sync)return lease!=null&&!lease.IsDisposed&&!acting&&!disliking&&hoverLeases.Contains(lease)&&HoverCandidateMatches(lease);
        }
        void ReleaseHoverPreview(HoverPreviewLease lease)
        {
            lock(sync)
            {
                if(!hoverLeases.Remove(lease))return;
                if(!HoverCandidateMatches(lease))
                {
                    cleanupPending=true;
                    nextCleanupTry=UtcNow;
                }
            }
            LogHover("lease-released batch="+lease.BatchId+" id="+lease.ItemId);
        }
    }
}
