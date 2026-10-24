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
    internal static class RecommendationSelector
    {
        internal static IEnumerable<string> RelevantTags(IEnumerable<string> tags)
        {
            return (tags??new string[0]).Where(t=>!new[]
            {
                "Everyone","Scene","Video","Approved","3840 x 2160","1920 x 1080","2560 x 1440"
            }.Contains(t,StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase);
        }
        static double AspectRank(Candidate c)
        {
            var screen=System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            double ratio=(double)screen.Width/screen.Height;
            if((c.Tags??new string[0]).Contains("Dynamic resolution",StringComparer.OrdinalIgnoreCase))return 1;
            foreach(string t in c.Tags??new string[0])
            {
                Match m=Regex.Match(t,@"^(\d+)\s*x\s*(\d+)$",RegexOptions.IgnoreCase);
                if(m.Success)
                {
                    double w=Double.Parse(m.Groups[1].Value),h=Double.Parse(m.Groups[2].Value);
                    if(h>0)return Math.Abs(w/h-ratio)<.06?1:0;
                }
            }
            return .5;
        }
        static double Preference(Candidate c,AppState state)
        {
            double score=0;
            foreach(string t in RelevantTags(c.Tags))
            {
                double s;
                if(state.SeedTags.TryGetValue(t,out s))score+=s;
                foreach(var choice in state.Choices.Take(30))if((choice.Tags??new string[0]).Contains(t,StringComparer.OrdinalIgnoreCase))score+=3;
            }
            return score;
        }
        public static bool Eligible(Candidate c,long max)
        {
            return c!=null&&!String.IsNullOrEmpty(c.Id)&&c.Id.All(Char.IsDigit)&&c.FileSize>0&&c.FileSize<=max&&(c.Tags??new string[0]).Contains("Everyone",StringComparer.OrdinalIgnoreCase)&&(c.Tags.Contains("Scene",StringComparer.OrdinalIgnoreCase)||c.Tags.Contains("Video",StringComparer.OrdinalIgnoreCase))&&!c.Tags.Any(x=>new[]
            {
                "Mature","Questionable","Application","Web","Preset"
            }.Contains(x,StringComparer.OrdinalIgnoreCase));
        }
        internal static List<Candidate> RetainAllowedCandidates(IEnumerable<Candidate> previous,RecommendationPreferences preferences,long max)
        {
            var retained=new List<Candidate>();
            foreach(string pool in new[]
            {
                "popular","newest","classic"
            })
            {
                var item=(previous??Enumerable.Empty<Candidate>()).FirstOrDefault(c=>Eligible(c,max)&&c.Pool==pool&&preferences.Allows(c)&&!retained.Any(x=>x.Id==c.Id));
                if(item!=null)retained.Add(item);
            }
            return retained;
        }
        internal static Candidate ChooseRecommendation(List<Candidate> eligible,string pool,RecommendationFeedback feedback,AppState state)
        {
            if(pool=="newest")return eligible.OrderByDescending(AspectRank).ThenBy(feedback.Penalty).ThenByDescending(x=>x.CreatedUtc).First();
            return eligible.Select((x,i)=>new
            {
                Item=x,Rank=i,Weight=Preference(x,state)-4*feedback.Penalty(x),Aspect=AspectRank(x)
            }).OrderByDescending(x=>x.Aspect).ThenByDescending(x=>x.Weight).ThenBy(x=>x.Rank).First().Item;
        }
    }
}
