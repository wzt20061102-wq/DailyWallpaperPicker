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

namespace DailyWallpaperPicker {
 public class RunResult {public int ExitCode; public string Output; public string Error;}
 public static class Processes {
  public static string Quote(string s){if(s.IndexOf('"')>=0)throw new ArgumentException("参数不能含引号");return "\""+s+"\"";}
  public static async Task<RunResult> Run(string exe,string args,string cwd,int seconds){
   using(var p=new Process()){
    p.StartInfo=new ProcessStartInfo(exe,args){WorkingDirectory=cwd,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
    p.Start();var so=p.StandardOutput.ReadToEndAsync();var se=p.StandardError.ReadToEndAsync();
    var end=Task.Run(()=>p.WaitForExit(seconds*1000));
    if(!await end.ConfigureAwait(false)){try{p.Kill();}catch{}throw new TimeoutException("操作超时，稍后可以重试。");}
    return new RunResult{ExitCode=p.ExitCode,Output=await so.ConfigureAwait(false),Error=await se.ConfigureAwait(false)};
   }
  }
 }
  public class WorkerReply {public bool ok {get;set;}public string error {get;set;}public List<Candidate> items {get;set;}public string id {get;set;}public string installPath {get;set;} public int? appId {get;set;}public bool? subscribedBefore {get;set;}public bool? subscribedAfter {get;set;}}
 public class WorkerProgress {public string id {get;set;}public long downloaded {get;set;}public long total {get;set;}public string status {get;set;}}
 public class SteamClient {
  readonly PickerConfig config;readonly string root;readonly string exe;
  public SteamClient(PickerConfig c,string data){config=c;root=Path.Combine(data,"jobs");Directory.CreateDirectory(root);exe=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"SteamWorker.exe");}
  public async Task<WorkerReply> Call(string verb,object input,Action<WorkerProgress> progress){
   string id=Guid.NewGuid().ToString("N"),req=Path.Combine(root,id+".request.json"),resp=Path.Combine(root,id+".response.json"),prog=Path.Combine(root,id+".progress.json");
   var d=Json.Decode<Dictionary<string,object>>(Json.Encode(input));d["steamBin"]=config.SteamBin;d["appId"]=431960;d["progressFile"]=prog;Json.Write(req,d);
   var task=Processes.Run(exe,verb+" --request "+Processes.Quote(req)+" --response "+Processes.Quote(resp),config.SteamBin,verb=="download"?1200:180);
   while(!task.IsCompleted){await Task.WhenAny(task,Task.Delay(1000)).ConfigureAwait(false);if(progress!=null&&File.Exists(prog))try{progress(Json.Read<WorkerProgress>(prog));}catch(IOException){}catch(ArgumentException){} }
   RunResult result=await task.ConfigureAwait(false);
   if(!File.Exists(resp))throw new IOException("Steam 接口没有返回结果："+result.Error);
   WorkerReply reply=Json.Read<WorkerReply>(resp);if(!reply.ok||result.ExitCode!=0)throw new IOException("Steam："+(reply.error??result.Error));
   return reply;
  }
 }
 public static class CacheStore {
  public static string HashString(string text){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-","").Substring(0,20);}
  public static string Hash(string path){using(var sha=SHA256.Create())using(var f=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(f)).Replace("-","").ToLowerInvariant();}
  public static bool Under(string path,string root){string p=Path.GetFullPath(path),r=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;return p.StartsWith(r,StringComparison.OrdinalIgnoreCase);}
  static void NoLinks(string p){if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("缓存中检测到链接，已停止操作。");}
  public static List<string> Files(string dir){NoLinks(dir);var r=new List<string>();foreach(var f in Directory.GetFiles(dir)){NoLinks(f);r.Add(f);}foreach(var d in Directory.GetDirectories(dir)){NoLinks(d);r.AddRange(Files(d));}return r;}
  static void CopyTree(string src,string dst){NoLinks(src);Directory.CreateDirectory(dst);foreach(var f in Directory.GetFiles(src)){NoLinks(f);File.Copy(f,Path.Combine(dst,Path.GetFileName(f)),false);}foreach(var d in Directory.GetDirectories(src))CopyTree(d,Path.Combine(dst,Path.GetFileName(d)));}
  public static string Import(string src,string root,string id,long maxBytes){
   if(!id.All(Char.IsDigit))throw new IOException("无效壁纸编号");Directory.CreateDirectory(root);string dest=Path.Combine(root,id);
   if(Directory.Exists(dest)&&File.Exists(Path.Combine(dest,".verified.json")))return Verify(dest);
   string stage=Path.Combine(root,id+"-"+Guid.NewGuid().ToString("N")+".staging");
   var srcFiles=Files(src);if(srcFiles.Count==0||srcFiles.Sum(f=>new FileInfo(f).Length)>maxBytes)throw new IOException("壁纸为空或超过 300 MB。");
   try{
    CopyTree(src,stage);var dstFiles=Files(stage);if(srcFiles.Count!=dstFiles.Count)throw new IOException("复制文件数量不一致。");
    var manifest=new Dictionary<string,string>();foreach(string f in srcFiles){string relative=f.Substring(src.TrimEnd('\\').Length+1);string copy=Path.Combine(stage,relative);string hash=Hash(f);if(hash!=Hash(copy))throw new IOException("壁纸复制校验失败。");manifest.Add(relative,hash);}
    var srcDirs=Directory.GetDirectories(src,"*",SearchOption.AllDirectories).Select(p=>p.Substring(src.TrimEnd('\\').Length+1)).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToArray();
    var dstDirs=Directory.GetDirectories(stage,"*",SearchOption.AllDirectories).Select(p=>p.Substring(stage.Length+1)).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToArray();
    if(!srcDirs.SequenceEqual(dstDirs,StringComparer.OrdinalIgnoreCase))throw new IOException("壁纸目录集合不一致。");
    CheckProject(stage);Json.Write(Path.Combine(stage,".verified.json"),manifest);
    if(Directory.Exists(dest))SafeDelete(dest,root);Directory.Move(stage,dest);return Path.Combine(dest,"project.json");
   }catch{if(Directory.Exists(stage))SafeDelete(stage,root);throw;}
  }
  public static string CheckProject(string root){
   string p=Path.Combine(root,"project.json");var d=Json.Read<Dictionary<string,object>>(p);string type=d.ContainsKey("type")?Convert.ToString(d["type"]).ToLowerInvariant():"";
   if(type!="scene"&&type!="video")throw new IOException("该壁纸不是 Scene 或 Video，已跳过。");
   if(!d.ContainsKey("file"))throw new IOException("壁纸缺少主文件。");string file=Path.GetFullPath(Path.Combine(root,Convert.ToString(d["file"])));
   if(!Under(file,root))throw new IOException("壁纸主文件路径越界。");
   bool packed=type=="scene"&&File.Exists(Path.Combine(root,"scene.pkg"))&&new FileInfo(Path.Combine(root,"scene.pkg")).Length>0;
   if(!File.Exists(file)&&!packed)throw new IOException("壁纸主文件缺失。");return p;
  }
  public static string Verify(string root){
   root=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);var files=Files(root);
   var hashes=Json.Read<Dictionary<string,string>>(Path.Combine(root,".verified.json"));if(hashes==null||hashes.Count==0)throw new IOException("缓存校验记录为空。");
   var expected=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(var kv in hashes){
    if(String.IsNullOrWhiteSpace(kv.Key)||Path.IsPathRooted(kv.Key))throw new IOException("缓存校验记录含无效路径。");
    string f=Path.GetFullPath(Path.Combine(root,kv.Key));if(!Under(f,root))throw new IOException("缓存校验记录路径越界。");string relative=f.Substring(root.Length+1);
    if(relative.Equals(".verified.json",StringComparison.OrdinalIgnoreCase)||relative.Equals(".verified.json.previous",StringComparison.OrdinalIgnoreCase)||!expected.Add(f)||!File.Exists(f)||!String.Equals(Hash(f),kv.Value,StringComparison.OrdinalIgnoreCase))throw new IOException("壁纸缓存损坏，请重试下载。");
   }
   // Wallpaper Engine compiles Scene shaders beside the verified source after playback.
   // Only these narrowly named generated blobs may be added; every source file still hashes.
   var actual=new HashSet<string>(files.Where(f=>expected.Contains(f)||(!String.Equals(f,Path.Combine(root,".verified.json"),StringComparison.OrdinalIgnoreCase)&&!String.Equals(f,Path.Combine(root,".verified.json.previous"),StringComparison.OrdinalIgnoreCase)&&!Regex.IsMatch(f.Substring(root.Length+1),@"^shaders\\blobsSM[0-9]{2}\\[0-9a-f]{40}\.dxs$",RegexOptions.IgnoreCase))),StringComparer.OrdinalIgnoreCase);
   if(!actual.SetEquals(expected))throw new IOException("壁纸缓存文件集合已改变，请重试下载。");return CheckProject(root);
  }
  public static void SafeDelete(string path,string root){if(!Under(path,root))throw new IOException("拒绝清理缓存目录以外的文件。");Files(path);Directory.Delete(path,true);}
 }
 public class WallpaperBridge {
  readonly string exe;readonly Action<string> log;readonly object gate=new object();readonly string instanceId=Guid.NewGuid().ToString("N").Substring(0,8);
  readonly Dictionary<int,string> previewTitles=new Dictionary<int,string>();int previewGeneration;
  DateTime lastLiveReadUtc=DateTime.MinValue;ConfigSnapshot trustedConfig;string trustedProject;
  public string PreviewPath {get;private set;}
  public bool LastReadWasLive {get;private set;}
  public DateTime LastConfigWriteUtc {get;private set;}
  sealed class ConfigSnapshot {public string Path;public string Hash;public DateTime WriteUtc;}
  sealed class Readback {public string Path;public bool Live;public ConfigSnapshot Config;}
  const string PreviewTitle="DailyWallpaperPreview";
  [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr FindWindow(string c,string t);
  [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
  [DllImport("user32.dll")]static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
  [DllImport("user32.dll")]static extern bool ShowWindowAsync(IntPtr h,int command);
  [DllImport("user32.dll")]static extern bool IsZoomed(IntPtr h);
  public WallpaperBridge(string e,Action<string> l){exe=e;log=l;}
  static Dictionary<string,object> Child(Dictionary<string,object> parent,string key){object value;return parent!=null&&parent.TryGetValue(key,out value)?value as Dictionary<string,object>:null;}
  ConfigSnapshot ReadConfig(){
   string file=Path.Combine(Path.GetDirectoryName(exe),"config.json");
   try{for(int attempt=0;attempt<3;attempt++){
    DateTime before=File.GetLastWriteTimeUtc(file);string text=File.ReadAllText(file,Encoding.UTF8);DateTime after=File.GetLastWriteTimeUtc(file);if(before!=after)continue;
    var data=Json.Decode<Dictionary<string,object>>(text);var user=Child(data,Environment.UserName);
    if(user==null)user=data.Values.OfType<Dictionary<string,object>>().FirstOrDefault(v=>v.ContainsKey("general"));
    var monitor=Child(Child(Child(Child(user,"general"),"wallpaperconfig"),"selectedwallpapers"),"Monitor0");object value;
    if(monitor==null||!monitor.TryGetValue("file",out value)||String.IsNullOrWhiteSpace(Convert.ToString(value)))return null;
    return new ConfigSnapshot{Path=Path.GetFullPath(Convert.ToString(value)),Hash=CacheStore.HashString(text),WriteUtc=after};
   }}catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(InvalidOperationException){}return null;
  }
  static bool SameConfig(ConfigSnapshot first,ConfigSnapshot second){return first!=null&&second!=null&&first.WriteUtc==second.WriteUtc&&first.Hash==second.Hash;}
  void ForgetRead(){lock(gate){LastReadWasLive=false;LastConfigWriteUtc=DateTime.MinValue;lastLiveReadUtc=DateTime.MinValue;}}
  sealed class LiveReply {public bool ok {get;set;}public string path {get;set;}public string error {get;set;}public int exitCode {get;set;}public string source {get;set;}public string createdUtc {get;set;}}
  async Task<Readback> ReadCurrent(){
   ForgetRead();lock(gate){trustedConfig=null;trustedProject=null;}
   string helper=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"WallpaperReadback.exe");
   string jobs=Path.Combine(Path.GetTempPath(),"DailyWallpaperPicker","readback");Directory.CreateDirectory(jobs);
   string response=Path.Combine(jobs,Guid.NewGuid().ToString("N")+".json");DateTime started=DateTime.UtcNow;
   try{
    if(!File.Exists(helper))throw new IOException("实时壁纸读取组件缺失。");
    int exitCode;using(var process=new Process()){
     process.StartInfo=new ProcessStartInfo(helper,"--exe "+Processes.Quote(exe)+" --output "+Processes.Quote(response)){WorkingDirectory=Path.GetDirectoryName(helper),UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
     process.Start();if(!await Task.Run(()=>process.WaitForExit(15000)).ConfigureAwait(false)){try{process.Kill();}catch{}throw new TimeoutException("实时壁纸读取超时。");}exitCode=process.ExitCode;
    }
    if(!File.Exists(response))throw new IOException("实时壁纸读取没有返回结果。");var result=Json.Read<LiveReply>(response);DateTime created;
    if(result==null||!result.ok||exitCode!=0||result.exitCode!=0||result.source!="private-console-buffer"||String.IsNullOrWhiteSpace(result.path)||!Path.IsPathRooted(result.path)||!DateTime.TryParse(result.createdUtc,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out created)||created.ToUniversalTime()<started)throw new IOException("实时壁纸读取失败："+(result==null?"无效结果":result.error??"结果不完整或已过期"));
    string path=Path.GetFullPath(result.path);var config=ReadConfig();
    lock(gate){LastReadWasLive=true;LastConfigWriteUtc=config==null?DateTime.MinValue:config.WriteUtc;lastLiveReadUtc=DateTime.UtcNow;}
    log("getWallpaper LIVE private console: "+path);return new Readback{Path=path,Live=true,Config=config};
   }catch(Exception ex){ForgetRead();lock(gate){trustedConfig=null;trustedProject=null;}log("getWallpaper LIVE FAILED; switching and cleanup blocked: "+ex.Message);throw;}
   finally{try{if(File.Exists(response))File.Delete(response);}catch(IOException){}catch(UnauthorizedAccessException){}}
  }
  public async Task<string> GetCurrent(){return (await ReadCurrent().ConfigureAwait(false)).Path;}
  public string SavedCurrent(){var config=ReadConfig();return config==null?null:config.Path;}
  public bool CanSafelyClean {get{
   if(System.Windows.Forms.Screen.AllScreens.Length!=1)return false;
   ConfigSnapshot saved;string project;lock(gate){if(LastReadWasLive&&DateTime.UtcNow-lastLiveReadUtc<TimeSpan.FromSeconds(5))return true;saved=trustedConfig;project=trustedProject;}
   var current=ReadConfig();return SameConfig(saved,current)&&SameProject(current.Path,project);
  }}
  bool Trustworthy(Readback read){if(read.Live)return true;ConfigSnapshot saved;string project;lock(gate){saved=trustedConfig;project=trustedProject;}return SameConfig(saved,read.Config)&&SameProject(read.Path,project);}
  static bool FreshMatch(Readback read,string project,DateTime start,ConfigSnapshot before){return SameProject(read.Path,project)&&(read.Live||(read.Config!=null&&read.Config.WriteUtc>=start&&!SameConfig(before,read.Config)));}
  void RememberConfirmed(Readback read,string project,DateTime start,ConfigSnapshot before){lock(gate){trustedConfig=read.Config!=null&&read.Config.WriteUtc>=start&&!SameConfig(before,read.Config)&&SameProject(read.Config.Path,project)?read.Config:null;trustedProject=project;}}
  public static bool SameProject(string actual,string project){
   if(String.IsNullOrEmpty(actual)||String.IsNullOrEmpty(project))return false;
   try{string a=Path.GetFullPath(actual);if(String.Equals(a,Path.GetFullPath(project),StringComparison.OrdinalIgnoreCase))return true;
    if(!String.Equals(Path.GetExtension(project),".json",StringComparison.OrdinalIgnoreCase)||!File.Exists(project))return false;
    var p=Json.Read<Dictionary<string,object>>(project);
    if(p.ContainsKey("file")&&String.Equals(a,Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project),Convert.ToString(p["file"]))),StringComparison.OrdinalIgnoreCase))return true;
    string package=Path.Combine(Path.GetDirectoryName(project),"scene.pkg");
    return p.ContainsKey("type")&&String.Equals(Convert.ToString(p["type"]),"scene",StringComparison.OrdinalIgnoreCase)&&File.Exists(package)&&String.Equals(a,Path.GetFullPath(package),StringComparison.OrdinalIgnoreCase);
   }catch(IOException){return false;}catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}
  }
  public async Task Apply(string project){
   if(System.Windows.Forms.Screen.AllScreens.Length!=1)throw new IOException("当前版本仅在单屏时切换；检测到多屏，已保持原壁纸。");
   Readback old=await ReadCurrent().ConfigureAwait(false);bool canRestore=Trustworthy(old);DateTime start=DateTime.UtcNow;Exception failure=null;
   lock(gate){trustedConfig=null;trustedProject=null;LastReadWasLive=false;lastLiveReadUtc=DateTime.MinValue;}
   try{
    var r=await Processes.Run(exe,"-control openWallpaper -file "+Processes.Quote(project)+" -monitor 0",Path.GetDirectoryName(exe),30).ConfigureAwait(false);
    if(r.ExitCode!=0)throw new IOException("Wallpaper Engine 未接受切换请求。");
    for(int n=0;n<20;n++){await Task.Delay(500).ConfigureAwait(false);var got=await ReadCurrent().ConfigureAwait(false);if(FreshMatch(got,project,start,old.Config)){RememberConfirmed(got,project,start,old.Config);log("APPLY VERIFIED "+(got.Live?"live stdout: ":"fresh saved config: ")+project);return;}}
    failure=new IOException("无法确认切换成功。");
   }catch(Exception ex){failure=ex;}
   string restore=old.Path;
   if(!File.Exists(restore)){string manifest=Path.Combine(Path.GetDirectoryName(restore),"project.json");restore=File.Exists(manifest)&&SameProject(old.Path,manifest)?manifest:null;}
   if(canRestore&&!String.IsNullOrEmpty(restore)){bool restored=false;
    try{
     var before=ReadConfig();DateTime restoreStart=DateTime.UtcNow;
     var result=await Processes.Run(exe,"-control openWallpaper -file "+Processes.Quote(restore)+" -monitor 0",Path.GetDirectoryName(exe),30).ConfigureAwait(false);
     if(result.ExitCode==0)for(int n=0;n<10;n++){await Task.Delay(500).ConfigureAwait(false);var read=await ReadCurrent().ConfigureAwait(false);if(FreshMatch(read,restore,restoreStart,before)){RememberConfirmed(read,restore,restoreStart,before);log("RESTORE VERIFIED "+restore);restored=true;break;}}
    }catch(Exception ex){log("RESTORE not verified: "+ex.Message);}
    if(restored)throw new IOException("切换未通过核验；已恢复原壁纸并重新核验。",failure);
    throw new IOException("切换未通过核验；已请求恢复原壁纸，但无法确认恢复结果，请检查桌面。",failure);
   }
   throw new IOException("切换未通过核验；原壁纸只有未验证的保存记录，无法安全自动恢复，请检查桌面。",failure);
  }
  bool PreviewActive(int generation){lock(gate)return generation==previewGeneration;}
  bool OwnWindow(string title,out IntPtr handle){handle=FindWindow(null,title);if(handle==IntPtr.Zero)return false;uint pid;GetWindowThreadProcessId(handle,out pid);try{using(var p=Process.GetProcessById((int)pid))return Path.GetFullPath(p.MainModule.FileName).Equals(Path.GetFullPath(exe),StringComparison.OrdinalIgnoreCase);}catch{return false;}}
  void CloseOwnedPreview(string title){IntPtr handle;if(OwnWindow(title,out handle))PostMessage(handle,0x10,IntPtr.Zero,IntPtr.Zero);}
  async Task WaitForPreviewWindow(string title){for(int n=0;n<30;n++){IntPtr handle;if(OwnWindow(title,out handle))return;await Task.Delay(100).ConfigureAwait(false);}}
  async Task<bool> MaximizeOwnedPreview(int generation,string title){
   IntPtr expected;
   lock(gate){
    string registered;if(generation!=previewGeneration||!previewTitles.TryGetValue(generation,out registered)||registered!=title)return false;
    if(!OwnWindow(title,out expected))throw new IOException("未发现本次独立预览窗口。");
    if(!ShowWindowAsync(expected,3))throw new IOException("独立预览无法最大化，已关闭预览。"); // SW_SHOWMAXIMIZED; only the explicitly requested pop-out.
   }
   for(int n=0;n<20;n++){
    await Task.Delay(100).ConfigureAwait(false);
    lock(gate){
     string registered;if(generation!=previewGeneration||!previewTitles.TryGetValue(generation,out registered)||registered!=title)return false;
     IntPtr actual;if(!OwnWindow(title,out actual)||actual!=expected)throw new IOException("本次独立预览窗口已关闭或改变。");
     if(IsZoomed(actual)){log("PREVIEW MAXIMIZED verified=True title="+title+" handle="+actual.ToInt64());return true;}
    }
   }
   throw new IOException("独立预览最大化未能通过核验，已关闭预览。");
  }
  void EndPreview(int generation,string title){CloseOwnedPreview(title);lock(gate){previewTitles.Remove(generation);if(previewGeneration==generation)PreviewPath=null;}}
  public async Task Preview(string project){
   int generation;string title;string[] previous;
   lock(gate){generation=++previewGeneration;previous=previewTitles.Values.ToArray();previewTitles.Clear();title=PreviewTitle+"-"+instanceId+"-"+generation;previewTitles[generation]=title;PreviewPath=project;}
   foreach(var item in previous)CloseOwnedPreview(item);
   try{
    if(!PreviewActive(generation)){EndPreview(generation,title);return;}
    await Task.Run(()=>CacheStore.Verify(Path.GetDirectoryName(project))).ConfigureAwait(false);
    if(!PreviewActive(generation)){EndPreview(generation,title);return;}
    var old=await ReadCurrent().ConfigureAwait(false);if(!PreviewActive(generation)){EndPreview(generation,title);return;}
    string props="RAW~({\"volume\":0})~END";
    var r=await Processes.Run(exe,"-control openWallpaper -file "+Processes.Quote(project)+" -playInWindow "+Processes.Quote(title)+" -width 800 -height 450 -properties "+props,Path.GetDirectoryName(exe),30).ConfigureAwait(false);
    // The IPC launcher may exit before the preview window exists. A cancelled
    // request still waits for its own title so Hide cannot leave a late window open.
    await WaitForPreviewWindow(title).ConfigureAwait(false);
    if(!PreviewActive(generation)){EndPreview(generation,title);return;}
    // Scope the documented property command to this pop-out only; never mute the desktop.
    var muted=await Processes.Run(exe,"-control applyProperties -location "+Processes.Quote(title)+" -properties "+props,Path.GetDirectoryName(exe),20).ConfigureAwait(false);
    if(muted.ExitCode!=0)throw new IOException("独立预览静音设置失败，已关闭预览。");
    if(!PreviewActive(generation)){EndPreview(generation,title);return;}
    if(!await MaximizeOwnedPreview(generation,title).ConfigureAwait(false)){EndPreview(generation,title);return;}
    await Task.Delay(1200).ConfigureAwait(false);if(!PreviewActive(generation)){EndPreview(generation,title);return;}
    var now=await ReadCurrent().ConfigureAwait(false);if(!PreviewActive(generation)){EndPreview(generation,title);return;}
    bool bothLive=old.Live&&now.Live;bool configUnchanged=SameConfig(old.Config,now.Config);
    if(r.ExitCode!=0||!bothLive||!String.Equals(old.Path,now.Path,StringComparison.OrdinalIgnoreCase))throw new IOException("独立预览未通过检查，已关闭预览；请检查桌面。");
    IntPtr handle;if(!OwnWindow(title,out handle))throw new IOException("未发现本次独立预览窗口，预览已停止。");
    if(!PreviewActive(generation)){EndPreview(generation,title);return;}
    log("PREVIEW window verified; "+(configUnchanged?"saved config unchanged; ":"")+(bothLive?"live desktop unchanged: ":"live readback unavailable; saved path: ")+old.Path);
   }catch{EndPreview(generation,title);throw;}
  }
  public void HidePreview(){string[] titles;lock(gate){previewGeneration++;titles=previewTitles.Values.ToArray();previewTitles.Clear();PreviewPath=null;}foreach(var title in titles)CloseOwnedPreview(title);}
 }
}
