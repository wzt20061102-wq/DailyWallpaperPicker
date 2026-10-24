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
        // Feedback acceptance is independent of the long-running query/download queue.
        // Holding sync through the small atomic JSON write orders it before any candidate commit.
        public async Task DislikeAsync(string itemId)
        {
            string batch;
            bool recorded = false;
            bool gateAcquired = false;
            Candidate candidate;
            lock (sync)
            {
                cancellation.ThrowIfCancellationRequested();
                if (acting || disliking) throw new InvalidOperationException("正在处理上一张壁纸，请稍等。");
                batch = state.BatchId;
                var current = state.Candidates.FirstOrDefault(c => c.Id == itemId);
                if (batch != Schedule.Due(UtcNow) || current == null) throw new IOException("候选已更新，请重新选择。");
                candidate = Json.Decode<Candidate>(Json.Encode(current));
                disliking = true;
            }
            try
            {
                lock (sync)
                {
                    feedbackStore.Record(candidate, UtcNow);
                    recorded = true;
                    if (batch == state.BatchId && batch == Schedule.Due(UtcNow))
                    {
                        state.MessageSeverity = MessageSeverity.Info;
                        state.Message = "已记住不喜欢，正在换一张…";
                        Save();
                    }
                }
                LogSafely("DISLIKE batch=" + batch + " id=" + itemId);
                Notify();
                await recommendationGate.WaitAsync(cancellation).ConfigureAwait(false);
                gateAcquired = true;
                // The click remains recorded even when the displayed day changed while waiting.
                lock (sync)
                {
                    if (batch != state.BatchId || batch != Schedule.Due(UtcNow)) return;
                }
                await PrepareBatch(batch, ReadPreferences(data), feedbackStore.Read(), true).ConfigureAwait(false);
                lock (sync)
                {
                    if (batch == state.BatchId && batch == Schedule.Due(UtcNow))
                    {
                        state.MessageSeverity = state.Candidates.All(c => c.Ready) ? MessageSeverity.Success : MessageSeverity.Info;
                        state.Message = state.Candidates.All(c => c.Ready) ? "已换一张，今后会减少相似题材的推荐。" : "已记住不喜欢，新候选仍在准备；桌面保持不变。";
                        Save();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                string message = (recorded ? "已记住不喜欢，但换一张暂未完成：" : "未能记录不喜欢：") + Friendly(ex);
                lock (sync)
                {
                    if (batch == state.BatchId && batch == Schedule.Due(UtcNow))
                    {
                        state.MessageSeverity = MessageSeverity.Error;
                        state.Message = message;
                        try
                        {
                            Save();
                        }
                        catch (Exception saveError)
                        {
                            LogSafely("STATE SAVE ERROR " + saveError.Message);
                        }
                    }
                }
                LogSafely("DISLIKE ERROR id=" + itemId + " recorded=" + recorded + " " + ex.Message);
                throw new IOException(message, ex);
            }
            finally
            {
                lock (sync) disliking = false;
                if (gateAcquired) recommendationGate.Release();
                Notify();
            }
        }
        // Internal coordination hook keeps contention tests independent of private field names.
        internal async Task<IDisposable> HoldRecommendationQueueAsync()
        {
            await recommendationGate.WaitAsync(cancellation).ConfigureAwait(false);
            return new QueueLease(recommendationGate);
        }
        sealed class QueueLease : IDisposable
        {
            SemaphoreSlim gate;
            internal QueueLease(SemaphoreSlim gate)
            {
                this.gate = gate;
            }
            public void Dispose()
            {
                var current = Interlocked.Exchange(ref gate, null);
                if (current != null) current.Release();
            }
        }
        internal static RecommendationFeedback ReadFeedback(string directory)
        {
            return new FeedbackStore(directory).Read();
        }
    }
}
