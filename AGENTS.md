# AudioRouter 项目规则

## 协作与修改范围

- 默认使用中文交流、编写说明和用户可见文案。
- 明确、低风险的任务直接执行；关键歧义、重大冲突或不可逆操作再询问。用户明确要求只分析或不测试时，遵守该边界。
- 修改前查看 `git status --short`，保留已有修改和未跟踪文件；只改任务涉及的文件，不顺带重构、格式化或清理目录。
- 使用现有环境；未经要求不安装、升级依赖，不修改 SDK、系统音频配置或默认播放设备。
- 本文件适用于整个仓库；子目录存在更近的 `AGENTS.md` 时，按其作用域执行。

## 项目定位与技术栈

- 本项目是 Windows 桌面音频转发工具：捕获一个播放设备的 WASAPI Loopback 音频，输出到另一个播放设备。
- 解决方案：`AudioRouter.sln`；主项目：`AudioRouter/AudioRouter.csproj`，目标框架为 `net8.0-windows`，使用 WPF。
- SDK 以 `global.json` 为准，当前为 `8.0.423`，`rollForward` 为 `latestPatch`。
- 依赖版本以 `.csproj` 为准；主要使用 NAudio、HandyControl、CommunityToolkit.Mvvm 和 Serilog。
- 当前采用单项目、根命名空间 `AudioRouter` 的组织方式。小范围需求沿用现有结构，不为单次修改引入额外项目或框架。
- README 用于理解产品背景；与当前源码冲突时以源码为准，涉及本次行为变化时同步相关说明。

## 定位代码

- 仓库存在 `.codegraph/` 时，理解架构、定位符号或追踪调用链先使用 `codegraph_explore`，不可用时可用 `codegraph explore`；以当前源码核对关键行为。不要自行初始化、重建或修改索引。
- 搜索优先使用 `rg`；默认排除 `.git/`、`.vs/`、`.idea/`、`bin/`、`obj/`、日志和缓存，按需读取片段。
- `_codex_buffer_smoke/` 不在当前解决方案中，不应当作正式测试项目或业务入口。

| 文件 | 职责 |
| --- | --- |
| `AudioRouter/MainWindow.xaml` | 窗口布局、样式、数据绑定 |
| `AudioRouter/MainWindow.xaml.cs` | 创建 ViewModel、窗口关闭时释放资源 |
| `AudioRouter/MainWindowViewModel.cs` | 命令、配置选择、UI 状态、诊断刷新和设备恢复 |
| `AudioRouter/AudioForwarder.cs` | 捕获与输出生命周期、处理管线、兼容回退和异常通知 |
| `AudioRouter/AudioDeviceService.cs` | 活动播放设备枚举、默认设备查询、端点主音量访问 |
| `AudioRouter/AudioConfigService.cs` | 配置加载与保存 |
| `AudioRouter/AudioBufferSettings.cs` | 延迟档位及各管线的缓冲水位 |
| `AudioRouter/RealtimeWaveBuffer.cs` | 实时缓冲、过量数据丢弃及统计 |
| `AudioRouter/PrebufferingWaveProvider.cs` | 预缓存、欠载与重缓冲 |
| `AudioRouter/AdaptiveResamplingSampleProvider.cs` | 采样率转换和设备时钟漂移校正 |
| `AudioRouter/ReadFullyWaveProvider.cs` | 输出读取补齐与相关统计 |
| `AudioRouter/LowLatencyLoopbackCapture.cs` | 低延迟 Loopback 捕获 |
| `AudioRouter/AudioDiagnosticsService.cs`、`AudioRouter/AudioForwarderDiagnostics.cs` | 诊断采集与快照 |
| `AudioRouter/App.xaml.cs` | 日志初始化、全局异常记录与退出清理 |

## WPF 与编码约定

- 保持 MVVM：布局和绑定写在 XAML，交互状态和命令写在 ViewModel，音频与设备逻辑写在对应服务中；不向窗口后台代码堆放业务逻辑。
- 沿用 CommunityToolkit.Mvvm 的 `[ObservableProperty]`、`[RelayCommand]` 及通知特性；修改状态时同时检查绑定属性和命令可执行条件。
- 保持现有风格：四空格缩进、文件范围命名空间、私有字段 `_camelCase`、类型及成员使用 PascalCase，启用可空引用检查。
- 不编辑 `obj/`、`bin/` 中的生成代码；资源设计器文件优先通过资源源文件维护。
- 音频回调和后台故障事件更新界面时通过 Dispatcher；注意窗口关闭后已排队回调的释放状态检查。
- UI 变更检查运行中、已停止、等待恢复三种状态下的按钮、提示和配置可编辑性。

## 音频链路约束

- 来源和目标均为活动播放端点，必须是不同设备；不要将来源误改为麦克风或按设备友好名称匹配。
- 设备选择、配置持久化和恢复以 Windows 端点 ID 为依据。运行中或恢复中保持设备及延迟配置锁定，除非需求明确改变该行为。
- 当前延迟选项为 `20 / 50 / 100 / 150 / 200 ms`，默认 `50 ms`；目标档位、软件估算延迟和硬件端到端延迟必须区分。
- 调整音频链路前确认实际使用的捕获方式、输出回退阶段、采样格式及诊断数据；不要仅凭档位推断实际延迟。
- 保留低延迟路径、稳定输出回退和 Media Foundation 兼容路径的职责差异；兼容路径的缓冲需求不能直接套用低延迟参数。
- 调整水位时保持 `0 < LowWatermark < StartWatermark <= RecoveryWatermark < SoftLimit < HardLimit < Capacity`，并保持音频字节数按 `WaveFormat.BlockAlign` 对齐。
- 缓冲、重采样和补零修改需同时考虑欠载、重缓冲、追赶、丢弃、时钟校正及统计口径；不要通过隐藏指标掩盖链路问题。
- 音频高频回调内避免磁盘 I/O、同步 UI 调用、逐帧日志、不必要的分配或长时间持锁。共享状态沿用现有锁、`Interlocked` 和 `Volatile` 等同步方式。
- 修改启动、停止或回退时检查部分初始化失败、重复停止、异步停止回调与释放之间的竞争；确保事件解绑、定时器停止及捕获、输出、重采样器和设备资源释放完整。
- 首次启动失败直接反馈错误；成功运行后的异步故障才进入自动恢复。当前恢复每 2 秒检查原设备 ID，用户停止或关闭窗口后必须终止恢复。
- 当前音量滑块修改的是输出端点的系统主音量，可能影响其他应用；不能按仅对本转发链路生效的数字增益处理。

## 配置与日志

- 配置位于 `%LOCALAPPDATA%\AudioRouter\settings.json`，日志位于 `%LOCALAPPDATA%\AudioRouter\Logs\`；运行数据不写入仓库。
- 保持旧配置兼容、无效配置回退及临时文件写入后替换的保存方式；配置保存失败不应中断音频转发。
- 沿用 Serilog 结构化消息模板，保留异常对象及必要的设备、管线和状态上下文；高频性能数据优先用统计快照呈现。

## 验证与交付

- 文档或规则修改只做内容、路径和 diff 检查，不启动程序、不默认构建。
- 代码修改先做与影响范围相称的静态检查；需要编译验证时，在仓库根目录执行 `dotnet build AudioRouter.sln -c Debug --no-restore`。缺少还原产物时说明情况，按任务需要决定是否执行还原，不自行安装 SDK 或升级包。
- 用户要求完整构建或运行时，可使用 README 中的命令：

  ```powershell
  dotnet restore AudioRouter.sln
  dotnet build AudioRouter.sln -c Release
  dotnet run --project AudioRouter/AudioRouter.csproj -c Release
  ```

- 当前解决方案未包含正式测试项目。不要声称已有自动化音频或端到端测试通过，也不为低影响修改默认引入测试框架。
- 涉及音频行为时，按需验证开始/停止、相同设备拦截、档位切换、设备断开重连、恢复取消和关闭释放；需要真实 Windows 音频设备的验证必须明确记录是否实际执行。
- 性能结论需给出设备、档位、实际管线、观察时长及相关诊断数据；编译成功不能代替听感、延迟或长期稳定性验证。
- 完成后复查 diff，仅汇报实际修改、已执行的验证和必要限制；不提交构建产物、IDE 缓存、日志或本机配置。
