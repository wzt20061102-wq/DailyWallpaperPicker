// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.ExceptionServices;

namespace DailyWallpaperPicker
{
    public class WorkerReply
    {
        public bool ok { get; set; }
        public string error { get; set; }
        public List<Candidate> items { get; set; }
        public string id { get; set; }
        public string installPath { get; set; }
        public int? appId { get; set; }
        public bool? subscribedBefore { get; set; }
        public bool? subscribedAfter { get; set; }
    }
    public class WorkerProgress
    {
        public string id { get; set; }
        public long downloaded { get; set; }
        public long total { get; set; }
        public string status { get; set; }
    }

    public class SteamClient
    {
        readonly PickerConfig config;
        readonly string root;
        readonly string exe;
        readonly CancellationToken cancellationToken;
        public SteamClient(PickerConfig config, string data) : this(config, data, CancellationToken.None) { }
        public SteamClient(PickerConfig config, string data, CancellationToken cancellationToken)
            : this(config, data, cancellationToken, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SteamWorker.exe")) { }
        internal SteamClient(PickerConfig config, string data, CancellationToken cancellationToken, string workerExecutable)
        {
            this.config = config;
            this.cancellationToken = cancellationToken;
            root = Path.Combine(data, "jobs");
            Directory.CreateDirectory(root);
            exe = workerExecutable;
        }

        public virtual async Task<WorkerReply> Call(string verb, object input, Action<WorkerProgress> progress)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = Guid.NewGuid().ToString("N");
            string request = Path.Combine(root, id + ".request.json");
            string response = Path.Combine(root, id + ".response.json");
            string progressFile = Path.Combine(root, id + ".progress.json");
            using (JobRetention.Acquire(root, id))
            using (var callLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task<RunResult> task = null;
                WorkerReply reply = null;
                Exception failure = null;
                try
                {
                    var payload = Json.Decode<Dictionary<string, object>>(Json.Encode(input));
                    payload["steamBin"] = config.SteamBin;
                    payload["appId"] = 431960;
                    payload["progressFile"] = progressFile;
                    Json.Write(request, payload);
                    task = Processes.Run(exe, verb + " --request " + Processes.Quote(request) + " --response " + Processes.Quote(response),
                        config.SteamBin, verb == "download" ? 1200 : 180, callLifetime.Token);
                    while (!task.IsCompleted)
                    {
                        await Task.WhenAny(task, Task.Delay(1000)).ConfigureAwait(false);
                        if (progress != null && File.Exists(progressFile))
                        {
                            try { progress(Json.Read<WorkerProgress>(progressFile)); }
                            catch (IOException) { }
                            catch (ArgumentException) { }
                        }
                    }
                    var result = await task.ConfigureAwait(false);
                    if (!File.Exists(response)) throw new IOException("Steam 接口没有返回结果：" + result.Error);
                    reply = Json.Read<WorkerReply>(response);
                    if (reply == null || !reply.ok || result.ExitCode != 0)
                        throw new IOException("Steam：" + (reply == null ? result.Error : reply.error ?? result.Error));
                }
                catch (Exception ex) { failure = ex; }
                if (failure != null)
                {
                    // A faulty progress subscriber must not abandon a running worker.
                    // Drain it before releasing its active-file lease.
                    callLifetime.Cancel();
                    if (task != null) try { await task.ConfigureAwait(false); } catch { }
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }
                JobRetention.DeleteCompleted(root, id);
                return reply;
            }
        }
    }
}
