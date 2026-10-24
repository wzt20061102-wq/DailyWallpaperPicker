// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
namespace DailyWallpaperPicker
{
    public enum MessageSeverity
    {
        Routine, Info, Success, Error
    }
    public enum DownloadStage
    {
        Pending, Downloading, Verifying, Ready, Failed
    }
    public class Candidate
    {
        public string Id
        {
            get;
            set;
        }
        public string Title
        {
            get;
            set;
        }
        public string Pool
        {
            get;
            set;
        }
        public string[] Tags
        {
            get;
            set;
        }
        public string PreviewUrl
        {
            get;
            set;
        }
        public long FileSize
        {
            get;
            set;
        }
        public double Score
        {
            get;
            set;
        }
        public long CreatedUtc
        {
            get;
            set;
        }
        public long UpdatedUtc
        {
            get;
            set;
        }
        public string ThumbnailPath
        {
            get;
            set;
        }
        public string Status
        {
            get;
            set;
        }
        public string Error
        {
            get;
            set;
        }
        public DownloadStage Stage
        {
            get;
            set;
        }
        public long Downloaded
        {
            get;
            set;
        }
        public long Total
        {
            get;
            set;
        }
        public bool Ready
        {
            get;
            set;
        }
        public string ProjectPath
        {
            get;
            set;
        }
        public string CachePath
        {
            get;
            set;
        }
        public Candidate()
        {
            Tags=new string[0];
            Status="等待下载";
        }
    }
    public class Choice
    {
        public string Id
        {
            get;
            set;
        }
        public string Utc
        {
            get;
            set;
        }
        public string[] Tags
        {
            get;
            set;
        }
        public string CachePath
        {
            get;
            set;
        }
    }
    public class Shown
    {
        public string Id
        {
            get;
            set;
        }
        public string Date
        {
            get;
            set;
        }
    }
    public class AppState
    {
        public int SchemaVersion
        {
            get;
            set;
        }
        public MessageSeverity MessageSeverity
        {
            get;
            set;
        }
        public string BatchId
        {
            get;
            set;
        }
        public string Message
        {
            get;
            set;
        }
        public bool Paused
        {
            get;
            set;
        }
        public List<Candidate> Candidates
        {
            get;
            set;
        }
        public string AppliedId
        {
            get;
            set;
        }
        public string PresentedBatch
        {
            get;
            set;
        }
        public List<Choice> Choices
        {
            get;
            set;
        }
        public List<Shown> Seen
        {
            get;
            set;
        }
        public Dictionary<string,double> SeedTags
        {
            get;
            set;
        }
        public bool SeedImported
        {
            get;
            set;
        }
        public string PreparedBatchId
        {
            get;
            set;
        }
        public List<Candidate> PreparedCandidates
        {
            get;
            set;
        }
        public AppState()
        {
            Candidates=new List<Candidate>();
            PreparedCandidates=new List<Candidate>();
            Choices=new List<Choice>();
            Seen=new List<Shown>();
            SeedTags=new Dictionary<string,double>();
            Message="每天 23:00，为你准备三张新壁纸";
        }
    }
    public class PickerConfig
    {
        public string SteamBin
        {
            get;
            set;
        }
        public string WallpaperExe
        {
            get;
            set;
        }
        public int MaxSizeMB
        {
            get;
            set;
        }
        public int RetainCount
        {
            get;
            set;
        }
        public PickerConfig()
        {
            MaxSizeMB=300;
            RetainCount=10;
        }
    }
    public class RecommendationPreferences
    {
        public string[] ExcludedIds
        {
            get;
            set;
        }
        public string[] ExcludedTags
        {
            get;
            set;
        }
        public string[] ExcludedTitleKeywords
        {
            get;
            set;
        }
        public RecommendationPreferences()
        {
            ExcludedIds=new string[0];
            ExcludedTags=new string[0];
            ExcludedTitleKeywords=new string[0];
        }
        public bool Allows(Candidate candidate)
        {
            if(candidate==null)return false;
            foreach(string value in ExcludedIds??new string[0])if(!String.IsNullOrWhiteSpace(value)&&String.Equals(candidate.Id,value.Trim(),StringComparison.Ordinal))return false;
            foreach(string value in ExcludedTags??new string[0])
            {
                if(String.IsNullOrWhiteSpace(value))continue;
                foreach(string tag in candidate.Tags??new string[0])if(tag!=null&&String.Equals(tag.Trim(),value.Trim(),StringComparison.OrdinalIgnoreCase))return false;
            }
            string title=NormalizeTitle(candidate.Title);
            foreach(string value in ExcludedTitleKeywords??new string[0])
            {
                string keyword=NormalizeTitle(value);
                if(keyword.Length==0)continue;
                // CJK phrases need substring matching; Latin words must not match inside
                // unrelated names such as "Pinkerton". Punctuation in a rule is literal.
                if(Regex.IsMatch(keyword,@"[\u3400-\u9fff\uf900-\ufaff\u3040-\u30ff\uac00-\ud7af]"))
                {
                    if(title.IndexOf(keyword,StringComparison.OrdinalIgnoreCase)>=0)return false;
                }
                else if(Regex.IsMatch(title,@"(?<![\p{L}\p{M}\p{N}])"+Regex.Escape(keyword)+@"(?![\p{L}\p{M}\p{N}])",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))return false;
            }
            return true;
        }
        static string NormalizeTitle(string value)
        {
            return Regex.Replace(value??"",@"[\s_\p{Pd}]+"," ").Trim();
        }
    }
    public sealed class DislikeRecord
    {
        public string Id
        {
            get;
            set;
        }
        public string Utc
        {
            get;
            set;
        }
        public string[] Tags
        {
            get;
            set;
        }
        public DislikeRecord()
        {
            Tags=new string[0];
        }
    }
    public sealed class RecommendationFeedback
    {
        public List<DislikeRecord> Dislikes
        {
            get;
            set;
        }
        public RecommendationFeedback()
        {
            Dislikes=new List<DislikeRecord>();
        }
        public bool Record(Candidate candidate,DateTime utc)
        {
            string id=candidate==null?null:NormalizedId(candidate.Id);
            if(id==null)throw new ArgumentException("不喜欢记录需要有效的壁纸编号。","candidate");
            if(Dislikes==null)Dislikes=new List<DislikeRecord>();
            Validate();
            foreach(var record in Dislikes)if(record.Id==id)return false;
            DateTime when=utc.Kind==DateTimeKind.Unspecified?DateTime.SpecifyKind(utc,DateTimeKind.Utc):utc.ToUniversalTime();
            var tags=new List<string>(TopicTags(candidate.Tags));
            tags.Sort(StringComparer.OrdinalIgnoreCase);
            Dislikes.Add(new DislikeRecord
            {
                Id=id,Utc=when.ToString("o",CultureInfo.InvariantCulture),Tags=tags.ToArray()
            });
            return true;
        }
        public bool Allows(Candidate candidate)
        {
            string id=candidate==null?null:NormalizedId(candidate.Id);
            if(id==null)return false;
            foreach(var record in Dislikes??new List<DislikeRecord>())if(record!=null&&NormalizedId(record.Id)==id)return false;
            return true;
        }
        public double Penalty(Candidate candidate)
        {
            if(candidate==null)return 0;
            var topics=TopicTags(candidate.Tags);
            if(topics.Count==0)return 0;
            var counted=new HashSet<string>(StringComparer.Ordinal);
            double penalty=0;
            foreach(var record in Dislikes??new List<DislikeRecord>())
            {
                string id=record==null?null:NormalizedId(record.Id);
                if(id==null||!counted.Add(id))continue;
                // Count a distinct rejection once, regardless of how many of its tags match.
                // This is a bounded ranking penalty; only the exact ID is excluded by Allows.
                if(topics.Overlaps(TopicTags(record.Tags)))
                {
                    penalty+=1;
                    if(penalty>=6)return 6;
                }
            }
            return penalty;
        }
        public void Validate()
        {
            if(Dislikes==null)throw new IOException("不喜欢记录列表无效，请检查 feedback.json。");
            var ids=new HashSet<string>(StringComparer.Ordinal);
            foreach(var record in Dislikes)
            {
                DateTime when;
                string id=record==null?null:NormalizedId(record.Id);
                if(record==null||id==null||id!=record.Id||!ids.Add(id)||record.Tags==null||!DateTime.TryParseExact(record.Utc,"o",CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out when)||when.Kind!=DateTimeKind.Utc)throw new IOException("不喜欢记录格式无效，请检查 feedback.json。");
                foreach(string tag in record.Tags)if(String.IsNullOrWhiteSpace(tag))throw new IOException("不喜欢记录标签无效，请检查 feedback.json。");
            }
        }
        static string NormalizedId(string value)
        {
            ulong id;
            return UInt64.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out id)&&id>0?id.ToString(CultureInfo.InvariantCulture):null;
        }
        static HashSet<string> TopicTags(IEnumerable<string> values)
        {
            var topics=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(string value in values??new string[0])
            {
                string topic=Topic(value);
                if(topic!=null)topics.Add(topic);
            }
            return topics;
        }
        static string Topic(string value)
        {
            if(String.IsNullOrWhiteSpace(value))return null;
            string key=Regex.Replace(value.Trim().ToLowerInvariant(),@"[\s_\p{Pd}]+","");
            // Explicit content topics only. Never learn from media type, resolution,
            // ratings, approval, audio responsiveness, HDR or other rendering metadata.
            switch(key)
            {
                case "nature":return "Nature";
                case "anime":return "Anime";
                case "scifi":case "sciencefiction":return "Sci-Fi";
                case "game":case "games":case "gaming":return "Games";
                case "girl":case "girls":return "Girls";
                case "guy":case "guys":return "Guys";
                case "technology":return "Technology";
                case "abstract":return "Abstract";
                case "fantasy":return "Fantasy";
                case "cyberpunk":return "Cyberpunk";
                case "landscape":case "landscapes":return "Landscape";
                case "animal":case "animals":return "Animals";
                case "vehicle":case "vehicles":return "Vehicles";
                case "space":return "Space";
                case "sport":case "sports":return "Sports";
                case "pixelart":return "Pixel Art";
                case "cartoon":case "cartoons":return "Cartoon";
                case "retro":return "Retro";
                case "music":return "Music";
                case "movie":case "movies":return "Movies";
                case "television":return "Television";
                default:return null;
            }
        }
    }
    public static class Json
    {
        static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer
            {
                MaxJsonLength=64*1024*1024,RecursionLimit=100
            };
        }
        public static string Encode(object v)
        {
            return Serializer().Serialize(v);
        }
        public static T Decode<T>(string s)
        {
            return Serializer().Deserialize<T>(s);
        }
        public static T Read<T>(string p)
        {
            return Decode<T>(File.ReadAllText(p,Encoding.UTF8));
        }
        public static void Write(string p,object v)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(p)));
            string t=p+"."+Guid.NewGuid().ToString("N")+".tmp";
            File.WriteAllText(t,Encode(v),new UTF8Encoding(false));
            if(File.Exists(p))File.Replace(t,p,p+".previous");
            else File.Move(t,p);
        }
    }
    public static class Schedule
    {
        public static DateTime Beijing(DateTime utc)
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc,DateTimeKind.Utc),TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"));
        }
        public static string Due(DateTime utc)
        {
            DateTime d=Beijing(utc);
            return (d.TimeOfDay>=TimeSpan.FromHours(23)?d.Date:d.Date.AddDays(-1)).ToString("yyyy-MM-dd");
        }
        public static string Prepare(DateTime utc)
        {
            DateTime d=Beijing(utc);
            return (d.TimeOfDay>=new TimeSpan(22,50,0)?d.Date:d.Date.AddDays(-1)).ToString("yyyy-MM-dd");
        }
    }
}
