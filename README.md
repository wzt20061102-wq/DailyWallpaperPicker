# DailyWallpaperPicker · 每日壁纸三选一

每天给你三张新的 Wallpaper Engine 创意工坊壁纸。喜欢哪张，再换上桌面。

这是一个 Windows 小工具：北京时间每天 **23:00** 打开选择窗口，**22:50** 提前准备候选。只有点击「使用这张」才会切换壁纸；不选、关闭窗口或下载失败，都保留当前桌面。

当前为单屏预览版，依赖你已安装并可正常使用的 Steam 与 Wallpaper Engine。尚未完成不同电脑、真实重启和长期运行的完整验收，详情见[已知限制](docs/KNOWN_LIMITATIONS.md)和[验证记录](docs/VALIDATION.md)。

## 怎么用

1. 从 [Releases](https://github.com/wzt20061102-wq/DailyWallpaperPicker/releases) 下载 Windows x64 ZIP，完整解压到一个固定目录。
2. 登录 Steam，确认 Wallpaper Engine 能正常显示壁纸。
3. 双击 `Launch.cmd`。程序会查找本机 Steam 库中的 Wallpaper Engine，并开始准备最近一期候选；第一次需要联网下载。
4. 等待候选准备完成，把鼠标放到 Video 封面上看动态效果，或点击「预览」打开完整预览。确定喜欢后，再点击「使用这张」。

`Launch.cmd` 相当于运行 `DailyWallpaperPicker.exe --show`。请将包内三个 `.exe` 保持在同一目录，不要只复制主程序。程序首次启动不需要导入别人的候选或缓存，也不要求填写密码、Cookie 或 API Key。

关闭窗口会收起到托盘。右键托盘图标可以重新打开今日候选、暂停每日推荐或退出。便携运行退出后不再提醒；如果已有独立的每日 Windows 启动任务，任务仍可在约定时间重新启动程序。

### 可选：固定安装与登录启动

便携运行不需要安装，也不会自动添加登录启动。若需要固定安装，使用 PowerShell 7 运行发布包内的 `scripts/Install.ps1`；默认复制到“文档”中的 `DailyWallpaperPicker-PublicPreview`。安装脚本本身不会启动程序或添加登录启动。

在解压目录打开 PowerShell 7，运行：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install.ps1
```

在固定目录中，运行 `scripts/Enable-Startup.ps1` 可添加当前用户的 `DailyWallpaperPicker-PublicPreview` 登录任务。脚本检查是否已有旧版本任务，避免两个版本同时运行；不要在临时解压目录启用自启后再移动文件。

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\Enable-Startup.ps1
```

运行 `scripts/Disable-Startup.ps1` 可移除与当前安装匹配的登录任务。若程序仍在运行，请再从托盘选择「退出」。这些操作保留壁纸、缓存和选择记录。脚本的发布前验证范围见[验证记录](docs/VALIDATION.md)。

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\Disable-Startup.ps1
```

## 预览与选择

| 操作 | 效果 |
| --- | --- |
| 鼠标放到 Video 封面 | 预热完成后播放静音小预览；移开即暂停并回到封面 |
| 鼠标放到 Scene 封面 | 保持静态封面，不弹出说明气泡；需要动态效果时点击「预览」 |
| 点击「不喜欢」 | 只替换这张候选，记住反馈并减少相似题材推荐；当前桌面不变 |
| 点击「预览」 | 打开 Wallpaper Engine 独立预览，默认静音、最大化，可手动还原窗口 |
| 点击「使用这张」 | 应用所选壁纸，并读取当前壁纸确认结果 |
| 不选、关闭或下载失败 | 保留当前桌面壁纸 |

同一时间只播放一张预览。视频第一次打开可能需要几秒准备；为减少之后的悬停等待，窗口显示时会预先打开并暂停视频，这会增加内存占用。独立预览、收起窗口或退出时会释放这组悬停播放器。

## 每天推荐什么

- 每组从近期热门、新发布、历史热门候选中各选一张，优先匹配主屏比例。
- 初次读取 Wallpaper Engine 本地最近使用记录中的壁纸标签作为偏好线索；之后参考成功选用的壁纸。仅预览、关闭或不选，不算“不喜欢”。
- 新作保留探索性。推荐历史和偏好保存在本机，90 天内避免重复推荐。
- 明确点过「不喜欢」的作品不再推荐；相似题材按工坊标签降低排序权重，仍保留探索机会。反馈只存本机，不会发送给作者或 Steam。换图期间显示准备进度，失败时保留已保存的反馈并允许重试。
- 反馈在点击被接受时立即保存；即使等待换图期间跨过 23:00，反馈仍会保留，过期批次的换卡操作会取消。
- 默认仅选择标记为全年龄的 Scene 或 Video，单张不超过 300 MB。分类依据工坊标签，并非人工审核。
- 错过时间后，在下次运行时补最近一期，不连续弹出多天的窗口；程序不会唤醒关机或休眠的电脑。

当前每日时间固定为北京时间 23:00，不随 Windows 所在时区变化。下载尚未完成时会显示进度，网络或 Steam 暂不可用时显示原因并等待重试。

## 缓存与本地数据

程序把完整下载后的壁纸复制到自己的缓存，校验后再用于预览和应用。不自动订阅或取消 Steam 订阅，也不删除原有工坊文件。

通常保留最近选用的 10 张和当前最多 3 张候选；提前准备下一期时会短时多保留一组。正在显示或预览的资源受保护，无法可靠读回当前壁纸时会推迟清理。

便携版默认把数据保存在三个程序旁的 `data` 目录；使用可选安装脚本后，则位于“文档”中的 `DailyWallpaperPicker-PublicPreview/data`：

| 文件或目录 | 内容 |
| --- | --- |
| `config.json` | 本机 Wallpaper Engine 路径和缓存设置 |
| `state.json` | 候选、选择记录与偏好 |
| `feedback.json` | 点击「不喜欢」产生的作品 ID、时间及题材标签；首次反馈后创建 |
| `preferences.json` | 可选的本机排除规则，默认没有规则；支持 `ExcludedIds`、`ExcludedTags`、`ExcludedTitleKeywords` 数组 |
| `cache/` | 校验后的壁纸副本 |
| `thumbnails/` | 候选封面 |
| `runtime_output.txt` | 本机运行日志 |

`state.json` 带有格式版本，升级时自动迁移旧记录；遇到更新版本的数据会停止，避免覆盖。成功作业的辅助文件及时清理，失败作业最多保留 30 组、7 天；日志超过 4 MB 轮转，历史日志最多 5 份、14 天。

已有自定义安装可通过 `--data-dir` 指定数据位置，或在程序旁放置 `install-profile.json`（包含绝对路径 `DataDirectory`）。命令行参数优先；升级时保留此配置，公开包不携带任何人的数据路径。

停止使用工具不会自动删除这些数据。若桌面仍使用工具缓存里的壁纸，请先在 Wallpaper Engine 中切换到其他壁纸，再删除缓存。反馈问题时先检查日志，删去用户名、完整路径和不希望公开的壁纸记录。

升级时先从托盘退出程序，再用新版程序和随附文件更新原目录，保留自己的 `data` 目录。若想重置「不喜欢」，先退出程序，再将 `data/feedback.json` 移到目录外备存；下次启动会从空反馈开始。

## 运行要求

- 64 位 Windows，.NET Framework 4.8 和可用的 Windows 媒体播放组件。
- 已安装并拥有 Wallpaper Engine，Steam 客户端已登录；下载新候选需要联网。
- 当前仅验证单屏环境。检测到多屏时，程序会拒绝应用壁纸和清理缓存。

不附带 Steam、Wallpaper Engine 或任何工坊壁纸；这些需要你自行安装或通过 Steam 获取。Windows N、精简系统、不同视频编码及未来的 Steam / Wallpaper Engine 版本可能需要额外适配。

## 从源码构建

需要 Windows x64、.NET Framework 4.8 和 PowerShell 7。在仓库根目录运行：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test.ps1
```

构建结果在 `artifacts/bin`。可用 `DailyWallpaperPicker.sln` 查看主程序、两个辅助程序及独立测试工程；主程序不包含测试代码。离线测试默认使用新建的临时目录，外部进程启动边界会拒绝非测试调用，并在结果中记录实际计数。测试不会使用真实 Steam 或切换桌面；它不是操作系统级文件访问审计。`Build.ps1` 支持 `-OutputDirectory` 和 `-SourceRevision`；`Test.ps1` 支持 `-BinaryDirectory` 和 `-OutputDirectory`，测试输出目录必须是尚不存在的新目录。

程序文件属性、启动日志和 `build-info.json` 记录版本及构建提交。模块边界与维护约束见[代码结构说明](docs/ARCHITECTURE.md)。

运行 `scripts/Package.ps1` 可执行发布构建、检查并生成 ZIP。发布包只应包含程序、许可和使用文件；不要把运行后生成的 `data`、工坊媒体、个人日志或本机快捷方式重新打包上传。

## 许可与第三方

本项目源码采用 [GNU GPLv3，仅第 3 版](LICENSE)，并附带 GPLv3 第 7 条下的[Steamworks 链接例外](LICENSE.STEAMWORKS-EXCEPTION)。例外仅针对本项目与 Steamworks 的链接，不授权重新分发 Steam、Wallpaper Engine 或工坊作品。具体范围以许可文件为准，第三方说明见[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。本工具是独立社区项目，与 Valve 或 Wallpaper Engine 官方没有隶属关系。

## 我的另一个项目

[AnimatedThisPC · 动态此电脑](https://github.com/wzt20061102-wq/AnimatedThisPC)：让 Windows 桌面上的“此电脑”显示动画，并跟随 Wallpaper Engine 更新壁纸画面，同时保留原生桌面的打开、右键和拖动等交互。两个项目可以分别使用；安装方式、兼容性和许可请以各自仓库为准。
