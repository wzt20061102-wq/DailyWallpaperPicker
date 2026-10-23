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

namespace DailyWallpaperPicker {
 public sealed class HoverPreviewLease : IDisposable {
  Action<HoverPreviewLease> release;int disposed;
  public string ItemId {get;private set;} public string BatchId {get;private set;}
  public string ProjectPath {get;private set;} public string MediaPath {get;private set;}
  public bool IsVideo {get;private set;} public bool IsScene {get{return !IsVideo;}}
  public bool IsDisposed {get{return Interlocked.CompareExchange(ref disposed,0,0)!=0;}}
  internal string CachePath {get;private set;}
  internal HoverPreviewLease(string itemId,string batchId,string project,string media,bool video,Action<HoverPreviewLease> onRelease){ItemId=itemId;BatchId=batchId;ProjectPath=project;MediaPath=media;IsVideo=video;CachePath=Path.GetDirectoryName(project);release=onRelease;}
  public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;var callback=Interlocked.Exchange(ref release,null);if(callback!=null)callback(this);}
 }
 public class PickerController {
  readonly object sync=new object();readonly SemaphoreSlim cleanupGate=new SemaphoreSlim(1,1);readonly string data,statePath,cache;
  readonly HashSet<HoverPreviewLease> hoverLeases=new HashSet<HoverPreviewLease>();
  // These parsed entries are process-local; disk metadata must match before every reuse.
  sealed class HoverVerifiedEntry {public string Fingerprint;public string Project;public string Media;public bool Video;}
  readonly Dictionary<string,HoverVerifiedEntry> hoverVerified=new Dictionary<string,HoverVerifiedEntry>(StringComparer.OrdinalIgnoreCase);
  readonly Dictionary<string,Task> hoverWarmJobs=new Dictionary<string,Task>(StringComparer.OrdinalIgnoreCase);
  [StructLayout(LayoutKind.Sequential)]struct HoverBasicInfo {public long CreationTime;public long LastAccessTime;public long LastWriteTime;public long ChangeTime;public uint Attributes;}
  [StructLayout(LayoutKind.Sequential)]struct HoverFileIdentity {public uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,VolumeSerial,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int infoClass,out HoverBasicInfo info,uint size);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetFileInformationByHandle(SafeFileHandle handle,out HoverFileIdentity info);
  readonly PickerConfig config;readonly SteamClient steam;readonly WallpaperBridge wallpaper;readonly Func<DateTime> clock;
  AppState state;bool preparing;bool acting;bool cleaning;bool cleanupPending;string activeCache;int previewEpoch;DateTime nextTry=DateTime.MinValue;DateTime nextCleanupTry=DateTime.MinValue;
  public event Action Changed;
  public bool Busy {get{lock(sync)return acting;}}
  public AppState State {get{lock(sync)return Json.Decode<AppState>(Json.Encode(state));}}
  public string DataDirectory {get{return data;}}
  DateTime UtcNow {get{return clock();}}
  public PickerController(string dataDirectory):this(dataDirectory,()=>DateTime.UtcNow){}
  internal PickerController(string dataDirectory,Func<DateTime> utcClock){
   if(utcClock==null)throw new ArgumentNullException("utcClock");clock=utcClock;
   data=Path.GetFullPath(dataDirectory);Directory.CreateDirectory(data);cache=Path.Combine(data,"cache");Directory.CreateDirectory(cache);statePath=Path.Combine(data,"state.json");
   ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
   string conf=Path.Combine(data,"config.json");config=File.Exists(conf)?Json.Read<PickerConfig>(conf):Discover();if(!File.Exists(conf))Json.Write(conf,config);
   state=File.Exists(statePath)?Json.Read<AppState>(statePath):new AppState();
   if(state.Candidates==null||state.Choices==null||state.Seen==null)throw new IOException("本地记录损坏，请保留 state.json 并使用 .previous 恢复。");
   if(state.PreparedCandidates==null)state.PreparedCandidates=new List<Candidate>();
   if(state.SeedTags==null)state.SeedTags=new Dictionary<string,double>();
   if(state.PreparedBatchId==state.BatchId&&state.Candidates.Count==3)state.PreparedCandidates=state.Candidates;
   steam=new SteamClient(config,data);wallpaper=new WallpaperBridge(config.WallpaperExe,Log);
  }
  static PickerConfig Discover(){
   var dirs=new List<string>();using(var k=Registry.CurrentUser.OpenSubKey("Software\\Valve\\Steam")){if(k!=null){string s=Convert.ToString(k.GetValue("SteamPath"));if(!String.IsNullOrEmpty(s)){dirs.Add(s);string v=Path.Combine(s,"steamapps","libraryfolders.vdf");if(File.Exists(v))foreach(Match m in Regex.Matches(File.ReadAllText(v),"\"path\"\\s+\"([^\"]+)\""))dirs.Add(m.Groups[1].Value.Replace("\\\\","\\"));}}}
   dirs.Add(@"C:\steam");foreach(var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase)){string e=Path.Combine(d,"steamapps","common","wallpaper_engine","wallpaper64.exe");if(File.Exists(e)&&File.Exists(Path.Combine(Path.GetDirectoryName(e),"bin","steam_api64.dll")))return new PickerConfig{WallpaperExe=e,SteamBin=Path.Combine(Path.GetDirectoryName(e),"bin")};}
   throw new IOException("没有找到 Wallpaper Engine。请先通过 Steam 安装正版应用。");
  }
  void Save(){Json.Write(statePath,state);}
  void Notify(){Action c=Changed;if(c!=null)foreach(Action handler in c.GetInvocationList())try{handler();}catch(Exception ex){LogSafely("UI notification failed: "+ex.Message);}}
  void LogSafely(string text){try{Log(text);}catch{}}
  public void Log(string text){lock(sync){string p=Path.Combine(data,"runtime_output.txt");if(File.Exists(p)&&new FileInfo(p).Length>4*1024*1024)File.Move(p,p+"."+DateTime.UtcNow.ToString("yyyyMMddHHmmss")+".log");File.AppendAllText(p,DateTime.UtcNow.ToString("o")+" "+text+Environment.NewLine,new UTF8Encoding(false));}}
  public void LogHover(string message){LogSafely("HOVER "+message);}
  public void SetPaused(bool value){lock(sync){state.Paused=value;state.Message=value?"每日推荐已暂停；当前壁纸不会改变。":"每天 23:00 提醒，22:50 提前准备。";Save();}Notify();}
  public bool ShouldPresent(){lock(sync){if(state.Paused)return false;PromoteIfDue();string due=Schedule.Due(UtcNow);if(state.BatchId!=due||state.PresentedBatch==due)return false;state.PresentedBatch=due;Save();return true;}}
  public async Task TickAsync(){
   await RetryCleanupIfDue().ConfigureAwait(false);
   string target=Schedule.Prepare(UtcNow);
   lock(sync){if(state.Paused)return;PromoteIfDue();if(preparing||UtcNow<nextTry)return;
    if(state.PreparedBatchId==target&&state.PreparedCandidates.Count==3&&state.PreparedCandidates.All(x=>x.Ready&&File.Exists(x.ProjectPath)))return;
    if(state.BatchId==target&&state.Candidates.Count==3&&state.Candidates.All(x=>x.Ready&&File.Exists(x.ProjectPath)))return;
    preparing=true;
   }
   try{nextTry=DateTime.MinValue;await PrepareBatch(target).ConfigureAwait(false);}
   catch(Exception ex){LogSafely("PREPARE ERROR "+ex);lock(sync){if(target==Schedule.Prepare(UtcNow)&&!state.Paused){state.Message="准备暂未完成："+Friendly(ex)+"；当前壁纸保持不变。";Save();}}nextTry=UtcNow.AddMinutes(5);Notify();}
   finally{lock(sync){preparing=false;PromoteIfDue();}Notify();}
  }
  public async Task RetryAsync(){nextTry=DateTime.MinValue;await TickAsync();}
  static string Friendly(Exception ex){string m=ex.Message??"未知错误";return m.Length>180?m.Substring(0,180):m;}
  void PromoteIfDue(){
   string due=Schedule.Due(UtcNow);bool changed=false;
   if(state.BatchId!=due){
    state.BatchId=due;state.Candidates=state.PreparedBatchId==due?state.PreparedCandidates:new List<Candidate>();state.AppliedId=null;
    state.Message=state.Candidates.Count==3?"选一张喜欢的，或暂时不选。":"正在准备三张候选，当前壁纸保持不变。";changed=true;
   }else if(state.PreparedBatchId==due&&!Object.ReferenceEquals(state.Candidates,state.PreparedCandidates)){
    state.Candidates=state.PreparedCandidates;changed=true;
   }
   if(state.PreparedBatchId!=null&&String.CompareOrdinal(state.PreparedBatchId,Schedule.Prepare(UtcNow))<0&&state.PreparedBatchId!=due){state.PreparedBatchId=null;state.PreparedCandidates=new List<Candidate>();changed=true;}
   if(changed){cleanupPending=true;nextCleanupTry=UtcNow;Save();}
  }
  List<Candidate> ForBatch(string id){if(state.BatchId==id)return state.Candidates;if(state.PreparedBatchId==id)return state.PreparedCandidates;return null;}
  bool IsPreparationCurrent(string batch){lock(sync)return !state.Paused&&batch==Schedule.Prepare(UtcNow);}
  void Update(string batch,string id,Action<Candidate> change){lock(sync){if(batch!=Schedule.Due(UtcNow)&&batch!=Schedule.Prepare(UtcNow))return;var list=ForBatch(batch);if(list==null)return;var c=list.Find(x=>x.Id==id);if(c==null)return;change(c);Save();}Notify();}
  async Task ImportSeeds(){
   lock(sync)if(state.SeedImported)return;
   string p=Path.Combine(Path.GetDirectoryName(config.WallpaperExe),"config.json");var configText=File.ReadAllText(p,Encoding.UTF8);
   var root=Json.Decode<Dictionary<string,object>>(configText);var owner=root.Values.OfType<Dictionary<string,object>>().FirstOrDefault(x=>x.ContainsKey("general"));
   var g=owner==null?null:owner["general"] as Dictionary<string,object>;
   string selected="";if(g!=null){if(g.ContainsKey("wallpaperconfigrecent"))selected+=Json.Encode(g["wallpaperconfigrecent"]);if(g.ContainsKey("wallpaperconfig"))selected+=Json.Encode(g["wallpaperconfig"]);}
   string[] ids=Regex.Matches(selected,@"431960[/\\]+(\d+)").Cast<Match>().Select(x=>x.Groups[1].Value).Distinct().Take(20).ToArray();
   if(ids.Length>0){var r=await steam.Call("query",new {ids=ids,pool="popular",limit=50},null).ConfigureAwait(false);lock(sync){foreach(var item in r.items??new List<Candidate>())foreach(string tag in RelevantTags(item.Tags)){if(!state.SeedTags.ContainsKey(tag))state.SeedTags[tag]=0;state.SeedTags[tag]+=1;}}}
   lock(sync){state.SeedImported=true;Save();}Log("Imported local wallpaper preference seed (no account data exported).");
  }
  static IEnumerable<string> RelevantTags(IEnumerable<string> tags){return (tags??new string[0]).Where(t=>!new[]{"Everyone","Scene","Video","Approved","3840 x 2160","1920 x 1080","2560 x 1440"}.Contains(t,StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase);}
  static double AspectRank(Candidate c){
   var screen=System.Windows.Forms.Screen.PrimaryScreen.Bounds;double ratio=(double)screen.Width/screen.Height;
   if((c.Tags??new string[0]).Contains("Dynamic resolution",StringComparer.OrdinalIgnoreCase))return 1;
   foreach(string t in c.Tags??new string[0]){Match m=Regex.Match(t,@"^(\d+)\s*x\s*(\d+)$",RegexOptions.IgnoreCase);if(m.Success){double w=Double.Parse(m.Groups[1].Value),h=Double.Parse(m.Groups[2].Value);if(h>0)return Math.Abs(w/h-ratio)<.06?1:0;}}return .5;
  }
  double Preference(Candidate c){double score=0;foreach(string t in RelevantTags(c.Tags)){double s;if(state.SeedTags.TryGetValue(t,out s))score+=s;foreach(var choice in state.Choices.Take(30))if((choice.Tags??new string[0]).Contains(t,StringComparer.OrdinalIgnoreCase))score+=3;}return score;}
  public static bool Eligible(Candidate c,long max){return c!=null&&!String.IsNullOrEmpty(c.Id)&&c.Id.All(Char.IsDigit)&&c.FileSize>0&&c.FileSize<=max&&(c.Tags??new string[0]).Contains("Everyone",StringComparer.OrdinalIgnoreCase)&&(c.Tags.Contains("Scene",StringComparer.OrdinalIgnoreCase)||c.Tags.Contains("Video",StringComparer.OrdinalIgnoreCase))&&!c.Tags.Any(x=>new[]{"Mature","Questionable","Application","Web","Preset"}.Contains(x,StringComparer.OrdinalIgnoreCase));}
  async Task PrepareBatch(string batch){
   if(!IsPreparationCurrent(batch))return;
   await ImportSeeds().ConfigureAwait(false);
   if(!IsPreparationCurrent(batch))return;
   List<Candidate> items;lock(sync){items=ForBatch(batch);if(items!=null)items=items.ToList();}
   if(items==null||items.Count!=3){
    items=new List<Candidate>();foreach(string pool in new[]{"popular","newest","classic"}){
     List<Candidate> eligible=new List<Candidate>();
     for(int page=1;page<=10&&eligible.Count==0;page++){
      var r=await steam.Call("query",new {pool=pool,limit=50,page=page},null).ConfigureAwait(false);
      if(!IsPreparationCurrent(batch))return;
      eligible=(r.items??new List<Candidate>()).Where(c=>Eligible(c,(long)config.MaxSizeMB*1024*1024)&&!items.Any(x=>x.Id==c.Id)).ToList();
      lock(sync){string oldest=Schedule.Beijing(UtcNow).AddDays(-90).ToString("yyyy-MM-dd");eligible=eligible.Where(c=>!state.Seen.Any(h=>h.Id==c.Id&&String.CompareOrdinal(h.Date,oldest)>=0)&&!state.Choices.Any(h=>h.Id==c.Id)).ToList();}
     }
     if(eligible.Count==0)throw new IOException("暂时没有足够的不重复候选，稍后会重试。");
     Candidate pick;if(pool=="newest")pick=eligible.OrderByDescending(AspectRank).ThenByDescending(x=>x.CreatedUtc).First();else lock(sync)pick=eligible.Select((x,i)=>new{Item=x,Rank=i,Weight=Preference(x),Aspect=AspectRank(x)}).OrderByDescending(x=>x.Aspect).ThenByDescending(x=>x.Weight).ThenBy(x=>x.Rank).First().Item;
     pick.Pool=pool;pick.Status="等待下载";pick.Total=pick.FileSize;items.Add(pick);
    }
    lock(sync){if(!IsPreparationCurrent(batch))return;state.PreparedBatchId=batch;state.PreparedCandidates=items;foreach(var c in items)state.Seen.Add(new Shown{Id=c.Id,Date=batch});state.Seen=state.Seen.Where(x=>String.CompareOrdinal(x.Date,Schedule.Beijing(UtcNow).AddDays(-90).ToString("yyyy-MM-dd"))>=0).ToList();PromoteIfDue();Save();}Notify();
   }
   foreach(var c in items){
    if(!IsPreparationCurrent(batch))return;if(c.Ready&&File.Exists(c.ProjectPath))continue;
    try{
     await Thumbnail(c,batch).ConfigureAwait(false);
     if(!IsPreparationCurrent(batch))return;
     string cached=Path.Combine(cache,c.Id);string project;
     project=null;bool corrupt=false;
     if(Directory.Exists(cached)&&File.Exists(Path.Combine(cached,".verified.json")))try{project=await Task.Run(()=>CacheStore.Verify(cached)).ConfigureAwait(false);}catch(IOException){corrupt=true;}
     if(corrupt){
      string current=await wallpaper.GetCurrent().ConfigureAwait(false);
      if(!IsPreparationCurrent(batch))return;
      await QuarantineCorruptCacheAsync(cached,c.Id,current).ConfigureAwait(false);
     }
     if(project==null){
      Update(batch,c.Id,x=>{x.Status="正在下载完整壁纸";x.Error=null;});
      var reply=await steam.Call("download",new{id=c.Id},p=>Update(batch,c.Id,x=>{x.Downloaded=p.downloaded;x.Total=p.total;x.Status="正在下载完整壁纸";})).ConfigureAwait(false);
      ValidateDownloadReply(reply,c.Id);if(!IsPreparationCurrent(batch))return;
      Update(batch,c.Id,x=>x.Status="正在校验文件");project=await ImportCacheAsync(reply.installPath,c.Id).ConfigureAwait(false);
     }
     if(!IsPreparationCurrent(batch))return;
     Update(batch,c.Id,x=>{x.ProjectPath=project;x.CachePath=Path.GetDirectoryName(project);x.Ready=true;x.Status="已就绪";x.Error=null;x.Downloaded=x.Total;});LogSafely("READY batch="+batch+" id="+c.Id+" "+project);
    }catch(Exception ex){Update(batch,c.Id,x=>{x.Ready=false;x.Status="暂未下载完成";x.Error=Friendly(ex);});LogSafely("CANDIDATE ERROR "+c.Id+" "+ex.Message);}
   }
   if(!IsPreparationCurrent(batch))return;
   lock(sync){PromoteIfDue();if(state.BatchId==batch)state.Message=state.Candidates.All(x=>x.Ready)?"三张都已准备好。选一张喜欢的，或保持现在。":"部分壁纸仍待下载，稍后会重试。";Save();}
   if(items.All(x=>x.Ready)){try{await Cleanup().ConfigureAwait(false);}catch(Exception ex){DeferCleanup(ex);}}else nextTry=UtcNow.AddMinutes(5);Notify();
  }
  internal static void ValidateDownloadReply(WorkerReply reply,string expectedId){
   if(reply==null||!reply.ok||reply.id!=expectedId||!reply.appId.HasValue||reply.appId.Value!=431960)throw new IOException("Steam 下载结果与本次壁纸不一致，已停止。");
   if(!reply.subscribedBefore.HasValue||!reply.subscribedAfter.HasValue)throw new IOException("Steam 未返回完整订阅校验结果，已停止。");
   if(reply.subscribedBefore.Value!=reply.subscribedAfter.Value)throw new IOException("Steam 订阅状态发生异常变化，已停止。");
   if(String.IsNullOrWhiteSpace(reply.installPath))throw new IOException("Steam 未返回完整壁纸位置，已停止。");
  }
  Task Thumbnail(Candidate c,string batch){return Thumbnail(c,batch,TimeSpan.FromSeconds(15));}
  // A cover is optional: failure must not stop the full wallpaper download or
  // leave the daily preparation job waiting indefinitely on an image server.
  internal async Task Thumbnail(Candidate c,string batch,TimeSpan timeout){
   string temporary=null;
   try{
    if(String.IsNullOrWhiteSpace(c.Id)||!c.Id.All(Char.IsDigit))throw new IOException("无效封面编号。");
    string dir=Path.Combine(data,"thumbnails");Directory.CreateDirectory(dir);string file=Path.Combine(dir,c.Id+".img");
    if(!File.Exists(file)&&!String.IsNullOrEmpty(c.PreviewUrl)){
     byte[] bytes=await DownloadThumbnail(c.PreviewUrl,timeout).ConfigureAwait(false);
     if(!IsPreparationCurrent(batch))return;
     temporary=Path.Combine(dir,c.Id+"-"+Guid.NewGuid().ToString("N")+".tmp");
     File.WriteAllBytes(temporary,bytes);
     if(!File.Exists(file))File.Move(temporary,file);
    }
    if(File.Exists(file))Update(batch,c.Id,x=>x.ThumbnailPath=file);
   }catch(Exception ex){LogSafely("THUMBNAIL SKIPPED batch="+batch+" id="+c.Id+" "+Friendly(ex));}
   finally{if(temporary!=null)try{if(File.Exists(temporary))File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}
  }
  internal static async Task<byte[]> DownloadThumbnail(string url,TimeSpan timeout){
   Uri uri;if(!Uri.TryCreate(url,UriKind.Absolute,out uri)||(uri.Scheme!=Uri.UriSchemeHttp&&uri.Scheme!=Uri.UriSchemeHttps))throw new IOException("封面地址仅支持 HTTP 或 HTTPS。");
   if(timeout<=TimeSpan.Zero||timeout.TotalMilliseconds>Int32.MaxValue)throw new ArgumentOutOfRangeException("timeout");
   using(var cancellation=new CancellationTokenSource())using(var deadline=new CancellationTokenSource()){
    Task elapsed=Task.Delay(timeout,deadline.Token);Task<byte[]> transfer=Task.Run(()=>ReadThumbnail(uri,cancellation.Token));
    if(await Task.WhenAny(transfer,elapsed).ConfigureAwait(false)!=transfer){
     cancellation.Cancel();
     // Abort also interrupts a pending response/body read. Observe a late fault,
     // but never let an unresponsive transport extend the public deadline.
     ObserveThumbnailFailure(transfer);
     throw new TimeoutException("封面下载超时，已保留占位图并继续下载壁纸。");
    }
    deadline.Cancel();return await transfer.ConfigureAwait(false);
   }
  }
  static void ObserveThumbnailFailure(Task transfer){transfer.ContinueWith(t=>{var observed=t.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);}
  static async Task<byte[]> ReadThumbnail(Uri uri,CancellationToken cancellation){
   const int limit=10*1024*1024;
   var request=WebRequest.CreateHttp(uri);
   using(cancellation.Register(()=>request.Abort())){
    using(var response=(HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false)){
     if(response.ResponseUri.Scheme!=Uri.UriSchemeHttp&&response.ResponseUri.Scheme!=Uri.UriSchemeHttps)throw new IOException("封面重定向地址无效。");
     if(response.ContentLength>limit)throw new IOException("预览图超过 10 MB。");
     using(var input=response.GetResponseStream())using(var output=new MemoryStream()){
      byte[] buffer=new byte[16*1024];int total=0;
      while(true){
       cancellation.ThrowIfCancellationRequested();
       int count=await input.ReadAsync(buffer,0,Math.Min(buffer.Length,limit-total+1),cancellation).ConfigureAwait(false);
       if(count==0)break;total+=count;if(total>limit)throw new IOException("预览图超过 10 MB。");
       output.Write(buffer,0,count);
      }
      cancellation.ThrowIfCancellationRequested();if(total==0)throw new IOException("封面内容为空。");return output.ToArray();
     }
    }
   }
  }
  static bool SamePath(string first,string second){return !String.IsNullOrWhiteSpace(first)&&!String.IsNullOrWhiteSpace(second)&&String.Equals(Path.GetFullPath(first).TrimEnd('\\','/'),Path.GetFullPath(second).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase);}
  string OwnedCachePath(string id,string directory){
   if(String.IsNullOrWhiteSpace(id)||!id.All(Char.IsDigit)||String.IsNullOrWhiteSpace(directory))throw new IOException("无效壁纸缓存位置。");
   string expected=Path.Combine(cache,id);if(!SamePath(expected,directory)||!CacheStore.Under(expected,cache))throw new IOException("悬停预览只能读取工具自己的缓存。");
   if((File.GetAttributes(cache)&FileAttributes.ReparsePoint)!=0)throw new IOException("缓存根目录不能是链接。");
   return Path.GetFullPath(expected);
  }
  bool CacheIsInUse(string directory){
   // Caller holds sync; cleanupGate prevents a verifier/import from racing this check.
   return (activeCache!=null&&SamePath(activeCache,directory))||hoverLeases.Any(x=>SamePath(x.CachePath,directory))||(!String.IsNullOrEmpty(wallpaper.PreviewPath)&&CacheStore.Under(wallpaper.PreviewPath,directory));
  }
  internal async Task QuarantineCorruptCacheAsync(string cached,string id,string current){
   await cleanupGate.WaitAsync().ConfigureAwait(false);
   try{lock(sync){
    cached=OwnedCachePath(id,cached);
    if(String.IsNullOrWhiteSpace(current)||CacheStore.Under(current,cached)||CacheIsInUse(cached))throw new IOException("缓存正在使用或预览中，暂缓重新下载。");
    string quarantine=Path.Combine(data,"quarantine");Directory.CreateDirectory(quarantine);CacheStore.Files(cached);
    hoverVerified.Remove(cached);Directory.Move(cached,Path.Combine(quarantine,id+"-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+"-"+Guid.NewGuid().ToString("N").Substring(0,8)));LogSafely("Corrupt own cache quarantined "+id);
   }}finally{cleanupGate.Release();}
  }
  async Task<string> ImportCacheAsync(string source,string id){
   await cleanupGate.WaitAsync().ConfigureAwait(false);
   try{lock(sync){string owned=OwnedCachePath(id,Path.Combine(cache,id));if(CacheIsInUse(owned))throw new IOException("缓存正在预览，稍后重试下载。");hoverVerified.Remove(owned);}
    return await Task.Run(()=>CacheStore.Import(source,cache,id,(long)config.MaxSizeMB*1024*1024)).ConfigureAwait(false);
   }finally{cleanupGate.Release();}
  }
  public async Task<HoverPreviewLease> AcquireHoverPreviewAsync(string itemId){
   // Hold the same gate as deletion, quarantine and import while hashing. Once verified,
   // register a lease before releasing the gate so there is no unprotected hand-off.
   var elapsed=Stopwatch.StartNew();await cleanupGate.WaitAsync().ConfigureAwait(false);long gateWaitMs=elapsed.ElapsedMilliseconds;
   try{
    Candidate candidate;string batch;
    lock(sync){
     if(acting)throw new InvalidOperationException("正在处理壁纸，请稍后预览。");
     if(state.BatchId!=Schedule.Due(UtcNow))throw new IOException("候选已更新，请重新选择。");
     var current=state.Candidates.FirstOrDefault(x=>x.Id==itemId&&x.Ready);if(current==null)throw new IOException("这张壁纸尚未准备好。");
     candidate=Json.Decode<Candidate>(Json.Encode(current));batch=state.BatchId;
    }
    string directory=OwnedCachePath(candidate.Id,candidate.CachePath);
    if(!SamePath(candidate.ProjectPath,Path.Combine(directory,"project.json")))throw new IOException("壁纸项目文件与缓存不一致。");
    var lease=await Task.Run(()=>{
     var verifyTimer=Stopwatch.StartNew();string before=HoverFingerprint(directory);HoverVerifiedEntry entry;bool cacheHit=hoverVerified.TryGetValue(directory,out entry)&&entry.Fingerprint==before;
     if(!cacheHit){
     hoverVerified.Remove(directory);
     string project=CacheStore.Verify(directory);var document=Json.Read<Dictionary<string,object>>(project);object typeValue,fileValue;
     if(!document.TryGetValue("type",out typeValue)||!(typeValue is string))throw new IOException("壁纸类型无效。");
     string type=((string)typeValue).ToLowerInvariant();if(type!="video"&&type!="scene")throw new IOException("仅支持 Scene 或 Video 壁纸。");
     if(!document.TryGetValue("file",out fileValue)||!(fileValue is string)||String.IsNullOrWhiteSpace((string)fileValue))throw new IOException("壁纸缺少主文件。");
     string relative=(string)fileValue;
     if(Path.IsPathRooted(relative)||relative.IndexOf(':')>=0)throw new IOException("壁纸主文件必须是缓存内的相对路径。");
     string media=Path.GetFullPath(Path.Combine(directory,relative));if(!CacheStore.Under(media,directory))throw new IOException("壁纸主文件路径越界。");
     if(!File.Exists(media)&&type=="scene")media=Path.Combine(directory,"scene.pkg");
     if(!File.Exists(media)||new FileInfo(media).Length==0)throw new IOException("壁纸主文件缺失或为空。");
     var manifest=Json.Read<Dictionary<string,string>>(Path.Combine(directory,".verified.json"));
     if(!manifest.Keys.Any(x=>SamePath(Path.Combine(directory,x),media)))throw new IOException("壁纸主文件没有经过副本校验。");
     entry=new HoverVerifiedEntry{Project=project,Media=media,Video=type=="video",Fingerprint=before};
     }
     string after=HoverFingerprint(directory);if(before!=after){hoverVerified.Remove(directory);throw new IOException("校验期间壁纸文件发生变化，请稍后重试。");}
     hoverVerified[directory]=entry;
     LogHover("verified id="+itemId+" cacheHit="+(cacheHit?"true":"false")+" verifyMs="+verifyTimer.ElapsedMilliseconds);
     return new HoverPreviewLease(candidate.Id,batch,entry.Project,entry.Media,entry.Video,ReleaseHoverPreview);
    }).ConfigureAwait(false);
    lock(sync){
     if(acting||!HoverCandidateMatches(lease))throw new IOException("候选已更新，本次悬停已取消。");
     hoverLeases.Add(lease);
    }
    LogHover("lease-acquired batch="+batch+" id="+itemId+" type="+(lease.IsVideo?"video":"scene")+" gateWaitMs="+gateWaitMs+" totalMs="+elapsed.ElapsedMilliseconds);return lease;
   }catch(Exception ex){LogHover("lease-failed id="+itemId+" gateWaitMs="+gateWaitMs+" totalMs="+elapsed.ElapsedMilliseconds+" "+ex.Message);if(ex is ArgumentException||ex is NotSupportedException)throw new IOException("壁纸内容或文件路径格式无效。",ex);throw;}
   finally{cleanupGate.Release();}
  }
  static string HoverFingerprint(string directory){
   var records=new SortedDictionary<string,string>(StringComparer.Ordinal);HoverFingerprintDirectory(new DirectoryInfo(directory),directory.Length,records);return Json.Encode(records);
  }
  static void HoverFingerprintDirectory(DirectoryInfo directory,int prefixLength,SortedDictionary<string,string> records){
   directory.Refresh();if(!directory.Exists||(directory.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("壁纸缓存目录不存在或包含链接。");
   records.Add("D:"+directory.FullName.Substring(prefixLength),HoverFileStamp(directory.FullName));
   foreach(var info in directory.GetFileSystemInfos()){
    info.Refresh();if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("壁纸缓存包含链接，已停止预览。");
    var child=info as DirectoryInfo;if(child!=null){HoverFingerprintDirectory(child,prefixLength,records);continue;}
    var file=info as FileInfo;if(file==null||!file.Exists)throw new IOException("壁纸文件集合发生变化。");
    records.Add("F:"+file.FullName.Substring(prefixLength),file.Length+"|"+HoverFileStamp(file.FullName));
   }
  }
  static string HoverFileStamp(string path){
   // Zero requested data access reads metadata even while a decoder holds the file.
   // OPEN_REPARSE_POINT prevents following a link; BACKUP_SEMANTICS also opens directories.
   using(var handle=CreateFile(path,0,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero)){
    HoverBasicInfo basic;HoverFileIdentity identity;
    if(handle.IsInvalid||!GetFileInformationByHandleEx(handle,0,out basic,(uint)Marshal.SizeOf(typeof(HoverBasicInfo)))||!GetFileInformationByHandle(handle,out identity))throw new IOException("无法确认壁纸缓存文件身份。",new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
    if((basic.Attributes&(uint)FileAttributes.ReparsePoint)!=0)throw new IOException("壁纸缓存包含链接，已停止预览。");
    return identity.VolumeSerial+"|"+identity.IndexHigh+"|"+identity.IndexLow+"|"+identity.SizeHigh+"|"+identity.SizeLow+"|"+basic.CreationTime+"|"+basic.LastWriteTime+"|"+basic.ChangeTime+"|"+basic.Attributes;
   }
  }
  public Task WarmHoverPreviewsAsync(){
   var jobs=new List<Task>();lock(sync){
    string batch=state.BatchId;if(acting||batch!=Schedule.Due(UtcNow))return Task.FromResult(0);
    foreach(string key in hoverWarmJobs.Keys.Where(x=>!x.StartsWith(batch+"|",StringComparison.Ordinal)).ToArray())hoverWarmJobs.Remove(key);
    foreach(var candidate in state.Candidates.Where(x=>x.Ready&&(x.Tags??new string[0]).Contains("Video",StringComparer.OrdinalIgnoreCase)&&!(x.Tags??new string[0]).Contains("Scene",StringComparer.OrdinalIgnoreCase))){
     string id=candidate.Id,key=batch+"|"+id+"|"+candidate.ProjectPath;Task job;
     if(!hoverWarmJobs.TryGetValue(key,out job)){
      job=Task.Run(async()=>{
       try{lock(sync){if(state.BatchId!=batch||batch!=Schedule.Due(UtcNow)||acting){hoverWarmJobs.Remove(key);return;}}
        using(var lease=await AcquireHoverPreviewAsync(id).ConfigureAwait(false)){LogHover("warm-ready batch="+batch+" id="+id);}
       }catch(Exception ex){lock(sync)hoverWarmJobs.Remove(key);LogHover("warm-failed batch="+batch+" id="+id+" "+ex.Message);}
      });hoverWarmJobs.Add(key,job);
     }
     jobs.Add(job);
    }
   }return Task.WhenAll(jobs);
  }
  bool HoverCandidateMatches(HoverPreviewLease lease){
   return lease.BatchId==state.BatchId&&lease.BatchId==Schedule.Due(UtcNow)&&state.Candidates.Any(x=>x.Id==lease.ItemId&&x.Ready&&SamePath(x.ProjectPath,lease.ProjectPath)&&SamePath(x.CachePath,lease.CachePath));
  }
  public bool IsHoverPreviewCurrent(HoverPreviewLease lease){lock(sync)return lease!=null&&!lease.IsDisposed&&!acting&&hoverLeases.Contains(lease)&&HoverCandidateMatches(lease);}
  void ReleaseHoverPreview(HoverPreviewLease lease){lock(sync){if(!hoverLeases.Remove(lease))return;if(!HoverCandidateMatches(lease)){cleanupPending=true;nextCleanupTry=UtcNow;}}LogHover("lease-released batch="+lease.BatchId+" id="+lease.ItemId);}
  public async Task PreviewAsync(string itemId){
   Candidate c;string batch;int epoch;lock(sync){if(acting)throw new InvalidOperationException("正在处理上一张壁纸，请稍等。");if(state.BatchId!=Schedule.Due(UtcNow))throw new IOException("候选已更新，请重新选择。");c=state.Candidates.FirstOrDefault(x=>x.Id==itemId&&x.Ready);if(c==null)throw new IOException("这张壁纸尚未准备好，请稍后重试。");batch=state.BatchId;acting=true;activeCache=c.CachePath;epoch=previewEpoch;}Notify();
   try{
    await VerifyCandidate(c,batch).ConfigureAwait(false);
    lock(sync){if(epoch!=previewEpoch)return;if(batch!=state.BatchId||batch!=Schedule.Due(UtcNow))throw new IOException("候选已更新，请重新选择。");}
    await wallpaper.Preview(c.ProjectPath).ConfigureAwait(false);
    lock(sync){if(epoch!=previewEpoch||batch!=state.BatchId||batch!=Schedule.Due(UtcNow))wallpaper.HidePreview();else{state.Message="独立预览已打开；桌面保持不变。";Save();}}
   }
   catch(Exception ex){lock(sync){if(batch==state.BatchId){state.Message="预览未打开："+Friendly(ex);try{Save();}catch(Exception saveError){LogSafely("STATE SAVE ERROR "+saveError.Message);}}}LogSafely("PREVIEW ERROR "+ex);throw;}
   finally{lock(sync){acting=false;activeCache=null;}Notify();}
  }
  public async Task ApplyAsync(string itemId){
   Candidate c;string batch;lock(sync){if(acting)throw new InvalidOperationException("正在处理上一张壁纸，请稍等。");if(state.BatchId!=Schedule.Due(UtcNow))throw new IOException("候选已更新，请重新选择。");c=state.Candidates.FirstOrDefault(x=>x.Id==itemId&&x.Ready);if(c==null)throw new IOException("这张壁纸尚未准备好，请稍后重试。");batch=state.BatchId;acting=true;activeCache=c.CachePath;state.Message="正在切换，请稍等…";}Notify();
   try{
    await VerifyCandidate(c,batch).ConfigureAwait(false);
    lock(sync)if(state.BatchId!=batch||batch!=Schedule.Due(UtcNow))throw new IOException("候选已更新，请重新选择。");
    wallpaper.HidePreview();await wallpaper.Apply(c.ProjectPath).ConfigureAwait(false);
   }catch(Exception ex){lock(sync){if(state.BatchId==batch){state.Message="未能完成切换："+Friendly(ex);try{Save();}catch(Exception saveError){LogSafely("STATE SAVE ERROR "+saveError.Message);}}}LogSafely("APPLY ERROR "+ex);lock(sync){acting=false;activeCache=null;}Notify();throw;}
   // The desktop switch has already been read back successfully. Later housekeeping
   // must never turn that confirmed outcome into an apparent failed switch.
   try{await RecordConfirmedChoiceAsync(c,batch,Cleanup).ConfigureAwait(false);}
   finally{lock(sync){acting=false;activeCache=null;}Notify();}
  }
  internal async Task RecordConfirmedChoiceAsync(Candidate c,string batch,Func<Task> cleanup){
   bool saveFailed=false;lock(sync){
    state.Choices.RemoveAll(x=>x.Id==c.Id);state.Choices.Insert(0,new Choice{Id=c.Id,Utc=UtcNow.ToString("o"),Tags=c.Tags,CachePath=c.CachePath});state.Choices=state.Choices.Take(1000).ToList();
    if(state.BatchId==batch){state.AppliedId=c.Id;state.Message="已换上："+c.Title;}
    try{Save();}catch(Exception ex){saveFailed=true;if(state.BatchId==batch)state.Message="已换上："+c.Title+"；选择记录暂未保存。";LogSafely("CONFIRMED CHOICE SAVE ERROR "+ex);}
   }
   LogSafely("USER CHOICE "+c.Id);
   try{await cleanup().ConfigureAwait(false);}
   catch(Exception ex){DeferCleanup(ex);lock(sync){if(state.BatchId==batch){state.Message="已换上："+c.Title+"；旧缓存暂未清理，将稍后重试。"+(saveFailed?"选择记录暂未保存。":"");try{Save();}catch(Exception saveError){LogSafely("STATE SAVE ERROR "+saveError.Message);}}}}
  }
  async Task VerifyCandidate(Candidate candidate,string batch){
   try{await Task.Run(()=>CacheStore.Verify(candidate.CachePath)).ConfigureAwait(false);}
   catch(IOException ex){Update(batch,candidate.Id,c=>{c.Ready=false;c.Status="缓存校验失败，请点重试";c.Error=ex.Message;});throw;}
  }
  void DeferCleanup(Exception ex){lock(sync){cleanupPending=true;nextCleanupTry=UtcNow.AddMinutes(5);}LogSafely("CACHE CLEAN deferred: "+ex.Message);}
  async Task RetryCleanupIfDue(){
   lock(sync){if(state.Paused||preparing||acting||cleaning||!cleanupPending||UtcNow<nextCleanupTry)return;cleaning=true;}
   try{await Cleanup().ConfigureAwait(false);}catch(Exception ex){DeferCleanup(ex);}finally{lock(sync)cleaning=false;}
  }
  public void HidePreview(){lock(sync)previewEpoch++;wallpaper.HidePreview();}
  internal string[] FindCleanupCandidates(string current,string preview){
   if(String.IsNullOrWhiteSpace(current))throw new IOException("当前壁纸位置未知，暂缓清理。");
   HashSet<string> keep;lock(sync){keep=new HashSet<string>(state.Choices.Take(config.RetainCount).Select(x=>x.Id));foreach(var x in state.Candidates.Concat(state.PreparedCandidates))keep.Add(x.Id);if(activeCache!=null)keep.Add(Path.GetFileName(activeCache));foreach(var lease in hoverLeases)keep.Add(lease.ItemId);}
   return Directory.GetDirectories(cache).Where(dir=>{string id=Path.GetFileName(dir);return id.Length>0&&id.All(Char.IsDigit)&&!keep.Contains(id)&&!CacheStore.Under(current,dir)&&(String.IsNullOrEmpty(preview)||!CacheStore.Under(preview,dir));}).ToArray();
  }
  public async Task Cleanup(){
   lock(sync){if(acting){cleanupPending=true;nextCleanupTry=UtcNow;return;}}
   await cleanupGate.WaitAsync().ConfigureAwait(false);
   try{
    string current=await wallpaper.GetCurrent().ConfigureAwait(false);if(!wallpaper.CanSafelyClean)throw new IOException("未取得当前壁纸的实时结果，暂缓清理。");
    lock(sync){
     if(acting){cleanupPending=true;nextCleanupTry=UtcNow;return;}
     foreach(var dir in FindCleanupCandidates(current,wallpaper.PreviewPath)){string id=Path.GetFileName(dir);hoverVerified.Remove(dir);CacheStore.SafeDelete(dir,cache);LogSafely("CACHE CLEAN "+id);}
     cleanupPending=false;nextCleanupTry=DateTime.MinValue;
    }
   }finally{cleanupGate.Release();}
  }
  public async Task<string> CurrentAsync(){return await wallpaper.GetCurrent();}
 }
}
