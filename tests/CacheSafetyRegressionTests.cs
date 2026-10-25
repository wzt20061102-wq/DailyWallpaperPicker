// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DailyWallpaperPicker
{
    public static partial class SelfTests
    {
        private static void CacheSafetyRegressionTests(List<TestResult> results,string fixtureRoot)
        {
            foreach(string position in new[] { "root", "ancestor", "leaf" })
            {
                string linkPosition=position;
                Check(results,"cache_cleanup_rejects_"+position+"_junction_without_changing_target",delegate
                {
                    string folder=Path.Combine(fixtureRoot,"cache-links-"+linkPosition);
                    string target=Path.Combine(folder,"external-target");
                    Directory.CreateDirectory(target);
                    string data=Path.Combine(folder,"data");
                    string cache=Path.Combine(data,"cache");
                    string sentinel;
                    if(linkPosition=="ancestor")
                    {
                        sentinel=Path.Combine(target,"data","cache","993001","sentinel.txt");
                        Directory.CreateDirectory(Path.GetDirectoryName(sentinel));
                        string alias=Path.Combine(folder,"linked-parent");
                        JunctionFixture.Create(alias,target);
                        data=Path.Combine(alias,"data");
                        cache=Path.Combine(data,"cache");
                    }
                    else if(linkPosition=="root")
                    {
                        sentinel=Path.Combine(target,"993001","sentinel.txt");
                        Directory.CreateDirectory(Path.GetDirectoryName(sentinel));
                        Directory.CreateDirectory(data);
                        JunctionFixture.Create(cache,target);
                    }
                    else
                    {
                        sentinel=Path.Combine(target,"sentinel.txt");
                        Directory.CreateDirectory(cache);
                        JunctionFixture.Create(Path.Combine(cache,"993001"),target);
                    }
                    File.WriteAllText(sentinel,"ORIGINAL OUTSIDE CACHE");
                    string hash=CacheStore.Hash(sentinel);
                    var bridge=new CacheRegressionBridge { Current=Path.Combine(folder,"current","project.json"),Reliable=true };
                    var controller=CacheRegressionController(data,new AppState { SeedImported=true },bridge,null);
                    Rejected(()=>controller.Cleanup().GetAwaiter().GetResult());
                    Require(File.Exists(sentinel)&&CacheStore.Hash(sentinel)==hash,"Public Cleanup crossed a linked cache path.");
                    Rejected(()=>CacheStore.SafeDelete(Path.Combine(cache,"993001"),cache));
                    Require(CacheStore.Hash(sentinel)==hash,"Direct safe deletion crossed a linked cache path.");
                    Require(bridge.ApplyCalls==0&&bridge.PreviewCalls==0,"Cleanup attempted a desktop action.");
                    return new { realJunction=true,linkPosition=linkPosition,publicCleanupRejected=true,directDeleteRejected=true,targetPreserved=true };
                });
            }
            Check(results,"cache_cleanup_normal_root_removes_only_unused_directory",delegate
            {
                string folder=Path.Combine(fixtureRoot,"cache-normal-cleanup"),data=Path.Combine(folder,"data"),cache=Path.Combine(data,"cache");
                string current=Path.Combine(cache,"993004"),unused=Path.Combine(cache,"993005");
                WriteCacheRegressionSource(current,"CURRENT");
                WriteCacheRegressionSource(unused,"UNUSED");
                string sentinel=Path.Combine(folder,"outside.txt");
                File.WriteAllText(sentinel,"OUTSIDE");
                string currentHash=CacheStore.Hash(Path.Combine(current,"scene.pkg"));
                var bridge=new CacheRegressionBridge { Current=Path.Combine(current,"scene.pkg"),Reliable=true };
                var controller=CacheRegressionController(data,new AppState { SeedImported=true },bridge,null);
                controller.Cleanup().GetAwaiter().GetResult();
                Require(!Directory.Exists(unused)&&CacheStore.Hash(Path.Combine(current,"scene.pkg"))==currentHash,"Normal cleanup failed or removed current content.");
                Require(File.ReadAllText(sentinel)=="OUTSIDE"&&bridge.ApplyCalls==0&&bridge.PreviewCalls==0,"Normal cleanup crossed ownership or changed desktop.");
                return new { unusedDeleted=true,currentPreserved=true,outsidePreserved=true };
            });
            Check(results,"cache_import_rejects_linked_root_before_creating_cache_files",delegate
            {
                string folder=Path.Combine(fixtureRoot,"import-root-link");
                string source=Path.Combine(folder,"source"),target=Path.Combine(folder,"external"),link=Path.Combine(folder,"cache");
                WriteCacheRegressionSource(source,"SOURCE");
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target,"sentinel.txt"),"UNCHANGED");
                JunctionFixture.Create(link,target);
                Rejected(()=>CacheStore.Import(source,link,"993002",1024*1024));
                Require(Directory.GetFiles(target).Length==1&&Directory.GetDirectories(target).Length==0,"Import wrote through a linked root.");
                return new { rejected=true,targetNotWritten=true };
            });
            Check(results,"cache_import_never_replaces_existing_directory_with_missing_manifest",delegate
            {
                string folder=Path.Combine(fixtureRoot,"import-existing-invalid");
                string original=Path.Combine(folder,"original"),replacement=Path.Combine(folder,"replacement"),cache=Path.Combine(folder,"cache");
                WriteCacheRegressionSource(original,"ORIGINAL");
                WriteCacheRegressionSource(replacement,"NEW");
                string project=CacheStore.Import(original,cache,"993003",1024*1024);
                string directory=Path.GetDirectoryName(project),media=Path.Combine(directory,"scene.pkg");
                string hash=CacheStore.Hash(media);
                Require(CacheStore.Import(replacement,cache,"993003",1024*1024)==project&&CacheStore.Hash(media)==hash,"Valid existing cache was not reused without replacement.");
                File.Delete(Path.Combine(directory,".verified.json"));
                Rejected(()=>CacheStore.Import(replacement,cache,"993003",1024*1024));
                Require(CacheStore.Hash(media)==hash&&!File.Exists(Path.Combine(directory,".verified.json")),"Import replaced unverified existing content.");
                Require(Directory.GetDirectories(cache).Length==1,"Import left a staging directory.");
                return new { originalPreserved=true,stagingDirectories=0 };
            });
            foreach(string damage in new[] { "missing", "malformed", "hash-mismatch" })
            {
                string mode=damage;
                Check(results,"current_cache_"+damage+"_manifest_preview_retry_preserves_displayed_files",delegate
                {
                    return RunManifestRepairCase(fixtureRoot,"current-"+mode,mode,true,true);
                });
            }
            foreach(string damage in new[] { "missing", "malformed" })
            {
                string mode=damage;
                Check(results,"unused_cache_"+damage+"_manifest_repairs_only_after_reliable_readback",delegate
                {
                    return RunManifestRepairCase(fixtureRoot,"repair-"+mode,mode,false,true);
                });
            }
            Check(results,"damaged_cache_with_unreliable_current_readback_defers_repair",delegate
            {
                return RunManifestRepairCase(fixtureRoot,"unreliable-readback","missing",false,false);
            });
        }

        private static readonly DateTime CacheRegressionNow=new DateTime(2026,10,25,15,5,0,DateTimeKind.Utc);
        private static void WriteCacheRegressionSource(string directory,string content)
        {
            Directory.CreateDirectory(directory);
            Json.Write(Path.Combine(directory,"project.json"),new { type="scene",file="scene.pkg" });
            File.WriteAllText(Path.Combine(directory,"scene.pkg"),content);
        }
        private static PickerController CacheRegressionController(string data,AppState state,CacheRegressionBridge bridge,
            Func<string,object,Action<WorkerProgress>,Task<WorkerReply>> worker)
        {
            Directory.CreateDirectory(data);
            Json.Write(Path.Combine(data,"config.json"),new PickerConfig { WallpaperExe=Path.Combine(data,"forbidden-engine.exe"),SteamBin=Path.Combine(data,"forbidden-steam"),MaxSizeMB=300,RetainCount=10 });
            Json.Write(Path.Combine(data,"state.json"),state);
            return new PickerController(data,()=>CacheRegressionNow,worker??((verb,input,progress)=>{throw new InvalidOperationException("Unexpected fake worker call");}),CancellationToken.None,bridge);
        }
        private static object RunManifestRepairCase(string fixtureRoot,string name,string damage,bool current,bool reliable)
        {
            // Atomic writes add both staging and GUID suffixes. Keep fixture names
            // short enough for the .NET Framework file APIs in a deep workspace.
            string folder=Path.Combine(fixtureRoot,"m-"+CacheStore.HashString(name).Substring(0,8)),data=Path.Combine(folder,"data");
            string oldSource=Path.Combine(folder,"source-original"),newSource=Path.Combine(folder,"source-new");
            WriteCacheRegressionSource(oldSource,"ORIGINAL CONTENT");
            WriteCacheRegressionSource(newSource,"NEW CONTENT");
            var state=new AppState { BatchId=Schedule.Due(CacheRegressionNow),PreparedBatchId=Schedule.Due(CacheRegressionNow),PresentedBatch=Schedule.Due(CacheRegressionNow),SeedImported=true };
            for(int index=0;index<3;index++)
            {
                string id=(994001+index).ToString(),project=CacheStore.Import(oldSource,Path.Combine(data,"cache"),id,1024*1024);
                state.Candidates.Add(new Candidate { Id=id,Pool=new[] {"popular","newest","classic"}[index],Tags=new[] {"Everyone","Scene","Nature"},Ready=true,Stage=DownloadStage.Ready,ProjectPath=project,CachePath=Path.GetDirectoryName(project) });
            }
            state.PreparedCandidates=state.Candidates;
            Candidate first=state.Candidates[0];
            string manifest=Path.Combine(first.CachePath,".verified.json"),media=Path.Combine(first.CachePath,"scene.pkg");
            if(damage=="missing")File.Delete(manifest);
            else if(damage=="malformed")File.WriteAllText(manifest,"{invalid JSON");
            else File.AppendAllText(media," CORRUPTION");
            string before=CacheStore.Hash(media),originalSourceHash=CacheStore.Hash(Path.Combine(oldSource,"scene.pkg"));
            var bridge=new CacheRegressionBridge { Current=current?first.ProjectPath:Path.Combine(folder,"other-current","project.json"),Reliable=reliable };
            int downloads=0;
            var controller=CacheRegressionController(data,state,bridge,(verb,input,progress)=>
            {
                Require(verb=="download"&&bridge.Reads>0,"Download ran before checking the displayed wallpaper.");
                downloads++;
                return Task.FromResult(new WorkerReply { ok=true,id=first.Id,appId=431960,installPath=newSource,subscribedBefore=false,subscribedAfter=false });
            });
            Rejected(()=>controller.PreviewAsync(first.Id).GetAwaiter().GetResult());
            Require(!controller.State.Candidates[0].Ready,"Invalid cache stayed Ready after preview validation.");
            controller.RetryAsync().GetAwaiter().GetResult();
            bool shouldRepair=!current&&reliable;
            if(shouldRepair)
            {
                Require(downloads==1&&controller.State.Candidates[0].Ready,"Unused damaged cache was not repaired.");
                Require(CacheStore.Hash(media)!=before&&CacheStore.Verify(first.CachePath)==first.ProjectPath,"Repaired cache did not validate.");
                string quarantine=Path.Combine(data,"quarantine");
                string[] saved=Directory.GetDirectories(quarantine);
                Require(saved.Length==1&&CacheStore.Hash(Path.Combine(saved[0],"scene.pkg"))==before,"Original damaged cache was not preserved in quarantine.");
            }
            else
            {
                Require(downloads==0&&!controller.State.Candidates[0].Ready,"Current or uncertain cache was downloaded/replaced.");
                Require(CacheStore.Hash(media)==before&&!Directory.Exists(Path.Combine(data,"quarantine")),"Protected cache moved or changed.");
            }
            Require(bridge.ApplyCalls==0&&bridge.PreviewCalls==0,"Validation or repair attempted a desktop action.");
            Require(CacheStore.Hash(Path.Combine(oldSource,"scene.pkg"))==originalSourceHash,"Source workshop fixture changed.");
            Require(controller.State.Choices.Count==0,"Preview/repair changed choice history.");
            return new { damage=damage,current=current,reliableReadback=reliable,downloads=downloads,readbackCalls=bridge.Reads,repaired=shouldRepair,desktopActionCalls=0,originalWorkshopPreserved=true };
        }

        private sealed class CacheRegressionBridge:WallpaperBridge
        {
            internal string Current;
            internal bool Reliable;
            internal int Reads,PreviewCalls,ApplyCalls;
            internal CacheRegressionBridge():base("forbidden-fixture-engine.exe",message=>{}) { }
            public override bool CanSafelyClean { get { return Reliable; } }
            public override Task<string> GetCurrent() { Reads++;return Task.FromResult(Current); }
            public override Task Preview(string project) { PreviewCalls++;throw new InvalidOperationException("No real preview permitted"); }
            public override Task Apply(string project) { ApplyCalls++;throw new InvalidOperationException("No desktop change permitted"); }
        }
    }
}
