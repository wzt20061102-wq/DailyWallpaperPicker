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
    internal static class InstallationDiscovery
    {
        internal static PickerConfig Discover()
        {
            var dirs=new List<string>();
            using(var k=Registry.CurrentUser.OpenSubKey("Software\\Valve\\Steam"))
            {
                if(k!=null)
                {
                    string s=Convert.ToString(k.GetValue("SteamPath"));
                    if(!String.IsNullOrEmpty(s))
                    {
                        dirs.Add(s);
                        string v=Path.Combine(s,"steamapps","libraryfolders.vdf");
                        if(File.Exists(v))foreach(Match m in Regex.Matches(File.ReadAllText(v),"\"path\"\\s+\"([^\"]+)\""))dirs.Add(m.Groups[1].Value.Replace("\\\\","\\"));
                    }
                }
            }
            dirs.Add(@"C:\steam");
            foreach(var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string e=Path.Combine(d,"steamapps","common","wallpaper_engine","wallpaper64.exe");
                if(File.Exists(e)&&File.Exists(Path.Combine(Path.GetDirectoryName(e),"bin","steam_api64.dll")))return new PickerConfig
                {
                    WallpaperExe=e,SteamBin=Path.Combine(Path.GetDirectoryName(e),"bin")
                };
            }
            throw new IOException("没有找到 Wallpaper Engine。请先通过 Steam 安装正版应用。");
        }
    }
}
