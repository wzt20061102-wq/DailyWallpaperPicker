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
        public void SetPaused(bool value)
        {
            lock(sync)
            {
                state.Paused=value;
                state.MessageSeverity=MessageSeverity.Info;
                state.Message=value?"每日推荐已暂停；当前壁纸不会改变。":"每天 23:00 提醒，22:50 提前准备。";
                Save();
            }
            Notify();
        }
        public bool ShouldPresent()
        {
            lock(sync)
            {
                if(state.Paused)return false;
                PromoteIfDue();
                string due=Schedule.Due(UtcNow);
                if(state.BatchId!=due||state.PresentedBatch==due)return false;
                state.PresentedBatch=due;
                Save();
                return true;
            }
        }
        public async Task TickAsync()
        {
            cancellation.ThrowIfCancellationRequested();
            await RetryCleanupIfDue().ConfigureAwait(false);
            if(!await recommendationGate.WaitAsync(0,cancellation).ConfigureAwait(false))return;
            string target=Schedule.Prepare(UtcNow);
            bool started=false;
            try
            {
                lock(sync)
                {
                    if(state.Paused)return;
                    PromoteIfDue();
                    if(preparing||acting||disliking||UtcNow<nextTry)return;
                    preparing=true;
                    started=true;
                }
                nextTry=DateTime.MinValue;
                var preferences=ReadPreferences(data);
                var feedback=ReadFeedback(data);
                string due=Schedule.Due(UtcNow);
                bool repairCurrent;
                lock(sync)repairCurrent=due!=target&&state.BatchId==due&&(state.Candidates.Count!=3||state.Candidates.Any(c=>!c.Ready||!File.Exists(c.ProjectPath)||!preferences.Allows(c)||!feedback.Allows(c)));
                if(repairCurrent)await PrepareBatch(due,preferences,feedback,true).ConfigureAwait(false);
                lock(sync)
                {
                    var existing=ForBatch(target);
                    if(existing!=null&&existing.Count==3&&existing.All(x=>x.Ready&&File.Exists(x.ProjectPath)&&preferences.Allows(x)&&feedback.Allows(x)))return;
                }
                await PrepareBatch(target,preferences,feedback,false).ConfigureAwait(false);
            }
            catch(OperationCanceledException)
            {
                if(!cancellation.IsCancellationRequested)throw;
            }
            catch(Exception ex)
            {
                LogSafely("PREPARE ERROR "+ex);
                lock(sync)
                {
                    if(target==Schedule.Prepare(UtcNow)&&!state.Paused)
                    {
                        state.MessageSeverity=MessageSeverity.Error;
                        state.Message="准备暂未完成："+Friendly(ex)+"；当前壁纸保持不变。";
                        Save();
                    }
                }
                nextTry=UtcNow.AddMinutes(5);
                Notify();
            }
            finally
            {
                try
                {
                    if(started)
                    {
                        lock(sync)
                        {
                            preparing=false;
                            if(!cancellation.IsCancellationRequested)PromoteIfDue();
                        }
                        Notify();
                    }
                }
                finally
                {
                    recommendationGate.Release();
                }
            }
        }
        public async Task RetryAsync()
        {
            nextTry=DateTime.MinValue;
            await TickAsync();
        }
        void PromoteIfDue()
        {
            string due=Schedule.Due(UtcNow);
            bool changed=false;
            if(state.BatchId!=due)
            {
                state.BatchId=due;
                state.Candidates=state.PreparedBatchId==due?state.PreparedCandidates:new List<Candidate>();
                state.AppliedId=null;
                state.MessageSeverity=MessageSeverity.Routine;
                state.Message=state.Candidates.Count==3?"选一张喜欢的，或暂时不选。":"正在准备三张候选，当前壁纸保持不变。";
                changed=true;
            }
            else if(state.PreparedBatchId==due&&!Object.ReferenceEquals(state.Candidates,state.PreparedCandidates))
            {
                state.Candidates=state.PreparedCandidates;
                changed=true;
            }
            if(state.PreparedBatchId!=null&&String.CompareOrdinal(state.PreparedBatchId,Schedule.Prepare(UtcNow))<0&&state.PreparedBatchId!=due)
            {
                state.PreparedBatchId=null;
                state.PreparedCandidates=new List<Candidate>();
                changed=true;
            }
            if(changed)
            {
                cleanupPending=true;
                nextCleanupTry=UtcNow;
                Save();
            }
        }
        List<Candidate> ForBatch(string id)
        {
            if(state.BatchId==id)return state.Candidates;
            if(state.PreparedBatchId==id)return state.PreparedCandidates;
            return null;
        }
        bool IsPreparationCurrent(string batch,bool currentBatch=false)
        {
            cancellation.ThrowIfCancellationRequested();
            lock(sync)return currentBatch?batch==state.BatchId&&batch==Schedule.Due(UtcNow):!state.Paused&&batch==Schedule.Prepare(UtcNow);
        }
        void Update(string batch,string id,Action<Candidate> change)
        {
            lock(sync)
            {
                if(cancellation.IsCancellationRequested)return;
                if(batch!=Schedule.Due(UtcNow)&&batch!=Schedule.Prepare(UtcNow))return;
                var list=ForBatch(batch);
                if(list==null)return;
                var c=list.Find(x=>x.Id==id);
                if(c==null)return;
                change(c);
                Save();
            }
            Notify();
        }
    }
}
