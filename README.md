# CCAutoApprove

## CCAutoApprove 是什么

CCAutoApprove 是一个仅面向 Windows 的本地工具。它通过 Claude Code 官方的 `PermissionRequest` Hook，在你明确选择的目录树内自动允许权限请求；其他情况仍交给 Claude 正常询问。v1 是本地模块化单体，不联网、不调用 AI、不提供手机端，也不会自动选择“拒绝”。

Hook 是“某个事件发生时运行本地程序”的集成点：本项目在权限请求发生时读取 JSON、做本地判断，并把官方格式的结果写回 Claude。MCP 则是让 AI 客户端发现和调用工具、资源、提示词的协议；它不是这里所需的权限拦截点，两者不能互相替代。

## 安全警告

开启后，所选目录及其子目录中的请求可能不再逐次等待你确认。只应选择你信任的项目，离开电脑前请暂停或退出 App。自动批准属于目录树，不属于某个聊天或会话；Claude 中的 `/clear` 不会清除这个目录范围状态。暂停、App 停止、心跳过期、目录不匹配、配置或处理出错时，系统均降级为 Ask，让 Claude 恢复正常询问。

Hook 的最早总预算由 CLI 限制为 1000 ms。程序先在内存中构造一个完整响应，再进行一次阻塞式 stdout 写入；但操作系统管道已经接收的字节无法回滚。请阅读 [已知限制](docs/known-limitations.md)。本项目坚持使用官方 Hook；模拟终端按键不是等价或更安全的方案。

## 系统要求

- Windows 10/11 x64。
- 正常使用自包含发布包时无需另装 .NET；从源码构建需要 .NET 10 SDK。
- 已安装并能正常使用支持 `PermissionRequest` Hook 的 Claude Code。

当前开发环境缺少与框架依赖调试版匹配的桌面运行时，因此没有现场目测验证窗口导航、托盘、第二实例提示及 100%–150% DPI；自包含发布已成功。当前环境也没有 `iscc`，所以没有在本地编译安装脚本，也没有生成 Setup EXE 或执行真实安装/卸载冒烟测试。Windows CI 会安装 Inno Setup 并编译脚本。

## 安装

在 CI 或发布流程成功生成安装包后，运行 `artifacts\installer\CCAutoApprove-Setup-<版本>.exe`。安装程序按当前用户安装到 `%LOCALAPPDATA%\Programs\CCAutoApprove`，不要求管理员权限。仓库本身不声称已经发布了可下载的 Setup EXE。

安装完成后启动 CCAutoApprove。Windows 自启动默认关闭，只有你在“设置”中明确开启后才会写入当前用户启动项。

## 首次配置

1. 打开“设置”，点击“安装 Hook”，再运行“检查”。安装器会合并 `~\.claude\settings.json` 并在写入前创建备份；遇到不支持或损坏的结构会停止，而不是覆盖。
2. 回到“状态”，选择一个可信项目目录。范围包含该目录及其子目录，不包含相邻目录。
3. 点击“开启自动批准”。只有 Hook 健康、目录存在、App 进程身份有效且心跳新鲜时才能开启。
4. 用无害请求进行人工验证。真实的十步 Claude 验收尚未执行；请按“测试”一节运行安全清单并亲自确认。

## 日常使用

状态中心显示运行/暂停、所选项目、Hook 状态、心跳和当天批准数量。关闭主窗口会隐藏到系统托盘；要彻底停止，请使用托盘“退出”。需要换项目时先暂停，再选择目录并重新开启。

“暂停”会停止心跳，下一次请求应恢复正常询问。强制结束 App 后，旧心跳最多在 10 秒边界内失效；验证器把达到 10 秒的心跳判为过期。多个 Claude 会话可共享同一个目录范围，但这一真实场景仍待十步人工验收确认。`/clear` 只清理 Claude 会话上下文，不改变 CCAutoApprove 的目录级开关。

## 日志隐私等级

- `Disabled`：不写审计日志。
- `PrivacySafe`：默认值，记录时间、项目、工具、决定和来源，不保存会话 ID、权限模式或工具输入。
- `Detailed`：额外保存会话 ID、权限模式、工具输入和权限建议，可能包含敏感数据，只在确有需要时开启。

日志位于 `%LOCALAPPDATA%\CCAutoApprove\logs`，可在“记录”页查看、刷新和确认后清空。设置模型虽然包含默认 7 天的保留值，当前 v1 启动流程却没有调用自动过期清理，因此不要依赖日志在 7 天后自动删除。离开 Detailed 时，界面会询问是否删除已有详细日志。等级变更会保存到设置；对已经创建的 `JsonLineAuditLog` 是否立即重配置尚待最终验证，稳妥做法是修改后退出并重启 App，再进行敏感操作。

## 命令行诊断

安装目录的 CLI 位于 `%LOCALAPPDATA%\Programs\CCAutoApprove\cli\CCAutoApprove.Cli.exe`。在 PowerShell 中可运行：

```powershell
& "$env:LOCALAPPDATA\Programs\CCAutoApprove\cli\CCAutoApprove.Cli.exe" status
& "$env:LOCALAPPDATA\Programs\CCAutoApprove\cli\CCAutoApprove.Cli.exe" doctor
```

`status` 输出 JSON，报告 Hook、运行状态和自动批准是否有效；`doctor` 输出逐项检查。`install` 和 `uninstall` 会修改真实 Claude 设置，应优先通过 App 的按钮执行并先确认备份。`hook` 是 Claude 调用的协议入口，不是给人手工批准用的命令。

## 卸载

先在 App“设置”中点击“卸载 Hook”，确认 Claude 已恢复正常询问，再从 Windows“已安装的应用”卸载 CCAutoApprove。安装程序卸载时也会尝试移除 Hook 和当前用户启动项；交互卸载会询问是否删除 `%LOCALAPPDATA%\CCAutoApprove` 中的设置、运行状态和日志，静默卸载默认保留用户数据。

由于当前开发环境没有 Inno Setup 编译器，本项目尚未完成真实安装/卸载冒烟验证。卸载后请人工检查 Claude 的正常询问行为。

## 从源码构建

```powershell
dotnet restore CCAutoApprove.sln
dotnet build CCAutoApprove.sln -c Release --no-restore
dotnet test CCAutoApprove.sln -c Release --no-build
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1 -Version 0.1.0
```

发布脚本只清理仓库内固定的 `artifacts\publish\win-x64`，然后生成 App 和 CLI 两个自包含单文件程序，并运行发布布局测试。编译安装器还需要 Inno Setup 6：

```powershell
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DAppVersion=0.1.0 installer/CCAutoApprove.iss
```

## 测试

自动化测试覆盖 Core 决策、JSON/Windows 适配器、Hook 协议与真实子进程、ViewModel、并发/生命周期和发布布局。测试思想、层级和 fail-safe 矩阵见 [测试指南](docs/testing.md)。

真实 Claude 十步验收尚未运行，不能把自动化测试当成这一步已经通过。请在你准备好备份和恢复真实 Claude 设置后运行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/manual-acceptance.ps1
```

脚本只显示说明并记录你输入的 `y`/`n`，不会启动或控制 Claude，不会安装/卸载 Hook，也不会写注册表或发送终端按键。验收记录默认由你指定到仓库或临时目录；不要提交含私人路径的记录。

## 项目结构

```text
src/CCAutoApprove.Core            业务模型、接口、fail-safe 决策
src/CCAutoApprove.Infrastructure  JSON、Claude Hook、Windows、日志适配器
src/CCAutoApprove.App             WPF 状态中心、托盘、生命周期
src/CCAutoApprove.Cli             Hook 与诊断命令入口
tests/                            单元、集成、进程与发布布局测试
installer/                        Inno Setup 脚本
scripts/                          发布与人工验收脚本
```

面向初学者的 Solution/Project、EXE/DLL、接口/适配器等讲解见 [架构学习指南](docs/learning/architecture.md)。

## 未来方向

Core 中的 `IDecisionProvider` 为未来本地规则、AI 或手机确认保留替换点，`IAuditLog`、`ISettingsStore` 等接口也允许替换基础设施。但 v1 只使用本地 `AlwaysAllowDecisionProvider`，没有 AI 调用、MCP 服务、移动端服务器、网络监听、WSL/Git Bash 集成或自动 Deny；这些是未来设计缝隙，不是已经实现的功能。
