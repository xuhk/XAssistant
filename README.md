# XAssistant

**XAssistant** 是一款 Windows 桌面活动统计工具（WPF / C# / .NET 10），常驻系统托盘，记录键盘、鼠标点击数及使用时长。**所有数据仅保存在本机，不联网上传。**

## 说明

这是一个 **vibe coding** 产物：代码由 **0731 之前的 DeepSeek Flash 网页版**编写，由 **DeepSeek Flash 0731** 发布，作者本人对 C# / .NET 并不熟悉。项目仅在作者自己的机器上验证可用（Windows 10 专业版 22H2 build 19045，64 位），**不保证其它环境兼容**。不懂的地方可以直接问 AI，或者干脆让AI写一个更好。

## 安装

1. 从 Releases 下载两个压缩包：主程序 `XAssistant-win-x64-*.zip` 与后台服务 `XAssistant.Service-win-x64-*.zip`。
2. 解压到同一文件夹，保持目录结构：

   ```
   你的文件夹/
   ├─ XAssistant.exe
   └─ XAssistant.Service/
      ├─ XAssistant.Service.exe
      └─ install-service.ps1
   ```

3. 右键 `install-service.ps1` → **使用 PowerShell 运行**（会请求管理员授权），或在管理员 PowerShell 中执行：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\XAssistant.Service\install-service.ps1
   ```

   后台服务随开机启动，请把整个文件夹放在本机固定磁盘（例如 `C:\`）；放在可能离线的副盘或移动盘上时，开机瞬间服务会因为找不到文件而起不来。

4. 双击 `XAssistant.exe` 即可。
5. 默认**不会**开机自启动。需要常驻统计的话，在应用界面左侧勾选"**开机自动启动**"：登录时由计划任务拉起（约 8 秒），注册表 Run 键作为兜底。

> 不装后台服务也能用键盘 / 鼠标 / 应用时长等功能，仅"电脑使用时长"无法获取。

## 功能

| 功能 | 说明 |
| --- | --- |
| ⌨️ 键盘按键统计 | 按键计数，区分左右修饰键（Ctrl / Shift / Alt / Win） |
| 🖱️ 鼠标点击统计 | 记录左 / 中 / 右键点击次数 |
| 💻 电脑使用时长 | 记录电脑每天的开机 / 使用时长，依赖后台服务 XAssistant.Service |
| 📊 应用使用时长 | WMI 追踪各应用使用时长——**不完善，统计可能有偏差** |
| ℹ️ 关于 | 查看当前版本、手动检查更新和更新日志 |

关闭窗口只是隐藏到托盘，托盘菜单中才有"退出"。

## 支持环境

- 亲测环境：Windows 10 专业版 22H2（19045），64 位
- 直接运行 Release 包**无需安装 .NET**（已自包含打包）
- 其它环境不保证兼容

## 数据存储

- 主程序数据：`%APPDATA%\XAssistant`（统计数据库与配置 `appsettings.json`）
- 后台服务数据：`C:\ProgramData\XAssistant\UsageTracker`（电脑使用时长数据库）
- 日志：`%APPDATA%\XAssistant\logs\`（滚动保留 31 天）

均为本地文件。只在你手动点「检查更新」时向 GitHub 查询一次最新版本号（不发送任何本机数据，不做启动自动检查）。

## 构建

```powershell
# 构建整个解决方案（主程序 + 后台服务）
dotnet build XAssistant.sln -c Release

# 发布主程序（自包含，win-x64）
dotnet publish XAssistant.csproj -r win-x64 -c Release --self-contained true

# 发布后台服务（自包含，win-x64）
dotnet publish XAssistant.Service\XAssistant.Service.csproj -c Release --self-contained true -r win-x64
```

需安装 [.NET 10 SDK](https://dotnet.microsoft.com/download)。


## 许可证

[MIT](./LICENSE)