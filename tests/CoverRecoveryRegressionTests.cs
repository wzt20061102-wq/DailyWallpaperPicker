// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace DailyWallpaperPicker
{
    public static partial class SelfTests
    {
        private static void CoverRecoveryRegressionTests(List<TestResult> results,string fixtureRoot)
        {
            Check(results,"thumbnail_http_success_non_image_is_rejected",delegate
            {
                using(var server=new RecoveryCoverServer(n=>Encoding.ASCII.GetBytes("<html>server error</html>"),n=>200))
                {
                    object result=ThumbnailFailureCase(fixtureRoot,"cover-invalid-success",server.Url,TimeSpan.FromSeconds(3),"Cover");
                    Require(server.Requests==1,"Invalid-image fixture was not requested.");
                    return result;
                }
            });
            Check(results,"thumbnail_legacy_invalid_cache_is_replaced_with_decodable_image",delegate
            {
                DateTime now=new DateTime(2026,10,25,15,5,0,DateTimeKind.Utc);
                byte[] body=ValidHttpCover();
                using(var server=new RecoveryCoverServer(n=>body,n=>200))
                {
                    var candidate=new Candidate {Id="882001",PreviewUrl=server.Url};
                    var state=new AppState {BatchId=Schedule.Due(now),SeedImported=true};state.Candidates.Add(candidate);
                    var controller=FixtureController(fixtureRoot,"cover-legacy-invalid",state,()=>now);
                    string thumbnails=Path.Combine(controller.DataDirectory,"thumbnails");Directory.CreateDirectory(thumbnails);
                    string file=Path.Combine(thumbnails,candidate.Id+".img");File.WriteAllText(file,"<html>old bad cache</html>");
                    controller.Thumbnail(candidate,state.BatchId,TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                    string actual=controller.State.Candidates.Single().ThumbnailPath;
                    Require(actual==file&&File.ReadAllBytes(actual).SequenceEqual(body),"Legacy invalid cache was reused instead of replaced.");
                    Require(CoverAnimation.Load(actual).Frames.Count>0,"Published replacement cannot be decoded.");
                    controller.Thumbnail(controller.State.Candidates.Single(),state.BatchId,TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                    Require(server.Requests==1,"Validated cached cover was unnecessarily downloaded again.");
                    Require(Directory.GetFiles(thumbnails,"*.tmp").Length==0,"Cover replacement left temporary data.");
                    return new {requests=server.Requests,legacyInvalidRepaired=true,imageDecoded=true,temporaryFiles=0};
                }
            });
            Check(results,"ready_wallpaper_cover_failure_recovers_on_explicit_retry",delegate
            {
                return CoverReadyRecoveryCase(fixtureRoot,"cover-manual-retry","retry");
            });
            Check(results,"ready_wallpaper_cover_failure_recovers_after_restart",delegate
            {
                return CoverReadyRecoveryCase(fixtureRoot,"cover-restart","restart");
            });
            Check(results,"ready_wallpaper_cover_backoff_then_automatic_recovery",delegate
            {
                return CoverReadyRecoveryCase(fixtureRoot,"cover-auto-retry","clock");
            });
            Check(results,"current_ready_cover_recovers_during_next_batch_prefetch",delegate
            {
                DateTime now=new DateTime(2026,10,25,14,55,0,DateTimeKind.Utc);
                using(var server=new RecoveryCoverServer(n=>ValidHttpCover(),n=>200))
                {
                    string name="cover-current-prefetch";
                    AppState state=ReadyCoverState(fixtureRoot,name,now,server.Url);
                    state.PreparedBatchId=Schedule.Prepare(now);
                    state.PreparedCandidates=state.Candidates.Select((c,i)=>new Candidate {Id=(884001+i).ToString(),Ready=true,ProjectPath=c.ProjectPath,Tags=new[]{"Everyone","Scene"}}).ToList();
                    int calls=0;
                    var controller=FixtureController(fixtureRoot,name,state,()=>now,(v,i,p)=>{calls++;throw new IOException("Steam is forbidden in ready-cover recovery.");});
                    controller.TickAsync().GetAwaiter().GetResult();
                    Require(controller.State.Candidates[0].ThumbnailPath!=null&&server.Requests==1,"The still-current ready batch did not recover its missing cover during prefetch.");
                    Require(calls==0&&controller.State.PreparedBatchId==Schedule.Prepare(now),"Cover recovery changed preparation or called Steam.");
                    return new {currentBatch=state.BatchId,preparedBatch=state.PreparedBatchId,requests=server.Requests,steamCalls=calls};
                }
            });
            Check(results,"thumbnail_late_previous_day_response_is_not_published",delegate
            {
                DateTime now=new DateTime(2026,10,25,15,5,0,DateTimeKind.Utc);
                using(var server=new RecoveryCoverServer(n=>{now=now.AddDays(1);return ValidHttpCover();},n=>200))
                {
                    var candidate=new Candidate {Id="882002",PreviewUrl=server.Url};
                    var state=new AppState {BatchId=Schedule.Due(now),SeedImported=true};state.Candidates.Add(candidate);
                    var controller=FixtureController(fixtureRoot,"cover-late-batch",state,()=>now);
                    controller.Thumbnail(candidate,state.BatchId,TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                    Require(controller.State.Candidates[0].ThumbnailPath==null,"Late response changed the old candidate.");
                    Require(Directory.GetFiles(Path.Combine(controller.DataDirectory,"thumbnails")).Length==0,"Late response published cached bytes.");
                    return new {requests=server.Requests,staleResultDiscarded=true};
                }
            });
            Check(results,"card_cover_transient_lock_retries_same_identity_without_decode_storm",delegate
            {
                string file=Path.Combine(fixtureRoot,"card-cover-lock.gif");
                WriteCoverGif(file,8,8,new[]{CoverFrame(8,8,2,4),CoverFrame(8,8,3,7)});
                long originalTime=File.GetLastWriteTimeUtc(file).Ticks;
                long originalLength=new FileInfo(file).Length;
                var context=SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                try
                {
                    var owner=new RecoveryCardActions();
                    using(var card=new CardView(owner,0))
                    {
                        var candidate=new Candidate {Id="882003",Title="Generated cover",ThumbnailPath=file,Tags=new[]{"Scene"},Ready=true};
                        using(var held=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                        {
                            card.Update(candidate,false);
                            Require(PumpCoverDispatcher(()=>CoverRecoveryField<TextBlock>(card,"_placeholderText").Text=="封面暂不可用",3000),"Locked cover did not reach the expected temporary failure.");
                            int failed=CoverRecoveryField<int>(card,"_coverGeneration");
                            for(int i=0;i<20;i++)card.Update(candidate,false);
                            Require(CoverRecoveryField<int>(card,"_coverGeneration")==failed,"Repeated renders bypassed decode retry backoff.");
                        }
                        PumpCoverDispatcher(()=>false,2100);
                        card.Update(candidate,false);
                        Require(PumpCoverDispatcher(()=>CoverRecoveryField<Image>(card,"_image").Source!=null,3000),"Same card did not recover once the temporary file lock was released.");
                        Require(card.HasAnimatedCover,"Recovered GIF lost hover animation frames.");
                        Require(File.GetLastWriteTimeUtc(file).Ticks==originalTime&&new FileInfo(file).Length==originalLength,"Test changed the file identity instead of exercising retry.");
                        return new {sameCardRecovered=true,sameFileIdentity=true,animatedCoverPreserved=true,realPointerTest=false};
                    }
                }
                finally {SynchronizationContext.SetSynchronizationContext(context);}
            });
        }

        private static AppState ReadyCoverState(string fixtureRoot,string name,DateTime now,string previewUrl)
        {
            string root=Path.Combine(fixtureRoot,name+"-media");Directory.CreateDirectory(root);
            string project=Path.Combine(root,"project.json");Json.Write(project,new{type="scene",file="scene.pkg"});
            File.WriteAllText(Path.Combine(root,"scene.pkg"),"inert generated scene fixture");
            var state=new AppState {BatchId=Schedule.Due(now),SeedImported=true};
            for(int i=0;i<3;i++)state.Candidates.Add(new Candidate {Id=(883001+i).ToString(),Ready=true,ProjectPath=project,Tags=new[]{"Everyone","Scene"},PreviewUrl=i==0?previewUrl:null});
            return state;
        }

        private static object CoverReadyRecoveryCase(string fixtureRoot,string name,string mode)
        {
            DateTime now=new DateTime(2026,10,25,15,5,0,DateTimeKind.Utc);
            using(var server=new RecoveryCoverServer(n=>ValidHttpCover(),n=>n==1?404:200))
            {
                AppState state=ReadyCoverState(fixtureRoot,name,now,server.Url);
                int calls=0;
                Func<string,object,Action<WorkerProgress>,Task<WorkerReply>> worker=(v,i,p)=>{calls++;throw new IOException("Steam is forbidden in ready-cover recovery.");};
                var controller=FixtureController(fixtureRoot,name,state,()=>now,worker);
                controller.TickAsync().GetAwaiter().GetResult();
                Require(server.Requests==1&&controller.State.Candidates[0].ThumbnailPath==null&&controller.State.Candidates.All(c=>c.Ready),"Optional failure incorrectly changed readiness or did not call HTTP.");
                if(mode=="retry")controller.RetryAsync().GetAwaiter().GetResult();
                else if(mode=="restart")
                {
                    controller=new PickerController(controller.DataDirectory,()=>now,worker);
                    controller.TickAsync().GetAwaiter().GetResult();
                }
                else
                {
                    for(int i=0;i<5;i++)controller.TickAsync().GetAwaiter().GetResult();
                    Require(server.Requests==1,"Automatic ticks retried before network backoff expired.");
                    now=now.AddMinutes(5);
                    controller.TickAsync().GetAwaiter().GetResult();
                }
                string file=controller.State.Candidates[0].ThumbnailPath;
                Require(file!=null&&File.Exists(file)&&CoverAnimation.Load(file).Frames.Count>0,"Ready wallpaper cover did not recover.");
                Require(server.Requests==2&&calls==0&&controller.State.Candidates.All(c=>c.Ready),"Recovery duplicated wallpaper preparation or failed request bounds.");
                return new {mode=mode,httpRequests=server.Requests,steamCalls=calls,allWallpapersRemainReady=true,coverDecoded=true};
            }
        }

        private sealed class RecoveryCoverServer:IDisposable
        {
            readonly TcpListener listener=new TcpListener(IPAddress.Loopback,0);
            readonly Task worker;
            volatile bool stopped;
            int requests;
            public string Url {get;private set;}
            public int Requests {get{return Volatile.Read(ref requests);}}
            public RecoveryCoverServer(Func<int,byte[]> body,Func<int,int> status)
            {
                listener.Start();Url="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/cover";
                worker=Task.Run(async()=>
                {
                    while(!stopped)try
                    {
                        using(var client=await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                        using(var stream=client.GetStream())
                        {
                            var header=new StringBuilder();byte[] input=new byte[1024];
                            while(header.Length<16384&&!header.ToString().Contains("\r\n\r\n"))
                            {
                                int count=await stream.ReadAsync(input,0,input.Length).ConfigureAwait(false);
                                if(count==0)throw new IOException("Request ended early.");
                                header.Append(Encoding.ASCII.GetString(input,0,count));
                            }
                            int number=Interlocked.Increment(ref requests);int code=status(number);byte[] bytes=body(number);
                            byte[] response=Encoding.ASCII.GetBytes("HTTP/1.1 "+code+(code==200?" OK":" Not Found")+"\r\nContent-Length: "+bytes.Length+"\r\nConnection: close\r\n\r\n");
                            await stream.WriteAsync(response,0,response.Length).ConfigureAwait(false);
                            await stream.WriteAsync(bytes,0,bytes.Length).ConfigureAwait(false);
                        }
                    }
                    catch(ObjectDisposedException) {if(!stopped)throw;}
                    catch(SocketException) {if(!stopped)throw;}
                    catch(IOException) {if(!stopped)throw;}
                });
            }
            public void Dispose()
            {
                stopped=true;listener.Stop();
                Require(worker.Wait(2000),"Recovery HTTP fixture failed to stop.");
            }
        }

        private static T CoverRecoveryField<T>(object value,string name)
        {
            return (T)value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value);
        }

        private sealed class RecoveryCardActions:ICardActions
        {
            public bool ActionsUnavailable {get{return false;}}
            public bool IsDisposed {get{return false;}}
            public void BeginHover(CardView card) { }
            public void EndHover(CardView card,string reason) { }
            public void InvalidatePrepared(string reason) { }
            public Task DislikeAsync(string id) {throw new InvalidOperationException("Forbidden card command.");}
            public Task ApplyAsync(string id) {throw new InvalidOperationException("Forbidden card command.");}
            public Task PreviewAsync(string id) {throw new InvalidOperationException("Forbidden card command.");}
            public void RefreshCards() { }
            public bool CanRestoreFocus(IInputElement focused,IInputElement original) {return false;}
        }
    }
}
