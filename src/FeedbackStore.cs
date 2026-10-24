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
    /// <summary>Atomic, bounded local persistence for explicit negative feedback.</summary>
    internal sealed class FeedbackStore
    {
        readonly string path;
        internal FeedbackStore(string directory)
        {
            path = Path.Combine(directory, "feedback.json");
        }
        internal RecommendationFeedback Read()
        {
            if (!File.Exists(path)) return new RecommendationFeedback();
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new IOException("不喜欢记录文件过大，请检查 feedback.json。");
            var feedback = Json.Read<RecommendationFeedback>(path);
            if (feedback == null) throw new IOException("不喜欢记录损坏，请保留 feedback.json 并恢复备份。");
            feedback.Validate();
            return feedback;
        }
        internal void Record(Candidate candidate, DateTime utc)
        {
            var feedback = Read();
            if (feedback.Record(candidate, utc)) Json.Write(path, feedback);
        }
    }
}
