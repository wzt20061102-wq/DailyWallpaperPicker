// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.
using System;
using System.IO;
using System.Threading.Tasks;

namespace DailyWallpaperPicker
{
    internal sealed class SteamPreparationWaitingException : IOException
    {
        internal readonly TimeSpan RetryAfter;
        internal SteamPreparationWaitingException(SteamReadiness readiness)
            :base(readiness.Message ?? "正在等待 Steam 登录并联网，恢复后会自动准备。")
        {
            RetryAfter=readiness.RetryAfter<TimeSpan.FromSeconds(15)?TimeSpan.FromSeconds(15):readiness.RetryAfter;
        }
    }
    internal sealed class PreparationSupersededException : Exception { }

    public partial class PickerController
    {
        // Gate actual network work, not cache verification or already-ready wallpaper use.
        // Keeping this lazy also permits local recovery after a copied cache outlives a failed save.
        async Task<WorkerReply> CallPreparationSteamAsync(string batch,bool currentBatch,string verb,object input,Action<WorkerProgress> progress)
        {
            if(!IsPreparationCurrent(batch,currentBatch))throw new PreparationSupersededException();
            string launchBatch=Schedule.Prepare(UtcNow);
            var readiness=await checkSteamReadiness(launchBatch).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if(!IsPreparationCurrent(batch,currentBatch))throw new PreparationSupersededException();
            if(readiness==null)throw new IOException("Steam 状态检查没有返回结果。");
            if(!readiness.Online)throw new SteamPreparationWaitingException(readiness);
            return await callSteam(verb,input,progress).ConfigureAwait(false);
        }
    }
}
