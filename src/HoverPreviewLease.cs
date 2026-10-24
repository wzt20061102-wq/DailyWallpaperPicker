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
    public sealed class HoverPreviewLease : IDisposable
    {
        Action<HoverPreviewLease> release;
        int disposed;
        public string ItemId
        {
            get;
            private set;
        }
        public string BatchId
        {
            get;
            private set;
        }
        public string ProjectPath
        {
            get;
            private set;
        }
        public string MediaPath
        {
            get;
            private set;
        }
        public bool IsVideo
        {
            get;
            private set;
        }
        public bool IsScene
        {
            get
            {
                return !IsVideo;
            }
        }
        public bool IsDisposed
        {
            get
            {
                return Interlocked.CompareExchange(ref disposed,0,0)!=0;
            }
        }
        internal string CachePath
        {
            get;
            private set;
        }
        internal HoverPreviewLease(string itemId,string batchId,string project,string media,bool video,Action<HoverPreviewLease> onRelease)
        {
            ItemId=itemId;
            BatchId=batchId;
            ProjectPath=project;
            MediaPath=media;
            IsVideo=video;
            CachePath=Path.GetDirectoryName(project);
            release=onRelease;
        }
        public void Dispose()
        {
            if(Interlocked.Exchange(ref disposed,1)!=0)return;
            var callback=Interlocked.Exchange(ref release,null);
            if(callback!=null)callback(this);
        }
    }
}
