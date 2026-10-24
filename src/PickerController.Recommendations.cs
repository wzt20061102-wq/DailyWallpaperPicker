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
        async Task ImportSeeds()
        {
            lock(sync)if(state.SeedImported)return;
            string p=Path.Combine(Path.GetDirectoryName(config.WallpaperExe),"config.json");
            var configText=File.ReadAllText(p,Encoding.UTF8);
            var root=Json.Decode<Dictionary<string,object>>(configText);
            var owner=root.Values.OfType<Dictionary<string,object>>().FirstOrDefault(x=>x.ContainsKey("general"));
            var g=owner==null?null:owner["general"] as Dictionary<string,object>;
            string selected="";
            if(g!=null)
            {
                if(g.ContainsKey("wallpaperconfigrecent"))selected+=Json.Encode(g["wallpaperconfigrecent"]);
                if(g.ContainsKey("wallpaperconfig"))selected+=Json.Encode(g["wallpaperconfig"]);
            }
            string[] ids=Regex.Matches(selected,@"431960[/\\]+(\d+)").Cast<Match>().Select(x=>x.Groups[1].Value).Distinct().Take(20).ToArray();
            if(ids.Length>0)
            {
                var r=await callSteam("query",new
                {
                    ids=ids,pool="popular",limit=50
                },null).ConfigureAwait(false);
                lock(sync)
                {
                    foreach(var item in r.items??new List<Candidate>())foreach(string tag in RecommendationSelector.RelevantTags(item.Tags))
                    {
                        if(!state.SeedTags.ContainsKey(tag))state.SeedTags[tag]=0;
                        state.SeedTags[tag]+=1;
                    }
                }
            }
            lock(sync)
            {
                state.SeedImported=true;
                Save();
            }
            Log("Imported local wallpaper preference seed (no account data exported).");
        }
        internal static RecommendationPreferences ReadPreferences(string directory)
        {
            string path=Path.Combine(directory,"preferences.json");
            if(!File.Exists(path))return new RecommendationPreferences();
            if(new FileInfo(path).Length>64*1024)throw new IOException("偏好设置文件过大，请检查 preferences.json。");
            var preferences=Json.Read<RecommendationPreferences>(path);
            if(preferences==null)throw new IOException("偏好设置为空，请检查 preferences.json。");
            return preferences;
        }
        public static bool Eligible(Candidate c,long max)
        {
            return RecommendationSelector.Eligible(c,max);
        }
        internal static List<Candidate> RetainAllowedCandidates(IEnumerable<Candidate> previous,RecommendationPreferences preferences,long max)
        {
            return RecommendationSelector.RetainAllowedCandidates(previous,preferences,max);
        }
        internal Candidate ChooseRecommendation(List<Candidate> eligible,string pool,RecommendationFeedback feedback)
        {
            lock(sync)return RecommendationSelector.ChooseRecommendation(eligible,pool,feedback,state);
        }
        async Task PrepareBatch(string batch, RecommendationPreferences preferences, RecommendationFeedback feedback, bool currentBatch)
        {
            if (!IsPreparationCurrent(batch, currentBatch)) return;
            await ImportSeeds().ConfigureAwait(false);
            if (!IsPreparationCurrent(batch, currentBatch)) return;
            List<Candidate> items;
            lock (sync)
            {
                feedback = feedbackStore.Read();
                var existing = ForBatch(batch);
                items = existing == null ? null : existing.ToList();
            }
            if (items == null || items.Count != 3 || items.Any(c => !preferences.Allows(c) || !feedback.Allows(c)))
            {
                var retained = RetainAllowedCandidates(items == null ? null : items.Where(feedback.Allows), preferences, (long)config.MaxSizeMB * 1024 * 1024);
                items = new List<Candidate>();
                foreach (string pool in new[]
                {
                    "popular", "newest", "classic"
                })
                {
                    var keep = retained.FirstOrDefault(c => c.Pool == pool);
                    if (keep != null)
                    {
                        items.Add(keep);
                        continue;
                    }
                    var eligible = new List<Candidate>();
                    for (int page = 1; page <= 10 && eligible.Count == 0; page++)
                    {
                        var reply = await callSteam("query", new
                        {
                            pool = pool, limit = 50, page = page
                        }, null).ConfigureAwait(false);
                        if (!IsPreparationCurrent(batch, currentBatch)) return;
                        // A valid click can arrive during this query. Never commit a stale feedback snapshot.
                        lock (sync)
                        {
                            feedback = feedbackStore.Read();
                            string oldest = Schedule.Beijing(UtcNow).AddDays(-90).ToString("yyyy-MM-dd");
                            eligible = (reply.items ?? new List<Candidate>()).Where(c =>
                                Eligible(c, (long)config.MaxSizeMB * 1024 * 1024) &&
                                preferences.Allows(c) && feedback.Allows(c) &&
                                !items.Any(x => x.Id == c.Id) && !retained.Any(x => x.Id == c.Id) &&
                                !state.Seen.Any(h => h.Id == c.Id && String.CompareOrdinal(h.Date, oldest) >= 0) &&
                                !state.Choices.Any(h => h.Id == c.Id)).ToList();
                        }
                    }
                    if (eligible.Count == 0) throw new IOException("暂时没有足够的不重复候选，稍后会重试。");
                    Candidate pick = ChooseRecommendation(eligible, pool, feedback);
                    pick.Pool = pool;
                    pick.Status = "等待下载";
                    pick.Stage = DownloadStage.Pending;
                    pick.Total = pick.FileSize;
                    items.Add(pick);
                }
                lock (sync)
                {
                    if (!IsPreparationCurrent(batch, currentBatch)) return;
                    // Commit and feedback acceptance share sync, closing the last query-to-commit race.
                    var currentFeedback = feedbackStore.Read();
                    if (acting || items.Any(c => !currentFeedback.Allows(c)))
                    {
                        nextTry = UtcNow.AddSeconds(15);
                        return;
                    }
                    if (currentBatch)
                    {
                        state.Candidates = items;
                        if (state.PreparedBatchId == batch) state.PreparedCandidates = items;
                    }
                    else
                    {
                        state.PreparedBatchId = batch;
                        state.PreparedCandidates = items;
                    }
                    foreach (var candidate in items) if (!state.Seen.Any(x => x.Id == candidate.Id && x.Date == batch)) state.Seen.Add(new Shown
                    {
                        Id = candidate.Id, Date = batch
                    });
                    string oldest = Schedule.Beijing(UtcNow).AddDays(-90).ToString("yyyy-MM-dd");
                    state.Seen = state.Seen.Where(x => String.CompareOrdinal(x.Date, oldest) >= 0).ToList();
                    cleanupPending = true;
                    PromoteIfDue();
                    Save();
                }
                LogSafely("CANDIDATES prepared batch=" + batch + " retained=" + retained.Count + " ids=" + String.Join(",", items.Select(x => x.Id)));
                Notify();
            }
            await DownloadCandidatesAsync(items, batch, currentBatch).ConfigureAwait(false);
        }
    }
}
