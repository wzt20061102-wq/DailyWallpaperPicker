// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// Steamworks v020, Windows x64, C# 5. No SubscribeItem/UnsubscribeItem bindings exist.
// ABI source: Valve isteamugc.h (STEAMUGC_INTERFACE_VERSION020), steam_api.h.
internal static class SteamWorker
{
    private const uint AppId = 431960;
    private const int QueryCallback = 3401, DownloadCallback = 3406, ApiCompletedCallback = 703;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16777216 };
    private static IntPtr ugc;
    private static int pipe;
    private static bool initialized;

    private sealed class Request
    {
        public string steamBin { get; set; }
        public uint appId { get; set; }
        public string pool { get; set; }
        public string[] ids { get; set; }
        public int limit { get; set; }
        public int? page { get; set; }
        public string id { get; set; }
        public string progressFile { get; set; }
    }
    private sealed class Item
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string[] Tags { get; set; }
        public string PreviewUrl { get; set; }
        public long FileSize { get; set; }
        public double Score { get; set; }
        public long CreatedUtc { get; set; }
        public long UpdatedUtc { get; set; }
    }

    private static int Main(string[] args)
    {
        string responsePath = null;
        Request request = null;
        try
        {
            if (args.Length != 5 || (args[0] != "query" && args[0] != "download") || args[1] != "--request" || args[3] != "--response")
                throw new ArgumentException("Usage: SteamWorker.exe query|download --request <json> --response <json>");
            string requestPath = Path.GetFullPath(args[2]);
            responsePath = Path.GetFullPath(args[4]);
            request = Json.Deserialize<Request>(File.ReadAllText(requestPath, Encoding.UTF8));
            if (request == null || request.appId != AppId) throw new ArgumentException("appId must be 431960.");
            Initialize(request.steamBin);
            object response = args[0] == "query" ? Query(request) : Download(request);
            AtomicJson(responsePath, response);
            Dictionary<string, object> responseFields = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(response));
            bool responseOk = responseFields.ContainsKey("ok") && Object.Equals(responseFields["ok"], true);
            Console.WriteLine(Json.Serialize(new { ok = responseOk, command = args[0], appId = AppId, response = responsePath }));
            return responseOk ? 0 : 1;
        }
        catch (Exception ex)
        {
            if (responsePath != null)
            {
                try
                {
                    string error = ex.GetType().Name + ": " + ex.Message;
                    if (args.Length > 0 && args[0] == "download")
                        AtomicJson(responsePath, new { ok = false, error = error, id = request == null ? null : request.id,
                            installPath = (string)null, bytes = 0L, subscribedBefore = (bool?)null, subscribedAfter = (bool?)null, appId = AppId });
                    else AtomicJson(responsePath, new { ok = false, error = error, items = new Item[0], appId = AppId });
                }
                catch (Exception writeEx) { Console.Error.WriteLine("Response write failed: " + writeEx.Message); }
            }
            Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
        finally { if (initialized) Native.SteamAPI_Shutdown(); }
    }

    private static void Initialize(string steamBin)
    {
        if (IntPtr.Size != 8) throw new InvalidOperationException("A 64-bit process is required.");
        if (String.IsNullOrWhiteSpace(steamBin)) throw new ArgumentException("steamBin is required.");
        steamBin = Path.GetFullPath(steamBin);
        string dll = Path.Combine(steamBin, "steam_api64.dll");
        string appIdFile = Path.Combine(steamBin, "steam_appid.txt");
        if (!File.Exists(dll) || !File.Exists(appIdFile)) throw new FileNotFoundException("Official Steam API DLL or steam_appid.txt is missing.");
        if (File.ReadAllText(appIdFile, Encoding.UTF8).Trim() != AppId.ToString(CultureInfo.InvariantCulture))
            throw new InvalidOperationException("Existing steam_appid.txt must identify Wallpaper Engine (431960).");
        // Process-local only. Neither the installed DLL nor AppID file is copied or modified.
        Directory.SetCurrentDirectory(steamBin);
        Environment.SetEnvironmentVariable("SteamAppId", AppId.ToString(CultureInfo.InvariantCulture), EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("SteamGameId", AppId.ToString(CultureInfo.InvariantCulture), EnvironmentVariableTarget.Process);
        if (Native.LoadLibraryEx(dll, IntPtr.Zero, 8) == IntPtr.Zero)
            throw new InvalidOperationException("LoadLibraryEx failed: " + Marshal.GetLastWin32Error());
        byte[] initError = new byte[1024];
        int initResult = Native.SteamAPI_InitFlat(initError);
        if (initResult != 0) throw new InvalidOperationException("SteamAPI_InitFlat failed (" + initResult + "): " + Utf8(initError));
        initialized = true;
        IntPtr utils = Native.SteamAPI_SteamUtils_v010();
        if (utils == IntPtr.Zero || Native.SteamAPI_ISteamUtils_GetAppID(utils) != AppId)
            throw new InvalidOperationException("Steam returned a different AppID.");
        ugc = Native.SteamAPI_SteamUGC_v020();
        if (ugc == IntPtr.Zero) throw new InvalidOperationException("SteamUGC_v020 is unavailable.");
        pipe = Native.SteamAPI_GetHSteamPipe();
        if (pipe == 0) throw new InvalidOperationException("Steam returned an invalid pipe.");
        if (Marshal.SizeOf(typeof(UgcDetails)) != 9784 || Marshal.SizeOf(typeof(QueryCompleted)) != 280 ||
            Marshal.SizeOf(typeof(DownloadResult)) != 24 || Marshal.SizeOf(typeof(CallbackMsg)) != 24 || Marshal.SizeOf(typeof(ApiCompleted)) != 16)
            throw new InvalidOperationException("Steamworks x64 ABI size validation failed.");
        Native.SteamAPI_ManualDispatch_Init();
    }

    private static object Query(Request request)
    {
        int limit = request.limit == 0 ? 50 : request.limit;
        if (limit < 1 || limit > 50) throw new ArgumentException("limit must be 1..50.");
        int page = request.page ?? 1;
        if (page < 1 || page > 20) throw new ArgumentException("page must be 1..20.");
        ulong query;
        if (request.ids != null && request.ids.Length > 0)
        {
            if (request.ids.Length > 50) throw new ArgumentException("At most 50 detail IDs are supported per request.");
            ulong[] ids = request.ids.Select(ParseId).Distinct().ToArray();
            query = Native.SteamAPI_ISteamUGC_CreateQueryUGCDetailsRequest(ugc, ids, (uint)ids.Length);
        }
        else
        {
            int sort;
            if (request.pool == "popular") sort = 3;
            else if (request.pool == "newest") sort = 1;
            else if (request.pool == "classic") sort = 0;
            else throw new ArgumentException("pool must be popular, newest, or classic.");
            query = Native.SteamAPI_ISteamUGC_CreateQueryAllUGCRequestPage(ugc, sort, 0, AppId, AppId, (uint)page);
            if (query == UInt64.MaxValue || query == 0) throw new InvalidOperationException("CreateQueryAllUGCRequestPage failed.");
        }
        if (query == UInt64.MaxValue || query == 0) throw new InvalidOperationException("Query handle is invalid.");
        try
        {
            if (request.ids == null || request.ids.Length == 0)
            {
                if (request.pool == "popular" && !Native.SteamAPI_ISteamUGC_SetRankedByTrendDays(ugc, query, 7))
                    throw new InvalidOperationException("Could not configure the seven-day trend query.");
                if (!Native.SteamAPI_ISteamUGC_AddRequiredTag(ugc, query, "Everyone"))
                    throw new InvalidOperationException("Could not set the Everyone query tag.");
            }
            ulong call = Native.SteamAPI_ISteamUGC_SendQueryUGCRequest(ugc, query);
            if (call == 0) throw new InvalidOperationException("SendQueryUGCRequest failed.");
            QueryCompleted completed = WaitQuery(call, query, TimeSpan.FromSeconds(120));
            List<Item> items = new List<Item>();
            for (uint index = 0; index < completed.Returned && items.Count < limit; index++)
            {
                UgcDetails details;
                if (!Native.SteamAPI_ISteamUGC_GetQueryUGCResult(ugc, query, index, out details))
                    throw new InvalidOperationException("GetQueryUGCResult failed for index " + index);
                if (details.Result != 1 || details.ConsumerApp != AppId || details.Banned != 0) continue;
                byte[] preview = new byte[4096];
                string previewUrl = Native.SteamAPI_ISteamUGC_GetQueryUGCPreviewURL(ugc, query, index, preview, (uint)preview.Length) ? Utf8(preview) : "";
                long bytes = details.TotalFilesSize > Int64.MaxValue ? Int64.MaxValue : (long)details.TotalFilesSize;
                if (bytes == 0 && details.FileSize > 0) bytes = details.FileSize;
                items.Add(new Item {
                    Id = details.Id.ToString(CultureInfo.InvariantCulture), Title = Utf8(details.Title),
                    Tags = Utf8(details.Tags).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray(),
                    PreviewUrl = previewUrl, FileSize = bytes, Score = details.Score,
                    CreatedUtc = details.Created, UpdatedUtc = details.Updated
                });
            }
            return new { ok = true, error = (string)null, items = items, appId = AppId };
        }
        finally { Native.SteamAPI_ISteamUGC_ReleaseQueryUGCRequest(ugc, query); }
    }

    private static QueryCompleted WaitQuery(ulong call, ulong query, TimeSpan timeout)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            Native.SteamAPI_ManualDispatch_RunFrame(pipe);
            CallbackMsg message;
            while (Native.SteamAPI_ManualDispatch_GetNextCallback(pipe, out message))
            {
                try
                {
                    if (message.Callback != ApiCompletedCallback || message.Size != Marshal.SizeOf(typeof(ApiCompleted))) continue;
                    ApiCompleted completed = (ApiCompleted)Marshal.PtrToStructure(message.Data, typeof(ApiCompleted));
                    if (completed.Call != call) continue;
                    if (completed.Callback != QueryCallback || completed.Size != Marshal.SizeOf(typeof(QueryCompleted)))
                        throw new InvalidOperationException("Unexpected query callback ABI.");
                    IntPtr buffer = Marshal.AllocHGlobal((int)completed.Size);
                    try
                    {
                        bool failed;
                        if (!Native.SteamAPI_ManualDispatch_GetAPICallResult(pipe, call, buffer, (int)completed.Size, QueryCallback, out failed) || failed)
                            throw new InvalidOperationException("Steam query callback failed.");
                        QueryCompleted result = (QueryCompleted)Marshal.PtrToStructure(buffer, typeof(QueryCompleted));
                        if (result.Handle != query || result.Result != 1 || result.Returned > 50)
                            throw new InvalidOperationException("Steam query failed or mismatched: result=" + result.Result + ", count=" + result.Returned);
                        return result;
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                finally { Native.SteamAPI_ManualDispatch_FreeLastCallback(pipe); }
            }
            Thread.Sleep(40);
        }
        throw new TimeoutException("Steam query timed out after 120 seconds.");
    }

    private static object Download(Request request)
    {
        ulong id = ParseId(request.id);
        string progressPath = String.IsNullOrWhiteSpace(request.progressFile) ? null : Path.GetFullPath(request.progressFile);
        uint initialState = Native.SteamAPI_ISteamUGC_GetItemState(ugc, id);
        bool subscribedBefore = (initialState & 1) != 0;
        bool started = false;
        try
        {
            bool currentInstalled = (initialState & 4) != 0 && (initialState & (8 | 16 | 32)) == 0;
            if (!currentInstalled)
            {
                // false means ordinary priority; never pause the user's other Steam downloads.
                started = Native.SteamAPI_ISteamUGC_DownloadItem(ugc, id, false);
                if (!started) throw new InvalidOperationException("DownloadItem was rejected by Steam.");
                WaitDownload(id, progressPath, TimeSpan.FromMinutes(15));
            }
            uint state = Native.SteamAPI_ISteamUGC_GetItemState(ugc, id);
            if ((state & 4) == 0 || (state & (8 | 16 | 32)) != 0) throw new InvalidOperationException("Steam reported success but item is not fully installed/current: " + state);
            byte[] folder = new byte[32768];
            ulong size; uint stamp;
            if (!Native.SteamAPI_ISteamUGC_GetItemInstallInfo(ugc, id, out size, folder, (uint)folder.Length, out stamp))
                throw new InvalidOperationException("GetItemInstallInfo failed.");
            string installPath = Utf8(folder);
            if (!Directory.Exists(installPath) || !File.Exists(Path.Combine(installPath, "project.json")))
                throw new InvalidOperationException("Installed item has no project.json.");
            if (size == 0) throw new InvalidOperationException("Steam returned an empty installed item.");
            bool subscribedAfter = (Native.SteamAPI_ISteamUGC_GetItemState(ugc, id) & 1) != 0;
            if (subscribedBefore != subscribedAfter) throw new InvalidOperationException("Subscription state changed unexpectedly; no cleanup is attempted.");
            Progress(progressPath, id, size, size, "complete");
            return new { ok = true, error = (string)null, id = request.id, installPath = installPath, bytes = size,
                subscribedBefore = subscribedBefore, subscribedAfter = subscribedAfter, downloadedNow = started, appId = AppId };
        }
        catch (Exception ex)
        {
            bool subscribedAfter = (Native.SteamAPI_ISteamUGC_GetItemState(ugc, id) & 1) != 0;
            Progress(progressPath, id, 0, 0, "failed");
            return new { ok = false, error = ex.GetType().Name + ": " + ex.Message, id = request.id,
                installPath = (string)null, bytes = 0L, subscribedBefore = subscribedBefore, subscribedAfter = subscribedAfter, appId = AppId };
        }
    }

    private static void WaitDownload(ulong id, string progressPath, TimeSpan timeout)
    {
        Stopwatch timer = Stopwatch.StartNew();
        long lastProgress = -1000;
        while (timer.Elapsed < timeout)
        {
            Native.SteamAPI_ManualDispatch_RunFrame(pipe);
            CallbackMsg message;
            while (Native.SteamAPI_ManualDispatch_GetNextCallback(pipe, out message))
            {
                try
                {
                    if (message.Callback != DownloadCallback) continue;
                    if (message.Size != Marshal.SizeOf(typeof(DownloadResult))) throw new InvalidOperationException("Unexpected download callback ABI.");
                    DownloadResult result = (DownloadResult)Marshal.PtrToStructure(message.Data, typeof(DownloadResult));
                    if (result.App != AppId || result.Id != id) continue;
                    if (result.Result != 1) throw new InvalidOperationException("Steam download failed with EResult=" + result.Result);
                    return;
                }
                finally { Native.SteamAPI_ManualDispatch_FreeLastCallback(pipe); }
            }
            if (timer.ElapsedMilliseconds - lastProgress >= 500)
            {
                ulong downloaded, total;
                if (!Native.SteamAPI_ISteamUGC_GetItemDownloadInfo(ugc, id, out downloaded, out total)) { downloaded = 0; total = 0; }
                Progress(progressPath, id, downloaded, total, "downloading");
                lastProgress = timer.ElapsedMilliseconds;
            }
            Thread.Sleep(80);
        }
        throw new TimeoutException("Steam download timed out after 15 minutes. Steam may continue its own queued download; no subscription change or cancellation is attempted.");
    }

    private static ulong ParseId(string text)
    {
        ulong id;
        if (String.IsNullOrWhiteSpace(text) || !UInt64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id == 0)
            throw new ArgumentException("Workshop ID must be a positive integer string.");
        return id;
    }
    private static string Utf8(byte[] bytes)
    {
        if (bytes == null) return "";
        int end = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }
    private static void Progress(string path, ulong id, ulong downloaded, ulong total, string status)
    {
        if (path != null) AtomicJson(path, new { id = id.ToString(CultureInfo.InvariantCulture), downloaded = downloaded, total = total, status = status });
    }
    private static void AtomicJson(string path, object value)
    {
        string directory = Path.GetDirectoryName(path);
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, Json.Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct CallbackMsg { public int User; public int Callback; public IntPtr Data; public int Size; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct ApiCompleted { public ulong Call; public int Callback; public uint Size; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct QueryCompleted
    {
        public ulong Handle; public int Result; public uint Returned; public uint Total; public byte Cached;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] Cursor;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct DownloadResult { public uint App; public ulong Id; public int Result; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct UgcDetails
    {
        public ulong Id; public int Result; public int FileType; public uint CreatorApp; public uint ConsumerApp;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 129)] public byte[] Title;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8000)] public byte[] Description;
        public ulong Owner; public uint Created; public uint Updated; public uint Added; public int Visibility;
        public byte Banned; public byte Accepted; public byte TagsTruncated;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1025)] public byte[] Tags;
        public ulong File; public ulong Preview;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 260)] public byte[] Filename;
        public int FileSize; public int PreviewSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] Url;
        public uint VotesUp; public uint VotesDown; public float Score; public uint Children; public ulong TotalFilesSize;
    }
    private static class Native
    {
        private const string Dll = "steam_api64.dll";
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SteamAPI_InitFlat([Out] byte[] error);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SteamAPI_Shutdown();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SteamAPI_SteamUGC_v020();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SteamAPI_SteamUtils_v010();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint SteamAPI_ISteamUtils_GetAppID(IntPtr self);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int SteamAPI_GetHSteamPipe();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SteamAPI_ManualDispatch_Init();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SteamAPI_ManualDispatch_RunFrame(int pipe);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ManualDispatch_GetNextCallback(int pipe, out CallbackMsg msg);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void SteamAPI_ManualDispatch_FreeLastCallback(int pipe);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ManualDispatch_GetAPICallResult(int pipe, ulong call, IntPtr result, int size, int expected, [MarshalAs(UnmanagedType.I1)] out bool failed);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong SteamAPI_ISteamUGC_CreateQueryAllUGCRequestPage(IntPtr self, int sort, int match, uint creator, uint consumer, uint page);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong SteamAPI_ISteamUGC_CreateQueryUGCDetailsRequest(IntPtr self, [In] ulong[] ids, uint count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamUGC_SetRankedByTrendDays(IntPtr self, ulong query, uint days);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamUGC_AddRequiredTag(IntPtr self, ulong query, string tag);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong SteamAPI_ISteamUGC_SendQueryUGCRequest(IntPtr self, ulong query);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamUGC_GetQueryUGCResult(IntPtr self, ulong query, uint index, out UgcDetails result);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamUGC_GetQueryUGCPreviewURL(IntPtr self, ulong query, uint index, [Out] byte[] url, uint capacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamUGC_ReleaseQueryUGCRequest(IntPtr self, ulong query);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint SteamAPI_ISteamUGC_GetItemState(IntPtr self, ulong id);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamUGC_DownloadItem(IntPtr self, ulong id, [MarshalAs(UnmanagedType.I1)] bool highPriority);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamUGC_GetItemDownloadInfo(IntPtr self, ulong id, out ulong downloaded, out ulong total);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool SteamAPI_ISteamUGC_GetItemInstallInfo(IntPtr self, ulong id, out ulong size, [Out] byte[] folder, uint capacity, out uint timestamp);
    }
}
