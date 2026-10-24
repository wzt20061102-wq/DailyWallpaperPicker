# 代码结构与维护约定

仓库是唯一源码来源。本机部署和公开发布使用同一份构建产物；数据目录通过 `--data-dir` 指定，也可在程序旁的 `install-profile.json` 配置绝对 `DataDirectory`。没有参数或配置时使用程序旁的 `data`。安装配置和用户数据不进入发布包。

## 模块

| 部分 | 职责 |
| --- | --- |
| `Program`、`ProfilePaths` | 入口、单实例、启动模式、退出取消、数据位置 |
| `PickerWindow` | 窗口组合与交互；不承担播放器生命周期 |
| `CardView`、`PickerStyles`、`TrayManager` | 卡片、视觉样式、托盘资源 |
| `HoverPreviewManager` | 视频预热、悬停暂停、播放器和缓存租约释放 |
| `PickerController` 及职责分文件 | 调度、候选准备、应用、预览、清理的协调 |
| `FeedbackStore`、`RecommendationSelector` | 明确负反馈持久化与候选排序 |
| `ThumbnailDownloader` | 有截止时间和容量上限的封面下载 |
| `HoverPreviewVerifier`、`HoverPreviewLease` | 校验缓存复用及预览期间的资源保护 |
| `SteamClient`、`ProcessRunner` | 工作进程协议、超时、取消、子进程归属 |
| `CacheStore`、`WallpaperBridge` | 自有缓存校验与边界、Wallpaper Engine 独立预览与读回 |
| `StateMigration`、`Models` | 数据格式版本、业务状态和兼容迁移 |
| `DiagnosticsRetention` | 日志与工作进程辅助文件的有限保留 |
| `SteamWorker`、`WallpaperReadback` | 两个独立 x64 辅助程序 |
| `tests/` | 独立的离线回归测试程序，不编入主程序 |

## 必须保持的约束

- 仅明确点击「使用这张」允许应用壁纸；下载、换候选和悬停不调用桌面切换。
- 明确的不喜欢先持久化，再排队替换；跨日取消旧换卡任务不撤销已接受的反馈。
- 异步结果绑定批次与作品 ID；新的反馈在候选提交前再次检查。
- 锁只保护状态和短写入，避免在状态锁内等待网络或长任务。
- 页面根据 `MessageSeverity` 和 `DownloadStage` 呈现状态，不依赖提示文字。
- `state.json` 的结构变更必须增加迁移规则；未来版本的状态不得静默降级覆盖。
- 进程统一经过 `Processes`，测试可在启动边界拒绝意外外部调用。取消只结束本工具创建的子进程，不结束 Steam 或正在运行的 Wallpaper Engine。
- 只清理工具缓存；活跃预览、当前桌面、下载作业均受到保护。
- 成功作业立即清理辅助 JSON；失败作业最多保留 30 组、7 天。运行日志超过 4 MB 轮转，最多留 5 份历史日志、14 天。

## 常用工作流

使用解决方案查看各工程，以 `scripts/Build.ps1`、`scripts/Test.ps1` 和 `scripts/Package.ps1` 作为统一入口。发布从冻结的源码副本构建、测试和打包，安装同一份 ZIP 内的程序，避免本机另编一版。

测试应通过可替换时钟、工作进程委托、壁纸接口以及进程启动边界验证可观察行为。已有少数缓存保护测试保留内部实现检查；增加用例时优先验证输出、落盘状态和调用记录。

离线测试与实际 Steam 下载、媒体解码和 Windows 登录恢复是不同证据。发布说明需分别记录，不把离线通过当作所有电脑均已验收。
