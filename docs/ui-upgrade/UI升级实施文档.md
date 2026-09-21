# UI 升级实施文档（分步）

**索引**

0. 怎么用这份文档
1. 已定前提（不再讨论）
2. 阶段总览与验收线
3. P0 数据层（6 步）
4. P1 WebView2 宿主（6 步）
5. P2 视觉与交互对齐（6 步）
6. P3 收尾验收（4 步）
7. 贯穿性约定
8. 与速记移除的交叉影响
9. 回滚点清单

依据：`可行性评估.md`（路线与实测）、`数据层改造-安全方案.md`（迁移纪律）、`速记功能-移除与本地保留.md`（剥离顺序）。本文只写**怎么做**，不重复理由。

---

## 0. 怎么用这份文档

- **每一步都能单独验收、单独回滚。** 做完一步就跑该步的验收判据，过了再进下一步。
- 步骤编号 `S<阶段>.<序号>`，可以按编号引用。
- 每步四段：**目标 / 动哪些文件 / 做法 / 验收判据**。没写"回滚"的默认是"删掉新增的文件即回滚"。
- 括号里的数字是**实测基线**，不是估值——达不到就是有问题，别放过去。

---

## 1. 已定前提（不再讨论）

| 项 | 决定 |
|---|---|
| 路线 | 混合：原生 WPF 外壳 + WebView2 承载 v4 界面 |
| 运行时 | Evergreen，不打包固定版；缺失时降级页 + 安装引导 |
| 加载方式 | 懒加载，**不做开机预热** |
| 骨架屏 | 必须 |
| 速记 | 从开源版移除，本地版走 `Local/` 私有模块 |
| 数据层 | 预聚合表；只增不删；迁移失败不拦启动 |
| 速记窗（本地版） | 保持原生 WPF，**绝不迁 WebView2** |

---

## 2. 阶段总览与验收线

| 阶段 | 内容 | 做完之后的验收线 | 可停？ |
|---|---|---|---|
| **P0** | 数据层：预聚合表 + 聚合查询 + 版本号落地 | 键盘页任一范围切换 **< 50 ms**；exe 版本信息为 `1.1.0+sha`；现有功能零回归 | 可停，先看效果 |
| **P1** | WebView2 宿主打通 + 骨架屏 + 数据桥 | 主窗显示 v4 真界面；**常驻时进程里没有 msedgewebview2** | 可停 |
| **P2** | 视觉与交互对齐 + 检查更新 | 深浅色全局一致；托盘/Toast/骨架屏风格统一；检查更新能取到 `v1.0.0` | 可停 |
| **P3** | 收尾与验收 | 有真实的内存与启动数字 | — |

**前后依赖**：P0 与速记移除可并行；**速记移除必须在 P1 之前做完**（见第 8 节）。P2 依赖 P1，P3 依赖 P2。

---

## 3. P0 数据层

### S0.1 schema 版本号 + AggMeta 表

**目标**：给三个库加上可判断的版本，之后不再靠"猜表存不存在"。

**动哪些文件**：`Services/ClickDatabaseService.cs`、`Services/KeyDatabaseService.cs`、`Services/ProcessUsageTracker.cs`（它自己管 `app_usage.db`）

**做法**
- 每个库新增 `AggMeta(Name TEXT PRIMARY KEY, Value TEXT)`。
- 引入 `PRAGMA user_version`：读出来决定要不要跑迁移；跑完写回新版本号。首个版本定为 `1`。
- 这是唯一一次"补历史"的机会——做完之后所有迁移都按版本号判断。

**验收判据**
- 连开 5 次 app，`user_version` 稳定为 1，`AggMeta` 不重复建。
- 拿 v1.0.0 的 exe 打开升级后的库，读写正常（对应迁移判据 E）。

### S0.2 建聚合表 + 后台回填

**目标**：把"每次扫 192 万行"变成"读 1.1 万行"。

**动哪些文件**：两个数据库 Service

**做法**
- 建表：
  - `KeyDaily(Date TEXT NOT NULL, Key TEXT NOT NULL, Cnt INTEGER NOT NULL, PRIMARY KEY(Date, Key))`
  - `ClickDaily(Date TEXT NOT NULL, Button TEXT NOT NULL, Cnt INTEGER NOT NULL, PRIMARY KEY(Date, Button))`
- 回填用幂等语句（重复跑结果一致）：
  - `INSERT INTO KeyDaily(Date, Key, Cnt) SELECT substr(PressTime,1,10), Key, COUNT(*) FROM KeyPressRecords GROUP BY 1,2 ON CONFLICT(Date,Key) DO UPDATE SET Cnt = excluded.Cnt`
- 放在 `Task.Run` 里跑，**不阻塞启动**；完成后把最大日期写进 `AggMeta.AggregatedThrough`。
- 失败只记警告日志，界面退回慢查询。

**验收判据**
- 本机 192.7 万行回填 **< 5 秒**（实测 2.49 秒）；点击库 39.4 万行 < 2 秒（实测 0.50 秒）。
- `SELECT SUM(Cnt) FROM KeyDaily` **等于** `SELECT COUNT(*) FROM KeyPressRecords`（实测两者都是 1,927,850）。
- 原表行数与内容校验和不变（迁移判据 A）。
- 启动过程无卡顿：从双击到托盘图标出现的时间与改造前相比没有可感差异。

### S0.3 写入路径增量 upsert

**目标**：让聚合表随写入自动跟上，不需要每天重算。

**动哪些文件**：`KeyDatabaseService.SaveKeyPress`、`ClickDatabaseService.SaveClick`

**做法**
- 改成同一连接、同一事务里两条语句：
  ```
  INSERT INTO KeyPressRecords (Key, PressTime) VALUES (@k, @t);
  INSERT INTO KeyDaily (Date, Key, Cnt) VALUES (@d, @k, 1)
    ON CONFLICT(Date, Key) DO UPDATE SET Cnt = Cnt + 1;
  ```
- **关键约定：先写原表；聚合表失败不回滚原记录。** 原表是事实来源，聚合表可以事后补（S0.2 的回填就是补的手段）。

**验收判据**
- 连按 100 次键，`SUM(Cnt)` 增量正好 100，`COUNT(*)` 增量也是 100。
- 手工把 `KeyDaily` 删掉后继续按键：**按键记录照常写入成功**，日志里有一条警告，不弹错。

### S0.4 聚合查询 API

**目标**：一次查询拿够一个视图要的全部数据，不要在 C# 里循环查。

**动哪些文件**：新增 `Services/Interfaces/IAggregateQueryService.cs` + 实现（**新开接口，不要塞进现有两个库 Service**，避免读写逻辑搅在一起）

**做法**：4 个口径，每个都基于聚合表

| 口径 | 用途 | 典型耗时（实测） |
|---|---|---|
| 日序列（日期 → 计数） | KPI 统计条、sparkline、逐日折线 | 整年 0.7 ms |
| 日 × 维度（日期 × 键/按钮 → 计数） | 年热力图、104 键热力图 | 全量 12.9 ms、单日 0.3 ms |
| 小时分布（0–23 → 计数） | 24 小时时间轴 | — |
| 区间合计 | KPI 大数字、vs 昨日 | 0.2 ms |

**验收判据**
- 键盘页任一范围切换 **< 50 ms**（实测预聚合全量 12.9 ms）。
- 一次视图切换只发一次查询，不是 N 次（用日志数一下 SQL 条数）。

### S0.5 口径对齐

**目标**：同一个数字在不同位置必须是同一个值。

**做法**：v4 的设计要求"键盘热度的目标总量 === 上方 KPI 总按键次数 === 逐日折线图的月度合计"。实现上必须三者**用同一份聚合结果算**，而不是各算各的。因此定死三条：
- 日期口径：一律用 `yyyy-MM-dd`（本地时区），与 `ProcessSession.Date` 的既有约定一致。
- 范围口径：一律左闭右开 `[from, to)`。
- 截断口径：`substr(PressTime,1,10)` —— 只用于回填；运行时一律查聚合表，不再截断原表。

**验收判据**：随机挑 3 个日期，KPI 数字、热力图当日列合计、折线图当日点三个值完全相等。

### S0.6 版本号落地

**目标**：让"我装的是哪个版本"能被人和程序同时回答。**这是检查更新的前置，也越早做越好**（之后每个版本都要用）。

**动哪些文件**：`XAssistant.csproj`、`App.xaml.cs`（或一个 `Services/AppVersion.cs`）

**现状（实测）**

| 项 | 现状 |
|---|---|
| csproj 里的版本属性 | **一个都没有** —— 全靠 SDK 默认 |
| `FileVersion` | `1.0.0.0` |
| `ProductVersion` | `1.0.0+f6c80c7f29dc9339ea095da3b77a5c56dedba8b4` |

注意后半截：**git 提交 sha 已经被 SDK 自动打进 ProductVersion 了**（只要是从 git 工作区构建）。所以"具体是哪个提交"已经能查到，缺的只是**可做大小比较的语义化版本号**。

**做法**
- csproj 加 `<Version>`，首个值建议 `1.1.0`（带着这个功能的版本）。
- 运行时读 `FileVersionInfo.GetVersionInfo(Environment.ProcessPath).ProductVersion`，拆成 `1.1.0` 和 `+sha` 两段。
- 设置页显示："版本 1.1.0（f6c80c7）"。

**验收判据**
- `(Get-Item <exe>).VersionInfo.ProductVersion` 以 `1.1.0+` 开头。
- 设置页显示的版本号与 exe 属性一致。
- 发版时给 git 打同名 tag（`v1.1.0`），与 release 名一致。

**关于断层（要有预期，不是 bug）**：v1.0.0 已经发布，它二进制里没有可对比的语义化版本，所以**老用户不会主动收到一次"有新版本"的提示**，他们得自己来查。从 1.1.0 起这个链路才完整。

---

## 4. P1 WebView2 宿主

### S1.1 加依赖 + 前端资源入库

**动哪些文件**：`XAssistant.csproj`、把 `temp/ui-design/v4/` 移进仓库

**做法**
- 加 `Microsoft.Web.WebView2`（`1.0.*`）→ 包体积 **+1.0 MB**（实测：Core 682 KB + Loader 160 KB + Wpf 85 KB）。
- **先把 v4 从 `temp/` 搬出来**——`temp/` 在 `.gitignore` 里，不搬的话发布包里不会带上这份资源。建议落 `web/`（`web/index.html`、`web/assets/app.css`、`web/assets/app.js`）。
- csproj 里声明为 `Content` + `CopyToOutputDirectory=PreserveNewest`。

**验收判据**
- `publish` 输出里有 `web/` 目录且文件齐全。
- 包体积增量 ≤ 2 MB。

### S1.2 运行时探测 + 降级页

**动哪些文件**：新增 `Services/WebView2/WebView2Availability.cs`、新增降级视图

**做法**
- `CoreWebView2Environment.GetAvailableBrowserVersionString()` 探测一次（本机实测返回 `153.0.4234.48`）。
- 取不到 → 显示原生 WPF 降级页：说明情况 + 给 WebView2 运行时下载链接 + "仍然使用旧版界面"按钮（若 P2 保留了旧视图）。
- **不做静默失败。**

**验收判据**：把本机 WebView2 安装目录临时改名后启动，能看到降级页且不崩；改回来恢复正常。

### S1.3 WebView2HostService（懒加载 + 释放）

**动哪些文件**：新增 `Services/WebView2/WebView2HostService.cs`、改 `App.xaml.cs` 装配、改 `Views/MainWindow.xaml.cs`

**做法**
- **不在 `OnStartup` 建 WebView2。** 第一次打开主窗时才 `CreateAsync`。
- `CoreWebView2Environment` 创建一次并复用（实测：复用后 CoreReady 从 4116 ms 降到 1007 ms）。
- 关窗到托盘时：`Content = null` → `Close()` → `Dispose()` → 置空引用。
- 记住一个不可逆的事实：**第一次用过之后宿主进程会永久多占约 70 MB 私有提交量**（70 个 DLL 装进进程，无法卸载）。这是接受的代价，不要试图回收。

**验收判据（这步最重要）**
- **常驻状态下（从未开过主窗）**：任务管理器里没有 `msedgewebview2.exe` 进程。
- **关窗 5 秒后**：`msedgewebview2.exe` 数量为 **0**（实测 3 秒内清零）。
- **连开三轮**：内存不涨台阶（实测三轮分别是 82.2 / 83.8 / 84.1 MB）。
- 速记窗（本地版）的热键仍能秒开 —— WebView2 的存在不能拖慢它。

### S1.4 骨架屏

**目标**：把 3.1 秒的首帧藏起来。

**动哪些文件**：新增 `Views/SkeletonOverlay.xaml`（或直接在主窗里叠一层）、改 `Views/MainWindow.xaml`

**做法**
- 点开主窗时**先显示原生 WPF 壳**：侧栏轮廓 + 标题 + 4 张 KPI 卡，数字用 C# 侧**已有**的数据直接填（这些数字来自 S0.4 的聚合查询，本来就不需要等 WebView2）。
- WebView2 就绪后切换，避免跳变（骨架的布局要和 v4 的最终布局对齐）。
- 首帧耗时实测 **冷 4.6 s / 热 3.1 s**，其中约 2.1 s 是 v4 页面自身渲染（365 格热力 + 104 键 + SVG），**预热消不掉**，只能靠骨架屏遮。

**验收判据**：点开主窗 **200 ms 内**出现骨架；3.5 秒内切到真界面；切换时无明显位移跳变。

### S1.5 数据桥（JSON 契约）

**动哪些文件**：新增 `Services/WebView2/WebViewBridge.cs`、`web/assets/bridge.js`（或并入 app.js）

**做法**
- **上行（C# → JS）**：`CoreWebView2.PostWebMessageAsJson(json)`。
- **下行（JS → C#）**：`window.chrome.webview.postMessage({ cmd, args })`。
- **统一信封**：`{ type: 'query' | 'action' | 'log', payload }`。
- **白名单**：C# 侧只响应显式枚举的命令，不接受任意方法名（避免变成一个通用 RPC 口子）。
- **JS 侧的错误要回流**：接 `CoreWebView2.ProcessFailed`，把 `console.error` 通过 `type: 'log'` 送回 C# 日志，否则页面里报错你在这边看不见。
- v4 现在的 `DATA` 是本地确定性 mock，替换成桥来的数据；`NOTES.md` 说"换 `DATA` 那一段即可"，但 `app.js` 是 68 KB 单文件，**动手前先确认渲染逻辑与数据的耦合面**（这是 P1 唯一估不准的地方）。

**验收判据**：6 个视图逐个切换，每个都显示真数据；浏览器控制台无 error；把 C# 侧故意抛一次异常，能在 app 日志里看到 JS 侧的错误上报。

### S1.6 主窗迁移 + 托盘行为对齐

**动哪些文件**：`Views/MainWindow.xaml(.cs)`、`ViewModels/MainWindowViewModel.cs`、`App.xaml.cs`

**做法**
- 内容区从 `ContentControl`（VM 映射）换成宿主控件。
- `NavigateCommand` 改为转发给 JS（`type: 'nav'`）。
- 旧视图（`ClickCounterView` / `KeyCounterView` / `UsageView` / `AppUsageView` / `HomeView`）**先留着不删**——S1.2 的降级页要能回退到它们。
- 关窗语义保持：隐藏到托盘 + 释放 WebView2；托盘"显示"= 重建。

**验收判据**：托盘菜单全部动作正常；关窗后进程里没有 `msedgewebview2.exe`；速记窗（本地版）热键正常。

---

## 5. P2 视觉与交互对齐

### S2.1 清掉速记相关的 6 处

先做这步，否则界面上有死按钮。清单见 `速记功能-移除与本地保留.md` 第 5 节（标题栏按钮、速记窗预览、快捷设置"立即速记"行、设置页两行、日志 mock 与交互）。

**验收判据**：重跑 `temp/ui-design/_verify/v4-check.mjs`，无断言失败。

### S2.2 三态主题 + 持久化

**做法**：浅色 / 深色 / 跟随系统三态，落到 `AppSettings`；系统主题变化时通过桥通知 JS 切换（v4 已支持 `[data-theme]`）。

**验收判据**：切换三态，WPF 侧（骨架屏、托盘、Toast）与 JS 侧同时变；重启后保持。

### S2.3 设置页落位

**做法**：v4 的 `settings` 视图接真配置：数据目录、保留天数、界面显示开关（日志面板 / 统计条 / 侧栏折叠 / 键盘热度 / 紧凑行距）。**连接串那一行随速记一起删掉。**

**验收判据**：设置页里每一项改完都能真生效（不是只弹 toast）。

### S2.4 Toast 按 v4 重做

**做法**：`Views/ToastWindow` 按 v4 样式重做（右下角、`ok` / `warn` 两种），保持在原生 WPF 里。v4 有 12 处用到 toast，其中 11 处与速记无关。

**验收判据**：触发几个不同来源的 toast（日志清空、主题切换、开关切换），样式与 v4 一致。

### S2.5 引擎外元素风格统一

**做法**：托盘图标与菜单、骨架屏、降级页统一到 v4 的配色。**WPF 侧的十六进制值集中写在一处**（如 `Services/Palette.cs`），不要散落——v4 那边的唯一来源是 CSS 变量，两边要能对上。

**验收判据**：随机挑 3 个颜色值，与 v4 的 CSS 变量一致。

### S2.6 检查更新

**目标**：把 v4 快捷设置里"检查更新"那一行从假 toast 变成真检查。

**动哪些文件**：新增 `Services/UpdateService.cs`、`ViewModels`/桥的一个命令、`web/assets/app.js`（那一行的处理）、`README.md`

**现状**：v4 已经留好位置了 —— `web/index.html:520-522` 的 `<button class="qs-item" data-toast="已是最新版本 · v4.0.2">`，在快捷设置"其他"分组里；`NOTES.md:51` 也记了这一行。**UI 不用新增，只换实现。**

**做法 —— 用 302 重定向，不要用 API**（这是本节的核心）：

本机实测（2026-09-21）：

| 端点 | 结果 |
|---|---|
| `https://github.com/xuhk/XAssistant/releases/latest` | **HTTP 200，2.2 秒**，302 到 `/releases/tag/v1.0.0` → 从 Location 末段取出 tag |
| `https://api.github.com/repos/xuhk/XAssistant/releases/latest` | **HTTP 403 被拦**（间歇性；同一时刻网页端可达） |

所以：

- **请求 `https://github.com/<owner>/<repo>/releases/latest`，禁止自动重定向，读 `Location` 头的最后一段** = 最新 tag。去掉前缀 `v` 后与当前版本做语义化比较（手写约 30 行即可，不必引版本库）。
- 好处三条：不吃 GitHub API 的 60 次/小时配额、不受 API 端点在国内的间歇性 403 影响、不违反 GitHub 的使用条款（比解析 HTML 干净）。
- `releases/latest` 这个端点本身会**自动跳过 draft 与 pre-release**，正好符合需求。
- **必须带 User-Agent**（GitHub 对无 UA 的请求直接 403，实测报错信息就是这条）。
- 超时 5–8 秒；**失败一律静默**（只记日志），UI 显示"检查失败，请稍后再试"，绝不弹异常。
- 只在用户点"检查更新"时请求，**不做启动自动检查** —— 这样 README 里"不联网"的承诺基本不变。
- 加 5 分钟防抖（记住 `lastCheckedAt`），避免用户连点发出多个请求。
- 不做 release 说明解析（只拿 tag）；要看更新说明就直接打开 `https://github.com/xuhk/XAssistant/releases/latest` 给用户。

**不做自动下载安装。** 理由：Windows 上替换正在运行的 exe 需要额外的 helper 进程；还要处理 76 MB 包的进度、断点、哈希校验、失败回滚；而且让一个开源 app"自己下载并运行二进制"会引来用户信任顾虑与杀软告警。**这部分工作量比前面所有加起来都大，收益却只是省一次手动下载。**

**README 要同步改**：现在写的是"均为本地文件，不联网上传"。改成"数据均为本地文件；仅在手动点击「检查更新」时向 GitHub 查询最新版本号"。

**验收判据**
- 点"检查更新"：能取到 `v1.0.0`，与当前版本比较后给出正确提示。
- **断网状态**：8 秒内返回失败提示，界面不卡、无异常弹窗。
- 连点 5 次：只发出 1 个请求（防抖生效）。
- 在没有 Release 的仓库上测试：能优雅提示，不崩。

---

## 6. P3 收尾验收

### S3.1 降级页打磨 + 安装说明
把 S1.2 的降级页做完整（检测 + 下载引导 + 一键打开浏览器），README 补一句"需要 WebView2 运行时（Win11 及大多数 Win10 已自带）"。

### S3.2 真实内存与启动耗时验收

拿这三个实测基线对比，偏差过大要查原因：

| 指标 | 实测基线 |
|---|---|
| 常驻工作集（未开过主窗） | ≈79 MB（改前 78.8 MB，基本不变） |
| 开主窗时合计 | ≈480 MB |
| 关窗 5 秒后 | 子进程 0 个；宿主回落 |
| 首帧 | 冷 4.6 s / 热 3.1 s |
| 空闲 CPU | 整机 0.12% |

**注意**：上表来自空 WPF 壳的探针。真实 `XAssistant.exe` 常驻是私有 122 MB / 工作集 78.8 MB，叠加后预期私有 ≈192 MB、工作集 90–100 MB —— **这个数字是推算的，本步要用真实 app 实测一次**。

### S3.3 打包决策

| | 自包含 | 框架依赖（FDD） |
|---|---|---|
| 体积 | 76 MB（解压 176 MB） | **5.1 MB** |
| 用户门槛 | 无 | 要装 .NET 10 Desktop Runtime |

体积差距 33 倍，远大于 WebView2 的 1 MB 增量。这一步要把 README 的安装说明和 release 产物一起定下来。

### S3.4 文档更新

`CLAUDE.md`（架构节要重写：WebView2 宿主、数据桥、骨架屏、聚合表；删速记节）、`CONTEXT.md`（随速记移除已重写）、`README.md`（功能项与安装说明）。

---

## 7. 贯穿性约定

| 项 | 约定 |
|---|---|
| 色彩 | 唯一来源是 v4 的 CSS 变量（取自 xapp `index.css`）；WPF 侧硬编码同一批值，集中一处 |
| 数据契约 | 一份信封 `{ type, payload }`；白名单命令；契约变更要同改两边 |
| 错误处理三级 | WebView2 缺失 → 降级页；查询失败 → 视图内错误卡；单条写入失败 → Toast + 日志 |
| 日志 | JS 的 `console.error` 必须回流到 C# 日志（否则页面报错你看不见） |
| 时间口径 | `yyyy-MM-dd`、左闭右开 `[from, to)`、本地时区（与 `ProcessSession.Date` 一致） |
| 迁移纪律 | 只增不删；每次执行都幂等；失败不拦启动（详见 `数据层改造-安全方案.md`） |

---

## 8. 与速记移除的交叉影响

**顺序：速记移除（`速记功能-移除与本地保留.md` 第 7 节的第 2–6 步）在 P1 之前做完。**

理由：
- P1 要改 `App.xaml.cs`（装配 + 托盘菜单）和主窗导航，而速记的挂载点正好在这儿。一起做省一次返工。
- P2 要清 v4 的 6 处速记痕迹，也属于同一批改动。
- **P0 与速记移除无耦合，可以并行。**

**本地速记版在 P1 之后的额外要求**：它挂在 `Local/` 私有模块里，`Attach()` 时通过 `App.TrayMenuCreated` 事件插托盘项 —— 这个事件在 S1.6 改托盘时要保留，别顺手删掉。

---

## 9. 回滚点清单

| 步骤 | 回滚方式 | 风险 |
|---|---|---|
| S0.1 | `DROP TABLE AggMeta`（原表不动） | 无 |
| S0.2 | `DROP TABLE KeyDaily / ClickDaily`（派生数据，判据 D 已验证） | 无 |
| S0.3 | 去掉写入路径里那条 upsert | 低 |
| S0.4 | 新接口无人调用即无影响 | 无 |
| S0.5 | 无代码可回滚（口径约定） | — |
| S0.6 | 版本号写进 csproj，改回即可 | 无 |
| S1.1 | 移除包引用 + `web/` 目录 | 无 |
| S1.2–S1.6 | 旧视图一直保留着，可整体退回旧界面 | 中（要改回 MainWindow） |
| S2.x | 逐项可退 | 低 |
| S2.6 | 删掉 `UpdateService` 与那一行的处理，回到假 toast | 无 |
| S3.3 | 打包方式随时可切 | 无 |

**唯一的不可逆点**：S1.3 首次创建 WebView2 后，宿主进程永久 +70 MB 私有提交量。这是设计事实，不是 bug（成因见 `可行性评估-证据.md` F3）。
