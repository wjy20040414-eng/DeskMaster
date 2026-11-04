# DeskMaster

DeskMaster 是一个 Windows 11 桌面文件夹工具。文件夹收起时显示快捷方式缩略图，展开后可以从图标网格启动快捷方式。

## 启动

直接运行 `bin\Release\net8.0-windows\DeskMaster.exe` 需要安装 .NET 8 Desktop Runtime。从源码构建需要 .NET 8 或更新版本的 SDK。

```powershell
dotnet build -c Release
.\bin\Release\net8.0-windows\DeskMaster.exe
```

## 操作

- 注册一次右键菜单后，在桌面空白处右击，选择「显示更多选项」→「新建桌面文件夹」。也可用 `Shift + F10` 打开经典菜单。DeskMaster 会在需要时启动。
- 在同一个桌面右键菜单中，可以选择「显示桌面文件夹」或「退出 DeskMaster」。
- 单击圆角方形预览可展开。展开后，单击标题可收起，双击标题可直接编辑名称，按 Enter 保存或 Esc 取消。拖动预览或标题后松开鼠标，卡片会吸附到附近的空闲桌面格子。
- 从桌面或资源管理器拖入文件、文件夹或 Windows 快捷方式。展开后单击图标即可打开原项目。DeskMaster 会保留 `.lnk` 和 `.url` 的副本，删除桌面上的原快捷方式后仍能使用。
- 将快捷方式拖出卡片后松手，可将它从文件夹移除。右击快捷方式可直接编辑显示名称，按 Enter 保存或 Esc 取消。
- 右击卡片，打开「文件夹尺寸」，选择 1 倍、4 倍或 9 倍图标大小，对应 1×1、2×2、3×3。卡片按 Windows 桌面图标网格确定基础边长。
- 右击任意卡片，打开「动画设置（全局）」，可独立选择帧率和播放速度。默认随屏幕刷新并以 1× 播放，设置对所有文件夹生效，重启后仍会保留。
- 右击标题可重命名或删除文件夹。

文件夹卡片位于普通应用窗口下方。可从桌面右键菜单选择「显示桌面文件夹」重新显示卡片。位置、尺寸和文件引用保存于 `%LOCALAPPDATA%\DeskMaster\folders.json`。普通文件和文件夹仍通过原路径打开，快捷方式的副本和配置保存在同一个 DeskMaster 数据目录下。

## 桌面右键菜单

在项目目录中运行以下命令，为当前 Windows 用户注册桌面菜单。移动程序后需重新注册。

```powershell
Start-Process -FilePath .\bin\Release\net8.0-windows\DeskMaster.exe -ArgumentList '--install-desktop-menu' -Wait
```

移除菜单项可运行以下命令。

```powershell
Start-Process -FilePath .\bin\Release\net8.0-windows\DeskMaster.exe -ArgumentList '--remove-desktop-menu' -Wait
```
