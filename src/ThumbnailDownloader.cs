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
    internal static class ThumbnailDownloader
    {
        internal static async Task<byte[]> DownloadAsync(string url,TimeSpan timeout,CancellationToken applicationCancellation)
        {
            Uri uri;
            if(!Uri.TryCreate(url,UriKind.Absolute,out uri)||(uri.Scheme!=Uri.UriSchemeHttp&&uri.Scheme!=Uri.UriSchemeHttps))throw new IOException("封面地址仅支持 HTTP 或 HTTPS。");
            if(timeout<=TimeSpan.Zero||timeout.TotalMilliseconds>Int32.MaxValue)throw new ArgumentOutOfRangeException("timeout");
            applicationCancellation.ThrowIfCancellationRequested();
            using(var cancellation=CancellationTokenSource.CreateLinkedTokenSource(applicationCancellation))using(var deadline=new CancellationTokenSource())
            {
                Task elapsed=Task.Delay(timeout,deadline.Token);
                Task<byte[]> transfer=Task.Run(()=>ReadThumbnail(uri,cancellation.Token));
                if(await Task.WhenAny(transfer,elapsed).ConfigureAwait(false)!=transfer)
                {
                    cancellation.Cancel();
                    // Abort also interrupts a pending response/body read. Observe a late fault,
                    // but never let an unresponsive transport extend the public deadline.
                    ObserveThumbnailFailure(transfer);
                    applicationCancellation.ThrowIfCancellationRequested();
                    throw new TimeoutException("封面下载超时，已保留占位图并继续下载壁纸。");
                }
                deadline.Cancel();
                try
                {
                    return await transfer.ConfigureAwait(false);
                }
                catch
                {
                    applicationCancellation.ThrowIfCancellationRequested();
                    throw;
                }
            }
        }
        static void ObserveThumbnailFailure(Task transfer)
        {
            transfer.ContinueWith(t=>
            {
                var observed=t.Exception;
            },CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        }
        static async Task<byte[]> ReadThumbnail(Uri uri,CancellationToken cancellation)
        {
            const int limit=10*1024*1024;
            var request=WebRequest.CreateHttp(uri);
            using(cancellation.Register(()=>request.Abort()))
            {
                using(var response=(HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                {
                    if(response.ResponseUri.Scheme!=Uri.UriSchemeHttp&&response.ResponseUri.Scheme!=Uri.UriSchemeHttps)throw new IOException("封面重定向地址无效。");
                    if(response.ContentLength>limit)throw new IOException("预览图超过 10 MB。");
                    using(var input=response.GetResponseStream())using(var output=new MemoryStream())
                    {
                        byte[] buffer=new byte[16*1024];
                        int total=0;
                        while(true)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            int count=await input.ReadAsync(buffer,0,Math.Min(buffer.Length,limit-total+1),cancellation).ConfigureAwait(false);
                            if(count==0)break;
                            total+=count;
                            if(total>limit)throw new IOException("预览图超过 10 MB。");
                            output.Write(buffer,0,count);
                        }
                        cancellation.ThrowIfCancellationRequested();
                        if(total==0)throw new IOException("封面内容为空。");
                        return output.ToArray();
                    }
                }
            }
        }
    }
}
