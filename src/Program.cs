// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace DailyWallpaperPicker {
 public static class Program {
  static string Get(string[] a,string key,string def){int n=Array.IndexOf(a,key);return n>=0&&n+1<a.Length?a[n+1]:def;}
  [STAThread] public static int Main(string[] args){
   string data=Get(args,"--data-dir",Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data"));
   try {
    if(args.Contains("--self-test"))return SelfTests.Run(data);
    if(args.Contains("--current")){var reader=new PickerController(data);Json.Write(Path.Combine(data,"current-result.json"),new{path=reader.CurrentAsync().GetAwaiter().GetResult(),utc=DateTime.UtcNow.ToString("o")});return 0;}
    bool created;string unique=CacheStore.HashString(Path.GetFullPath(data).ToLowerInvariant());
    using(var mutex=new Mutex(true,"Local\\DailyWallpaperPicker-"+unique,out created)){
     if(!created){if(args.Contains("--prepare")||args.Contains("--cleanup")||args.Contains("--preview"))return 3;File.WriteAllText(Path.Combine(data,"show.request"),DateTime.UtcNow.ToString("o"));return 0;}
     var controller=new PickerController(data);
     if(args.Contains("--prepare")){controller.RetryAsync().GetAwaiter().GetResult();var result=controller.State;Json.Write(Path.Combine(data,"prepare-result.json"),result);string target=Schedule.Prepare(DateTime.UtcNow);var items=result.PreparedBatchId==target?result.PreparedCandidates:(result.BatchId==target?result.Candidates:null);return items!=null&&items.Count==3&&items.All(x=>x.Ready&&File.Exists(x.ProjectPath))?0:2;}
     if(args.Contains("--cleanup")){controller.Cleanup().GetAwaiter().GetResult();return 0;}
     if(args.Contains("--preview")){string id=Get(args,"--preview","");controller.PreviewAsync(id).GetAwaiter().GetResult();Json.Write(Path.Combine(data,"preview-result.json"),controller.State);return 0;}
     var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
     var window=new PickerWindow(controller);app.MainWindow=window;
     if(args.Contains("--test-scale"))window.SetTestScale(Double.Parse(Get(args,"--test-scale","1"),System.Globalization.CultureInfo.InvariantCulture));
     bool exiting=false;
     Action quitApp=()=>{if(exiting)return;exiting=true;controller.HidePreview();window.AllowClose=true;window.Close();app.Shutdown();};
     window.ExitRequested+=quitApp;
     app.DispatcherUnhandledException+=(s,e)=>{controller.Log("UI ERROR "+e.Exception);e.Handled=true;MessageBox.Show("界面遇到问题，当前壁纸不会自动改变。\n"+e.Exception.Message,"每日壁纸");};
     var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(15)};
     Func<bool> check=()=>{if(exiting)return false;string quit=Path.Combine(data,"exit.request");if(File.Exists(quit)){File.Delete(quit);quitApp();return false;}if(controller.ShouldPresent())window.ShowPicker(false);string req=Path.Combine(data,"show.request");if(File.Exists(req)){File.Delete(req);window.ShowPicker(true);}return !exiting;};
     timer.Tick+=async(s,e)=>{if(!check())return;await controller.TickAsync();check();};
     app.Startup+=async(s,e)=>{timer.Start();if(args.Contains("--show"))window.ShowPicker(false);if(!check())return;await controller.TickAsync();check();};
     app.Exit+=(s,e)=>{timer.Stop();controller.HidePreview();};
     return app.Run();
    }
   }catch(Exception ex){Directory.CreateDirectory(data);File.WriteAllText(Path.Combine(data,"fatal-error.txt"),DateTime.UtcNow.ToString("o")+"\n"+ex);if(!args.Any(a=>a.StartsWith("--"))||args.Contains("--show"))MessageBox.Show(ex.Message,"每日壁纸启动失败");return 1;}
  }
 }
}
