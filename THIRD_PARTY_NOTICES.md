# 第三方说明

DailyWallpaperPicker 是独立的社区工具。本仓库的许可只授予本项目有权授权的源码与原创内容，不替代第三方软件、接口或创意工坊作品的许可。

## Wallpaper Engine

预览和桌面应用依赖用户自行安装的 Wallpaper Engine。程序调用其本机命令接口，并读取当前壁纸状态；仓库和发布包不包含 Wallpaper Engine 程序、资源或许可证。

接口参考：[Wallpaper Engine 命令行控制](https://help.wallpaperengine.io/en/functionality/cli.html)。Wallpaper Engine 的名称和相关权利属于各自权利人，本项目不是其官方产品。

## Steam 与 Steamworks

查询和下载由独立工作进程调用用户本机已有的 Steamworks 接口，使用当前 Steam 登录会话。本仓库和发布包不附带 Steam 客户端、Steamworks SDK、Steam DLL 或账号凭据；运行时所需组件来自用户自行安装的软件。

本项目采用 GPL-3.0-only，并对 Steamworks 提供 GPLv3 第 7 条下的额外链接许可，见 [LICENSE.STEAMWORKS-EXCEPTION](LICENSE.STEAMWORKS-EXCEPTION)。该例外不替代第三方许可，也不授予复制或分发 Steam DLL、SDK、客户端、Wallpaper Engine 或工坊作品的权利。

接口参考：[ISteamUGC 官方文档](https://partner.steamgames.com/doc/api/ISteamUGC?l=english)。Steam 和 Steamworks 保留其各自条款与权利；具体本项目授权条件以仓库许可文件为准。

## 创意工坊作品

候选封面和壁纸属于各自作者。它们由用户在运行工具时通过 Steam 获取，并保存在本机；公开仓库和发布包不包含测试使用的壁纸、封面、Scene 包或视频。

工具允许预览和应用，不代表壁纸可以再次公开分发或用于商业用途。相关使用条件请查看作品页面和作者说明。

## Microsoft Windows 与 .NET Framework

本工具使用 Windows、.NET Framework / WPF 和系统媒体能力。发布包仅包含本项目编译的程序，不捆绑 Windows 或 .NET Framework。相关组件由用户系统提供，适用各自条款。

## 相关项目链接

README 中的 AnimatedThisPC 仅是作者另一项目的介绍链接。本仓库不包含其代码、媒体或发行包，也不改变它自身的许可。
