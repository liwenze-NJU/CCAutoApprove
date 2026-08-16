# CCAutoApprove 第一版设计规格

- 日期：2026-08-16
- 状态：已由用户批准
- 目标仓库：https://github.com/liwenze-NJU/CCAutoApprove.git
- 首发平台：Windows 10/11 x64

## 1. 摘要

CCAutoApprove 是一个 Windows 桌面工具。用户仍按原习惯在项目目录中运行 Claude Code CLI；当用户临时离开时，可在 CCAutoApprove 中选择一个项目并开启自动批准。Claude Code 随后产生的 `PermissionRequest` 由官方 Hook 交给 CCAutoApprove，只有在应用存活、开关开启、心跳有效且项目匹配时才返回允许决定。

第一版对符合条件的权限请求统一批准，不模拟键盘、鼠标或终端选择，也不写入 Claude 的“以后不再询问”权限规则。暂停、退出或失联后，Hook 不作决定，Claude 恢复原有人工询问。

项目采用 C#、.NET 10 LTS、WPF、JSON 和 xUnit。内部是带端口与适配器思想的模块化单体，为未来的本地规则、AI 判断、手机审批及其他运行环境保留明确扩展点，但不提前实现这些功能。

## 2. 目标与非目标

### 2.1 第一版目标

1. 使用 Claude Code 官方 `PermissionRequest` Hook 自动处理权限请求。
2. 用户正常使用 `claude` 启动 Claude Code，不需要包装器命令。
3. 自动批准仅作用于用户选定的一个项目目录及其真正子目录。
4. 同一项目目录中的全部 Claude 会话均生效，包括 `/clear` 后的新对话。
5. 用户可以随时通过主窗口或系统托盘开启、暂停和退出。
6. 自动批准只在托盘应用存活且心跳有效时生效。
7. 用户级 Hook 只需安装一次；未选中项目不受影响。
8. 提供简体中文 WPF 窗口、托盘菜单和命令行诊断入口。
9. 提供三档审计记录：不记录、隐私模式、详细模式。
10. 提供可选开机启动，默认关闭；每次应用启动时自动批准仍默认关闭。
11. 提供每用户范围的 Windows 安装程序和可重复、可恢复的 Hook 安装/卸载流程。
12. 对关键安全决策、JSON 契约、配置合并、并发和隐私行为提供自动测试。

### 2.2 第一版非目标

第一版不实现：

- AI 权限判断；
- 本地危险命令规则库；
- 手机审批或手机回复；
- MCP Server；
- Named Pipe 或后台 Windows Service；
- WSL、远程服务器和云端 Claude；
- Git Bash 专项兼容承诺；
- Claude Code 非交互 `-p` 模式适配；
- 自动更新；
- Microsoft Store 或 MSIX 发布；
- ARM64 发布；
- 英文界面；
- 对 Claude 强制 deny、ask 或组织托管策略的绕过。

### 2.3 安全立场

CCAutoApprove 只能在 Claude Code 允许 Hook 作出决定的范围内返回允许。Claude 的 deny/ask 规则、组织策略、需要强制用户交互的工具及其他更严格 Hook 仍可阻止操作。

本产品会降低人工审批频率，因此用户开启时即授权选定项目内符合条件的权限请求自动执行。界面必须明确显示当前项目和启用状态。

## 3. 用户体验

### 3.1 首次使用

1. 用户安装并启动 CCAutoApprove。
2. 应用默认处于“自动批准已暂停”。
3. 用户点击“安装 Claude Hook”。
4. 应用备份并合并 `%USERPROFILE%\.claude\settings.json`。
5. 应用运行诊断并显示 Hook 是否正确注册。
6. 用户通过文件夹选择器选择项目目录。
7. 用户点击“开启自动批准”。
8. 应用开始心跳，窗口与托盘同步显示启用状态。

### 3.2 日常使用

用户仍可在 CMD、PowerShell、Windows Terminal 或 VS Code 的原生 Windows 终端中执行：

```cmd
cd D:\projects\my-app
claude
```

CCAutoApprove 不读取终端屏幕，不模拟输入，也不要求用户从 CCAutoApprove 启动 Claude。

### 3.3 暂停与退出

- “暂停自动批准”立即写入禁用状态，下一次权限请求恢复人工询问。
- 关闭主窗口只隐藏到系统托盘，不退出应用。
- 托盘“退出”先写入禁用状态、停止心跳，再退出进程。
- 强制结束或崩溃时，最后一次成功心跳写入满 10 秒即过期，之后恢复人工询问。
- 每次应用进程重新启动都以禁用状态开始，不恢复上次的启用状态。

### 3.4 多会话和 `/clear`

作用域按目录判断，不按单次会话绑定。只要工作目录仍属于选定项目：

- 同一目录的多个 Claude Code 会话都生效；
- `/clear` 后继续生效；
- `/compact`、恢复对话或重新启动 Claude 不改变目录级规则；
- 选定项目之外的会话继续人工询问。

## 4. 界面设计

### 4.1 主窗口

采用“状态中心”布局，包含三个页面。

#### 状态页

- 当前自动批准状态；
- 开启/暂停主按钮；
- 当前项目绝对路径；
- 选择项目按钮；
- Hook 安装状态；
- 心跳状态；
- 当日自动批准数量；
- 安装与诊断入口。

#### 记录页

- 当前日志等级；
- 最近记录列表；
- 时间、项目、工具、决定和来源；
- 详细模式下查看完整 PermissionRequest；
- 清除全部记录；
- 默认仅显示最近 200 条。

#### 设置页

- 日志等级；
- 随 Windows 启动；
- Hook 安装、诊断和卸载；
- 语言结构预留；
- 应用版本信息。

### 4.2 托盘

托盘菜单包含：

- 当前状态和项目摘要；
- 开启/暂停自动批准；
- 打开主窗口；
- 最近记录；
- 设置；
- 退出。

状态必须同时通过文字和图标表达，不能只依赖颜色。绿色表示开启，灰色表示暂停，红色表示异常。

### 4.3 单实例

应用使用当前用户范围的命名 Mutex，确保只有一个托盘实例。第二次启动显示“CCAutoApprove 已经在运行，请查看系统托盘”并退出。将来可以增加激活已有窗口的进程通信，但第一版不需要。

### 4.4 本地化

第一版只提供简体中文。所有用户可见字符串集中到资源文件中，不把中文散落在业务逻辑里，以便未来加入英文。

## 5. 解决方案架构

### 5.1 总体风格

采用带端口与适配器思想的模块化单体：同一个仓库、Solution、版本和发布包，不部署网络服务，不使用消息队列或数据库。

```text
CCAutoApprove.sln
├─ src/CCAutoApprove.Core
├─ src/CCAutoApprove.Infrastructure
├─ src/CCAutoApprove.Cli
├─ src/CCAutoApprove.App
├─ tests/CCAutoApprove.Core.Tests
├─ tests/CCAutoApprove.Infrastructure.Tests
├─ tests/CCAutoApprove.Cli.Tests
└─ tests/CCAutoApprove.App.Tests
```

### 5.2 Core

Core 只包含平台无关的业务模型、接口和协调逻辑：

- `ApprovalRequest`；
- `ApprovalDecision`；
- `RuntimeState`；
- `AuditRecord`；
- `IDecisionProvider`；
- `IRuntimeStateStore`；
- `IAuditLog`；
- `IClock`；
- `IProjectMatcher`；
- `IProcessIdentityValidator`；
- `ApprovalCoordinator`。

Core 不引用 WPF、Windows Registry、Claude settings、具体 JSON 文件路径、具体 AI SDK 或网络客户端。

### 5.3 Infrastructure

Infrastructure 引用 Core 并实现外部能力：

- JSON 配置与运行状态存储；
- Claude 用户级 Hook 安装、诊断和卸载；
- 隐私/详细审计日志；
- Windows 路径标准化与匹配；
- Windows 进程身份验证；
- Windows 开机启动；
- 系统时间；
- 原子文件替换与进程间日志锁。

### 5.4 CLI

CLI 是控制台程序，也是 Claude Hook 的入口。命令至少包括：

```text
hook
install
uninstall
status
doctor
```

`hook` 从标准输入读取 Claude JSON，标准输出只允许写 Claude 可解析的决定 JSON。诊断信息写入日志或标准错误，不得污染成功响应。

第一版不提供 CLI `enable`/`disable`，因为短生命周期 CLI 不能维持心跳，也没有实现与托盘进程通信。启停必须由正在运行的 App 或托盘完成；未来加入安全的本地 IPC 后再扩展 CLI 启停命令。

### 5.5 App

App 是 WPF/托盘程序，负责：

- 主窗口和 ViewModel；
- 目录选择；
- 启用/暂停；
- 心跳循环；
- 托盘状态；
- 单实例；
- 日志查看；
- 设置；
- Hook 管理与诊断入口。

App 不复制 Core 的路径判断或决策规则。

### 5.6 依赖方向

```text
App ───────────────> Core
Cli ───────────────> Core
App ───────────────> Infrastructure ──> Core
Cli ───────────────> Infrastructure ──> Core
```

Core 不反向依赖任何入口或 Infrastructure。

## 6. 核心模型与扩展点

### 6.1 ApprovalRequest

内部请求至少包含：

```text
RequestId
SessionId
ProjectPath
ToolName
ToolInput
PermissionMode
PermissionSuggestions
ReceivedAtUtc
```

`RequestId` 由本地为每次 Hook 调用生成。完整 `ToolInput` 默认只在内存中存在。

### 6.2 ApprovalDecision

```text
Allow
Deny
Ask
```

- `Allow`：向 Claude 返回允许；
- `Deny`：明确拒绝；
- `Ask`：不作 Hook 决定，保留 Claude 人工询问。

第一版正常启用时使用 `Allow`；所有安全检查失败或内部异常使用 `Ask`。第一版 UI 不提供自动拒绝规则。

决定还包含来源：

```text
LocalAlwaysAllow
LocalRule
AI
RemotePhone
HumanDesktop
```

第一版只产生 `LocalAlwaysAllow`。

### 6.3 IDecisionProvider

```csharp
public interface IDecisionProvider
{
    Task<ApprovalDecision> DecideAsync(
        ApprovalRequest request,
        CancellationToken cancellationToken);
}
```

第一版只实现 `AlwaysAllowDecisionProvider`。异步签名和取消令牌为未来 AI、Named Pipe 和手机审批提供等待、超时和取消能力。

未来实现可包括：

- `RuleBasedDecisionProvider`；
- `AiDecisionProvider`；
- `NamedPipeDecisionProvider`；
- `RemoteApprovalDecisionProvider`。

## 7. 运行状态与心跳

### 7.1 数据目录

```text
%LOCALAPPDATA%\CCAutoApprove\
├─ settings.json
├─ runtime.json
└─ logs\
   ├─ audit-YYYY-MM-DD.jsonl
   └─ error-YYYY-MM-DD.jsonl
```

程序文件与用户数据分离。用户设置和日志不写入安装目录。

### 7.2 长期设置

`settings.json` 至少包含：

```json
{
  "schemaVersion": 1,
  "selectedProject": "D:\\projects\\my-app",
  "startWithWindows": false,
  "language": "zh-CN",
  "auditDetailLevel": "PrivacySafe",
  "auditRetentionDays": 7
}
```

长期设置不保存启用状态。

### 7.3 短期状态

`runtime.json` 至少包含：

```json
{
  "schemaVersion": 1,
  "enabled": true,
  "processId": 12345,
  "processStartUtc": "2026-08-16T10:00:00Z",
  "instanceId": "8f48f1c9-2b9a-4b46-8c31-21be17c274af",
  "heartbeatUtc": "2026-08-16T10:30:08Z",
  "selectedProject": "D:\\projects\\my-app"
}
```

应用每次启动时必须先用本次进程身份写入 `enabled: false` 的新运行状态，再允许用户操作开启按钮；不得沿用上一次进程遗留的 `enabled: true`。

### 7.4 时序

- 心跳周期：2 秒；
- 有效条件：心跳年龄严格小于 10 秒；
- 心跳年龄大于或等于 10 秒：过期；
- 心跳时间比当前 UTC 时间晚超过 2 秒（即年龄小于 -2 秒）：无效；
- 进程不存在或进程启动时间不匹配：无效。

心跳循环使用 `PeriodicTimer`、`async/await` 和 `CancellationToken`，不能阻塞 WPF UI 线程。

### 7.5 文件一致性

状态更新使用同目录临时文件加原子替换。Hook 只能看到完整旧状态或完整新状态。无法读取或验证状态时，不使用缓存的旧状态进行批准。

## 8. 项目范围匹配

### 8.1 规则

选定目录与 Hook 输入 `cwd` 在 Windows 语义下标准化后比较：

- 大小写不敏感；
- 忽略无意义的末尾分隔符；
- 要求绝对路径；
- 相同目录匹配；
- 真正子目录匹配；
- 父目录、兄弟目录或其他磁盘不匹配；
- `D:\projects\my-app-old` 不得匹配 `D:\projects\my-app`。

实现不能使用未经边界检查的字符串前缀比较。

### 8.2 符号链接和目录联接

第一版按标准化文本路径判断，不解析所有目录联接和符号链接到最终物理路径。通过另一个路径访问同一物理目录可能不匹配并恢复人工询问；这是可接受的安全假阴性。

### 8.3 目录有效性

启用前要求选定目录存在。启用期间目录被删除或移动时，应用关闭自动批准并显示错误，不自动猜测替代路径。

## 9. PermissionRequest 数据流

1. Claude Code 即将显示权限请求。
2. 用户级 `PermissionRequest` Hook 启动 `CCAutoApprove.Cli.exe hook`。
3. CLI 从标准输入读取完整 JSON。
4. CLI 验证输入大小、JSON 结构、事件名称、`cwd` 和 `tool_name`。第一版接受的 UTF-8 Hook 输入上限为 1 MiB，超过上限直接 `Ask`。
5. CLI 读取并验证 `runtime.json`。
6. CLI 检查启用状态、进程身份和心跳。
7. CLI 验证请求目录属于选定项目。
8. `ApprovalCoordinator` 调用 `IDecisionProvider`。
9. 只有明确 `Allow` 才向标准输出写结构化允许 JSON。
10. `Ask` 不写决定，Claude 继续原始人工询问。
11. 审计记录在不影响决定的前提下写入。
12. CLI 退出。

允许输出采用 Claude 当前官方契约：

```json
{
  "hookSpecificOutput": {
    "hookEventName": "PermissionRequest",
    "decision": {
      "behavior": "allow"
    }
  }
}
```

实现前需以当前 Claude Code 官方文档和本机实际版本复核字段。若契约不兼容，`doctor` 报错且 Hook 不批准。

### 9.1 性能与超时

- 本地 Hook 典型目标低于 100 毫秒；
- 第一版本地 Hook 内部总超时为 1000 毫秒；
- 超时取消并返回 `Ask`；
- Hook 不等待 WPF 窗口交互；
- 未来网络/AI Provider 必须有独立、有限的超时。

## 10. Hook 安装、诊断与卸载

### 10.1 安装位置

Hook 安装到当前用户的：

```text
%USERPROFILE%\.claude\settings.json
```

使用用户级 Hook 是为了安装一次后由 CCAutoApprove 自己进行项目范围判断，并支持未来多项目管理。

### 10.2 安装要求

1. 原文件不存在时创建合法配置。
2. 原文件存在时必须先完整解析。
3. 非法 JSON 时拒绝安装，不覆盖原文件。
4. 写入前创建带时间戳备份。
5. 保留所有未知字段、权限规则和其他 Hook。
6. 在内存中合并 CCAutoApprove 条目。
7. 用临时文件原子替换。
8. 重新读取验证。
9. 安装幂等，重复执行不添加重复条目。

### 10.3 卸载要求

- 只删除命令路径属于 CCAutoApprove 的 Hook 条目；
- 保留其他用户配置和其他 Hook；
- 重复卸载安全；
- 普通卸载不直接用旧备份覆盖当前设置；
- 卸载失败时明确警告用户存在残留 Hook。

### 10.4 Doctor

`doctor` 至少检查：

- Claude 用户配置是否可读取和解析；
- PermissionRequest Hook 是否存在且不重复；
- Hook 可执行文件是否存在；
- Hook 命令路径是否正确引用当前安装；
- CCAutoApprove 数据目录是否可读写；
- 是否存在禁用全部 Hook 的配置；
- 当前 Claude Code 是否支持所需结构化决定契约；
- 运行状态文件是否有效；
- 选定项目是否存在。

当前开发终端未能从 PowerShell PATH 找到 `claude`。实现期间需定位用户实际从 CMD 启动的 Claude 安装，并确保诊断逻辑不错误假设 PowerShell 与 CMD 环境完全相同。

## 11. 审计与隐私

### 11.1 等级

```text
Disabled
PrivacySafe
Detailed
```

#### Disabled

不保存权限操作记录。必要的应用崩溃信息也不得包含原始 Hook 输入。

#### PrivacySafe（默认）

保存：

- 时间；
- 标准化项目路径；
- 工具名称；
- 决定；
- 决定来源；
- 非敏感错误代码。

不保存：

- 完整 Bash 命令；
- `tool_input`；
- 文件内容；
- Claude 对话；
- 环境变量；
- API Key；
- 原始 Hook JSON。

#### Detailed

用户主动开启并确认警告后，保存完整 PermissionRequest，包括命令、工具参数、权限模式、建议和会话 ID。它不主动读取 `transcript_path` 指向的整段 Claude 对话，也不记录操作执行后的结果。

详细记录可能含密码、API Key、服务器地址或敏感路径。记录只保存在本机，不自动上传。

### 11.2 行为

- 等级切换只影响新记录；
- 从 Detailed 切回较低等级时询问是否删除已有详细记录；
- 提供一键清除全部记录；
- 默认保留 7 天；
- 程序启动时清理过期记录；
- 日志写入失败不改变权限决定；
- 多进程追加使用短时进程间锁；
- 无法取得日志锁时可丢弃该条记录，不阻塞 Hook。

### 11.3 扩展

审计通过 `IAuditLog` 抽象，可实现：

- `NullAuditLog`；
- `PrivacySafeAuditLog`；
- `DetailedAuditLog`。

未来可加入 Windows 当前用户范围加密、字段脱敏和显式导出，不属于第一版。

## 12. 错误处理与安全降级

以下情况全部返回 `Ask`：

- 托盘程序未运行；
- 开关关闭；
- 心跳过期、来自未来或无法解析；
- 进程身份验证失败；
- 项目不匹配；
- 状态文件不存在、损坏或读取失败；
- Hook 输入为空、损坏、字段缺失或超限；
- 决策器抛出异常、取消或超时；
- Claude 协议版本不兼容；
- 未预期顶层异常。

Hook 顶层捕获异常时不得输出 Allow。错误信息不得把原始敏感输入写入默认日志。

心跳写入连续失败时：

1. 最多立即重试一次；
2. 内存状态切换为关闭；
3. 托盘状态变红；
4. 用户看到明确错误；
5. 旧心跳自然过期。

日志、计数或非关键 UI 更新失败，不应把已经完整验证的 `Allow` 改成错误输出；同时失败本身要以不含敏感内容的方式报告。

## 13. 并发与一致性

- 多个 Claude 会话可以同时启动多个短生命周期 Hook 进程；
- Hook 进程只读取运行状态，不修改启用配置；
- 状态文件原子更新，避免部分 JSON；
- 日志使用进程间锁保证一行一个完整 JSON 记录；
- 应用心跳循环串行写入，避免同一实例重叠更新；
- 单实例 Mutex 避免两个托盘应用互相覆盖状态；
- 日志竞争失败允许丢记录，不允许产生错误批准；
- 第一版不引入数据库。

## 14. Async 使用原则

适合异步：

- 心跳计时；
- 文件读写；
- 日志写入；
- 决策器接口；
- 退出清理；
- 未来 AI、Named Pipe 和远程通信。

适合同步：

- 路径比较；
- 小型 JSON 对象的内存验证；
- 布尔条件判断；
- 简单模型转换。

不得在 WPF UI 线程使用阻塞睡眠或同步等待异步任务。

## 15. 开机启动

- 使用当前用户范围注册，不要求管理员权限；
- UI 提供开关，默认关闭；
- 开机后只启动托盘程序；
- 自动批准状态仍初始化为关闭；
- 注册失败不影响手动启动或 Hook；
- 界面不得在注册失败时显示已成功；
- 卸载时删除自己的启动项。

第一版使用当前用户 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 注册表项，并在 `IStartupManager` 后封装读取、幂等写入和删除行为。

## 16. 打包与发布

### 16.1 开发

- 使用 .NET 10 SDK；
- 当前机器仅检测到 .NET 9 SDK，实施前需安装 .NET 10 SDK；
- 使用普通 Debug/Release 构建和 `dotnet run`；
- 开发 Hook 指向稳定的本地发布目录，而不是随构建清理的临时输出。

### 16.2 正式发布

- .NET 10 Windows x64 自包含发布；
- 使用 Inno Setup 生成每用户 `Setup.exe`；
- 默认安装到 `%LOCALAPPDATA%\Programs\CCAutoApprove`；
- 不要求管理员权限；
- 创建开始菜单入口和标准卸载入口；
- GitHub Releases 可附带便携 ZIP，但安装程序是推荐形式；
- Hook 由应用首次运行时经用户明确操作安装，不由安装器静默添加。

### 16.3 卸载

卸载器在删除程序文件前：

1. 停止或请求退出托盘应用；
2. 调用 CLI 移除自身 Hook；
3. 删除自身开机启动项；
4. 删除程序文件；
5. 询问保留或删除用户设置和日志。

若 Hook 清理失败，卸载过程必须提示用户残留位置和手工修复方式。

### 16.4 签名与更新

第一版允许未签名开发发布，并在文档中说明可能出现 SmartScreen 警告。代码签名、自动更新、MSIX 和 Microsoft Store 留待后续版本。

## 17. 测试策略

### 17.1 Core 单元测试

覆盖：

- Windows 路径相同、大小写、末尾分隔符和子目录；
- 相似前缀兄弟目录不匹配；
- 相对、空和非法路径被拒绝；
- 心跳有效、边界、过期、未来和缺失；
- 进程不存在或启动时间不匹配；
- 启用、心跳、项目和 Provider 的完整决策矩阵；
- Provider 异常、取消和超时均为 `Ask`。

时间测试使用 `IClock` 假实现，不实际等待十秒。

### 17.2 JSON 契约测试

覆盖：

- Bash、Write 及其他工具；
- 有无 `permission_suggestions`；
- Windows 路径、Unicode 和中文；
- 未来未知字段可忽略；
- 空、截断、类型错误、缺字段、错误事件和超限输入；
- Allow 输出可被重新解析且没有多余标准输出。

### 17.3 Infrastructure 集成测试

全部使用临时目录或抽象环境，不读取或修改用户真实 Claude 配置。覆盖：

- Hook 安装、重复安装、卸载；
- 保留未知设置和其他 Hook；
- 非法配置拒绝覆盖；
- 备份、原子替换和验证失败恢复；
- 状态并发读写；
- 日志并发追加；
- 隐私日志中不存在测试命令和秘密字符串；
- 详细日志只在明确选择时出现完整请求；
- 开机启动抽象的启停和幂等行为。

### 17.4 CLI 进程契约测试

测试启动真实 CLI 子进程、写标准输入并读取标准输出：

- 禁用、心跳过期、项目不匹配、输入损坏和异常时无 Allow；
- 全部条件满足时输出唯一合法 Allow JSON；
- 启动时无欢迎文字污染 stdout；
- 处理在内部超时内结束。

### 17.5 WPF/ViewModel 测试

业务行为放入可测试 ViewModel：

- 未选项目不能开启；
- 项目不存在不能开启；
- Hook 安装成功/失败状态准确；
- 心跳失败自动关闭并显示异常；
- 日志等级切换和详细警告；
- 开机启动失败回滚 UI；
- 主窗口与托盘状态一致。

视觉部分进行人工检查：中文裁切、DPI、主题可读性、托盘菜单和窗口关闭行为。

### 17.6 并发与真实验收

- 并发至少 50 个模拟 Hook 请求；
- 所有决定正确；
- 日志每行均可解析；
- 无死锁和部分状态文件；
- 使用无副作用命令对真实 Claude Code 手工验收；
- 验收安装、禁用、项目不匹配、启用、暂停、强制退出、`/clear`、多会话和卸载。

### 17.7 CI

GitHub Actions 在 Windows runner 上对 push 和 Pull Request 执行：

- 还原；
- 构建；
- 测试；
- 发布配置的打包冒烟检查。

## 18. 未来扩展

### 18.1 AI 判断

增加 `AiDecisionProvider`，接收完整内存请求并返回结构化决定、理由和置信度。超时、服务不可用、格式无效或低置信度均为 `Ask`。第一版不引入 AI SDK、API Key 或联网能力。

### 18.2 手机审批

未来可增加：

```text
Hook → Named Pipe → 桌面后台程序 → 安全中继 → 手机网页/App
```

必须具备设备配对、请求 ID、会话/项目/内容绑定、过期、一次性响应、防重放、TLS、可撤销设备和安全降级。手机离线或响应无效时返回 `Ask`。

### 18.3 手机回复

权限批准、结构化 `AskUserQuestion` 回答和手机主动发送新提示是三类不同能力。后两者需要 PreToolUse、Claude Remote Control、Channels、Agent SDK 或其他会话控制设计，必须作为独立后续规格处理。

### 18.4 其他运行环境

原生 Windows 的 CMD、PowerShell、Windows Terminal 和 VS Code 终端共享第一版路径模型。Git Bash 需要额外兼容验证；WSL 需要独立路径和进程桥接适配器；远程/云端需要新的部署架构。

## 19. 第一版验收标准

第一版只有在以下条件全部满足时完成：

1. 安装后默认自动批准关闭。
2. 用户级 Hook 可安全安装、诊断、重复安装和卸载。
3. 现有 Claude 配置与其他 Hook 保持不变。
4. 选定项目且全部存活条件满足时自动批准。
5. 未选项目、项目不匹配、心跳过期、进程无效或异常时不自动批准。
6. 暂停后下一次请求恢复人工询问。
7. 强制退出后最迟在心跳有效期结束时恢复人工询问。
8. `/clear` 后目录级自动批准继续生效。
9. 同目录多个 Claude 会话均生效。
10. 默认日志不包含完整命令或 `tool_input`。
11. 详细日志只在用户明确启用并确认警告后记录完整 PermissionRequest。
12. 开机启动默认关闭，且不会自动恢复启用状态。
13. 单实例、托盘和主窗口状态一致。
14. 所有关键单元、集成和 CLI 契约测试通过。
15. 真实 Claude Code 无副作用验收通过。
16. 自包含安装包可在干净的受支持 Windows x64 环境安装、启动和卸载。

## 20. 参考资料

- Claude Code Hooks：https://code.claude.com/docs/en/hooks
- Claude Code Hooks Guide：https://code.claude.com/docs/en/hooks-guide
- Claude Code Permissions：https://code.claude.com/docs/en/permissions
- Claude Code Commands：https://code.claude.com/docs/en/commands
- Claude Code MCP：https://code.claude.com/docs/en/mcp
- WPF Overview：https://learn.microsoft.com/dotnet/desktop/wpf/overview/
- .NET Support Policy：https://dotnet.microsoft.com/platform/support/policy
- .NET Single-file Deployment：https://learn.microsoft.com/dotnet/core/deploying/single-file/overview
- MSIX Overview：https://learn.microsoft.com/windows/msix/overview
