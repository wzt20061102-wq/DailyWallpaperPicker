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
        readonly object sync=new object();
        readonly SemaphoreSlim cleanupGate=new SemaphoreSlim(1,1);
        readonly SemaphoreSlim recommendationGate=new SemaphoreSlim(1,1);
        readonly string data,statePath,cache;
        readonly CancellationToken cancellation;
        readonly FeedbackStore feedbackStore;
        readonly PickerConfig config;
        readonly SteamClient steam;
        readonly WallpaperBridge wallpaper;
        readonly Func<DateTime> clock;
        readonly Func<string,object,Action<WorkerProgress>,Task<WorkerReply>> callSteam;
        bool disliking;
        AppState state;
        bool preparing;
        bool acting;
        bool cleaning;
        bool cleanupPending;
        string activeCache;
        int previewEpoch;
        DateTime nextTry=DateTime.MinValue;
        DateTime nextCleanupTry=DateTime.MinValue;
        public event Action Changed;
        public bool Busy
        {
            get
            {
                lock(sync)return acting||disliking;
            }
        }
        public AppState State
        {
            get
            {
                lock(sync)return Json.Decode<AppState>(Json.Encode(state));
            }
        }
        public string DataDirectory
        {
            get
            {
                return data;
            }
        }
        DateTime UtcNow
        {
            get
            {
                return clock();
            }
        }
        public PickerController(string dataDirectory):this(dataDirectory,()=>DateTime.UtcNow)
        {
        }
        public PickerController(string dataDirectory,CancellationToken cancellation):this(dataDirectory,()=>DateTime.UtcNow,null,cancellation,null)
        {
        }
        internal PickerController(string dataDirectory,Func<DateTime> utcClock):this(dataDirectory,utcClock,null)
        {
        }
        internal PickerController(string dataDirectory,Func<DateTime> utcClock,Func<string,object,Action<WorkerProgress>,Task<WorkerReply>> workerCall):this(dataDirectory,utcClock,workerCall,CancellationToken.None,null)
        {
        }
        internal PickerController(string dataDirectory,Func<DateTime> utcClock,Func<string,object,Action<WorkerProgress>,Task<WorkerReply>> workerCall,CancellationToken cancellation,WallpaperBridge wallpaperBridge)
        {
            this.cancellation=cancellation;
            if(utcClock==null)throw new ArgumentNullException("utcClock");
            clock=utcClock;
            data=Path.GetFullPath(dataDirectory);
            Directory.CreateDirectory(data);
            feedbackStore=new FeedbackStore(data);
            cache=Path.Combine(data,"cache");
            Directory.CreateDirectory(cache);
            statePath=Path.Combine(data,"state.json");
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            string conf=Path.Combine(data,"config.json");
            config=File.Exists(conf)?Json.Read<PickerConfig>(conf):InstallationDiscovery.Discover();
            if(!File.Exists(conf))Json.Write(conf,config);
            state=File.Exists(statePath)?Json.Read<AppState>(statePath):new AppState();
            if(state==null)throw new IOException("状态记录为空，请保留 state.json 并使用 .previous 恢复。");
            if(state.Candidates==null||state.Choices==null||state.Seen==null)throw new IOException("本地记录损坏，请保留 state.json 并使用 .previous 恢复。");
            if(state.PreparedCandidates==null)state.PreparedCandidates=new List<Candidate>();
            if(state.SeedTags==null)state.SeedTags=new Dictionary<string,double>();
            StateMigration.Upgrade(state);
            if(state.PreparedBatchId==state.BatchId&&state.Candidates.Count==3)state.PreparedCandidates=state.Candidates;
            steam=new SteamClient(config,data,cancellation);
            callSteam=workerCall??steam.Call;
            wallpaper=wallpaperBridge??new WallpaperBridge(config.WallpaperExe,Log,cancellation);
        }
        void Save()
        {
            Json.Write(statePath,state);
        }
        void Notify()
        {
            Action c=Changed;
            if(c!=null)foreach(Action handler in c.GetInvocationList())try
            {
                handler();
            }
            catch(Exception ex)
            {
                LogSafely("UI notification failed: "+ex.Message);
            }
        }
        void LogSafely(string text)
        {
            try
            {
                Log(text);
            }
            catch
            {
            }
        }
        public void LogHover(string message)
        {
            LogSafely("HOVER "+message);
        }
        static string Friendly(Exception ex)
        {
            string m=ex.Message??"未知错误";
            return m.Length>180?m.Substring(0,180):m;
        }
        public void Log(string text)
        {
            BoundedLog.Write(data,text);
        }
    }
}
