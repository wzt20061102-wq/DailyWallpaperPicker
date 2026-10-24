// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DailyWallpaperPicker
{
    /// <summary>Offline tests: use only generated fixtures, never Steam or desktop state.</summary>
    public static class SelfTests
    {
        private sealed class TestResult
        {
            public string name { get; set; }
            public bool passed { get; set; }
            public long durationMs { get; set; }
            public object details { get; set; }
            public string error { get; set; }
        }

        public static int Run(string outputDir)
        {
            if (String.IsNullOrWhiteSpace(outputDir)) throw new ArgumentException("测试输出目录不能为空。", "outputDir");
            string output = Path.GetFullPath(outputDir);
            Directory.CreateDirectory(output);
            string fixtureRoot = Path.Combine(output, "fixtures-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(fixtureRoot);
            var results = new List<TestResult>();
            DateTime started = DateTime.UtcNow;

            Check(results, "schedule_22_49_before_prepare", delegate
            {
                return ScheduleCase(new DateTime(2026, 10, 22, 14, 49, 0, DateTimeKind.Utc), "2026-10-21", "2026-10-21", "2026-10-22 22:49:00");
            });
            Check(results, "schedule_22_50_prepare_only", delegate
            {
                return ScheduleCase(new DateTime(2026, 10, 22, 14, 50, 0, DateTimeKind.Utc), "2026-10-21", "2026-10-22", "2026-10-22 22:50:00");
            });
            Check(results, "schedule_22_59_not_yet_due", delegate
            {
                return ScheduleCase(new DateTime(2026, 10, 22, 14, 59, 59, DateTimeKind.Utc), "2026-10-21", "2026-10-22", "2026-10-22 22:59:59");
            });
            Check(results, "schedule_23_00_new_batch_due", delegate
            {
                return ScheduleCase(new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc), "2026-10-22", "2026-10-22", "2026-10-22 23:00:00");
            });
            Check(results, "schedule_after_days_off_latest_only", delegate
            {
                // The scheduler exposes only the latest due key, not one key for every missed day.
                object evening = ScheduleCase(new DateTime(2026, 10, 28, 15, 5, 0, DateTimeKind.Utc), "2026-10-28", "2026-10-28", "2026-10-28 23:05:00");
                object morning = ScheduleCase(new DateTime(2026, 10, 29, 1, 0, 0, DateTimeKind.Utc), "2026-10-28", "2026-10-28", "2026-10-29 09:00:00");
                return new { latestEvening = evening, latestMorning = morning, missedBatchEnumeration = false };
            });
            Check(results, "schedule_midnight_and_year_boundary", delegate
            {
                object midnight = ScheduleCase(new DateTime(2026, 10, 22, 16, 0, 0, DateTimeKind.Utc), "2026-10-22", "2026-10-22", "2026-10-23 00:00:00");
                object newYear = ScheduleCase(new DateTime(2025, 12, 31, 17, 0, 0, DateTimeKind.Utc), "2025-12-31", "2025-12-31", "2026-01-01 01:00:00");
                return new { midnight = midnight, newYear = newYear };
            });

            string source = Path.Combine(fixtureRoot, "原始壁纸");
            string cache = Path.Combine(fixtureRoot, "cache");
            string imported = null;
            Check(results, "cache_import_hidden_unicode_empty_directories_and_hashes", delegate
            {
                CreateWallpaperFixture(source);
                Directory.CreateDirectory(cache);
                var sourceFiles = RelativeFiles(source);
                var sourceDirs = RelativeDirectories(source);
                Require(sourceFiles.Count == 5, "预期源夹具应含 5 个实际文件。");
                Require(sourceDirs.Count == 5, "预期源夹具应含 5 个子目录。");
                string project = CacheStore.Import(source, cache, "900001", 1024 * 1024);
                imported = Path.GetDirectoryName(project);
                Require(File.Exists(project), "导入没有生成 project.json。");
                Require(File.Exists(Path.Combine(imported, ".verified.json")), "导入缺少校验清单。");
                var copiedFiles = RelativeFiles(imported);
                copiedFiles.Remove(".verified.json");
                var copiedDirs = RelativeDirectories(imported);
                Require(sourceFiles.SetEquals(copiedFiles), "复制前后的相对文件名集合不同。");
                Require(sourceDirs.SetEquals(copiedDirs), "复制前后的相对目录名集合不同。");
                var fileChecks = new List<object>();
                foreach (string relative in sourceFiles.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
                {
                    string sourceHash = CacheStore.Hash(Path.Combine(source, relative));
                    string copyHash = CacheStore.Hash(Path.Combine(imported, relative));
                    Require(String.Equals(sourceHash, copyHash, StringComparison.Ordinal), "逐文件 SHA-256 不同：" + relative);
                    fileChecks.Add(new { relativePath = relative, sourceSha256 = sourceHash, copySha256 = copyHash });
                }
                Require(Directory.Exists(Path.Combine(imported, "空目录")), "顶层空目录未保留。");
                Require(Directory.Exists(Path.Combine(imported, "说明", "另一空目录")), "嵌套空目录未保留。");
                Require(File.Exists(Path.Combine(imported, ".hidden-data.bin")), "隐藏文件未复制。");
                Require(File.Exists(Path.Combine(imported, ".hidden-folder", "metadata.json")), "隐藏目录中的文件未复制。");
                return new
                {
                    sourceFileCount = sourceFiles.Count,
                    copiedContentFileCount = copiedFiles.Count,
                    sourceDirectoryCount = sourceDirs.Count,
                    copiedDirectoryCount = copiedDirs.Count,
                    emptyDirectoryCount = 2,
                    sourceHiddenFileCount = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Count(p => (File.GetAttributes(p) & FileAttributes.Hidden) != 0),
                    sourceHiddenDirectoryCount = Directory.GetDirectories(source, "*", SearchOption.AllDirectories).Count(p => (File.GetAttributes(p) & FileAttributes.Hidden) != 0),
                    files = fileChecks,
                    projectPath = project,
                    note = "验证内容和目录集合完整；不声称保留了所有原始 Windows 属性。"
                };
            });
            Check(results, "cache_verified_round_trip", delegate
            {
                Require(!String.IsNullOrEmpty(imported), "导入前置用例未通过。");
                string verified = CacheStore.Verify(imported);
                Require(String.Equals(verified, Path.Combine(imported, "project.json"), StringComparison.OrdinalIgnoreCase), "Verify 未返回实际项目文件。");
                return new { projectPath = verified, manifestEntryCount = Json.Read<Dictionary<string, string>>(Path.Combine(imported, ".verified.json")).Count };
            });
            Check(results, "cache_empty_source_rejected", delegate
            {
                string empty = Path.Combine(fixtureRoot, "空源");
                Directory.CreateDirectory(Path.Combine(empty, "只有空子目录"));
                string reason = Rejected(delegate { CacheStore.Import(empty, cache, "900002", 1024 * 1024); });
                Require(!Directory.Exists(Path.Combine(cache, "900002")), "空源被导入成有效缓存。");
                return new { sourceFileCount = Directory.GetFiles(empty, "*", SearchOption.AllDirectories).Length, rejection = reason };
            });
            Check(results, "cache_size_limit_rejected", delegate
            {
                string reason = Rejected(delegate { CacheStore.Import(source, cache, "900003", 1); });
                Require(!Directory.Exists(Path.Combine(cache, "900003")), "超限源被导入。");
                return new { maxBytes = 1, actualBytes = CacheStore.Files(source).Sum(f => new FileInfo(f).Length), rejection = reason };
            });
            Check(results, "cache_boundary_and_outside_delete_rejected", delegate
            {
                string outside = Path.Combine(fixtureRoot, "outside");
                string prefixSibling = Path.Combine(fixtureRoot, "cache-neighbor");
                Directory.CreateDirectory(outside);
                Directory.CreateDirectory(prefixSibling);
                File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside sentinel", Encoding.UTF8);
                File.WriteAllText(Path.Combine(prefixSibling, "keep.txt"), "prefix sentinel", Encoding.UTF8);
                Require(!CacheStore.Under(cache, cache), "缓存根目录本身不能视为可删除子项。");
                Require(!CacheStore.Under(prefixSibling, cache), "同名前缀兄弟目录被误判为子目录。");
                Require(!CacheStore.Under(Path.Combine(cache, "..", "outside"), cache), ".. 路径越界被误判。");
                Require(CacheStore.Under(Path.Combine(cache, "child"), cache), "真实子目录被错误拒绝。");
                string outsideReason = Rejected(delegate { CacheStore.SafeDelete(outside, cache); });
                string prefixReason = Rejected(delegate { CacheStore.SafeDelete(prefixSibling, cache); });
                string rootReason = Rejected(delegate { CacheStore.SafeDelete(cache, cache); });
                Require(File.Exists(Path.Combine(outside, "keep.txt")), "越界目录哨兵被误删。");
                Require(File.Exists(Path.Combine(prefixSibling, "keep.txt")), "同名前缀兄弟目录哨兵被误删。");
                Require(Directory.Exists(cache), "缓存根被误删。");
                return new { rejectedCount = 3, survivingSentinelCount = 2, outsideReason = outsideReason, prefixReason = prefixReason, rootReason = rootReason };
            });
            Check(results, "cache_owned_directory_delete_allowed", delegate
            {
                string owned = Path.Combine(cache, "900004");
                Directory.CreateDirectory(Path.Combine(owned, "empty"));
                File.WriteAllText(Path.Combine(owned, "local.txt"), "test owned content", Encoding.UTF8);
                CacheStore.SafeDelete(owned, cache);
                Require(!Directory.Exists(owned), "自有缓存目录未被清理。");
                Require(Directory.Exists(cache), "清理自有项误删了缓存根。");
                return new { ownedDirectoryRemoved = true, cacheRootRetained = true };
            });
            Check(results, "cache_tampered_file_rejected", delegate
            {
                Require(!String.IsNullOrEmpty(imported), "导入前置用例未通过。");
                string target = Path.Combine(imported, "media", "海边.mp4");
                string originalHash = CacheStore.Hash(target);
                File.AppendAllText(target, "changed after verification", Encoding.UTF8);
                string changedHash = CacheStore.Hash(target);
                Require(originalHash != changedHash, "篡改夹具没有实际改变内容。");
                string reason = Rejected(delegate { CacheStore.Verify(imported); });
                return new { originalSha256 = originalHash, changedSha256 = changedHash, rejection = reason };
            });
            Check(results, "cache_missing_file_rejected", delegate
            {
                string project = CacheStore.Import(source, cache, "900005", 1024 * 1024);
                string destination = Path.GetDirectoryName(project);
                File.Delete(Path.Combine(destination, "说明", "选择记录.txt"));
                string reason = Rejected(delegate { CacheStore.Verify(destination); });
                return new { expectedContentFiles = 5, actualContentFiles = CacheStore.Files(destination).Count - 1, rejection = reason };
            });
            Check(results, "project_relative_escape_rejected", delegate
            {
                string projectRoot = Path.Combine(fixtureRoot, "越界项目");
                Directory.CreateDirectory(projectRoot);
                string external = Path.Combine(fixtureRoot, "outside-media.mp4");
                File.WriteAllBytes(external, new byte[] { 1, 2, 3, 4 });
                Json.Write(Path.Combine(projectRoot, "project.json"), new { type = "video", file = "../outside-media.mp4" });
                string reason = Rejected(delegate { CacheStore.CheckProject(projectRoot); });
                Require(File.Exists(external), "校验越界项目不应修改外部文件。");
                return new { externalFileRetained = true, rejection = reason };
            });
            Check(results, "project_absolute_escape_rejected", delegate
            {
                string projectRoot = Path.Combine(fixtureRoot, "绝对越界项目");
                Directory.CreateDirectory(projectRoot);
                string external = Path.Combine(fixtureRoot, "absolute-outside.mp4");
                File.WriteAllBytes(external, new byte[] { 4, 3, 2, 1 });
                Json.Write(Path.Combine(projectRoot, "project.json"), new { type = "video", file = external });
                string reason = Rejected(delegate { CacheStore.CheckProject(projectRoot); });
                return new { externalFileRetained = File.Exists(external), rejection = reason };
            });
            Check(results, "manifest_path_escape_rejected", delegate
            {
                string projectRoot = Path.Combine(fixtureRoot, "清单越界项目");
                Directory.CreateDirectory(projectRoot);
                File.WriteAllBytes(Path.Combine(projectRoot, "local.mp4"), new byte[] { 9, 8, 7 });
                Json.Write(Path.Combine(projectRoot, "project.json"), new { type = "video", file = "local.mp4" });
                string external = Path.Combine(fixtureRoot, "manifest-outside.txt");
                File.WriteAllText(external, "must remain outside", Encoding.UTF8);
                var manifest = new Dictionary<string, string>();
                manifest.Add("../manifest-outside.txt", CacheStore.Hash(external));
                Json.Write(Path.Combine(projectRoot, ".verified.json"), manifest);
                string reason = Rejected(delegate { CacheStore.Verify(projectRoot); });
                return new { manifestEntryCount = manifest.Count, rejection = reason };
            });
            Check(results, "unsupported_project_type_rejected", delegate
            {
                string projectRoot = Path.Combine(fixtureRoot, "不支持的项目");
                Directory.CreateDirectory(projectRoot);
                File.WriteAllBytes(Path.Combine(projectRoot, "example.exe"), new byte[] { 0, 0 });
                Json.Write(Path.Combine(projectRoot, "project.json"), new { type = "application", file = "example.exe" });
                return new { rejection = Rejected(delegate { CacheStore.CheckProject(projectRoot); }), projectType = "application" };
            });

            Check(results, "cache_generated_shader_blob_allowed_originals_still_verified", delegate
            {
                string destination = Path.GetDirectoryName(CacheStore.Import(source, cache, "900006", 1024 * 1024));
                string shaderDirectory = Path.Combine(destination, "shaders", "blobsSM40");
                Directory.CreateDirectory(shaderDirectory);
                string generated = Path.Combine(shaderDirectory, new string('a', 40) + ".dxs");
                File.WriteAllBytes(generated, new byte[] { 1, 4, 9, 16 });
                Require(File.Exists(CacheStore.Verify(destination)), "引擎新增的规定格式着色器缓存被误判。");
                File.AppendAllText(Path.Combine(destination, "media", "海边.mp4"), "source changed", Encoding.UTF8);
                string rejection = Rejected(delegate { CacheStore.Verify(destination); });
                return new { generatedFileAllowed = true, originalTamperRejected = true, rejection = rejection };
            });
            Check(results, "cache_unrecognized_added_files_rejected", delegate
            {
                string destination = Path.GetDirectoryName(CacheStore.Import(source, cache, "900007", 1024 * 1024));
                string extra = Path.Combine(destination, "unexpected.exe");
                File.WriteAllBytes(extra, new byte[] { 1, 2 });
                string executableReason = Rejected(delegate { CacheStore.Verify(destination); });
                File.Delete(extra);
                string shaderDirectory = Path.Combine(destination, "shaders", "blobsSM40");
                Directory.CreateDirectory(shaderDirectory);
                File.WriteAllBytes(Path.Combine(shaderDirectory, "not-a-hash.dxs"), new byte[] { 3, 4 });
                string shaderReason = Rejected(delegate { CacheStore.Verify(destination); });
                return new { rejectedAddedFileCount = 2, executableReason = executableReason, shaderReason = shaderReason };
            });
            Check(results, "cache_original_shader_blob_not_exempt_from_hash", delegate
            {
                string original = Path.Combine(fixtureRoot, "带原始着色器的源");
                CreateWallpaperFixture(original);
                string relative = Path.Combine("shaders", "blobsSM40", new string('b', 40) + ".dxs");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(original, relative)));
                File.WriteAllBytes(Path.Combine(original, relative), new byte[] { 5, 6 });
                string destination = Path.GetDirectoryName(CacheStore.Import(original, cache, "900008", 1024 * 1024));
                File.WriteAllBytes(Path.Combine(destination, relative), new byte[] { 6, 5 });
                return new { rejection = Rejected(delegate { CacheStore.Verify(destination); }), manifestFiles = 6 };
            });

            ControllerTests(results, fixtureRoot);
            HoverPreviewTests(results, fixtureRoot);
            ThumbnailTests(results, fixtureRoot);
            RecommendationPreferenceTests(results, fixtureRoot);
            RecommendationFeedbackTests(results, fixtureRoot);
            DislikeControllerTests(results, fixtureRoot);

            Check(results, "readback_scene_package_equivalence_is_directory_and_type_scoped", delegate
            {
                string sceneRoot = Path.Combine(fixtureRoot, "readback-scene");
                string otherRoot = Path.Combine(fixtureRoot, "readback-other-scene");
                string videoRoot = Path.Combine(fixtureRoot, "readback-video");
                Directory.CreateDirectory(sceneRoot);
                Directory.CreateDirectory(otherRoot);
                Directory.CreateDirectory(videoRoot);
                string sceneProject = Path.Combine(sceneRoot, "project.json");
                string scenePackage = Path.Combine(sceneRoot, "scene.pkg");
                string otherPackage = Path.Combine(otherRoot, "scene.pkg");
                string videoProject = Path.Combine(videoRoot, "project.json");
                string videoPackage = Path.Combine(videoRoot, "scene.pkg");
                Json.Write(sceneProject, new { type = "scene", file = "scene.json" });
                Json.Write(videoProject, new { type = "video", file = "video.mp4" });
                File.WriteAllBytes(scenePackage, new byte[] { 1, 2, 3 });
                File.WriteAllBytes(otherPackage, new byte[] { 1, 2, 3 });
                File.WriteAllBytes(videoPackage, new byte[] { 1, 2, 3 });
                Require(WallpaperBridge.SameProject(scenePackage, sceneProject), "同目录 Scene 的 scene.pkg 实时回读未识别为当前项目。");
                Require(!WallpaperBridge.SameProject(otherPackage, sceneProject), "其他目录的同名 scene.pkg 被误认作当前 Scene。");
                Require(!WallpaperBridge.SameProject(videoPackage, videoProject), "Video 项目被错误套用 Scene 包路径等价规则。");
                Require(WallpaperBridge.SameProject(Path.Combine(videoRoot, "video.mp4"), videoProject), "Video 自己的 file 路径不再被识别。");
                return new { sameDirectoryScenePackageEquivalent = true, differentDirectoryPackageRejected = true, videoScenePackageRejected = true, videoMainFileEquivalent = true, assertions = 4, syntheticPathFixturesOnly = true };
            });

            int failed = results.Count(r => !r.passed);
            var report = new
            {
                status = failed == 0 ? "passed" : "failed",
                startedUtc = started.ToString("o"),
                finishedUtc = DateTime.UtcNow.ToString("o"),
                total = results.Count,
                passed = results.Count - failed,
                failed = failed,
                fixtureRoot = fixtureRoot,
                fixtureFilesRetained = true,
                safety = new { realSteamCalled = false, desktopCalled = false, realUserDirectoriesModified = false, writesLimitedToGeneratedOutputFixtures = true },
                uiDpiValidation = new
                {
                    status = "not_run",
                    requestedRange = "100%-200%",
                    note = "本程序只验证调度、状态与文件缓存；不把源码中的缩放/滚动设置当作真实 DPI 或界面验收。需要另行运行应用并截图检查。"
                },
                limitations = new[]
                {
                    "媒体文件为明确标记的测试字节，不用于证明视频解码或动态预览成功。",
                    "这些离线测试不证明 Steam 查询、下载、独立预览、桌面切换或 Windows 登录任务已经成功。"
                },
                tests = results
            };
            Json.Write(Path.Combine(output, "tests.json"), report);
            return failed == 0 ? 0 : 1;
        }

        private sealed class ThumbnailHttpFixture : IDisposable
        {
            readonly TcpListener listener;
            readonly CancellationTokenSource stopped = new CancellationTokenSource();
            readonly Task server;
            TcpClient client;
            int requestSeen;
            public string Url { get; private set; }
            public bool RequestSeen { get { return Volatile.Read(ref requestSeen) != 0; } }
            public ThumbnailHttpFixture(Func<NetworkStream, CancellationToken, Task> respond)
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start(1);
                Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/fixture";
                server = Task.Run(async delegate
                {
                    try
                    {
                        using (client = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                        using (NetworkStream stream = client.GetStream())
                        {
                            var header = new StringBuilder();
                            byte[] buffer = new byte[1024];
                            while (header.Length < 16384)
                            {
                                int count = await stream.ReadAsync(buffer, 0, buffer.Length, stopped.Token).ConfigureAwait(false);
                                if (count == 0) throw new IOException("Fixture client closed before its request.");
                                header.Append(Encoding.ASCII.GetString(buffer, 0, count));
                                if (header.ToString().Contains("\r\n\r\n")) break;
                            }
                            if (!header.ToString().Contains("\r\n\r\n")) throw new InvalidOperationException("Fixture request header was incomplete.");
                            Interlocked.Exchange(ref requestSeen, 1);
                            await respond(stream, stopped.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { if (!stopped.IsCancellationRequested) throw; }
                    catch (IOException) { /* A bounded client may abort a deliberately oversized response. */ }
                    catch (SocketException) { if (!stopped.IsCancellationRequested) throw; }
                    catch (ObjectDisposedException) { if (!stopped.IsCancellationRequested) throw; }
                });
            }
            public void Dispose()
            {
                stopped.Cancel(); listener.Stop();
                TcpClient active = client; if (active != null) active.Close();
                try { if (!server.Wait(2000)) throw new IOException("Local HTTP fixture did not stop."); }
                finally { stopped.Dispose(); }
            }
        }

        private static Task WriteHttp(NetworkStream stream, string text, CancellationToken token)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            return stream.WriteAsync(bytes, 0, bytes.Length, token);
        }

        private static object ThumbnailFailureCase(string root, string name, string url, TimeSpan timeout, string expectedMessage)
        {
            DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc);
            var candidate = new Candidate { Id = "880001", PreviewUrl = url, Status = "waiting for complete wallpaper" };
            var state = new AppState { BatchId = Schedule.Due(now), SeedImported = true };
            state.Candidates.Add(candidate);
            var controller = FixtureController(root, name, state, delegate { return now; });
            string before = CacheStore.Hash(Path.Combine(controller.DataDirectory, "state.json"));
            var timer = Stopwatch.StartNew();
            controller.Thumbnail(candidate, state.BatchId, timeout).GetAwaiter().GetResult();
            timer.Stop();
            Candidate after = controller.State.Candidates.Single();
            Require(!after.Ready && after.Error == null && after.Status == candidate.Status && after.ThumbnailPath == null, "封面失败改变了完整壁纸的就绪/错误状态。");
            Require(CacheStore.Hash(Path.Combine(controller.DataDirectory, "state.json")) == before, "封面失败写入了候选下载失败状态。");
            Require(Directory.GetFiles(Path.Combine(controller.DataDirectory, "thumbnails")).Length == 0, "不完整或超限封面被保留。");
            Require(Directory.GetFiles(Path.Combine(controller.DataDirectory, "jobs")).Length == 0, "封面测试调用了 Steam 工作进程。");
            Require(timer.Elapsed < TimeSpan.FromSeconds(5), "封面失败没有及时返回给完整壁纸准备流程。");
            string log = File.ReadAllText(Path.Combine(controller.DataDirectory, "runtime_output.txt"), Encoding.UTF8);
            Require(log.Contains("THUMBNAIL SKIPPED") && log.Contains(expectedMessage), "未记录预期的非致命封面失败原因。");
            return new { elapsedMs = timer.ElapsedMilliseconds, candidateUnchanged = true, partialFiles = 0, realSteamCalled = false };
        }

        private static void ThumbnailTests(List<TestResult> results, string fixtureRoot)
        {
            Check(results, "thumbnail_http_404_is_optional_and_preserves_candidate", delegate
            {
                using (var server = new ThumbnailHttpFixture((stream, token) => WriteHttp(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", token)))
                {
                    object result = ThumbnailFailureCase(fixtureRoot, "thumbnail-404", server.Url, TimeSpan.FromSeconds(3), "404");
                    Require(server.RequestSeen, "没有实际请求本地 HTTP 夹具。"); return result;
                }
            });
            Check(results, "thumbnail_header_timeout_returns_without_marking_download_failed", delegate
            {
                using (var server = new ThumbnailHttpFixture((stream, token) => Task.Delay(30000, token)))
                {
                    object result = ThumbnailFailureCase(fixtureRoot, "thumbnail-header-timeout", server.Url, TimeSpan.FromMilliseconds(300), "封面下载超时");
                    Require(server.RequestSeen, "响应头超时夹具未收到请求。"); return result;
                }
            });
            Check(results, "thumbnail_body_timeout_never_publishes_partial_image", delegate
            {
                using (var server = new ThumbnailHttpFixture(async (stream, token) =>
                {
                    await WriteHttp(stream, "HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\na", token).ConfigureAwait(false);
                    await Task.Delay(30000, token).ConfigureAwait(false);
                }))
                {
                    object result = ThumbnailFailureCase(fixtureRoot, "thumbnail-body-timeout", server.Url, TimeSpan.FromMilliseconds(300), "封面下载超时");
                    Require(server.RequestSeen, "正文超时夹具未收到请求。"); return result;
                }
            });
            Check(results, "thumbnail_stream_limit_without_content_length_is_enforced", delegate
            {
                using (var server = new ThumbnailHttpFixture(async (stream, token) =>
                {
                    await WriteHttp(stream, "HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n", token).ConfigureAwait(false);
                    byte[] bytes = new byte[16 * 1024]; int remaining = 10 * 1024 * 1024 + 1;
                    while (remaining > 0) { int count = Math.Min(bytes.Length, remaining); await stream.WriteAsync(bytes, 0, count, token).ConfigureAwait(false); remaining -= count; }
                }))
                {
                    object result = ThumbnailFailureCase(fixtureRoot, "thumbnail-stream-limit", server.Url, TimeSpan.FromSeconds(3), "10 MB");
                    Require(server.RequestSeen, "流式上限夹具未收到请求。"); return result;
                }
            });
            Check(results, "thumbnail_success_publishes_complete_bytes_without_readiness_change", delegate
            {
                const string body = "OFFLINE THUMBNAIL FIXTURE";
                using (var server = new ThumbnailHttpFixture((stream, token) => WriteHttp(stream, "HTTP/1.1 200 OK\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n" + body, token)))
                {
                    DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc);
                    var candidate = new Candidate { Id = "880002", PreviewUrl = server.Url };
                    var state = new AppState { BatchId = Schedule.Due(now) }; state.Candidates.Add(candidate);
                    var controller = FixtureController(fixtureRoot, "thumbnail-success", state, delegate { return now; });
                    controller.Thumbnail(candidate, state.BatchId, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                    Candidate after = controller.State.Candidates.Single();
                    Require(server.RequestSeen && after.ThumbnailPath != null && File.ReadAllText(after.ThumbnailPath, Encoding.ASCII) == body, "完整封面字节未正确发布。");
                    Require(!after.Ready && after.Error == null && after.Status == candidate.Status, "封面成功错误地标记了完整壁纸已完成。");
                    Require(Directory.GetFiles(Path.Combine(controller.DataDirectory, "thumbnails")).Length == 1, "成功后遗留临时封面。");
                    return new { completeBytes = body.Length, candidateStillWaitingForFullWallpaper = true, imageDecodingClaimed = false, loopbackOnly = true };
                }
            });
            Check(results, "thumbnail_non_http_schemes_rejected_without_reading_files", delegate
            {
                string local = Path.Combine(fixtureRoot, "must-not-read-thumbnail.txt"); File.WriteAllText(local, "local sentinel", Encoding.UTF8);
                string before = CacheStore.Hash(local);
                object file = ThumbnailFailureCase(fixtureRoot, "thumbnail-file-url", new Uri(local).AbsoluteUri, TimeSpan.FromSeconds(1), "仅支持 HTTP 或 HTTPS");
                object ftp = ThumbnailFailureCase(fixtureRoot, "thumbnail-ftp-url", "ftp://example.invalid/cover", TimeSpan.FromSeconds(1), "仅支持 HTTP 或 HTTPS");
                Require(CacheStore.Hash(local) == before, "file URL 夹具被修改。");
                return new { fileRejected = file, ftpRejected = ftp, localSentinelUnchanged = true };
            });
        }

        private static void RecommendationPreferenceTests(List<TestResult> results, string fixtureRoot)
        {
            Check(results, "preferences_empty_and_json_null_fields_are_safe", delegate
            {
                var empty = new RecommendationPreferences();
                Require(empty.ExcludedIds.Length == 0 && empty.ExcludedTags.Length == 0 && empty.ExcludedTitleKeywords.Length == 0, "默认规则不应预设任何风格。");
                Require(empty.Allows(new Candidate { Id = null, Title = null, Tags = null }), "空规则不应拒绝仅缺少可选元数据的候选。");
                Require(!empty.Allows(null), "空候选不应被视作可推荐项目。");
                var nullable = Json.Decode<RecommendationPreferences>("{\"ExcludedIds\":null,\"ExcludedTags\":null,\"ExcludedTitleKeywords\":null}");
                Require(nullable.Allows(new Candidate { Title = "宇宙", Tags = null }), "JSON 中 null 数组没有按空规则处理。");
                var blank = new RecommendationPreferences { ExcludedIds = new[] { null, "", "  " }, ExcludedTags = new[] { null, " " }, ExcludedTitleKeywords = new[] { null, "", "_ -- \t" } };
                Require(blank.Allows(new Candidate { Id = "880001", Title = "Anime Girls", Tags = new[] { "Anime", "Girls" } }), "空白规则不应匹配所有候选。");
                Require(Json.Decode<RecommendationPreferences>("{}").Allows(new Candidate { Title = "星空" }), "缺省字段未保留空规则默认值。");
                return new { emptyDefaults = true, nullArraysAccepted = true, blankRulesIgnored = true, nullCandidateRejected = true };
            });
            Check(results, "preferences_excluded_id_matches_only_that_exact_item", delegate
            {
                var preferences = new RecommendationPreferences { ExcludedIds = new[] { "880001" } };
                Require(!preferences.Allows(new Candidate { Id = "880001", Title = "任何标题" }), "明确排除的 ID 没有被屏蔽。");
                foreach (string id in new[] { "8800010", "0880001", "880002", null })
                    Require(preferences.Allows(new Candidate { Id = id, Title = "宇宙" }), "ID 子串或数字转换误伤了其他项目。");
                return new { excludedIds = 1, similarIdsRetained = 4, exactStringMatch = true };
            });
            Check(results, "preferences_tags_use_case_insensitive_whole_tag_matching", delegate
            {
                var preferences = new RecommendationPreferences { ExcludedTags = new[] { "People", "Celebrity" } };
                Require(!preferences.Allows(new Candidate { Tags = new[] { "Everyone", "pEoPlE", "Scene" } }), "标签大小写变化绕过了排除。");
                Require(!preferences.Allows(new Candidate { Tags = new[] { null, " CELEBRITY " } }), "含空标签或首尾空白的排除失效。");
                Require(preferences.Allows(new Candidate { Title = "People collage", Tags = new[] { "People art", "Peoplescape" } }), "整标签规则错误地匹配了标签子串或标题。");
                Require(preferences.Allows(new Candidate { Title = "Anime Girls", Tags = new[] { "Anime", "Girls", "Everyone" } }), "真人标签排除误伤了未明确排除的动漫标签。");
                return new { caseInsensitive = true, wholeTagsOnly = true, animeGirlsRetained = true };
            });
            Check(results, "preferences_english_keywords_respect_word_boundaries", delegate
            {
                var preferences = new RecommendationPreferences { ExcludedTitleKeywords = new[] { "pink", "people" } };
                foreach (string title in new[] { "PINK wallpaper", "a pink-sky", "people collage", "(People)" })
                    Require(!preferences.Allows(new Candidate { Title = title }), "独立英文关键词没有匹配：" + title);
                foreach (string title in new[] { "Pinkerton landscape", "Pinkish clouds", "Peoplehood", "Pink2", "Cosmic universe" })
                    Require(preferences.Allows(new Candidate { Title = title }), "英文关键词误伤了词的一部分：" + title);
                var literal = new RecommendationPreferences { ExcludedTitleKeywords = new[] { "C++" } };
                Require(!literal.Allows(new Candidate { Title = "C++ code wallpaper" }) && literal.Allows(new Candidate { Title = "CCC code wallpaper" }), "关键词被当作正则表达式执行。");
                return new { excludedWholeWords = 4, similarWordsRetained = 5, regexCharactersAreLiteral = true };
            });
            Check(results, "preferences_hello_kitty_normalizes_case_and_separators", delegate
            {
                var preferences = new RecommendationPreferences { ExcludedTitleKeywords = new[] { "Hello Kitty", "HelloKitty" } };
                foreach (string title in new[] { "Hello_Kitty collage", "HELLO KITTY", "hello-kitty", "Hello—Kitty", "Hello  \t Kitty wallpaper", "HELLOKITTY" })
                    Require(!preferences.Allows(new Candidate { Title = title }), "Hello Kitty 标题变体没有匹配：" + title);
                Require(preferences.Allows(new Candidate { Title = "Hello Kittyland" }), "多词短语误伤了更长的英文名称。");
                Require(preferences.Allows(new Candidate { Title = "Hello universe" }), "完整短语规则错误地排除了单个 Hello。");
                return new { excludedTitleVariants = 6, unrelatedPhrasesRetained = 2 };
            });
            Check(results, "preferences_cjk_substrings_preserve_unrelated_anime_and_space", delegate
            {
                var preferences = new RecommendationPreferences { ExcludedTitleKeywords = new[] { "粉色", "真人明星", "人物拼贴" }, ExcludedTags = new[] { "People", "Celebrity" } };
                foreach (string title in new[] { "梦幻粉色桌面", "最新真人明星主题", "高清人物拼贴合集" })
                    Require(!preferences.Allows(new Candidate { Title = title }), "连续中文标题未命中明确关键词：" + title);
                foreach (string title in new[] { "Anime Girls", "宇宙星空", "抽象艺术", "山间湖泊" })
                    Require(preferences.Allows(new Candidate { Title = title, Tags = new[] { "Everyone", "Scene", "Anime", "Girls" } }), "排除规则扩大到了未确认的风格：" + title);
                return new { excludedCjkTitles = 3, unrelatedStylesRetained = 4, noGenderInference = true };
            });
            Check(results, "preferences_exclusions_apply_to_all_recommendation_pools", delegate
            {
                var preferences = new RecommendationPreferences { ExcludedIds = new[] { "880001" }, ExcludedTags = new[] { "People" }, ExcludedTitleKeywords = new[] { "pink", "Hello Kitty" } };
                foreach (string pool in new[] { "popular", "newest", "classic", null })
                {
                    Require(!preferences.Allows(new Candidate { Pool = pool, Id = "880001", Title = "宇宙" }), "推荐池绕过了明确 ID 排除。");
                    Require(!preferences.Allows(new Candidate { Pool = pool, Id = "880002", Title = "Real portrait", Tags = new[] { "people" } }), "推荐池绕过了标签排除。");
                    Require(!preferences.Allows(new Candidate { Pool = pool, Id = "880003", Title = "Hello_Kitty pink collage" }), "推荐池绕过了标题排除。");
                    Require(preferences.Allows(new Candidate { Pool = pool, Id = "880004", Title = "宇宙 · Anime Girls", Tags = new[] { "Anime", "Girls" } }), "推荐池过滤误伤未排除候选。");
                }
                return new { testedPoolsIncludingUnknown = 4, assertions = 16, explorationPoolAlsoHonorsExclusions = true };
            });
            Check(results, "preferences_missing_file_defaults_and_changes_reload", delegate
            {
                string directory = Path.Combine(fixtureRoot, "preferences-reload"); Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "preferences.json");
                var kitty = new Candidate { Id = "880001", Title = "HELLO_KITTY" };
                var pink = new Candidate { Id = "880002", Title = "Pink sky" };
                Require(PickerController.ReadPreferences(directory).Allows(kitty), "不存在偏好文件时没有按空规则处理。");
                Require(!File.Exists(path), "只读缺省偏好不应生成文件。");
                Json.Write(path, new RecommendationPreferences { ExcludedTitleKeywords = new[] { "hello kitty" } });
                var first = PickerController.ReadPreferences(directory);
                Require(!first.Allows(kitty) && first.Allows(pink), "首次规则文件没有正确读取。");
                Json.Write(path, new RecommendationPreferences { ExcludedTitleKeywords = new[] { "pink" } });
                var second = PickerController.ReadPreferences(directory);
                Require(second.Allows(kitty) && !second.Allows(pink), "更新后的偏好没有重新加载。");
                Require(!first.Allows(kitty) && first.Allows(pink), "重新读取修改了旧批次的规则快照。");
                return new { missingFileMeansNoExclusions = true, readingDoesNotWrite = true, updatedRulesReloaded = true, previousSnapshotUnchanged = true };
            });
            Check(results, "preferences_invalid_files_never_silently_allow_all", delegate
            {
                string directory = Path.Combine(fixtureRoot, "preferences-invalid"); Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "preferences.json");
                var rejected = new List<string>();
                foreach (string contents in new[] { "null", "{broken-json", "{\"ExcludedTags\":123}", new string(' ', 64 * 1024 + 1) })
                {
                    File.WriteAllText(path, contents, new UTF8Encoding(false));
                    bool failed = false;
                    try { PickerController.ReadPreferences(directory); }
                    catch (IOException ex) { rejected.Add(ex.GetType().Name); failed = true; }
                    catch (ArgumentException ex) { rejected.Add(ex.GetType().Name); failed = true; }
                    catch (InvalidOperationException ex) { rejected.Add(ex.GetType().Name); failed = true; }
                    Require(failed, "损坏、错误类型或超限偏好被退化为空规则放行。");
                    Require(File.ReadAllText(path, Encoding.UTF8) == contents, "失败读取修改了用户偏好内容。");
                }
                return new { invalidInputsRejected = rejected.Count, exceptionTypes = rejected, invalidFilePreserved = true, noFallbackToEmptyRules = true };
            });
            Check(results, "preferences_replacement_retains_first_and_third_objects_and_paths", delegate
            {
                var first = new Candidate { Id = "881001", Pool = "popular", Title = "宇宙", Tags = new[] { "Everyone", "Scene" }, FileSize = 100, Ready = true, CachePath = Path.Combine(fixtureRoot, "retain-first"), ProjectPath = Path.Combine(fixtureRoot, "retain-first", "project.json") };
                var second = new Candidate { Id = "881002", Pool = "newest", Title = "Hello_Kitty", Tags = new[] { "Everyone", "Video" }, FileSize = 100, Ready = true };
                var third = new Candidate { Id = "881003", Pool = "classic", Title = "Anime Girls", Tags = new[] { "Everyone", "Scene", "Anime", "Girls" }, FileSize = 100, Ready = true, CachePath = Path.Combine(fixtureRoot, "retain-third"), ProjectPath = Path.Combine(fixtureRoot, "retain-third", "project.json") };
                string firstPath = first.ProjectPath, thirdPath = third.ProjectPath;
                var preferences = new RecommendationPreferences { ExcludedIds = new[] { second.Id } };
                var retained = PickerController.RetainAllowedCandidates(new[] { third, second, first }, preferences, 1024);
                Require(retained.Count == 2 && Object.ReferenceEquals(retained[0], first) && Object.ReferenceEquals(retained[1], third), "排除第二张时重建或替换了第一、第三张对象。");
                Require(retained[0].ProjectPath == firstPath && retained[1].ProjectPath == thirdPath && retained.All(c => c.Ready), "保留候选的缓存路径或下载状态改变。");
                Require(retained.Select(c => c.Pool).SequenceEqual(new[] { "popular", "classic" }), "保留候选没有按推荐槽位排序。");
                Require(!retained.Any(c => c.Id == second.Id), "明确排除的第二张仍被保留。");
                return new { retainedCount = 2, sameCandidateObjects = true, projectPathsAndReadinessRetained = true, onlyNewestSlotMissing = true };
            });
            Check(results, "preferences_retention_deduplicates_ids_and_ignores_invalid_candidates", delegate
            {
                var first = new Candidate { Id = "882001", Pool = "popular", Tags = new[] { "Everyone", "Scene" }, FileSize = 100 };
                var duplicate = new Candidate { Id = first.Id, Pool = "newest", Tags = new[] { "Everyone", "Video" }, FileSize = 100 };
                var newest = new Candidate { Id = "882002", Pool = "newest", Tags = new[] { "Everyone", "Video" }, FileSize = 100 };
                var invalid = new Candidate { Id = "882003", Pool = "classic", Tags = new[] { "Everyone", "Scene", "Mature" }, FileSize = 100 };
                var third = new Candidate { Id = "882004", Pool = "classic", Tags = new[] { "Everyone", "Scene" }, FileSize = 100 };
                var preferences = new RecommendationPreferences();
                var retained = PickerController.RetainAllowedCandidates(new[] { invalid, duplicate, null, third, newest, first }, preferences, 1024);
                Require(retained.Count == 3 && retained.Select(c => c.Id).Distinct().Count() == 3, "保留候选包含重复 ID 或丢失有效槽位。");
                Require(retained.Select(c => c.Pool).SequenceEqual(new[] { "popular", "newest", "classic" }), "候选没有按照固定槽位排序。");
                Require(Object.ReferenceEquals(retained[0], first) && Object.ReferenceEquals(retained[1], newest) && Object.ReferenceEquals(retained[2], third), "去重或原有类型限制筛选未选出有效原对象。");
                Require(PickerController.RetainAllowedCandidates(null, preferences, 1024).Count == 0, "缺省旧候选没有作为空集合处理。");
                return new { retainedUniqueIds = 3, duplicateIgnored = true, invalidCandidateIgnored = true, nullInputHandled = true };
            });
        }

        private static void RecommendationFeedbackTests(List<TestResult> results, string fixtureRoot)
        {
            Check(results, "feedback_exact_dislike_is_idempotent_and_copies_first_metadata", delegate
            {
                DateTime firstTime = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
                var candidate = new Candidate { Id = "0883001", Tags = new[] { "Anime", "Girls", "Scene", "Everyone" } };
                var feedback = new RecommendationFeedback();
                Require(feedback.Record(candidate, firstTime), "首次不喜欢没有新增记录。");
                string first = Json.Encode(feedback);
                candidate.Tags[0] = "Technology";
                Require(Json.Encode(feedback) == first, "反馈持有可被候选对象修改的标签数组。");
                Require(!feedback.Record(new Candidate { Id = "883001", Tags = new[] { "Technology" } }, firstTime.AddDays(1)), "同一 ID 的重复点击再次训练。");
                Require(feedback.Dislikes.Count == 1 && Json.Encode(feedback) == first, "重复点击改变了首次时间或标签。");
                Require(!feedback.Allows(candidate) && !feedback.Allows(new Candidate { Id = "883001" }), "明确不喜欢的 ID 仍允许推荐。");
                Require(feedback.Allows(new Candidate { Id = "8830010", Tags = new[] { "Anime", "Girls" } }), "ID 前缀或相同题材被永久排除。");
                feedback.Validate();
                return new { uniqueRecords = 1, firstTimeAndTagsRetained = true, copiedTagSnapshot = true, onlyExactIdExcluded = true };
            });
            Check(results, "feedback_content_topics_lower_rank_without_excluding_similar_items", delegate
            {
                var feedback = new RecommendationFeedback();
                string[] topics = { "Nature", "Anime", "Sci-Fi", "Games", "Girls", "Guys", "Technology", "Abstract" };
                feedback.Record(new Candidate { Id = "883010", Tags = topics }, DateTime.UtcNow);
                foreach (string topic in topics)
                {
                    var similar = new Candidate { Id = "883011", Tags = new[] { topic } };
                    Require(feedback.Penalty(similar) == 1 && feedback.Allows(similar), "题材反馈没有作为软降权处理：" + topic);
                }
                Require(feedback.Penalty(new Candidate { Id = "883012", Tags = new[] { "Anime", "Girls", "Games" } }) == 1, "同一次不喜欢的多个共同标签被重复累计。");
                Require(feedback.Penalty(new Candidate { Id = "883013", Tags = new[] { "Sports" } }) == 0, "没有共同题材的壁纸被降权。");
                Require(feedback.Penalty(new Candidate { Id = "883014", Tags = new[] { "sci fi", "Game" } }) == 1, "常见题材标签别名没有匹配。");
                return new { testedTopics = topics.Length, weightPerUniqueDislike = 1, similarItemsStillAllowed = true, unrelatedTopicPenalty = 0 };
            });
            Check(results, "feedback_rendering_and_audio_metadata_never_train_preferences", delegate
            {
                string[] technical = { "Scene", "Video", "Wallpaper", "1920 x 1080", "3840x2160", "Dynamic resolution", "Audio responsive", "Audio", "HDR", "Everyone", "Approved", "4K", "Unspecified" };
                var feedback = new RecommendationFeedback();
                feedback.Record(new Candidate { Id = "883020", Tags = technical }, DateTime.UtcNow);
                Require(feedback.Dislikes[0].Tags.Length == 0, "技术标签写入了题材反馈。");
                Require(feedback.Penalty(new Candidate { Id = "883021", Tags = technical }) == 0, "共享播放格式或分辨率被降权。");
                Require(!feedback.Allows(new Candidate { Id = "883020" }), "没有题材标签时漏掉明确 ID 排除。");
                feedback.Record(new Candidate { Id = "883022", Tags = new[] { "Nature", "Video", "HDR", "Audio responsive" } }, DateTime.UtcNow);
                Require(feedback.Penalty(new Candidate { Id = "883023", Tags = new[] { "Scene", "HDR", "Everyone" } }) == 0, "混合元数据反馈通过技术标签传播。");
                Require(feedback.Penalty(new Candidate { Id = "883024", Tags = new[] { "Nature", "Scene", "1920 x 1080" } }) == 1, "同题材但不同技术格式没有软降权。");
                return new { ignoredMetadataTags = technical.Length, metadataPenalty = 0, crossMediaTopicMatch = true };
            });
            Check(results, "feedback_penalty_stays_finite_and_capped", delegate
            {
                var feedback = new RecommendationFeedback();
                for (int index = 0; index < 12; index++) feedback.Record(new Candidate { Id = (884000 + index).ToString(), Tags = new[] { "Anime", "Girls", "Anime" } }, DateTime.UtcNow);
                var similar = new Candidate { Id = "884100", Tags = new[] { "Anime", "Girls", "Anime" } };
                double penalty = feedback.Penalty(similar);
                Require(penalty == 6 && !Double.IsNaN(penalty) && !Double.IsInfinity(penalty), "重复题材反馈超过有限上限。");
                Require(feedback.Allows(similar), "较多负反馈将相似题材变为硬屏蔽。");
                var duplicate = new RecommendationFeedback(); duplicate.Record(new Candidate { Id = "884200", Tags = new[] { "Anime" } }, DateTime.UtcNow);
                duplicate.Dislikes.Add(duplicate.Dislikes[0]);
                Require(duplicate.Penalty(similar) == 1, "重复记录对象造成重复训练。");
                return new { independentDislikes = feedback.Dislikes.Count, cappedPenalty = penalty, duplicateIdCountedOnce = true, similarItemStillAllowed = true };
            });
            Check(results, "feedback_empty_and_null_inputs_are_safe_and_invalid_record_is_rejected", delegate
            {
                var feedback = new RecommendationFeedback { Dislikes = null };
                var candidate = new Candidate { Id = "885001", Tags = null };
                Require(feedback.Allows(candidate) && feedback.Penalty(candidate) == 0, "空反馈不应影响正常候选。");
                Require(!feedback.Allows(null) && feedback.Penalty(null) == 0, "空候选处理不安全。");
                foreach (string id in new[] { null, "", "0", "../outside", "abc" })
                {
                    bool rejected = false;
                    try { feedback.Record(new Candidate { Id = id }, DateTime.UtcNow); }
                    catch (ArgumentException) { rejected = true; }
                    Require(rejected, "无效反馈 ID 被持久化。");
                }
                Require(feedback.Record(candidate, new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Unspecified)), "空反馈列表不能初始化为首条记录。");
                feedback.Validate();
                Require(feedback.Dislikes.Count == 1 && feedback.Dislikes[0].Tags.Length == 0 && feedback.Dislikes[0].Utc.EndsWith("Z"), "无题材反馈或 UTC 时间保存错误。");
                return new { emptyFeedbackAllowed = true, invalidIdsRejected = 5, nullCandidateHandled = true, missingTagsCauseNoPenalty = true };
            });
            Check(results, "feedback_json_round_trip_preserves_dislikes_and_soft_penalties", delegate
            {
                string path = Path.Combine(fixtureRoot, "feedback-round-trip", "feedback.json");
                var feedback = new RecommendationFeedback();
                feedback.Record(new Candidate { Id = "886001", Tags = new[] { "Nature", "Scene" } }, new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc));
                feedback.Record(new Candidate { Id = "886002", Tags = new[] { "Games", "Sci-Fi", "Video" } }, new DateTime(2026, 10, 24, 12, 1, 0, DateTimeKind.Utc));
                Json.Write(path, feedback);
                var read = Json.Read<RecommendationFeedback>(path); read.Validate();
                Require(Json.Encode(read) == Json.Encode(feedback), "反馈 JSON 往返改变了记录内容。");
                Require(!read.Allows(new Candidate { Id = "886001" }) && !read.Allows(new Candidate { Id = "886002" }), "重新读取丢失了明确 ID 排除。");
                Require(read.Penalty(new Candidate { Id = "886003", Tags = new[] { "Nature", "Games" } }) == 2, "重新读取丢失了独立软反馈权重。");
                Require(read.Allows(new Candidate { Id = "886003", Tags = new[] { "Nature", "Games" } }), "持久化把相似题材变成了硬屏蔽。");
                return new { fileWritten = true, recordCount = read.Dislikes.Count, contentSha256 = CacheStore.Hash(path), roundTripUnchanged = true };
            });
            Check(results, "feedback_validation_rejects_corrupt_persistent_records", delegate
            {
                string time = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc).ToString("o");
                var good = new DislikeRecord { Id = "887001", Utc = time, Tags = new[] { "Anime" } };
                var invalid = new[] {
                    new RecommendationFeedback { Dislikes = null },
                    new RecommendationFeedback { Dislikes = new List<DislikeRecord> { null } },
                    new RecommendationFeedback { Dislikes = new List<DislikeRecord> { good, good } },
                    new RecommendationFeedback { Dislikes = new List<DislikeRecord> { new DislikeRecord { Id = "0", Utc = time } } },
                    new RecommendationFeedback { Dislikes = new List<DislikeRecord> { new DislikeRecord { Id = "887002", Utc = "not-a-date" } } },
                    new RecommendationFeedback { Dislikes = new List<DislikeRecord> { new DislikeRecord { Id = "887003", Utc = time, Tags = null } } },
                    new RecommendationFeedback { Dislikes = new List<DislikeRecord> { new DislikeRecord { Id = "887004", Utc = time, Tags = new[] { " " } } } }
                };
                foreach (var value in invalid)
                {
                    string encoded = Json.Encode(value);
                    string rejection = Rejected(delegate { Json.Decode<RecommendationFeedback>(encoded).Validate(); });
                    Require(rejection.Length > 0, "损坏反馈没有明确报错。");
                }
                return new { invalidSchemasRejected = invalid.Length, noSilentReset = true };
            });
        }

        private static AppState DislikeFixtureState(string fixtureRoot, string name, DateTime now)
        {
            string root = Path.Combine(fixtureRoot, "controllers", name);
            var state = new AppState { BatchId = Schedule.Due(now), PreparedBatchId = Schedule.Prepare(now), PresentedBatch = Schedule.Due(now), SeedImported = true };
            string[] pools = { "popular", "newest", "classic" };
            for (int index = 0; index < 3; index++)
            {
                foreach (bool prepared in new[] { false, true })
                {
                    string id = ((prepared ? 891001 : 890001) + index).ToString();
                    string directory = Path.Combine(root, "cache", id); Directory.CreateDirectory(directory);
                    string project = Path.Combine(directory, "project.json");
                    Json.Write(project, new { type = "scene", file = "scene.pkg" });
                    File.WriteAllBytes(Path.Combine(directory, "scene.pkg"), Encoding.UTF8.GetBytes("OFFLINE DISLIKE FIXTURE ONLY"));
                    var item = new Candidate { Id = id, Pool = pools[index], Title = "Fixture " + id, Tags = new[] { "Everyone", "Scene", "Nature", "Dynamic resolution" }, FileSize = 100, Ready = true, ProjectPath = project, CachePath = directory };
                    if (prepared) state.PreparedCandidates.Add(item); else state.Candidates.Add(item);
                }
            }
            return state;
        }

        private static WorkerReply DislikeQueryFixture()
        {
            return new WorkerReply { ok = true, items = new List<Candidate> { new Candidate { Id = "892001", Title = "Replacement fixture", Tags = new[] { "Everyone", "Scene", "Anime", "Dynamic resolution" }, FileSize = 100, CreatedUtc = 100 } } };
        }

        private static void AssertNoExternalDislikeCalls(PickerController controller)
        {
            Require(Directory.GetFiles(Path.Combine(controller.DataDirectory, "jobs")).Length == 0, "不喜欢测试调用了真实 Steam 工作进程。");
            string log = Path.Combine(controller.DataDirectory, "runtime_output.txt");
            Require(!File.Exists(log) || !File.ReadAllText(log, Encoding.UTF8).Contains("getWallpaper"), "不喜欢测试触发了桌面壁纸读取。");
        }

        private static void DislikeControllerTests(List<TestResult> results, string fixtureRoot)
        {
            Check(results, "dislike_22_55_replaces_only_current_slot_and_keeps_prefetched_batch", delegate
            {
                const string coverBody = "OFFLINE CURRENT BATCH COVER";
                using (var server = new ThumbnailHttpFixture((stream, token) => WriteHttp(stream, "HTTP/1.1 200 OK\r\nContent-Length: " + coverBody.Length + "\r\nConnection: close\r\n\r\n" + coverBody, token)))
                {
                DateTime now = new DateTime(2026, 10, 24, 14, 55, 0, DateTimeKind.Utc);
                string name = "dislike-current-prefetch"; var state = DislikeFixtureState(fixtureRoot, name, now);
                string prepared = Json.Encode(state.PreparedCandidates), first = Json.Encode(state.Candidates[0]), third = Json.Encode(state.Candidates[2]);
                int queries = 0, downloads = 0;
                var controller = FixtureController(fixtureRoot, name, state, delegate { return now; }, (verb, input, progress) =>
                {
                    if (verb == "query") { queries++; Require(Convert.ToString(Json.Decode<Dictionary<string, object>>(Json.Encode(input))["pool"]) == "newest", "单卡更换查询了其他推荐池。"); var reply = DislikeQueryFixture(); reply.items[0].PreviewUrl = server.Url; return Task.FromResult(reply); }
                    downloads++; throw new IOException("MOCK download unavailable; no Steam called");
                });
                controller.DislikeAsync("890002").GetAwaiter().GetResult();
                var after = controller.State;
                Require(after.BatchId == "2026-10-23" && after.PreparedBatchId == "2026-10-24", "22:55 更换混淆了当前与预取日期。");
                Require(Json.Encode(after.PreparedCandidates) == prepared, "更换当前卡片覆盖了下一期预取组。");
                Require(Json.Encode(after.Candidates[0]) == first && Json.Encode(after.Candidates[2]) == third && after.Candidates[1].Id == "892001", "未只替换第二张卡片。");
                Require(!after.Candidates[1].Ready && after.Candidates[1].Error.Contains("MOCK"), "模拟下载失败没有保留可重试的新候选。");
                Require(server.RequestSeen && after.Candidates[1].ThumbnailPath != null && File.ReadAllText(after.Candidates[1].ThumbnailPath, Encoding.ASCII) == coverBody, "22:55 手动替换的完整封面被误判为过期批次。");
                Require(Directory.GetFiles(Path.Combine(controller.DataDirectory, "thumbnails")).Length == 1, "22:55 换卡封面发布后遗留了临时文件。");
                Require(queries == 1 && downloads == 1 && PickerController.ReadFeedback(controller.DataDirectory).Dislikes.Count == 1, "单次更换重复查询或重复记录反馈。");
                AssertNoExternalDislikeCalls(controller);
                return new { queryCalls = queries, mockDownloadCalls = downloads, unchangedNeighborCards = 2, preparedBatchUnchanged = true, currentBatchThumbnailPublished = true, realSteamCalled = false, desktopCalled = false };
                }
            });
            Check(results, "dislike_failed_current_download_retries_before_23_00", delegate
            {
                DateTime now = new DateTime(2026, 10, 24, 14, 55, 0, DateTimeKind.Utc);
                string name = "dislike-retry-current"; var state = DislikeFixtureState(fixtureRoot, name, now);
                string prepared = Json.Encode(state.PreparedCandidates); int queries = 0, downloads = 0;
                var controller = FixtureController(fixtureRoot, name, state, delegate { return now; }, (verb, input, progress) =>
                {
                    if (verb == "query") { queries++; return Task.FromResult(DislikeQueryFixture()); }
                    downloads++; throw new IOException("MOCK retryable download failure");
                });
                controller.DislikeAsync("890002").GetAwaiter().GetResult();
                // Keep unrelated cache housekeeping outside this injected-worker test.
                typeof(PickerController).GetField("nextCleanupTry", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(controller, DateTime.MaxValue);
                controller.RetryAsync().GetAwaiter().GetResult();
                Require(downloads == 2 && queries == 1, "22:55 重试没有继续下载当前替代卡，或错误地重新选卡。");
                Require(controller.State.Candidates[1].Id == "892001" && Json.Encode(controller.State.PreparedCandidates) == prepared, "失败重试改变了替代卡或下一期组。");
                AssertNoExternalDislikeCalls(controller);
                return new { mockDownloadAttempts = downloads, selectionQueryCount = queries, currentBatchRetryVerified = true, prefetchedBatchUnchanged = true };
            });
            Check(results, "dislike_late_query_cannot_overwrite_new_day_candidates", delegate
            {
                DateTime now = new DateTime(2026, 10, 24, 14, 59, 59, DateTimeKind.Utc);
                string name = "dislike-cross-due"; var state = DislikeFixtureState(fixtureRoot, name, now);
                string nextBatch = Json.Encode(state.PreparedCandidates); int queries = 0, downloads = 0;
                var response = new TaskCompletionSource<WorkerReply>();
                var controller = FixtureController(fixtureRoot, name, state, delegate { return now; }, (verb, input, progress) =>
                {
                    if (verb == "query") { queries++; return response.Task; }
                    downloads++; throw new IOException("Unexpected mock download after due boundary");
                });
                Task operation = controller.DislikeAsync("890002");
                Require(queries == 1 && !operation.IsCompleted, "跨日夹具没有实际停在查询等待中。");
                now = new DateTime(2026, 10, 24, 15, 0, 0, DateTimeKind.Utc);
                Require(controller.ShouldPresent(), "23:00 未提升已准备的新一期。");
                response.SetResult(DislikeQueryFixture()); operation.GetAwaiter().GetResult();
                Require(controller.State.BatchId == "2026-10-24" && Json.Encode(controller.State.Candidates) == nextBatch, "旧批次查询覆盖了新一期卡片。");
                Require(downloads == 0 && !controller.State.Candidates.Any(c => c.Id == "892001"), "过期查询仍触发下载或写入新卡片。");
                Require(PickerController.ReadFeedback(controller.DataDirectory).Dislikes.Count == 1, "跨日取消错误地丢失或重复了已经保存的明确反馈。");
                AssertNoExternalDislikeCalls(controller);
                return new { staleQueryDiscarded = true, newBatchUnchanged = true, staleDownloadCalls = downloads, recordedDislikes = 1 };
            });
            Check(results, "dislike_duplicate_click_records_once_and_does_not_repeat_query", delegate
            {
                DateTime now = new DateTime(2026, 10, 24, 14, 55, 0, DateTimeKind.Utc);
                string name = "dislike-double-click"; var state = DislikeFixtureState(fixtureRoot, name, now);
                int queries = 0; var response = new TaskCompletionSource<WorkerReply>();
                var controller = FixtureController(fixtureRoot, name, state, delegate { return now; }, (verb, input, progress) =>
                {
                    if (verb == "query") { queries++; return response.Task; }
                    throw new IOException("MOCK download unavailable");
                });
                Task first = controller.DislikeAsync("890002");
                string concurrent = RejectedOperation(delegate { controller.DislikeAsync("890002").GetAwaiter().GetResult(); });
                response.SetResult(DislikeQueryFixture()); first.GetAwaiter().GetResult();
                string stale = Rejected(delegate { controller.DislikeAsync("890002").GetAwaiter().GetResult(); });
                Require(queries == 1 && PickerController.ReadFeedback(controller.DataDirectory).Dislikes.Count == 1, "重复点击追加了查询或反馈训练。");
                AssertNoExternalDislikeCalls(controller);
                return new { queryCalls = queries, feedbackRecords = 1, concurrentRejection = concurrent, oldCardRejection = stale };
            });
            Check(results, "dislike_feedback_write_failure_preserves_all_cards", delegate
            {
                DateTime now = new DateTime(2026, 10, 24, 14, 55, 0, DateTimeKind.Utc);
                string name = "dislike-write-failure"; var state = DislikeFixtureState(fixtureRoot, name, now);
                string cards = Json.Encode(state.Candidates), prepared = Json.Encode(state.PreparedCandidates); int workerCalls = 0;
                var controller = FixtureController(fixtureRoot, name, state, delegate { return now; }, (verb, input, progress) => { workerCalls++; throw new IOException("Worker must not run before feedback persistence"); });
                Directory.CreateDirectory(Path.Combine(controller.DataDirectory, "feedback.json"));
                string rejection = Rejected(delegate { controller.DislikeAsync("890002").GetAwaiter().GetResult(); });
                Require(workerCalls == 0 && Json.Encode(controller.State.Candidates) == cards && Json.Encode(controller.State.PreparedCandidates) == prepared, "反馈写入失败后仍查询或替换了候选。");
                Require(controller.State.Message.Contains("未能记录不喜欢"), "反馈保存失败被错误显示为已记住。");
                AssertNoExternalDislikeCalls(controller);
                return new { writeFailureSurfaced = true, workerCalls = workerCalls, allCardsUnchanged = true, rejection = rejection };
            });
            Check(results, "dislike_ranking_penalty_affects_all_pools_but_not_technical_tags", delegate
            {
                DateTime now = new DateTime(2026, 10, 24, 14, 55, 0, DateTimeKind.Utc);
                var controller = FixtureController(fixtureRoot, "dislike-ranking", new AppState { SeedImported = true }, delegate { return now; }, (verb, input, progress) => { throw new IOException("Ranking must not call Steam"); });
                var nature = new Candidate { Id = "893001", Title = "Nature", Tags = new[] { "Nature", "Scene", "HDR", "Dynamic resolution" }, CreatedUtc = 200 };
                var anime = new Candidate { Id = "893002", Title = "Anime", Tags = new[] { "Anime", "Video", "Dynamic resolution" }, CreatedUtc = 100 };
                var feedback = new RecommendationFeedback(); feedback.Record(new Candidate { Id = "893100", Tags = new[] { "Nature", "Scene" } }, now);
                var technical = new RecommendationFeedback(); technical.Record(new Candidate { Id = "893101", Tags = new[] { "Scene", "HDR", "Dynamic resolution" } }, now);
                foreach (string pool in new[] { "popular", "newest", "classic" })
                {
                    Require(Object.ReferenceEquals(controller.ChooseRecommendation(new List<Candidate> { nature, anime }, pool, feedback), anime), "推荐池没有降低相似题材权重：" + pool);
                    Require(Object.ReferenceEquals(controller.ChooseRecommendation(new List<Candidate> { nature, anime }, pool, technical), nature), "推荐池因技术标签改变原有排序：" + pool);
                }
                AssertNoExternalDislikeCalls(controller);
                return new { poolsChecked = 3, topicPenaltyChangesRank = true, technicalTagsDoNotChangeRank = true };
            });
            Check(results, "dislike_feedback_reader_defaults_only_when_missing_and_rejects_corruption", delegate
            {
                string directory = Path.Combine(fixtureRoot, "feedback-reader"); Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "feedback.json");
                Require(PickerController.ReadFeedback(directory).Dislikes.Count == 0 && !File.Exists(path), "旧数据无反馈文件时没有返回空记录。");
                int rejected = 0;
                foreach (string contents in new[] { "null", "{\"Dislikes\":null}", "{\"Dislikes\":[null]}" })
                {
                    File.WriteAllText(path, contents, new UTF8Encoding(false));
                    Rejected(delegate { PickerController.ReadFeedback(directory); }); rejected++;
                    Require(File.ReadAllText(path, Encoding.UTF8) == contents, "损坏反馈被静默覆盖。");
                }
                return new { missingFileDefaultsToEmpty = true, corruptFilesRejected = rejected, corruptContentsPreserved = true };
            });
        }

        private static void ControllerTests(List<TestResult> results, string fixtureRoot)
        {
            Check(results, "controller_new_due_batch_schedules_immediate_cleanup", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc);
                var initial = new AppState { BatchId = "2026-10-21", PresentedBatch = "2026-10-21" };
                var controller = FixtureController(fixtureRoot, "due-cleanup-pending", initial, delegate { return now; });
                FieldInfo pending = typeof(PickerController).GetField("cleanupPending", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo next = typeof(PickerController).GetField("nextCleanupTry", BindingFlags.Instance | BindingFlags.NonPublic);
                Require(!(bool)pending.GetValue(controller), "夹具初始清理状态应为空闲。");
                Require(controller.ShouldPresent(), "到点未产生新一期。");
                Require((bool)pending.GetValue(controller), "换期后没有安排过期缓存清理。");
                Require((DateTime)next.GetValue(controller) == now, "换期清理没有安排在当前时间。");
                return new { batch = controller.State.BatchId, cleanupPending = true, nextCleanupTryUtc = ((DateTime)next.GetValue(controller)).ToString("o"), realEngineCalled = false };
            });
            Check(results, "controller_cleanup_while_acting_defers_without_engine_or_delete", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc);
                var controller = FixtureController(fixtureRoot, "acting-cleanup-deferred", new AppState { BatchId = "2026-10-22" }, delegate { return now; });
                string directory = Path.Combine(controller.DataDirectory, "cache", "870001");
                Directory.CreateDirectory(directory);
                string sentinel = Path.Combine(directory, "rollback-source.txt");
                File.WriteAllText(sentinel, "keep while a wallpaper operation is active", Encoding.UTF8);
                string before = CacheStore.Hash(sentinel);
                typeof(PickerController).GetField("acting", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(controller, true);
                Task cleanup = controller.Cleanup();
                Require(cleanup.IsCompleted, "正在操作时清理没有立即返回。");
                cleanup.GetAwaiter().GetResult();
                Require(File.Exists(sentinel) && CacheStore.Hash(sentinel) == before, "正在操作时清理删改了回滚可能需要的缓存。");
                bool pending = (bool)typeof(PickerController).GetField("cleanupPending", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                DateTime retry = (DateTime)typeof(PickerController).GetField("nextCleanupTry", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                Require(pending && retry == now, "忙时清理没有进入待重试状态。");
                Require(!File.Exists(Path.Combine(controller.DataDirectory, "runtime_output.txt")), "忙时清理不应进入壁纸读取并产生日志。");
                Require(Directory.GetFiles(Path.Combine(controller.DataDirectory, "jobs")).Length == 0, "忙时清理创建了 Steam 请求。");
                return new { completedSynchronously = true, cleanupPending = pending, nextCleanupTryUtc = retry.ToString("o"), sentinelSha256Before = before, sentinelSha256After = CacheStore.Hash(sentinel), deletedFiles = 0, engineReadbackLogProduced = false, steamRequests = 0, configuredEngineIsNonexistentFixture = true };
            });
            Check(results, "controller_due_offline_empty_group_once_and_restart", delegate
            {
                DateTime now = new DateTime(2026, 10, 28, 15, 0, 0, DateTimeKind.Utc);
                var initial = new AppState { BatchId = "2026-10-20", PresentedBatch = "2026-10-20", PreparedBatchId = "2026-10-21", AppliedId = "800001" };
                initial.Candidates.Add(new Candidate { Id = "800001", Ready = true });
                initial.PreparedCandidates.Add(new Candidate { Id = "800002", Ready = true });
                var controller = FixtureController(fixtureRoot, "offline-due", initial, delegate { return now; });
                Require(controller.ShouldPresent(), "离线到点未立即产生最新一期提醒。");
                var shown = controller.State;
                Require(shown.BatchId == "2026-10-28" && shown.Candidates.Count == 0, "漏天补提醒应是最新空组，不应继承过期候选。");
                Require(shown.PreparedCandidates.Count == 0 && shown.AppliedId == null, "过期预取或旧选择标记未清除。");
                Require(!controller.ShouldPresent(), "同一期重复弹窗。");
                var restarted = new PickerController(controller.DataDirectory, delegate { return now; });
                Require(!restarted.ShouldPresent(), "重启后重复提醒已展示的一期。");
                Require(Directory.GetFiles(Path.Combine(controller.DataDirectory, "jobs")).Length == 0, "离线提醒不应调用 Steam。");
                return new { batch = shown.BatchId, candidateCount = shown.Candidates.Count, firstPresented = true, repeatPresented = false, restartPresented = false, steamRequests = 0 };
            });
            Check(results, "controller_prefetch_isolated_until_23_00", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 14, 59, 59, DateTimeKind.Utc);
                var initial = new AppState { BatchId = "2026-10-21", PresentedBatch = "2026-10-21", PreparedBatchId = "2026-10-22" };
                initial.Candidates.Add(new Candidate { Id = "810000", Title = "previous" });
                for (int i = 1; i <= 3; i++) initial.PreparedCandidates.Add(new Candidate { Id = (810000 + i).ToString(), Title = "prepared" });
                var controller = FixtureController(fixtureRoot, "prefetch-isolation", initial, delegate { return now; });
                Require(!controller.ShouldPresent() && controller.State.Candidates[0].Id == "810000", "23 点前提前替换了当前组。");
                now = now.AddSeconds(1);
                Require(controller.ShouldPresent(), "23 点没有切换到已经准备的一期。");
                Require(controller.State.BatchId == "2026-10-22" && controller.State.Candidates.Count == 3, "新组候选不完整。");
                Require(controller.State.Candidates.All(c => c.Id != "810000"), "旧候选未过期。");
                var restarted = new PickerController(controller.DataDirectory, delegate { return now; });
                InvokeUpdate(restarted, "2026-10-22", "810001", delegate(Candidate c) { c.Downloaded = 123; });
                Require(!restarted.ShouldPresent() && restarted.State.Candidates[0].Downloaded == 123, "重启后的 prepared/current 列表别名覆盖了新进度。");
                return new { beforeDueId = "810000", afterDueIds = controller.State.Candidates.Select(c => c.Id).ToArray(), persistedProgressBytes = restarted.State.Candidates[0].Downloaded };
            });
            Check(results, "controller_paused_does_not_present_then_resumes_latest", delegate
            {
                DateTime now = new DateTime(2026, 10, 25, 15, 1, 0, DateTimeKind.Utc);
                var initial = new AppState { BatchId = "2026-10-21", PresentedBatch = "2026-10-21", Paused = true };
                var controller = FixtureController(fixtureRoot, "pause", initial, delegate { return now; });
                Require(!controller.ShouldPresent(), "暂停状态仍发出了提醒。");
                controller.SetPaused(false);
                Require(controller.ShouldPresent() && controller.State.BatchId == "2026-10-25", "恢复时没有只补最新一期。");
                return new { pausedPresented = false, resumedBatch = controller.State.BatchId };
            });
            Check(results, "controller_cross_day_download_callbacks_rejected", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc);
                var initial = new AppState { BatchId = "2026-10-22", PreparedBatchId = "2026-10-21" };
                initial.Candidates.Add(new Candidate { Id = "820001", Downloaded = 7 });
                initial.PreparedCandidates.Add(new Candidate { Id = "820001", Downloaded = 8 });
                var controller = FixtureController(fixtureRoot, "stale-callback", initial, delegate { return now; });
                InvokeUpdate(controller, "2026-10-21", "820001", delegate(Candidate c) { c.Downloaded = 999; c.Ready = true; });
                Require(controller.State.Candidates[0].Downloaded == 7 && controller.State.PreparedCandidates[0].Downloaded == 8, "昨日结果写入了当前/过期状态。");
                InvokeUpdate(controller, "2026-10-22", "wrong-id", delegate(Candidate c) { c.Downloaded = 1000; });
                Require(controller.State.Candidates[0].Downloaded == 7, "错误壁纸编号被写入。");
                InvokeUpdate(controller, "2026-10-22", "820001", delegate(Candidate c) { c.Downloaded = 12; });
                Require(controller.State.Candidates[0].Downloaded == 12, "有效的当期进度未写入。");
                now = now.AddDays(1);
                controller.ShouldPresent();
                InvokeUpdate(controller, "2026-10-22", "820001", delegate(Candidate c) { c.Ready = true; });
                Require(controller.State.BatchId == "2026-10-23" && controller.State.Candidates.Count == 0, "跨日后旧下载恢复了旧组。");
                return new { rejectedOldUpdates = 2, rejectedWrongIds = 1, acceptedCurrentUpdates = 1, finalBatch = controller.State.BatchId };
            });
            Check(results, "controller_invalid_busy_and_expired_actions_rejected_before_io", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc);
                var initial = new AppState { BatchId = "2026-10-22" };
                initial.Candidates.Add(new Candidate { Id = "830001", Ready = true, CachePath = "does-not-exist", ProjectPath = "does-not-exist" });
                var controller = FixtureController(fixtureRoot, "invalid-actions", initial, delegate { return now; });
                string missingApply = Rejected(delegate { controller.ApplyAsync("missing").GetAwaiter().GetResult(); });
                string missingPreview = Rejected(delegate { controller.PreviewAsync("missing").GetAwaiter().GetResult(); });
                FieldInfo acting = typeof(PickerController).GetField("acting", BindingFlags.NonPublic | BindingFlags.Instance);
                acting.SetValue(controller, true);
                string busyApply = RejectedOperation(delegate { controller.ApplyAsync("830001").GetAwaiter().GetResult(); });
                string busyPreview = RejectedOperation(delegate { controller.PreviewAsync("830001").GetAwaiter().GetResult(); });
                acting.SetValue(controller, false);
                now = now.AddDays(1);
                string staleApply = Rejected(delegate { controller.ApplyAsync("830001").GetAwaiter().GetResult(); });
                string stalePreview = Rejected(delegate { controller.PreviewAsync("830001").GetAwaiter().GetResult(); });
                Require(controller.State.Choices.Count == 0 && controller.State.AppliedId == null && !controller.Busy, "拒绝操作改变了选择状态。");
                Require(Directory.GetFiles(Path.Combine(controller.DataDirectory, "jobs")).Length == 0, "拒绝操作仍创建了 Steam 请求。");
                return new { rejectedCount = 6, errors = new[] { missingApply, missingPreview, busyApply, busyPreview, staleApply, stalePreview }, choiceCount = 0, steamRequests = 0 };
            });
            Check(results, "controller_corrupt_cache_failure_releases_busy_and_propagates", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc);
                string source = Path.Combine(fixtureRoot, "controller-corrupt-source");
                CreateWallpaperFixture(source);
                string project = CacheStore.Import(source, Path.Combine(fixtureRoot, "controller-corrupt-cache"), "830002", 1024 * 1024);
                string dir = Path.GetDirectoryName(project);
                File.AppendAllText(Path.Combine(dir, "media", "海边.mp4"), "tamper", Encoding.UTF8);
                var initial = new AppState { BatchId = "2026-10-22" };
                initial.Candidates.Add(new Candidate { Id = "830002", Ready = true, CachePath = dir, ProjectPath = project });
                var controller = FixtureController(fixtureRoot, "corrupt-actions", initial, delegate { return now; });
                string preview = Rejected(delegate { controller.PreviewAsync("830002").GetAwaiter().GetResult(); });
                Require(!controller.Busy && controller.State.Message.StartsWith("预览未打开", StringComparison.Ordinal), "预览失败没有释放忙状态或显示原因。");
                string apply = Rejected(delegate { controller.ApplyAsync("830002").GetAwaiter().GetResult(); });
                Require(!controller.Busy && controller.State.Choices.Count == 0 && controller.State.AppliedId == null, "失败应用被记录为选择。");
                return new { previewError = preview, applyError = apply, busyReleased = true, appliedId = controller.State.AppliedId, choiceCount = controller.State.Choices.Count };
            });
            Check(results, "controller_confirmed_apply_cleanup_failure_keeps_success", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc);
                var controller = FixtureController(fixtureRoot, "confirmed-cleanup-failure", new AppState { BatchId = "2026-10-22" }, delegate { return now; });
                var candidate = new Candidate { Id = "840001", Title = "离线成功状态夹具", CachePath = Path.Combine(controller.DataDirectory, "cache", "840001"), Tags = new[] { "Nature" } };
                int cleanupCalls = 0;
                controller.RecordConfirmedChoiceAsync(candidate, "2026-10-22", delegate { cleanupCalls++; throw new IOException("simulated cleanup failure"); }).GetAwaiter().GetResult();
                var state = controller.State;
                Require(state.AppliedId == candidate.Id && state.Choices.Count == 1, "清理失败抹掉了已确认的选择。");
                Require(state.Message.StartsWith("已换上", StringComparison.Ordinal) && state.Message.Contains("稍后重试"), "清理失败被误报为应用失败。");
                var persisted = Json.Read<AppState>(Path.Combine(controller.DataDirectory, "state.json"));
                Require(persisted.AppliedId == candidate.Id && persisted.Choices[0].Id == candidate.Id, "已确认选择没有写入记录。");
                Require((bool)typeof(PickerController).GetField("cleanupPending", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(controller), "失败清理没有进入重试队列。");
                return new { simulatedPostConfirmationOnly = true, cleanupCalls = cleanupCalls, appliedId = state.AppliedId, message = state.Message, choices = state.Choices.Count, cleanupRetryPending = true };
            });
            Check(results, "controller_confirmed_apply_save_failure_keeps_success", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc);
                var controller = FixtureController(fixtureRoot, "confirmed-save-failure", new AppState { BatchId = "2026-10-22" }, delegate { return now; });
                var candidate = new Candidate { Id = "840002", Title = "记录写入失败夹具", Tags = new[] { "Nature" } };
                int cleanupCalls = 0;
                using (var lockedState = new FileStream(Path.Combine(controller.DataDirectory, "state.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    controller.RecordConfirmedChoiceAsync(candidate, "2026-10-22", delegate { cleanupCalls++; return Task.FromResult(0); }).GetAwaiter().GetResult();
                Require(controller.State.AppliedId == candidate.Id && controller.State.Message.Contains("记录暂未保存"), "记录写入失败被误报为切换失败。");
                return new { simulatedPostConfirmationOnly = true, cleanupCalls = cleanupCalls, appliedId = controller.State.AppliedId, message = controller.State.Message };
            });
            Check(results, "controller_late_confirmed_apply_does_not_replace_new_batch_message", delegate
            {
                DateTime now = new DateTime(2026, 10, 23, 15, 0, 0, DateTimeKind.Utc);
                var controller = FixtureController(fixtureRoot, "late-confirmed-apply", new AppState { BatchId = "2026-10-23", Message = "new batch message" }, delegate { return now; });
                controller.RecordConfirmedChoiceAsync(new Candidate { Id = "840003", Title = "旧组点击" }, "2026-10-22", delegate { return Task.FromResult(0); }).GetAwaiter().GetResult();
                Require(controller.State.Message == "new batch message" && controller.State.AppliedId == null && controller.State.Choices[0].Id == "840003", "旧组应用回执覆盖了新组界面。");
                return new { newBatchMessageRetained = true, successfulChoiceRecorded = true, appliedIdForNewBatch = controller.State.AppliedId };
            });
            Check(results, "controller_cache_retention_ten_choices_three_candidates_active_protection", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc);
                var initial = new AppState { BatchId = "2026-10-22" };
                for (int i = 1; i <= 12; i++) initial.Choices.Add(new Choice { Id = (850000 + i).ToString(), Utc = now.AddDays(-i).ToString("o") });
                for (int i = 13; i <= 15; i++) initial.Candidates.Add(new Candidate { Id = (850000 + i).ToString() });
                var controller = FixtureController(fixtureRoot, "retention", initial, delegate { return now; });
                string cache = Path.Combine(controller.DataDirectory, "cache");
                for (int i = 1; i <= 17; i++) { string dir = Path.Combine(cache, (850000 + i).ToString()); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "sentinel.txt"), "own generated cache " + i, Encoding.UTF8); }
                string original = Path.Combine(fixtureRoot, "retention-workshop-original");
                Directory.CreateDirectory(original);
                string sentinel = Path.Combine(original, "original.txt");
                File.WriteAllText(sentinel, "never part of owned cache", Encoding.UTF8);
                string originalHash = CacheStore.Hash(sentinel);
                typeof(PickerController).GetField("activeCache", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(controller, Path.Combine(cache, "850017"));
                string[] deletions = controller.FindCleanupCandidates(Path.Combine(cache, "850011", "project.json"), Path.Combine(cache, "850012", "project.json"));
                Require(deletions.Length == 1 && Path.GetFileName(deletions[0]) == "850016", "最近 10 张、当天 3 张或正在使用/预览/操作的资源被列入清理。");
                foreach (string dir in deletions) CacheStore.SafeDelete(dir, cache);
                Require(Directory.GetDirectories(cache).Length == 16, "实际夹具删除数量错误。");
                Require(CacheStore.Hash(sentinel) == originalHash, "工坊原始哨兵被修改。");
                string rejected = Rejected(delegate { controller.FindCleanupCandidates(null, null); });
                return new { selectedHistoryCount = 12, retainedRecentChoices = 10, retainedDailyCandidates = 3, protectedCurrent = "850011", protectedPreview = "850012", protectedActiveOperation = "850017", deletedOwnedIds = deletions.Select(Path.GetFileName).ToArray(), remainingOwnedDirectories = 16, originalHashBefore = originalHash, originalHashAfter = CacheStore.Hash(sentinel), missingReadbackRejected = rejected };
            });
            Check(results, "worker_nullable_failure_reply_preserves_reason", delegate
            {
                var reply = Json.Decode<WorkerReply>("{\"ok\":false,\"error\":\"Steam not logged in\",\"subscribedBefore\":null,\"subscribedAfter\":null}");
                Require(!reply.ok && reply.error == "Steam not logged in" && !reply.subscribedBefore.HasValue && !reply.subscribedAfter.HasValue, "失败回执被 nullable 反序列化错误掩盖。");
                return new { originalReason = reply.error, nullableFieldsPreserved = true };
            });
            Check(results, "worker_download_identity_and_subscription_validation", delegate
            {
                Func<WorkerReply> valid = delegate { return new WorkerReply { ok = true, id = "860001", appId = 431960, installPath = Path.Combine(fixtureRoot, "worker-result-fixture"), subscribedBefore = false, subscribedAfter = false }; };
                PickerController.ValidateDownloadReply(valid(), "860001");
                var reasons = new List<string>();
                var reply = valid(); reply.id = "860002"; reasons.Add(Rejected(delegate { PickerController.ValidateDownloadReply(reply, "860001"); }));
                reply = valid(); reply.appId = null; reasons.Add(Rejected(delegate { PickerController.ValidateDownloadReply(reply, "860001"); }));
                reply = valid(); reply.appId = 123; reasons.Add(Rejected(delegate { PickerController.ValidateDownloadReply(reply, "860001"); }));
                reply = valid(); reply.subscribedBefore = null; reasons.Add(Rejected(delegate { PickerController.ValidateDownloadReply(reply, "860001"); }));
                reply = valid(); reply.subscribedAfter = null; reasons.Add(Rejected(delegate { PickerController.ValidateDownloadReply(reply, "860001"); }));
                reply = valid(); reply.subscribedAfter = true; reasons.Add(Rejected(delegate { PickerController.ValidateDownloadReply(reply, "860001"); }));
                reply = valid(); reply.installPath = ""; reasons.Add(Rejected(delegate { PickerController.ValidateDownloadReply(reply, "860001"); }));
                reply = valid(); reply.ok = false; reasons.Add(Rejected(delegate { PickerController.ValidateDownloadReply(reply, "860001"); }));
                return new { acceptedCompleteReply = true, rejectedCount = reasons.Count, reasons = reasons };
            });
        }

        private static void HoverPreviewTests(List<TestResult> results, string fixtureRoot)
        {
            Check(results, "hover_verified_video_lease_keeps_state_and_preferences_unchanged", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc);
                string directory;
                var controller = HoverFixtureController(fixtureRoot, "hover-video", delegate { return now; }, out directory);
                string before = CacheStore.Hash(Path.Combine(controller.DataDirectory, "state.json"));
                string mediaHash = CacheStore.Hash(Path.Combine(directory, "clip.mp4"));
                HoverPreviewLease lease = controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult();
                Require(lease.IsVideo && !lease.IsScene && lease.ItemId == "870001" && lease.BatchId == "2026-10-22", "视频 lease 身份或类型错误。");
                Require(lease.ProjectPath == Path.Combine(directory, "project.json") && lease.MediaPath == Path.Combine(directory, "clip.mp4"), "未返回自有缓存内实际校验路径。");
                Require(controller.IsHoverPreviewCurrent(lease), "当期 Ready 视频的 lease 被判为过期。");
                lease.Dispose(); lease.Dispose();
                Require(lease.IsDisposed && !controller.IsHoverPreviewCurrent(lease), "已释放 lease 仍可播放，或重复释放有副作用。");
                Require(CacheStore.Hash(Path.Combine(controller.DataDirectory, "state.json")) == before && CacheStore.Hash(lease.MediaPath) == mediaHash, "悬停校验更改了选择记录或媒体内容。");
                Require(controller.State.Choices.Count == 0 && controller.State.SeedTags.Count == 0 && controller.State.AppliedId == null, "悬停被计为选择或偏好。");
                Require(Directory.GetFiles(Path.Combine(controller.DataDirectory, "jobs")).Length == 0, "悬停调用了 Steam。");
                return new { isVideo = true, mediaPath = lease.MediaPath, stateSha256 = before, mediaSha256 = mediaHash, idempotentRelease = true, steamRequests = 0, engineIsNonexistentFixture = true };
            });
            Check(results, "hover_verified_scene_uses_actual_package_for_packed_project", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc); string directory;
                var controller = HoverFixtureController(fixtureRoot, "hover-scene", delegate { return now; }, out directory);
                File.WriteAllBytes(Path.Combine(directory, "scene.pkg"), new byte[] { 1, 3, 5, 7 });
                WriteHoverProject(directory, "scene", "scene.json");
                using (var lease = controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult())
                {
                    Require(lease.IsScene && !lease.IsVideo && lease.MediaPath == Path.Combine(directory, "scene.pkg") && File.Exists(lease.MediaPath), "打包 Scene 未返回校验过的实际包文件。");
                    Require(controller.IsHoverPreviewCurrent(lease), "Scene lease 无效。");
                    return new { isScene = true, packagePath = lease.MediaPath, sceneRuntimeLaunched = false };
                }
            });
            Check(results, "hover_absolute_relative_uri_and_drive_paths_rejected", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc); string directory;
                var controller = HoverFixtureController(fixtureRoot, "hover-paths", delegate { return now; }, out directory);
                string external = Path.Combine(Path.GetDirectoryName(directory), "external.mp4");
                File.WriteAllBytes(external, new byte[] { 1, 2, 3 }); string sentinel = CacheStore.Hash(external);
                var errors = new List<string>();
                foreach (string invalid in new[] { "../external.mp4", external, Path.Combine(directory, "clip.mp4"), "C:clip.mp4", "https://example.invalid/clip.mp4" })
                {
                    WriteHoverProject(directory, "video", invalid);
                    errors.Add(Rejected(delegate { controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult(); }));
                }
                Require(CacheStore.Hash(external) == sentinel, "拒绝路径越界修改了外部文件。");
                Require(controller.State.Candidates[0].Ready && controller.State.Choices.Count == 0, "悬停失败改变了候选状态或选择。");
                return new { rejectedCount = errors.Count, errors = errors, externalSha256 = sentinel, absoluteInsideCacheAlsoRejected = true };
            });
            Check(results, "hover_nonmedia_missing_empty_and_tampered_payloads_rejected", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc); string directory;
                var controller = HoverFixtureController(fixtureRoot, "hover-invalid-media", delegate { return now; }, out directory);
                var errors = new List<string>();
                foreach (string type in new[] { "application", "web", "preset", "" })
                {
                    WriteHoverProject(directory, type, "clip.mp4");
                    errors.Add(Rejected(delegate { controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult(); }));
                }
                WriteHoverProject(directory, "video", "missing.mp4");
                errors.Add(Rejected(delegate { controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult(); }));
                File.WriteAllBytes(Path.Combine(directory, "empty.mp4"), new byte[0]); WriteHoverProject(directory, "video", "empty.mp4");
                errors.Add(Rejected(delegate { controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult(); }));
                WriteHoverProject(directory, "video", "clip.mp4"); File.AppendAllText(Path.Combine(directory, "clip.mp4"), "tampered");
                errors.Add(Rejected(delegate { controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult(); }));
                Require(controller.State.Candidates[0].Ready && controller.State.Choices.Count == 0, "悬停失败不应重写推荐或选择。");
                return new { rejectedCount = errors.Count, errors = errors, chosenCount = 0 };
            });
            Check(results, "hover_external_cache_and_mismatched_project_rejected", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc); string directory;
                var controller = HoverFixtureController(fixtureRoot, "hover-owned-only", delegate { return now; }, out directory);
                string source = Path.Combine(fixtureRoot, "hover-outside-original"); CreateWallpaperFixture(source);
                string outsideProject = CacheStore.Import(source, Path.Combine(fixtureRoot, "hover-outside-cache"), "870001", 1024 * 1024);
                string originalHash = CacheStore.Hash(outsideProject);
                InvokeUpdate(controller, "2026-10-22", "870001", delegate(Candidate c) { c.CachePath = Path.GetDirectoryName(outsideProject); c.ProjectPath = outsideProject; });
                string outside = Rejected(delegate { controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult(); });
                InvokeUpdate(controller, "2026-10-22", "870001", delegate(Candidate c) { c.CachePath = directory; c.ProjectPath = outsideProject; });
                string mismatch = Rejected(delegate { controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult(); });
                Require(CacheStore.Hash(outsideProject) == originalHash, "外部缓存项目被修改。");
                return new { rejectedCount = 2, errors = new[] { outside, mismatch }, externalProjectSha256 = originalHash };
            });
            Check(results, "hover_multiple_leases_protect_expired_cache_until_last_release", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc); string directory;
                var controller = HoverFixtureController(fixtureRoot, "hover-retention", delegate { return now; }, out directory);
                var leases = Task.WhenAll(controller.AcquireHoverPreviewAsync("870001"), controller.AcquireHoverPreviewAsync("870001")).GetAwaiter().GetResult();
                string unrelatedDesktop = Path.Combine(fixtureRoot, "unrelated-desktop.mp4");
                now = now.AddDays(1);
                Require(!controller.IsHoverPreviewCurrent(leases[0]), "日期已过但尚未 Tick 时旧 lease 仍有效。");
                controller.ShouldPresent();
                Require(controller.State.Candidates.Count == 0, "跨日未清除候选夹具。");
                Require(!controller.FindCleanupCandidates(unrelatedDesktop, null).Contains(directory), "跨日清理列出了仍在播放的 lease 缓存。");
                leases[0].Dispose();
                Require(!controller.FindCleanupCandidates(unrelatedDesktop, null).Contains(directory), "释放一份 lease 提前清理了另一份仍在用的缓存。");
                leases[1].Dispose();
                string[] deletions = controller.FindCleanupCandidates(unrelatedDesktop, null);
                Require(deletions.Length == 1 && deletions[0] == directory, "最后 lease 释放后过期缓存未恢复可清理。");
                Require((bool)typeof(PickerController).GetField("cleanupPending", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller), "过期 lease 释放未安排延迟清理。");
                foreach (string path in deletions) CacheStore.SafeDelete(path, Path.Combine(controller.DataDirectory, "cache"));
                Require(!Directory.Exists(directory), "过期夹具没有被实际清理。");
                return new { concurrentLeases = 2, invalidBeforeTick = true, protectedAfterPromotion = true, protectedAfterFirstRelease = true, deletedOnlyAfterFinalRelease = true };
            });
            Check(results, "hover_corrupt_quarantine_waits_for_live_lease", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc); string directory;
                var controller = HoverFixtureController(fixtureRoot, "hover-quarantine", delegate { return now; }, out directory);
                var lease = controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult();
                File.AppendAllText(lease.MediaPath, "simulated corruption");
                string current = Path.Combine(fixtureRoot, "unrelated-desktop.mp4");
                string rejection = Rejected(delegate { controller.QuarantineCorruptCacheAsync(directory, "870001", current).GetAwaiter().GetResult(); });
                Require(Directory.Exists(directory) && File.Exists(lease.MediaPath), "lease 存活期间腐坏隔离移动了目录。");
                lease.Dispose();
                controller.QuarantineCorruptCacheAsync(directory, "870001", current).GetAwaiter().GetResult();
                Require(!Directory.Exists(directory) && Directory.GetDirectories(Path.Combine(controller.DataDirectory, "quarantine")).Length == 1, "lease 释放后腐坏缓存未按范围隔离。");
                return new { protectedWhileLeased = true, movedAfterRelease = true, rejection = rejection };
            });
            Check(results, "hover_waiting_acquire_rechecks_due_after_cleanup_gate", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc); string directory;
                var controller = HoverFixtureController(fixtureRoot, "hover-gate", delegate { return now; }, out directory);
                var gate = (SemaphoreSlim)typeof(PickerController).GetField("cleanupGate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                gate.Wait(); Task<HoverPreviewLease> pending;
                try { pending = controller.AcquireHoverPreviewAsync("870001"); Require(!pending.IsCompleted, "悬停读取越过了缓存变更锁。"); now = now.AddDays(1); }
                finally { gate.Release(); }
                string rejection = Rejected(delegate { pending.GetAwaiter().GetResult(); });
                Require(File.Exists(Path.Combine(directory, "clip.mp4")), "等待后过期的悬停操作修改了缓存。");
                return new { waitedForMutationGate = true, staleWaiterRejected = true, rejection = rejection };
            });
            Check(results, "hover_ready_change_and_busy_cancel_current_without_choice", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc); string directory;
                var controller = HoverFixtureController(fixtureRoot, "hover-invalidation", delegate { return now; }, out directory);
                using (var lease = controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult())
                {
                    FieldInfo acting = typeof(PickerController).GetField("acting", BindingFlags.Instance | BindingFlags.NonPublic);
                    acting.SetValue(controller, true);
                    Require(!controller.IsHoverPreviewCurrent(lease), "显式操作开始后悬停仍有效。");
                    string busy = RejectedOperation(delegate { controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult(); });
                    acting.SetValue(controller, false);
                    InvokeUpdate(controller, "2026-10-22", "870001", delegate(Candidate c) { c.Ready = false; });
                    Require(!controller.IsHoverPreviewCurrent(lease), "Ready 变更未使悬停失效。");
                    string unavailable = Rejected(delegate { controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult(); });
                    Require(controller.State.Choices.Count == 0 && controller.State.AppliedId == null, "失效悬停被记录为选择。");
                    return new { rejectedBusy = busy, rejectedNotReady = unavailable, choiceCount = 0 };
                }
            });
            Check(results, "hover_unchanged_fingerprint_reuses_verification_without_reading_media", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc); string directory;
                var controller = HoverFixtureController(fixtureRoot, "hover-reuse", delegate { return now; }, out directory);
                using (controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult()) { }
                string media = Path.Combine(directory, "clip.mp4");
                // Sharing denies content reads, so a second full SHA-256 pass would fail.
                using (var lockedMedia = new FileStream(media, FileMode.Open, FileAccess.Read, FileShare.None))
                using (var reused = controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult())
                    Require(controller.IsHoverPreviewCurrent(reused), "未变缓存仍重复读取完整视频。");
                string[] logs = File.ReadAllLines(Path.Combine(controller.DataDirectory, "runtime_output.txt"));
                Require(logs.Count(x => x.Contains("HOVER verified id=870001 cacheHit=false")) == 1 && logs.Count(x => x.Contains("HOVER verified id=870001 cacheHit=true")) == 1, "首遍完整校验/后续复用记录不正确。");
                return new { firstFullVerification = true, subsequentContentReadsDeniedButReuseSucceeded = true, cacheHits = 1 };
            });
            Check(results, "hover_timestamp_and_attributes_force_full_reverification", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc); string directory;
                var controller = HoverFixtureController(fixtureRoot, "hover-metadata", delegate { return now; }, out directory);
                string media = Path.Combine(directory, "clip.mp4");
                using (controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult()) { }
                string originalHash = CacheStore.Hash(media);
                File.SetLastWriteTimeUtc(media, File.GetLastWriteTimeUtc(media).AddSeconds(5));
                string rejection;
                using (var lockedMedia = new FileStream(media, FileMode.Open, FileAccess.Read, FileShare.None))
                    rejection = Rejected(delegate { controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult(); });
                using (controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult()) { }
                File.SetAttributes(media, File.GetAttributes(media) | FileAttributes.Hidden);
                using (controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult()) { }
                string[] logs = File.ReadAllLines(Path.Combine(controller.DataDirectory, "runtime_output.txt"));
                Require(logs.Count(x => x.Contains("HOVER verified id=870001 cacheHit=false")) == 3 && !logs.Any(x => x.Contains("cacheHit=true")), "元数据变化后错误复用了旧校验。");
                Require(CacheStore.Hash(media) == originalHash, "元数据测试意外更改媒体内容。");
                return new { timestampChangeRequiredMediaRead = true, attributesChangeRequiredFullVerification = true, fullSuccessfulVerifications = 3, deniedReadRejection = rejection, unchangedContentSha256 = originalHash };
            });
            Check(results, "hover_directory_file_and_manifest_changes_invalidate_reuse", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc); string directory;
                var controller = HoverFixtureController(fixtureRoot, "hover-sets", delegate { return now; }, out directory);
                using (controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult()) { }
                Directory.CreateDirectory(Path.Combine(directory, "new-empty-directory"));
                using (controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult()) { }
                string extra = Path.Combine(directory, "unexpected.dat"); File.WriteAllBytes(extra, new byte[] { 8, 9 });
                string addedRejection = Rejected(delegate { controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult(); });
                File.Delete(extra);
                using (controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult()) { }
                string manifest = Path.Combine(directory, ".verified.json");
                File.AppendAllText(manifest, "\n", new UTF8Encoding(false));
                using (controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult()) { }
                var hashes = Json.Read<Dictionary<string, string>>(manifest); hashes["clip.mp4"] = new string('0', 64);
                File.WriteAllText(manifest, Json.Encode(hashes), new UTF8Encoding(false));
                string manifestRejection = Rejected(delegate { controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult(); });
                string[] logs = File.ReadAllLines(Path.Combine(controller.DataDirectory, "runtime_output.txt"));
                Require(logs.Count(x => x.Contains("HOVER verified id=870001 cacheHit=false")) == 4 && !logs.Any(x => x.Contains("cacheHit=true")), "目录、文件或校验清单变更未触发重校验。");
                return new { directorySetChangeReverified = true, unexpectedFileRejected = addedRejection, manifestMetadataChangeReverified = true, manifestTamperRejected = manifestRejection, fullSuccessfulVerifications = 4 };
            });
            Check(results, "hover_same_length_content_tamper_after_reuse_rejected", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc); string directory;
                var controller = HoverFixtureController(fixtureRoot, "hover-retamper", delegate { return now; }, out directory);
                using (controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult()) { }
                using (controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult()) { }
                string media = Path.Combine(directory, "clip.mp4"); byte[] bytes = File.ReadAllBytes(media); bytes[0] ^= 0x7f;
                DateTime previousWrite = File.GetLastWriteTimeUtc(media); File.WriteAllBytes(media, bytes); File.SetLastWriteTimeUtc(media, previousWrite.AddSeconds(7));
                string rejection = Rejected(delegate { controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult(); });
                Require(controller.State.Choices.Count == 0 && controller.State.Candidates[0].Ready, "缓存重校验失败污染了选择或状态。");
                return new { sameLengthTamperDetected = true, rejection = rejection, stateUnchanged = true };
            });
            Check(results, "hover_warmup_is_deduplicated_video_only_and_releases_all_leases", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc); string directory;
                var controller = HoverFixtureController(fixtureRoot, "hover-warm", delegate { return now; }, out directory);
                var initial = controller.State; initial.Candidates[0].Tags = new[] { "Everyone", "Video" };
                initial.Candidates.Add(new Candidate { Id = "870002", Ready = true, Tags = new[] { "Scene" }, ProjectPath = "must-not-read-scene" });
                initial.Candidates.Add(new Candidate { Id = "870003", Ready = false, Tags = new[] { "Video" } });
                initial.Candidates.Add(new Candidate { Id = "870004", Ready = true, Tags = new string[0] });
                initial.Candidates.Add(new Candidate { Id = "870005", Ready = true, Tags = new[] { "Video", "Scene" } });
                string statePath = Path.Combine(controller.DataDirectory, "state.json"); Json.Write(statePath, initial);
                controller = new PickerController(controller.DataDirectory, delegate { return now; });
                string before = CacheStore.Hash(statePath);
                var gate = (SemaphoreSlim)typeof(PickerController).GetField("cleanupGate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                Task first, second; gate.Wait();
                try { first = controller.WarmHoverPreviewsAsync(); second = controller.WarmHoverPreviewsAsync(); Require(!first.IsCompleted && !second.IsCompleted, "热身没有返回进行中的后台任务。"); }
                finally { gate.Release(); }
                Task.WhenAll(first, second).GetAwaiter().GetResult();
                controller.WarmHoverPreviewsAsync().GetAwaiter().GetResult();
                string[] logs = File.ReadAllLines(Path.Combine(controller.DataDirectory, "runtime_output.txt"));
                Require(logs.Count(x => x.Contains("HOVER lease-acquired")) == 1 && logs.Count(x => x.Contains("HOVER lease-released")) == 1 && logs.Count(x => x.Contains("HOVER warm-ready")) == 1, "同批次重复热身、读取了非Video项或泄漏lease。");
                Require(CacheStore.Hash(statePath) == before && controller.State.Choices.Count == 0 && controller.State.SeedTags.Count == 0, "后台热身污染了推荐和偏好。");
                using (controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult()) { }
                Require(File.ReadAllText(Path.Combine(controller.DataDirectory, "runtime_output.txt")).Contains("HOVER verified id=870001 cacheHit=true"), "热身结果未被后续悬停复用。");
                now = now.AddDays(1); controller.ShouldPresent();
                Require(controller.FindCleanupCandidates(Path.Combine(fixtureRoot, "unrelated-desktop.mp4"), null).Contains(directory), "热身结束后仍有缓存保护lease残留。");
                Require(Directory.GetFiles(Path.Combine(controller.DataDirectory, "jobs")).Length == 0, "后台热身调用了Steam。");
                return new { eligibleVideos = 1, ignoredCandidates = 4, concurrentCallsDeduplicated = true, repeatedCallDeduplicated = true, hoverCacheHitAfterWarmup = true, survivingLeases = 0, stateSha256 = before, engineIsNonexistentFixture = true };
            });
            Check(results, "hover_same_size_tamper_with_restored_timestamps_uses_ntfs_change_time", delegate
            {
                DateTime now = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc); string directory;
                var controller = HoverFixtureController(fixtureRoot, "hover-ntfs-change", delegate { return now; }, out directory);
                using (controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult()) { }
                string media = Path.Combine(directory, "clip.mp4"); long originalLength = new FileInfo(media).Length;
                DateTime write = File.GetLastWriteTimeUtc(media), created = File.GetCreationTimeUtc(media), dirWrite = Directory.GetLastWriteTimeUtc(directory);
                FileAttributes attributes = File.GetAttributes(media); byte[] bytes = File.ReadAllBytes(media); bytes[0] ^= 0x7f;
                File.WriteAllBytes(media, bytes); File.SetLastWriteTimeUtc(media, write); File.SetCreationTimeUtc(media, created); File.SetAttributes(media, attributes); Directory.SetLastWriteTimeUtc(directory, dirWrite);
                Require(new FileInfo(media).Length == originalLength && File.GetLastWriteTimeUtc(media) == write && File.GetCreationTimeUtc(media) == created && File.GetAttributes(media) == attributes, "夹具未成功恢复常规元数据。");
                string rejection = Rejected(delegate { controller.AcquireHoverPreviewAsync("870001").GetAwaiter().GetResult(); });
                return new { fileSizeAndCommonMetadataRestored = true, ntfsChangeTimeInvalidatedReuse = true, rejection = rejection };
            });
        }

        private static PickerController HoverFixtureController(string fixtureRoot, string name, Func<DateTime> clock, out string directory)
        {
            string root = Path.Combine(fixtureRoot, "controllers", name);
            directory = Path.Combine(root, "cache", "870001"); Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "clip.mp4"), Encoding.UTF8.GetBytes("OFFLINE FIXTURE ONLY - NOT A REAL VIDEO"));
            WriteHoverProject(directory, "video", "clip.mp4");
            var state = new AppState { BatchId = Schedule.Due(clock()), Message = "unchanged fixture state" };
            state.Candidates.Add(new Candidate { Id = "870001", Ready = true, CachePath = directory, ProjectPath = Path.Combine(directory, "project.json") });
            return FixtureController(fixtureRoot, name, state, clock);
        }

        private static void WriteHoverProject(string directory, string type, string file)
        {
            File.WriteAllText(Path.Combine(directory, "project.json"), Json.Encode(new { type = type, file = file }), new UTF8Encoding(false));
            var hashes = new Dictionary<string, string>();
            foreach (string path in CacheStore.Files(directory))
            {
                string relative = path.Substring(directory.TrimEnd('\\').Length + 1);
                if (relative == ".verified.json" || relative == ".verified.json.previous") continue;
                hashes.Add(relative, CacheStore.Hash(path));
            }
            File.WriteAllText(Path.Combine(directory, ".verified.json"), Json.Encode(hashes), new UTF8Encoding(false));
        }

        private static PickerController FixtureController(string fixtureRoot, string name, AppState state, Func<DateTime> clock)
        {
            return FixtureController(fixtureRoot, name, state, clock, null);
        }

        private static PickerController FixtureController(string fixtureRoot, string name, AppState state, Func<DateTime> clock, Func<string, object, Action<WorkerProgress>, Task<WorkerReply>> workerCall)
        {
            string root = Path.Combine(fixtureRoot, "controllers", name);
            Directory.CreateDirectory(root);
            Json.Write(Path.Combine(root, "config.json"), new PickerConfig { WallpaperExe = Path.Combine(root, "not-a-real-wallpaper-engine.exe"), SteamBin = Path.Combine(root, "not-a-real-steam-bin"), MaxSizeMB = 300, RetainCount = 10 });
            Json.Write(Path.Combine(root, "state.json"), state);
            return new PickerController(root, clock, workerCall);
        }

        private static void InvokeUpdate(PickerController controller, string batch, string id, Action<Candidate> update)
        {
            MethodInfo method = typeof(PickerController).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
            try { method.Invoke(controller, new object[] { batch, id, update }); }
            catch (TargetInvocationException ex) { throw ex.InnerException; }
        }

        private static string RejectedOperation(Action action)
        {
            try { action(); }
            catch (InvalidOperationException ex) { return ex.GetType().Name + ": " + ex.Message; }
            throw new IOException("重复操作未被拒绝。");
        }

        private static object ScheduleCase(DateTime utc, string due, string prepare, string beijing)
        {
            string actualDue = Schedule.Due(utc);
            string actualPrepare = Schedule.Prepare(utc);
            string actualBeijing = Schedule.Beijing(utc).ToString("yyyy-MM-dd HH:mm:ss");
            Require(actualDue == due, "Due 错误：预期 " + due + "，实际 " + actualDue);
            Require(actualPrepare == prepare, "Prepare 错误：预期 " + prepare + "，实际 " + actualPrepare);
            Require(actualBeijing == beijing, "北京时间转换不正确。");
            return new { utc = utc.ToString("o"), beijing = actualBeijing, due = actualDue, prepare = actualPrepare };
        }

        private static void CreateWallpaperFixture(string root)
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "media"));
            Directory.CreateDirectory(Path.Combine(root, "说明", "另一空目录"));
            Directory.CreateDirectory(Path.Combine(root, "空目录"));
            string hiddenDirectory = Path.Combine(root, ".hidden-folder");
            Directory.CreateDirectory(hiddenDirectory);
            File.SetAttributes(hiddenDirectory, File.GetAttributes(hiddenDirectory) | FileAttributes.Hidden);
            File.WriteAllBytes(Path.Combine(root, "media", "海边.mp4"), Encoding.UTF8.GetBytes("TEST FIXTURE ONLY - NOT A REAL VIDEO\n"));
            File.WriteAllText(Path.Combine(root, "说明", "选择记录.txt"), "中文路径、空目录与内容哈希测试。\r\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(hiddenDirectory, "metadata.json"), "{\"fixture\":true}", new UTF8Encoding(false));
            string hiddenFile = Path.Combine(root, ".hidden-data.bin");
            File.WriteAllBytes(hiddenFile, new byte[] { 0, 255, 127, 12, 34, 56 });
            File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);
            Json.Write(Path.Combine(root, "project.json"), new { title = "离线测试夹具", type = "video", file = "media/海边.mp4" });
        }

        private static HashSet<string> RelativeFiles(string root)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return new HashSet<string>(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(p => Path.GetFullPath(p).Substring(prefix.Length)), StringComparer.OrdinalIgnoreCase);
        }

        private static HashSet<string> RelativeDirectories(string root)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return new HashSet<string>(Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Select(p => Path.GetFullPath(p).Substring(prefix.Length)), StringComparer.OrdinalIgnoreCase);
        }

        private static string Rejected(Action action)
        {
            try { action(); }
            catch (IOException ex) { return ex.GetType().Name + ": " + ex.Message; }
            throw new InvalidOperationException("危险或无效输入没有被拒绝。");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Check(List<TestResult> results, string name, Func<object> test)
        {
            var timer = Stopwatch.StartNew();
            var result = new TestResult { name = name, passed = false };
            try { result.details = test(); result.passed = true; }
            catch (Exception ex) { result.error = ex.ToString(); }
            timer.Stop();
            result.durationMs = timer.ElapsedMilliseconds;
            results.Add(result);
        }
    }
}
