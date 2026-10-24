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
        static bool SamePath(string first,string second)
        {
            return !String.IsNullOrWhiteSpace(first)&&!String.IsNullOrWhiteSpace(second)&&String.Equals(Path.GetFullPath(first).TrimEnd('\\','/'),Path.GetFullPath(second).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase);
        }
        string OwnedCachePath(string id,string directory)
        {
            if(String.IsNullOrWhiteSpace(id)||!id.All(Char.IsDigit)||String.IsNullOrWhiteSpace(directory))throw new IOException("无效壁纸缓存位置。");
            string expected=Path.Combine(cache,id);
            if(!SamePath(expected,directory)||!CacheStore.Under(expected,cache))throw new IOException("悬停预览只能读取工具自己的缓存。");
            if((File.GetAttributes(cache)&FileAttributes.ReparsePoint)!=0)throw new IOException("缓存根目录不能是链接。");
            return Path.GetFullPath(expected);
        }
        bool CacheIsInUse(string directory)
        {
            // Caller holds sync; cleanupGate prevents a verifier/import from racing this check.
            return (activeCache!=null&&SamePath(activeCache,directory))||hoverLeases.Any(x=>SamePath(x.CachePath,directory))||(!String.IsNullOrEmpty(wallpaper.PreviewPath)&&CacheStore.Under(wallpaper.PreviewPath,directory));
        }
        internal async Task QuarantineCorruptCacheAsync(string cached,string id,string current)
        {
            await cleanupGate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                lock(sync)
                {
                    cached=OwnedCachePath(id,cached);
                    if(String.IsNullOrWhiteSpace(current)||CacheStore.Under(current,cached)||CacheIsInUse(cached))throw new IOException("缓存正在使用或预览中，暂缓重新下载。");
                    string quarantine=Path.Combine(data,"quarantine");
                    Directory.CreateDirectory(quarantine);
                    CacheStore.Files(cached);
                    hoverVerified.Remove(cached);
                    Directory.Move(cached,Path.Combine(quarantine,id+"-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+"-"+Guid.NewGuid().ToString("N").Substring(0,8)));
                    LogSafely("Corrupt own cache quarantined "+id);
                }
            }
            finally
            {
                cleanupGate.Release();
            }
        }
        async Task<string> ImportCacheAsync(string source,string id)
        {
            await cleanupGate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                lock(sync)
                {
                    string owned=OwnedCachePath(id,Path.Combine(cache,id));
                    if(CacheIsInUse(owned))throw new IOException("缓存正在预览，稍后重试下载。");
                    hoverVerified.Remove(owned);
                }
                return await Task.Run(()=>CacheStore.Import(source,cache,id,(long)config.MaxSizeMB*1024*1024)).ConfigureAwait(false);
            }
            finally
            {
                cleanupGate.Release();
            }
        }
        void DeferCleanup(Exception ex)
        {
            lock(sync)
            {
                cleanupPending=true;
                nextCleanupTry=UtcNow.AddMinutes(5);
            }
            LogSafely("CACHE CLEAN deferred: "+ex.Message);
        }
        async Task RetryCleanupIfDue()
        {
            lock(sync)
            {
                if(state.Paused||preparing||acting||cleaning||!cleanupPending||UtcNow<nextCleanupTry)return;
                cleaning=true;
            }
            try
            {
                await Cleanup().ConfigureAwait(false);
            }
            catch(Exception ex)
            {
                DeferCleanup(ex);
            }
            finally
            {
                lock(sync)cleaning=false;
            }
        }
        internal string[] FindCleanupCandidates(string current,string preview)
        {
            if(String.IsNullOrWhiteSpace(current))throw new IOException("当前壁纸位置未知，暂缓清理。");
            HashSet<string> keep;
            lock(sync)
            {
                keep=new HashSet<string>(state.Choices.Take(config.RetainCount).Select(x=>x.Id));
                foreach(var x in state.Candidates.Concat(state.PreparedCandidates))keep.Add(x.Id);
                if(activeCache!=null)keep.Add(Path.GetFileName(activeCache));
                foreach(var lease in hoverLeases)keep.Add(lease.ItemId);
            }
            return Directory.GetDirectories(cache).Where(dir=>
            {
                string id=Path.GetFileName(dir);
                return id.Length>0&&id.All(Char.IsDigit)&&!keep.Contains(id)&&!CacheStore.Under(current,dir)&&(String.IsNullOrEmpty(preview)||!CacheStore.Under(preview,dir));
            }).ToArray();
        }
        public async Task Cleanup()
        {
            lock(sync)
            {
                if(acting)
                {
                    cleanupPending=true;
                    nextCleanupTry=UtcNow;
                    return;
                }
            }
            await cleanupGate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                string current=await wallpaper.GetCurrent().ConfigureAwait(false);
                if(!wallpaper.CanSafelyClean)throw new IOException("未取得当前壁纸的实时结果，暂缓清理。");
                lock(sync)
                {
                    if(acting)
                    {
                        cleanupPending=true;
                        nextCleanupTry=UtcNow;
                        return;
                    }
                    foreach(var dir in FindCleanupCandidates(current,wallpaper.PreviewPath))
                    {
                        string id=Path.GetFileName(dir);
                        hoverVerified.Remove(dir);
                        CacheStore.SafeDelete(dir,cache);
                        LogSafely("CACHE CLEAN "+id);
                    }
                    cleanupPending=false;
                    nextCleanupTry=DateTime.MinValue;
                }
            }
            finally
            {
                cleanupGate.Release();
            }
        }
    }
}
