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
        public async Task PreviewAsync(string itemId)
        {
            Candidate c;
            string batch;
            int epoch;
            lock(sync)
            {
                cancellation.ThrowIfCancellationRequested();
                if(acting||disliking)throw new InvalidOperationException("正在处理上一张壁纸，请稍等。");
                if(state.BatchId!=Schedule.Due(UtcNow))throw new IOException("候选已更新，请重新选择。");
                c=state.Candidates.FirstOrDefault(x=>x.Id==itemId&&x.Ready);
                if(c==null)throw new IOException("这张壁纸尚未准备好，请稍后重试。");
                batch=state.BatchId;
                acting=true;
                activeCache=c.CachePath;
                epoch=previewEpoch;
            }
            Notify();
            try
            {
                await VerifyCandidate(c,batch).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                lock(sync)
                {
                    if(epoch!=previewEpoch)return;
                    if(batch!=state.BatchId||batch!=Schedule.Due(UtcNow))throw new IOException("候选已更新，请重新选择。");
                }
                await wallpaper.Preview(c.ProjectPath).ConfigureAwait(false);
                lock(sync)
                {
                    if(epoch!=previewEpoch||batch!=state.BatchId||batch!=Schedule.Due(UtcNow))wallpaper.HidePreview();
                    else
                    {
                        state.MessageSeverity=MessageSeverity.Routine;
                        state.Message="独立预览已打开；桌面保持不变。";
                        Save();
                    }
                }
            }
            catch(OperationCanceledException) { throw; }
            catch(Exception ex)
            {
                lock(sync)
                {
                    if(batch==state.BatchId)
                    {
                        state.MessageSeverity=MessageSeverity.Error;
                        state.Message="预览未打开："+Friendly(ex);
                        try
                        {
                            Save();
                        }
                        catch(Exception saveError)
                        {
                            LogSafely("STATE SAVE ERROR "+saveError.Message);
                        }
                    }
                }
                LogSafely("PREVIEW ERROR "+ex);
                throw;
            }
            finally
            {
                lock(sync)
                {
                    acting=false;
                    activeCache=null;
                }
                Notify();
            }
        }
        public async Task ApplyAsync(string itemId)
        {
            Candidate c;
            string batch;
            lock(sync)
            {
                cancellation.ThrowIfCancellationRequested();
                if(acting||disliking)throw new InvalidOperationException("正在处理上一张壁纸，请稍等。");
                if(state.BatchId!=Schedule.Due(UtcNow))throw new IOException("候选已更新，请重新选择。");
                c=state.Candidates.FirstOrDefault(x=>x.Id==itemId&&x.Ready);
                if(c==null)throw new IOException("这张壁纸尚未准备好，请稍后重试。");
                batch=state.BatchId;
                acting=true;
                activeCache=c.CachePath;
                state.MessageSeverity=MessageSeverity.Info;
                state.Message="正在切换，请稍等…";
            }
            Notify();
            try
            {
                await VerifyCandidate(c,batch).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                lock(sync)if(state.BatchId!=batch||batch!=Schedule.Due(UtcNow))throw new IOException("候选已更新，请重新选择。");
                wallpaper.HidePreview();
                await wallpaper.Apply(c.ProjectPath).ConfigureAwait(false);
            }
            catch(OperationCanceledException)
            {
                lock(sync) { acting=false; activeCache=null; }
                Notify();
                throw;
            }
            catch(Exception ex)
            {
                lock(sync)
                {
                    if(state.BatchId==batch)
                    {
                        state.MessageSeverity=MessageSeverity.Error;
                        state.Message="未能完成切换："+Friendly(ex);
                        try
                        {
                            Save();
                        }
                        catch(Exception saveError)
                        {
                            LogSafely("STATE SAVE ERROR "+saveError.Message);
                        }
                    }
                }
                LogSafely("APPLY ERROR "+ex);
                lock(sync)
                {
                    acting=false;
                    activeCache=null;
                }
                Notify();
                throw;
            }
            // The desktop switch has already been read back successfully. Later housekeeping
            // must never turn that confirmed outcome into an apparent failed switch.
            try
            {
                await RecordConfirmedChoiceAsync(c,batch,Cleanup).ConfigureAwait(false);
            }
            finally
            {
                lock(sync)
                {
                    acting=false;
                    activeCache=null;
                }
                Notify();
            }
        }
        internal async Task RecordConfirmedChoiceAsync(Candidate c,string batch,Func<Task> cleanup)
        {
            bool saveFailed=false;
            lock(sync)
            {
                state.Choices.RemoveAll(x=>x.Id==c.Id);
                state.Choices.Insert(0,new Choice
                {
                    Id=c.Id,Utc=UtcNow.ToString("o"),Tags=c.Tags,CachePath=c.CachePath
                });
                state.Choices=state.Choices.Take(1000).ToList();
                if(state.BatchId==batch)
                {
                    state.AppliedId=c.Id;
                    state.MessageSeverity=MessageSeverity.Success;
                    state.Message="已换上："+c.Title;
                }
                try
                {
                    Save();
                }
                catch(Exception ex)
                {
                    saveFailed=true;
                    if(state.BatchId==batch)
                    {
                        state.MessageSeverity=MessageSeverity.Error;
                        state.Message="已换上："+c.Title+"；选择记录暂未保存。";
                    }
                    LogSafely("CONFIRMED CHOICE SAVE ERROR "+ex);
                }
            }
            LogSafely("USER CHOICE "+c.Id);
            try
            {
                await cleanup().ConfigureAwait(false);
            }
            catch(OperationCanceledException)
            {
                if(!cancellation.IsCancellationRequested)throw;
            }
            catch(Exception ex)
            {
                DeferCleanup(ex);
                lock(sync)
                {
                    if(state.BatchId==batch)
                    {
                        state.MessageSeverity=MessageSeverity.Error;
                        state.Message="已换上："+c.Title+"；旧缓存暂未清理，将稍后重试。"+(saveFailed?"选择记录暂未保存。":"");
                        try
                        {
                            Save();
                        }
                        catch(Exception saveError)
                        {
                            LogSafely("STATE SAVE ERROR "+saveError.Message);
                        }
                    }
                }
            }
        }
        async Task VerifyCandidate(Candidate candidate,string batch)
        {
            try
            {
                await Task.Run(()=>CacheStore.Verify(candidate.CachePath)).ConfigureAwait(false);
            }
            catch(IOException ex)
            {
                Update(batch,candidate.Id,c=>
                {
                    c.Ready=false;
                    c.Stage=DownloadStage.Failed;
                    c.Status="缓存校验失败，请点重试";
                    c.Error=ex.Message;
                });
                throw;
            }
        }
        public void HidePreview()
        {
            lock(sync)previewEpoch++;
            wallpaper.HidePreview();
        }
        public void ShutdownPreview()
        {
            lock(sync)previewEpoch++;
            wallpaper.ShutdownPreview();
        }
        public async Task<string> CurrentAsync()
        {
            return await wallpaper.GetCurrent();
        }
    }
}
