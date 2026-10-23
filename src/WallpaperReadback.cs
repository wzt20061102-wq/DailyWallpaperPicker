// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.

using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;

// Wallpaper Engine writes getWallpaper into its parent's console buffer even
// when stdout is redirected. Use a private hidden console, never a user's console.
internal static class WallpaperReadback {
 [StructLayout(LayoutKind.Sequential)]struct Coord {public short X,Y;public Coord(short x,short y){X=x;Y=y;}}
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool AllocConsole();
 [DllImport("kernel32.dll")]static extern bool FreeConsole();
 [DllImport("kernel32.dll")]static extern IntPtr GetConsoleWindow();
 [DllImport("user32.dll")]static extern bool ShowWindow(IntPtr window,int command);
 [DllImport("kernel32.dll")]static extern bool SetConsoleOutputCP(uint codePage);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateFile(string name,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool ReadConsoleOutputCharacter(IntPtr output,StringBuilder text,uint count,Coord position,out uint read);
 [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
 sealed class Reply {
  public bool ok {get;set;}public string path {get;set;}public string error {get;set;}public int exitCode {get;set;}
  public string source {get;set;}public string createdUtc {get;set;}public string completedUtc {get;set;}
 }
 static string Value(string[] args,string name){for(int n=0;n+1<args.Length;n++)if(args[n]==name)return args[n+1];throw new ArgumentException("Missing "+name);}
 static bool RunningEngine(string exe){
  int session=Process.GetCurrentProcess().SessionId;
  foreach(var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))using(process){try{if(process.SessionId==session&&String.Equals(Path.GetFullPath(process.MainModule.FileName),exe,StringComparison.OrdinalIgnoreCase))return true;}catch(Win32Exception){}catch(InvalidOperationException){}}
  return false;
 }
 static bool ValidPath(string value){
  if(String.IsNullOrWhiteSpace(value)||!Path.IsPathRooted(value)||value.IndexOf('\n')>=0||value.IndexOf('\r')>=0)return false;
  try{if(File.Exists(value))return true;string parent=Path.GetDirectoryName(value);return String.Equals(Path.GetFileName(value),"scene.json",StringComparison.OrdinalIgnoreCase)&&File.Exists(Path.Combine(parent,"scene.pkg"))&&File.Exists(Path.Combine(parent,"project.json"));}catch(ArgumentException){return false;}
 }
 static int Main(string[] args){
  var reply=new Reply{exitCode=-1,source="private-console-buffer",createdUtc=DateTime.UtcNow.ToString("o")};string outputFile=null;IntPtr console=IntPtr.Zero;
  try{
   string exe=Path.GetFullPath(Value(args,"--exe"));outputFile=Path.GetFullPath(Value(args,"--output"));
   if(File.Exists(outputFile))throw new IOException("Output already exists; a unique fresh output path is required.");
   if(!File.Exists(exe)||!RunningEngine(exe))throw new IOException("Wallpaper Engine is not running in this session.");
   FreeConsole();if(!AllocConsole())throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot allocate private console.");
   ShowWindow(GetConsoleWindow(),0);SetConsoleOutputCP(65001);
   console=CreateFile("CONOUT$",0xC0000000,3,IntPtr.Zero,3,0,IntPtr.Zero);if(console==new IntPtr(-1))throw new Win32Exception(Marshal.GetLastWin32Error());
   using(var process=new Process()){
    process.StartInfo=new ProcessStartInfo(exe,"-control getWallpaper -monitor 0"){WorkingDirectory=Path.GetDirectoryName(exe),UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
    process.Start();if(!process.WaitForExit(8000)){try{process.Kill();}catch{}throw new TimeoutException("getWallpaper timed out.");}reply.exitCode=process.ExitCode;
   }
   if(reply.exitCode!=0)throw new IOException("getWallpaper returned "+reply.exitCode+".");
   string previous=null;
   for(int attempt=0;attempt<40;attempt++){
    var text=new StringBuilder(32768);uint read;
    if(!ReadConsoleOutputCharacter(console,text,32768,new Coord(0,0),out read))throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot read private console.");
    string path=text.ToString().Trim(' ','\0','\r','\n','\t','"');
    if(ValidPath(path)&&String.Equals(path,previous,StringComparison.Ordinal)){reply.path=Path.GetFullPath(path);reply.ok=true;break;}
    previous=path;Thread.Sleep(50);
   }
   if(!reply.ok)throw new IOException("getWallpaper did not return a valid current wallpaper path.");
  }catch(Exception ex){reply.ok=false;reply.error=ex.Message;}
  finally{
   if(console!=IntPtr.Zero&&console!=new IntPtr(-1))CloseHandle(console);FreeConsole();reply.completedUtc=DateTime.UtcNow.ToString("o");
   if(outputFile!=null)try{using(var file=new FileStream(outputFile,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(file,new UTF8Encoding(false)))writer.Write(new JavaScriptSerializer().Serialize(reply));}catch{reply.ok=false;}
  }
  return reply.ok?0:1;
 }
}
