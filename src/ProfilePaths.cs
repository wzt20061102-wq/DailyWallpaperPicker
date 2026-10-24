// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.
using System;
using System.IO;

namespace DailyWallpaperPicker
{
    public sealed class InstallProfile
    {
        public string DataDirectory { get; set; }
    }
    internal static class ProfilePaths
    {
        internal static string Resolve(string[] args, string baseDirectory)
        {
            int index = Array.IndexOf(args, "--data-dir");
            if (index >= 0)
            {
                if (index + 1 >= args.Length || String.IsNullOrWhiteSpace(args[index + 1]))
                    throw new ArgumentException("--data-dir 缺少目录参数。");
                return Path.GetFullPath(args[index + 1]);
            }
            string file = Path.Combine(baseDirectory, "install-profile.json");
            if (File.Exists(file))
            {
                var profile = Json.Read<InstallProfile>(file);
                if (profile == null || String.IsNullOrWhiteSpace(profile.DataDirectory) || !Path.IsPathRooted(profile.DataDirectory))
                    throw new IOException("安装数据路径无效，请检查 install-profile.json。");
                return Path.GetFullPath(profile.DataDirectory);
            }
            return Path.Combine(baseDirectory, "data");
        }
    }
}
