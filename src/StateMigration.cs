// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.
using System;
using System.IO;
using System.Linq;

namespace DailyWallpaperPicker
{
    internal static class StateMigration
    {
        internal const int CurrentVersion = 1;
        internal static AppState Upgrade(AppState state)
        {
            if (state == null) throw new IOException("状态记录为空。");
            if (state.SchemaVersion < 0 || state.SchemaVersion > CurrentVersion)
                throw new IOException("数据来自不兼容的程序版本，请保留数据并使用对应版本。");
            if (state.SchemaVersion == 0)
            {
                // Only legacy records infer a stage from old text. UI code never does.
                foreach (var candidate in state.Candidates.Concat(state.PreparedCandidates))
                {
                    candidate.Stage = candidate.Ready ? DownloadStage.Ready :
                        !String.IsNullOrEmpty(candidate.Error) ? DownloadStage.Failed :
                        candidate.Status == "正在校验" || candidate.Status == "校验中" || candidate.Status == "正在校验文件" ? DownloadStage.Verifying :
                        candidate.Status == "等待下载" ? DownloadStage.Pending :
                        candidate.Total > 0 ? DownloadStage.Downloading : DownloadStage.Pending;
                }
                state.MessageSeverity = state.Candidates.Any(c => c.Stage == DownloadStage.Failed)
                    ? MessageSeverity.Error : MessageSeverity.Routine;
                state.SchemaVersion = CurrentVersion;
            }
            return state;
        }
    }
}
