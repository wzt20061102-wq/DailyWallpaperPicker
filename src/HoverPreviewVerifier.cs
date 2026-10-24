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
    internal static class HoverPreviewVerifier
    {
        [StructLayout(LayoutKind.Sequential)]struct HoverBasicInfo
        {
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public long ChangeTime;
            public uint Attributes;
        }
        [StructLayout(LayoutKind.Sequential)]struct HoverFileIdentity
        {
            public uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,VolumeSerial,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;
        }
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int infoClass,out HoverBasicInfo info,uint size);
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetFileInformationByHandle(SafeFileHandle handle,out HoverFileIdentity info);
        internal static string Fingerprint(string directory)
        {
            var records=new SortedDictionary<string,string>(StringComparer.Ordinal);
            HoverFingerprintDirectory(new DirectoryInfo(directory),directory.Length,records);
            return Json.Encode(records);
        }
        static void HoverFingerprintDirectory(DirectoryInfo directory,int prefixLength,SortedDictionary<string,string> records)
        {
            directory.Refresh();
            if(!directory.Exists||(directory.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("壁纸缓存目录不存在或包含链接。");
            records.Add("D:"+directory.FullName.Substring(prefixLength),HoverFileStamp(directory.FullName));
            foreach(var info in directory.GetFileSystemInfos())
            {
                info.Refresh();
                if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("壁纸缓存包含链接，已停止预览。");
                var child=info as DirectoryInfo;
                if(child!=null)
                {
                    HoverFingerprintDirectory(child,prefixLength,records);
                    continue;
                }
                var file=info as FileInfo;
                if(file==null||!file.Exists)throw new IOException("壁纸文件集合发生变化。");
                records.Add("F:"+file.FullName.Substring(prefixLength),file.Length+"|"+HoverFileStamp(file.FullName));
            }
        }
        static string HoverFileStamp(string path)
        {
            // Zero requested data access reads metadata even while a decoder holds the file.
            // OPEN_REPARSE_POINT prevents following a link; BACKUP_SEMANTICS also opens directories.
            using(var handle=CreateFile(path,0,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero))
            {
                HoverBasicInfo basic;
                HoverFileIdentity identity;
                if(handle.IsInvalid||!GetFileInformationByHandleEx(handle,0,out basic,(uint)Marshal.SizeOf(typeof(HoverBasicInfo)))||!GetFileInformationByHandle(handle,out identity))throw new IOException("无法确认壁纸缓存文件身份。",new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                if((basic.Attributes&(uint)FileAttributes.ReparsePoint)!=0)throw new IOException("壁纸缓存包含链接，已停止预览。");
                return identity.VolumeSerial+"|"+identity.IndexHigh+"|"+identity.IndexLow+"|"+identity.SizeHigh+"|"+identity.SizeLow+"|"+basic.CreationTime+"|"+basic.LastWriteTime+"|"+basic.ChangeTime+"|"+basic.Attributes;
            }
        }
    }
}
