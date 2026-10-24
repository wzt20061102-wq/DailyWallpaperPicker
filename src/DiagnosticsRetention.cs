// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DailyWallpaperPicker
{
    internal static class BoundedLog
    {
        static readonly object Gate = new object();
        internal static void Write(string data, string message)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(data);
                RetentionPaths.RejectDirectoryLinks(data);
                string path = Path.Combine(data, "runtime_output.txt");
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("日志路径不能是链接。");
                if (File.Exists(path) && new FileInfo(path).Length >= 4 * 1024 * 1024)
                    File.Move(path, path + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff") + ".log");
                File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine, new UTF8Encoding(false));
                var previous = new DirectoryInfo(data).GetFiles("runtime_output.txt.*.log")
                    .Where(f => Regex.IsMatch(f.Name, @"^runtime_output\.txt\.[0-9]+\.log$"))
                    .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
                for (int i = 0; i < previous.Length; i++)
                    if (i >= 5 || previous[i].LastWriteTimeUtc < DateTime.UtcNow.AddDays(-14)) TryDelete(previous[i].FullName);
            }
        }
        internal static void TryDelete(string path)
        {
            try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Keep at most 30 failed jobs for 7 days. Active jobs are always excluded.
    internal static class JobRetention
    {
        static readonly object Gate = new object();
        static readonly HashSet<string> Active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Regex JobName = new Regex(@"^(?<id>[a-f0-9]{32})\.(request|response|progress)\.json(\.previous|\.[a-f0-9]{32}\.tmp)?$", RegexOptions.CultureInvariant);

        internal static IDisposable Acquire(string root, string id)
        {
            root = Path.GetFullPath(root);
            lock (Gate)
            {
                RetentionPaths.RejectDirectoryLinks(root);
                Active.Add(Path.Combine(root, id));
                try { Prune(root, DateTime.UtcNow); }
                catch { Active.Remove(Path.Combine(root, id)); throw; }
            }
            return new Lease(root, id);
        }
        internal static void DeleteCompleted(string root, string id)
        {
            lock (Gate)
                foreach (var file in OwnedFiles(root).Where(f => JobName.Match(f.Name).Groups["id"].Value == id)) BoundedLog.TryDelete(file.FullName);
        }
        static IEnumerable<FileInfo> OwnedFiles(string root)
        {
            if (!Directory.Exists(root)) return new FileInfo[0];
            RetentionPaths.RejectDirectoryLinks(root);
            return new DirectoryInfo(root).GetFiles().Where(f => JobName.IsMatch(f.Name) && (f.Attributes & FileAttributes.ReparsePoint) == 0);
        }
        internal static void Prune(string root, DateTime now)
        {
            lock (Gate)
            {
                var groups = OwnedFiles(root).GroupBy(f => JobName.Match(f.Name).Groups["id"].Value)
                    .Where(g => !Active.Contains(Path.Combine(Path.GetFullPath(root), g.Key)))
                    .OrderByDescending(g => g.Max(f => f.LastWriteTimeUtc)).ToArray();
                for (int i = 0; i < groups.Length; i++)
                    if (i >= 30 || groups[i].Max(f => f.LastWriteTimeUtc) < now.AddDays(-7))
                        foreach (var file in groups[i]) BoundedLog.TryDelete(file.FullName);
            }
        }
        sealed class Lease : IDisposable
        {
            readonly string root, id;
            public Lease(string root, string id) { this.root = root; this.id = id; }
            public void Dispose()
            {
                lock (Gate)
                {
                    Active.Remove(Path.Combine(root, id));
                    Prune(root, DateTime.UtcNow);
                }
            }
        }
    }

    internal static class RetentionPaths
    {
        internal static void RejectDirectoryLinks(string path)
        {
            for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory != null; directory = directory.Parent)
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("辅助文件目录含链接，已停止写入和清理。");
        }
    }
}
