# 从 CCAutoApprove 学习 .NET 架构

本文用仓库中已经存在的类解释概念。先记住一条主线：Claude 的 PermissionRequest JSON 进入 CLI，Infrastructure 把它翻译成 Core 模型，Core 做 fail-safe 判断，最后 CLI 只在 Allow 时写出官方响应；任何不确定情况都回到 Ask。

## Solution 与 Project

`CCAutoApprove.sln` 是 Solution（解决方案），像一个装着相关工程的目录：它让一次 `dotnet build` 或 `dotnet test` 可以处理全仓库。每个 `.csproj` 是 Project（项目），规定自己的目标框架、依赖和输出。

本解决方案有四个生产 Project：

- `CCAutoApprove.Core`：模型、抽象和决策规则。
- `CCAutoApprove.Infrastructure`：JSON 文件、Claude 协议和 Windows API 的实现。
- `CCAutoApprove.App`：WPF 桌面界面与应用生命周期。
- `CCAutoApprove.Cli`：Claude Hook 和诊断命令入口。

测试 Project 与生产 Project 分开，例如 `CCAutoApprove.Core.Tests` 只验证 Core，`CCAutoApprove.Cli.Tests` 还会启动真实 CLI 子进程。

## EXE 与 DLL

EXE 是可以启动的程序入口，DLL 是被其他 Project 引用的程序集。发布后 `CCAutoApprove.App.exe` 是状态中心，`CCAutoApprove.Cli.exe` 是 Hook/诊断入口。Core 和 Infrastructure 在构建阶段也会产生 DLL；自包含单文件发布会把运行所需内容打包进对应 EXE，所以发布目录不一定把每个 DLL 单独展示出来。

App 与 CLI 都引用 Infrastructure 和 Core，Infrastructure 引用 Core，Core 不反向引用外层。这个依赖方向让安全决定不会依赖 WPF 窗口或某个 JSON 文件细节。

## Core、Infrastructure、App、CLI 怎样协作

一次权限请求的主要流程如下：

1. `CliApplication` 给 Hook 路径设置最早 1000 ms 总预算并创建依赖。
2. `HookCommand` 调用 `ClaudePermissionRequestParser`，把 JSON 转成 `ApprovalRequest`。
3. `ApprovalCoordinator` 从 `IRuntimeStateStore` 读取 `RuntimeState`。
4. `RuntimeStateValidator` 验证启用标志、进程身份和 heartbeat；`WindowsProjectMatcher` 验证请求目录属于所选目录树。
5. 条件全部成立后，`AlwaysAllowDecisionProvider` 返回 Allow；否则协调器返回 Ask。
6. `JsonLineAuditLog` 尝试记录结果，`ClaudePermissionResponseWriter` 在内存中形成完整 Allow JSON，`HookCommand` 最后执行一次同步写入。

App 不直接替 Claude 做决定。`AppController` 保存所选项目并控制 `HeartbeatService`；`StatusViewModel`、`RecordsViewModel` 和 `SettingsViewModel` 把状态提供给 WPF 页面。App 停止或暂停后没有新鲜 heartbeat，CLI 就会安全降级。

审计有两条不同生命周期。每个 Hook 请求都会启动新的 CLI 进程，`CliApplication` 重新读取 `PersistentSettings`：Disabled 使用 `NullAuditLog`，其余等级按当时设置创建新的 `JsonLineAuditLog`，所以写入等级从下一次请求起生效。长期运行的 App 不写权限决定；`AppAuditMaintenance` 始终为 `RecordsViewModel` 和 `SettingsViewModel` 提供真实 `JsonLineAuditLog`，只用于读取、计数、清空和过期维护。这样即使 App 在 Disabled 状态启动，之后由 CLI 写入的 Detailed 记录仍可被界面读取和删除。

App 先完成 `RecordsViewModel` 的初始读取、状态计算、窗口和托盘初始化，随后 `AppAuditMaintenance.InitializeThenScheduleAsync` 才把 `AuditRetentionDays` 交给后台清理任务。App 自己持有生命周期 `CancellationTokenSource`；托盘退出进入 `OnExit` 时先请求取消，但不等待后台任务。后台包装任务会观察清理最终完成或失败，并安全吞掉失败，不显示内部路径。

两秒只是关联取消令牌的尽力工作预算，不是“操作系统保证两秒内停止”。`File.Delete` 是同步文件操作：如果它已经进入系统调用，取消只能在下一次可检查的位置生效。首次读取和界面/托盘初始化发生在调度清理之前，因此不会排在这个清理任务的日志互斥体之后；但用户稍后点击刷新时若清理仍占有互斥体，读取仍可能短暂等待。这个边界比声称所有文件操作都能立即取消更准确。

`OnStartup` 在第一次异步等待之前只读取一次 App 生命周期令牌，之后的续体只使用这个值，不再访问可能已由 `OnExit` 释放的令牌源。`AppStartupLifecycle` 把关机取消当作正常结束；设置读取、控制器初始化和各个界面加载 await 之后都有取消检查。若启动中途退出，后续窗口/托盘构造和 retention 调度都不会发生；若清理刚好已进入调度竞态，它仍使用同一个已捕获令牌，并由后台包装任务观察结果。

## 接口与适配器

接口描述“Core 需要什么”，适配器描述“Windows/文件系统如何做到”。例如：

- `IRuntimeStateStore` 的适配器是 `JsonRuntimeStateStore`。
- `ISettingsStore` 的适配器是 `JsonSettingsStore`。
- `IProjectMatcher` 的 Windows 适配器是 `WindowsProjectMatcher`。
- `IProcessIdentityValidator` 的适配器是 `WindowsProcessIdentityValidator`。
- `IAuditLog` 可由 `JsonLineAuditLog` 或 `NullAuditLog` 实现。
- `IDecisionProvider` 在 v1 由 `AlwaysAllowDecisionProvider` 实现。

这种结构也叫 ports and adapters（端口与适配器）：`ApprovalCoordinator` 依赖接口，不知道 JSON 文件或 Windows 进程查询的具体做法。测试可以传入可控实现，未来也可在不改协调器的前提下替换适配器。

## Hook 与 MCP 不是一回事

Hook 是事件触发的本地程序集成。Claude Code 发出 PermissionRequest 时启动 `CCAutoApprove.Cli.exe hook`，在 stdin 给出 JSON，并从 stdout 读取权限响应。这正是 v1 要介入的批准点。

MCP（Model Context Protocol）是 AI 客户端发现工具、资源和提示词的协议。它适合把“可调用能力”提供给模型，却不是同一个权限请求拦截点。CCAutoApprove v1 使用官方 Hook，没有 MCP 服务器；用键盘自动化替 Claude 选择答案也不等价，因为它绕过了明确的协议边界。

## JSON 序列化不是 C/C++ ABI

JSON serialization（序列化）是把 .NET 对象转换为文字数据，反序列化则相反。`ClaudePermissionRequestParser` 把 Claude JSON 变成 `ApprovalRequest`；`JsonSettingsStore` 和 `JsonRuntimeStateStore` 保存 `PersistentSettings`、`RuntimeState`；`ClaudePermissionResponseWriter` 生成官方 Allow JSON。

这与 C/C++ ABI 不同。ABI 关心二进制函数调用、内存布局和链接兼容；JSON 是跨进程的文本契约。字段名、类型和完整 JSON 文档才是双方约定，两个进程不共享对象内存。

## heartbeat 为什么存在

仅有“曾经点过开启”不够安全：App 可能已经崩溃。`HeartbeatService` 每 2 秒更新 `runtime.json`；`RuntimeStateValidator` 要求 `Enabled` 为真、App 进程 ID 与启动时间匹配，并且 heartbeat 小于 10 秒。停止、进程不匹配、未来时间异常或 heartbeat 达到 10 秒都会返回无效状态，`ApprovalCoordinator` 随即 Ask。

这里同时检查进程身份和时间，是为了避免 Windows 重用旧进程 ID 后误把新进程当成原 App。状态文件由 `AtomicFileWriter` 原子替换，读者不会依赖半写入文件。

## sync 与 async

async（异步）适合等待文件、计时器、进程结束等 I/O，不必占住线程。代码中的 `LoadAsync`、`SaveAsync`、`DecideAsync` 和 `HeartbeatService` 循环都属于这类工作。`SemaphoreSlim` 和互斥锁保护并发访问，取消令牌让总预算能向下传播。

sync（同步）表示当前线程等操作完成。Hook 的最后一次 stdout `Write` 刻意使用同步阻塞写：此前已完成解析、决定、审计尝试和内存序列化，只留下一个“提交点”。但 OS 管道不是事务，已经接受的部分字节仍无法回滚；完整说明见 `docs/known-limitations.md`。

## 为什么是模块化单体

CCAutoApprove 是模块化单体：Core、Infrastructure、App、CLI 有清楚边界，却在同一仓库、同一 Solution 中构建，并作为本机的一组程序协作。它没有网络服务发现、独立部署的后端或分布式数据一致性，因此不是微服务。

它也不只是传统三层架构。三层常按“界面—业务—数据”纵向划分；本项目还把 Claude、Windows、日志等外部细节作为适配器，并让依赖只指向 Core 抽象。这样既保留单体部署的简单，也能独立测试安全边界。

## 未来扩展缝隙

未来若增加 AI 决策，可以实现新的 `IDecisionProvider`；若增加移动端确认，可让新的 provider 调用一个明确鉴权的服务；其他 shell 若有正式权限事件，也可新增解析/响应适配器。这些缝隙不等于功能已经存在。

v1 只有 `AlwaysAllowDecisionProvider`，没有 AI 请求、移动端、MCP 服务、网络监听、WSL/Git Bash 集成或微服务。任何未来 provider 都仍应接受 `ApprovalCoordinator` 的目录、进程、heartbeat 和超时保护，失败时保持 Ask。
