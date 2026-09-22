# WinUI 3 性能诊断

[English](performance-diagnostics.md)

## 状态

本文是供讨论的设计方案。当前功能分支已经包含基础录制器、`.winappperf`
证据包、启动和资源摘要、可选收集器适配器、可重复场景、比较、带类型的窗口响应结果
和受控操作边界。下面简化后的默认录制和 `--full` 契约是建议的产品方向，不表示所有
部分都已经实现。

## 产品定义

> **Winapp 把一次 WinUI 3 性能问题复现转换成有界、时间对齐、机器可读的记录：
> 自动解释常见的延迟、响应、流畅度和资源异常，并保留原始证据供进一步分析。**

简而言之：

> **一次录制，解释时间和资源去了哪里，保留证据。**

Winapp 不是另一个通用 profiler。它是建立在成熟 Windows 和 .NET 诊断能力之上的
WinUI 3 专用入口和解释层。

## 现有空白

WinUI 3 已经拥有强大的底层诊断能力。WPR/WPA、EventPipe、Visual Studio、
PerfView、PresentMon 和 Windows 进程 API 分别提供了有用信息。真正缺少的是一个
WinUI 3 优先的工作流，把一次复现转换成有界、完整关联、可以自动解释的性能记录。

当前开发者必须在录制前就判断问题属于 CPU、XAML、GC、I/O、调度还是呈现。他们可能
使用不同工具多次复现同一问题，然后手工对齐：

- Windows App SDK 激活和正确的进程世代。
- HWND、对应 UI 线程和相关子进程。
- 启动、交互、XAML、Runtime、调度和呈现时钟。
- 迟附加、不受支持环境、事件丢失和空表。

这些工作需要专家知识，也可能使仅出现一次的问题失去有效证据。Winapp 负责协调；
底层 profiler 仍然是各自数据的权威来源。

## 用户问题

产品只聚焦四类 WinUI 3 性能问题：

| 用户现象 | Winapp 应解释什么 |
|---|---|
| 启动或首个窗口延迟 | 可观测时间分别落在激活、进程启动、首个窗口/响应、WinUI/XAML 初始化、Frame 和布局的什么位置 |
| UI 卡顿或交互缓慢 | 目标 HWND 是否停止响应，UI 线程当时在运行、等待、Ready 但未调度，还是执行了较长的可观测阶段 |
| 流畅度较差 | XAML frame/layout、CPU 调度或经过验证的呈现证据是否与问题区间重叠 |
| 资源使用异常 | 哪个目标进程/线程出现持续 CPU、内存增长、GC、I/O、句柄或其他有界资源活动 |

短时录制可以报告内存增长，但不能据此诊断内存泄漏。Throughput、功耗、长期泄漏分析
和任意系统级 profiling 不属于第一版产品边界。

## 产品承诺

### 一次录制

用户选择目标和场景，而不是底层采集技术：

```powershell
# 标准用户权限、进程级录制。
winapp perf record .\src\MyApp\MyApp.csproj

# 相同流程，额外加入有界的系统级证据。
winapp perf record .\src\MyApp\MyApp.csproj --full
```

两个命令都只启动应用一次，并发布一个 `.winappperf` 证据包。`--full` 增加需要权限的
系统证据，但不改变分析流程，也不扩大默认终端输出。

`--with-wpr` 是当前显式的系统证据控制项。Managed EventPipe 会在观察到新的 CoreCLR
进程世代后自动开始，不依赖全局诊断工具，也不要求指定时长。

### 自动解释

默认输出是一份简洁的事实说明，例如：

```text
First responsive window: 2.43 s

Observed WinUI phases:
  XAML initialization        1.44 s
  Longest interesting Frame 137 ms
  Longest UpdateLayout       96 ms

UI thread: 9340
Trace loss: none
Evidence: startup.winappperf
```

Winapp 在同一时间线上对齐目标标识、HWND、UI 线程、受控操作、XAML interval、
Runtime 事件、资源采样和可用系统证据，并说明观察到了什么、影响范围是什么、证据是否完整。

Winapp 不把时间相关性写成根因。尤其是，XAML 活动可以证明初始化、Frame、navigation
和 layout interval，但不能单独指出某个 element、Binding、Template 或应用方法是原因。

### 保留证据

同一个证据包在已采集时保留原始 ETL、EventPipe、呈现数据和 winapp 自有时间线。
开发者、Agent、WPA、PerfView 或其他专业工具不需要再次复现即可继续分析。

这就是**一次有界录制、两级分析**：

1. 第一级是自动生成的事实摘要。
2. 第二级分析同一个证据包中已经存在的深入证据。

Agent 是可选项。采集、摘要生成和证据包契约不能依赖 Agent。

输出契约分为三个界面：

1. 实时终端视图报告录制进度和采集器状态。
2. 录制结束后，终端短报告和规范的 `report.json` 使用同一组启动、资源、
   XAML 和覆盖率事实解释本次录制。
3. `timeline.ndjson`、ETL、nettrace 和专项摘要保留报告所引用的底层证据。

## WinUI 3 专有价值

通用 CPU 和内存计数是支持证据，不是产品本身。Winapp 的价值在于把它们与 WinUI 3
概念关联：

- Windows App SDK 激活，以及 packaged/unpackaged 目标。
- 进程世代安全的进程和进程树归属。
- Win32 HWND 发现、可见性、响应状态和所属 UI 线程。
- WinUI/XAML 初始化、Frame、navigation 和 `UpdateLayout`。
- 同一用户可见区间内的 Managed/native 执行、调度和呈现。
- 通过 `winapp ui` 执行的操作。

只有直接帮助解释 WinUI 3 启动、响应、流畅度或资源使用的数据，才应进入默认产品。

## 录制级别

| 能力 | 普通录制 | `--full` |
|---|---:|---:|
| 进程世代、启动、窗口、响应和退出生命周期 | 是 | 是 |
| 进程/线程资源和采样线程状态 | 是 | 是 |
| 受控 `winapp ui` 操作边界 | 是 | 是 |
| 观察到 CoreCLR 时的 Managed EventPipe | 是 | 是 |
| 系统调度、等待和原生调用栈 | 否 | 是 |
| 详细 Loader 和文件 I/O | 否 | 是 |
| WinUI/XAML 活动 | 否 | 是 |
| 经过验证的进程级呈现证据 | 否 | 是 |
| 原始深入工件 | 适用时 | 是 |

普通录制必须以标准用户权限运行，并保持进程级范围。`--full` 是显式启用、有界、需要
管理员权限的系统采集。启动目标前必须预检权限、冲突的机器级 session、可用空间、
所需采集 profile、事件丢失支持和呈现环境支持。

管理员 profile 是一个专门设计的 preset，不能直接叠加 `CPU.Verbose`、
`FileIO.Verbose`、`XAMLActivity.Verbose` 和 `DotNET.Verbose`；该组合已经产生
1.33 GB 且丢失 535,446 个事件的无效 trace。当前 `WinAppPerf.Verbose` 保留已经验证
的 XAML provider，只增加 sampled CPU、CSwitch/ReadyThread、进程/线程/映像、
hard-fault 和 File I/O 事件。Stackwalk 只用于 sampled CPU 和 ReadyThread；
托管运行时证据仍由独立的进程范围 EventPipe session 采集。

## 证据和解释契约

每条报告事实都直接包含或继承：

- PID 加进程创建时间。
- 适用时的 HWND 和所属进程世代。
- 适用时的 TID 和观测时间范围。
- 与外部工件时钟对齐的单调 QPC 时间线。
- Collector 起止时间、工具/配置版本、覆盖范围、配额、丢失和工件元数据。

覆盖状态区分 `complete`、`attached-after-activation`、`partial`、
`unavailable` 和 `not-observed`。除非采集和分析覆盖都确认完整，否则空结果不能作为
“没有活动”的证据。

具体解释边界：

- HWND timeout 只表示该采样点的指定消息没有在阈值内被处理。
- EventPipe 无法重建附加前已经完成的 CLR 工作。
- 模块出现只能证明顺序，不能证明模块加载成本。
- 采样调用栈可能错过很短或被内联的工作。
- XAML duration 可能重叠或嵌套，不能相加。
- XAML Region of Interest 是 plugin 推导的分析范围，不是直接 framework 执行成本。
- 退出时间和退出码只是生命周期事实。Crash 诊断属于
  `winapp run --debug-output`，可按需加入 symbols。

## XAML 采集和分析

XAML 采集和解释使用不同的依赖契约：

- 采集使用系统自带的 `%WINDIR%\System32\wpr.exe` 和有界 XAML profile。
- 自动解释从受信任位置解析兼容的已安装 Windows Performance Toolkit，包括
  `wpaexporter.exe`、`perf_xaml.dll` 和已启用的 plugin 注册。
- Winapp 随产品提供版本化的 `All Xaml Info` `.wpaProfile`，导出完整层级，
  过滤到已观测目标 PID，另外记录目标进程创建时间，并把事实型 XAML interval
  写入证据包。WPA 导出表不包含进程创建时间，因此有界、归属明确的采集会限制 PID
  重用风险，但无法只靠该 CSV 证明进程世代匹配。
- Winapp 不静默修改机器级 `perfcore.ini`。
- 如果兼容分析工具不可用，winapp 保留 ETL，并用 `analysis-unavailable` 报告缺失
  条件和修复操作；不能把空 XAML 摘要显示成“没有活动”。

第一版使用兼容的已安装 WPT，与现有 `perf open` 的依赖模式一致。在证明存在受支持的
独立软件包和再分发条款之前，不能随包分发 WPT 或在首次使用时下载。如果未来可行，
获取流程必须固定版本、复用缓存、验证完整性和微软签名，并检查组件兼容性。

## 证据包和失败语义

```text
capture.winappperf\
  manifest.json
  timeline.ndjson
  summaries\
    startup.json
    resources.json
    xaml.json
  traces\
    managed.nettrace
    system.etl
    presentation.csv
```

`manifest.json` 记录 schema/工具版本、目标归属、时钟、collector 覆盖范围、敏感性、
配额、事件丢失和工件哈希。`timeline.ndjson` 包含 winapp 自有的带类型事件。深入工件
保留原始格式。证据包以原子方式发布。

| 情况 | 结果 |
|---|---|
| 所有必须采集轨道完成 | `completed`，命令退出 0 |
| 目标正常或非零退出，但采集完成 | `completed`；单独记录目标结果 |
| 必须的 collector 启动后失败 | 保留 `partial`；命令非零退出 |
| 出现会影响结论的丢失或配额截断 | 保留 `partial`；抑制受影响结论；命令非零退出 |
| 未观察到 CoreCLR | `completed`，`managed: not-observed` |
| 可选的采集后分析器不可用 | 保留原始证据并报告 `analysis-unavailable` |
| `--full` 采集预检失败 | 不启动目标，也不发布形似成功的证据包 |

## 隐私和安全边界

普通摘要不保留 UI 文本/value 内容、原始键盘或指针输入、截图/视频、文件或网络内容、
连接字符串或原始异常消息。受控操作保留操作类型和时间，不保留私有参数。

完整 ETL 和调用栈工件可能包含路径、命令行、符号和其他进程活动。它们必须标记为
敏感，默认保留在本地，并从未来的脱敏导出中排除，除非用户明确包含。

即使 `--full` 也排除：

- 完整托管堆和对象保留关系图。
- 截图、视频和被动手动输入采集。
- UIA 内容值和网络内容。
- 无界 trace。
- 必需的应用埋点。

应用 marker 是可选的源码辅助流程，只用于无法从外部观察的业务阶段名称、隐藏异步
边界、跨进程 correlation ID 或应用自定义就绪状态。

## 验收目标

以下是评审目标，不是已经验证的产品常量：

| 目标 | 普通录制 | `--full` |
|---|---:|---:|
| 默认时长 | 30 秒 | 30 秒 |
| 30 秒 lab 证据包 | <= 25 MB | <= 250 MB |
| 会影响结论的事件丢失 | 0 | 0 |
| 场景中位耗时增量 | <= 3% | <= 5% |
| 权限 | 标准用户 | 管理员 |

更长录制必须显式指定时长，并按比例检查可用空间。任何模式都不能无限期等待 Enter。

## 验证附录

下表记录直接观察结果，而不只是 API 或 profile 是否存在。

| 轨道 | 已证明的证据 | 剩余验证 |
|---|---|---|
| 进程/窗口/启动 | 真实 bundle 保留 PID 加启动时间、激活、所属 HWND、可见/响应里程碑、terminal CPU/I/O 和准确退出码；exit-before-window 保留退出码 23 | 无法恢复采样之间的短时活动 |
| HWND 响应 | 受控卡顿产生 29/29 和 28/28 次重叠 timeout；带类型结果区分 responsive、timeout 1460、invalid handle 1400、access denied 和其他失败 | 短时卡顿可能落在采样之间；timeout 探测会降低 cadence |
| 进程/线程资源 | UI 和 worker 对照分别记录到约一个 core，并正确识别最热 UI 或 worker TID；idle 和 contention 对照观察到 Wait 和 Ready | 受控目标开销比较仍未完成 |
| 受控操作 | Invoke、Toggle、Select、Expand、value 应用和 mouse dispatch 具有准确 start/end hook；失败操作保留 failed end | 只覆盖 winapp 控制的操作，不覆盖被动手动输入 |
| Managed Runtime | 一个 32 秒、1.74 MB 的 session 同时保留深度采样栈和 864 个 `System.Runtime` counter event，覆盖 27 项指标。普通录制现在会在进程世代安全的 CoreCLR 附加后写入一个进程内 `managed.nettrace`；一次真实 4 秒采集包含 12,036 个 sample、108 个 counter event、1,949 个 rundown event，parser 报告零丢失 | 目标开销和更广的附加覆盖仍待验证；AOT CLI 保留 nettrace，但不解析其中的事件 |
| 系统 CPU/调度 | 所需 kernel provider 和受控 running/waiting/ready workload truth 已证明 | 短时管理员目标归属 ETL、体积、丢失和调用栈解析仍待证明 |
| Loader/file I/O | Provider 能力和 ImageLoad/rundown 区分已确认 | 正/负目标对照、隐私、体积和丢失仍待证明 |
| XAML | 三份独立管理员 trace 的 lost buffers/events 均为 0。Eager：ROI 1,591.6380 ms、WXM 1,441.2038 ms、interesting Frame 137.1286 ms、UpdateLayout 95.9532 ms；Deferred：ROI 447.5037 ms、WXM 316.0144 ms、Frame 122.1670 ms、UpdateLayout 92.1059 ms；exit-before-window 仅有 Create graphics device 20.4084 ms。生产 resolver 接受了微软签名的 WPAExporter 11.7.395.48728、perf_xaml 10.0.26100.8249 和 xperf 10.0.26100.8249；生产 analyzer 精确复现三份 WPA 摘要，生产 `tracestats` 解析报告零丢失 | 综合 profile 的开销/体积/丢失和更广的 WPT 支持矩阵仍待完成；由于实验有意未启用 sampled CPU，Weight 为 0 |
| 呈现 | PresentMon 来源和关闭 input 的命令形式已证明 | 当前非管理员运行未通过 access 预检；本地/RDP、idle/stress、可见性、多窗口归属和时钟对齐仍待证明 |

## 剩余实现门槛

1. 使用 lab workload 测量普通录制开销。
2. 在管理员权限下验证内嵌的有界 `WinAppPerf.Verbose` profile，包括归属、体积、
   零丢失、XAML 一致性和开销。
3. 将 XAML 兼容性验证扩展到已经证明的 WPT 11.7.395.48728 /
   perf_xaml 10.0.26100.8249 组合之外。
4. 跨权限、可见性、本地/RDP、stress、多窗口和时钟对齐对照验证呈现证据。
5. 完成隐私和中断采集测试。

待决定问题仅包括：

- 最终时长、体积和开销预算。
- 在无法保证环境契约时，呈现是否仍为 `--full` 必需项，还是成为单独命名的能力。
- 支持的 Windows、WPT、.NET、架构、本地控制台和 RDP 版本。
- 未来是否存在受支持的 WPT 分发方式，可以进行经过验证的首次使用下载；第一版使用
  已安装 WPT。
