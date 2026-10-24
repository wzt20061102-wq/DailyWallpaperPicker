// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
namespace DailyWallpaperPicker
{
    public static class CacheStore
    {
        public static string HashString(string text)
        {
            using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-","").Substring(0,20);
        }
        public static string Hash(string path)
        {
            using(var sha=SHA256.Create())using(var f=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(f)).Replace("-","").ToLowerInvariant();
        }
        public static bool Under(string path,string root)
        {
            string p=Path.GetFullPath(path),r=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            return p.StartsWith(r,StringComparison.OrdinalIgnoreCase);
        }
        static void NoLinks(string p)
        {
            if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("缓存中检测到链接，已停止操作。");
        }
        public static List<string> Files(string dir)
        {
            NoLinks(dir);
            var r=new List<string>();
            foreach(var f in Directory.GetFiles(dir))
            {
                NoLinks(f);
                r.Add(f);
            }
            foreach(var d in Directory.GetDirectories(dir))
            {
                NoLinks(d);
                r.AddRange(Files(d));
            }
            return r;
        }
        static void CopyTree(string src,string dst)
        {
            NoLinks(src);
            Directory.CreateDirectory(dst);
            foreach(var f in Directory.GetFiles(src))
            {
                NoLinks(f);
                File.Copy(f,Path.Combine(dst,Path.GetFileName(f)),false);
            }
            foreach(var d in Directory.GetDirectories(src))CopyTree(d,Path.Combine(dst,Path.GetFileName(d)));
        }
        public static string Import(string src,string root,string id,long maxBytes)
        {
            if(!id.All(Char.IsDigit))throw new IOException("无效壁纸编号");
            Directory.CreateDirectory(root);
            string dest=Path.Combine(root,id);
            if(Directory.Exists(dest)&&File.Exists(Path.Combine(dest,".verified.json")))return Verify(dest);
            string stage=Path.Combine(root,id+"-"+Guid.NewGuid().ToString("N")+".staging");
            var srcFiles=Files(src);
            if(srcFiles.Count==0||srcFiles.Sum(f=>new FileInfo(f).Length)>maxBytes)throw new IOException("壁纸为空或超过 300 MB。");
            try
            {
                CopyTree(src,stage);
                var dstFiles=Files(stage);
                if(srcFiles.Count!=dstFiles.Count)throw new IOException("复制文件数量不一致。");
                var manifest=new Dictionary<string,string>();
                foreach(string f in srcFiles)
                {
                    string relative=f.Substring(src.TrimEnd('\\').Length+1);
                    string copy=Path.Combine(stage,relative);
                    string hash=Hash(f);
                    if(hash!=Hash(copy))throw new IOException("壁纸复制校验失败。");
                    manifest.Add(relative,hash);
                }
                var srcDirs=Directory.GetDirectories(src,"*",SearchOption.AllDirectories).Select(p=>p.Substring(src.TrimEnd('\\').Length+1)).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToArray();
                var dstDirs=Directory.GetDirectories(stage,"*",SearchOption.AllDirectories).Select(p=>p.Substring(stage.Length+1)).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToArray();
                if(!srcDirs.SequenceEqual(dstDirs,StringComparer.OrdinalIgnoreCase))throw new IOException("壁纸目录集合不一致。");
                CheckProject(stage);
                Json.Write(Path.Combine(stage,".verified.json"),manifest);
                if(Directory.Exists(dest))SafeDelete(dest,root);
                Directory.Move(stage,dest);
                return Path.Combine(dest,"project.json");
            }
            catch
            {
                if(Directory.Exists(stage))SafeDelete(stage,root);
                throw;
            }
        }
        public static string CheckProject(string root)
        {
            string p=Path.Combine(root,"project.json");
            var d=Json.Read<Dictionary<string,object>>(p);
            string type=d.ContainsKey("type")?Convert.ToString(d["type"]).ToLowerInvariant():"";
            if(type!="scene"&&type!="video")throw new IOException("该壁纸不是 Scene 或 Video，已跳过。");
            if(!d.ContainsKey("file"))throw new IOException("壁纸缺少主文件。");
            string file=Path.GetFullPath(Path.Combine(root,Convert.ToString(d["file"])));
            if(!Under(file,root))throw new IOException("壁纸主文件路径越界。");
            bool packed=type=="scene"&&File.Exists(Path.Combine(root,"scene.pkg"))&&new FileInfo(Path.Combine(root,"scene.pkg")).Length>0;
            if(!File.Exists(file)&&!packed)throw new IOException("壁纸主文件缺失。");
            return p;
        }
        public static string Verify(string root)
        {
            root=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            var files=Files(root);
            var hashes=Json.Read<Dictionary<string,string>>(Path.Combine(root,".verified.json"));
            if(hashes==null||hashes.Count==0)throw new IOException("缓存校验记录为空。");
            var expected=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var kv in hashes)
            {
                if(String.IsNullOrWhiteSpace(kv.Key)||Path.IsPathRooted(kv.Key))throw new IOException("缓存校验记录含无效路径。");
                string f=Path.GetFullPath(Path.Combine(root,kv.Key));
                if(!Under(f,root))throw new IOException("缓存校验记录路径越界。");
                string relative=f.Substring(root.Length+1);
                if(relative.Equals(".verified.json",StringComparison.OrdinalIgnoreCase)||relative.Equals(".verified.json.previous",StringComparison.OrdinalIgnoreCase)||!expected.Add(f)||!File.Exists(f)||!String.Equals(Hash(f),kv.Value,StringComparison.OrdinalIgnoreCase))throw new IOException("壁纸缓存损坏，请重试下载。");
            }
            // Wallpaper Engine compiles Scene shaders beside the verified source after playback.
            // Only these narrowly named generated blobs may be added; every source file still hashes.
            var actual=new HashSet<string>(files.Where(f=>expected.Contains(f)||(!String.Equals(f,Path.Combine(root,".verified.json"),StringComparison.OrdinalIgnoreCase)&&!String.Equals(f,Path.Combine(root,".verified.json.previous"),StringComparison.OrdinalIgnoreCase)&&!Regex.IsMatch(f.Substring(root.Length+1),@"^shaders\\blobsSM[0-9]{2}\\[0-9a-f]{40}\.dxs$",RegexOptions.IgnoreCase))),StringComparer.OrdinalIgnoreCase);
            if(!actual.SetEquals(expected))throw new IOException("壁纸缓存文件集合已改变，请重试下载。");
            return CheckProject(root);
        }
        public static void SafeDelete(string path,string root)
        {
            if(!Under(path,root))throw new IOException("拒绝清理缓存目录以外的文件。");
            Files(path);
            Directory.Delete(path,true);
        }
    }
}
