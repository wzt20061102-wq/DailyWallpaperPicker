// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace DailyWallpaperPicker {
 public class Candidate {
  public string Id {get;set;} public string Title {get;set;} public string Pool {get;set;}
  public string[] Tags {get;set;} public string PreviewUrl {get;set;} public long FileSize {get;set;}
  public double Score {get;set;} public long CreatedUtc {get;set;} public long UpdatedUtc {get;set;}
  public string ThumbnailPath {get;set;} public string Status {get;set;} public string Error {get;set;}
  public long Downloaded {get;set;} public long Total {get;set;} public bool Ready {get;set;}
  public string ProjectPath {get;set;} public string CachePath {get;set;}
  public Candidate(){Tags=new string[0];Status="等待下载";}
 }
 public class Choice { public string Id {get;set;} public string Utc {get;set;} public string[] Tags {get;set;} public string CachePath {get;set;} }
 public class Shown { public string Id {get;set;} public string Date {get;set;} }
 public class AppState {
  public string BatchId {get;set;} public string Message {get;set;} public bool Paused {get;set;}
  public List<Candidate> Candidates {get;set;} public string AppliedId {get;set;}
  public string PresentedBatch {get;set;} public List<Choice> Choices {get;set;}
  public List<Shown> Seen {get;set;} public Dictionary<string,double> SeedTags {get;set;}
  public bool SeedImported {get;set;}
  public string PreparedBatchId {get;set;} public List<Candidate> PreparedCandidates {get;set;}
  public AppState(){Candidates=new List<Candidate>();PreparedCandidates=new List<Candidate>();Choices=new List<Choice>();Seen=new List<Shown>();SeedTags=new Dictionary<string,double>();Message="每天 23:00，为你准备三张新壁纸";}
 }
 public class PickerConfig {
  public string SteamBin {get;set;} public string WallpaperExe {get;set;}
  public int MaxSizeMB {get;set;} public int RetainCount {get;set;}
  public PickerConfig(){MaxSizeMB=300;RetainCount=10;}
 }
 public static class Json {
  static JavaScriptSerializer Serializer(){return new JavaScriptSerializer {MaxJsonLength=64*1024*1024,RecursionLimit=100};}
  public static string Encode(object v){return Serializer().Serialize(v);}
  public static T Decode<T>(string s){return Serializer().Deserialize<T>(s);}
  public static T Read<T>(string p){return Decode<T>(File.ReadAllText(p,Encoding.UTF8));}
  public static void Write(string p,object v){
   Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(p)));
   string t=p+"."+Guid.NewGuid().ToString("N")+".tmp";
   File.WriteAllText(t,Encode(v),new UTF8Encoding(false));
   if(File.Exists(p))File.Replace(t,p,p+".previous");else File.Move(t,p);
  }
 }
 public static class Schedule {
  public static DateTime Beijing(DateTime utc){return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc,DateTimeKind.Utc),TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"));}
  public static string Due(DateTime utc){DateTime d=Beijing(utc);return (d.TimeOfDay>=TimeSpan.FromHours(23)?d.Date:d.Date.AddDays(-1)).ToString("yyyy-MM-dd");}
  public static string Prepare(DateTime utc){DateTime d=Beijing(utc);return (d.TimeOfDay>=new TimeSpan(22,50,0)?d.Date:d.Date.AddDays(-1)).ToString("yyyy-MM-dd");}
 }
}
