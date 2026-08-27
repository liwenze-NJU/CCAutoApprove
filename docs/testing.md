# CCAutoApprove 测试指南

## Arrange / Act / Assert

一个易读的测试通常分三段：

1. Arrange（准备）：创建输入、依赖和预期环境。
2. Act（执行）：只调用要验证的行为。
3. Assert（断言）：检查返回值、文件、stdout 或状态。

例如 `ApprovalCoordinatorTests` 先 Arrange 一个过期 `RuntimeState`，再 Act 调用 `DecideAsync`，最后 Assert 结果为 Ask 且来源说明是 `StaleHeartbeat`。这种写法让初学者一眼看出“为什么测”和“怎样算通过”。

## 测试层级

- 单元测试：只验证一个小类及边界规则。例：`RuntimeStateValidatorTests`、`WindowsProjectMatcherTests`、`SettingsViewModelTests`。
- 集成测试：让多个真实组件一起工作。例：`JsonStoresTests` 验证 JSON 与原子文件写入，`ClaudeHookManagerTests` 验证 Hook 合并、备份与卸载。
- 进程测试：`HookProcessContractTests` 启动真正的 CLI 子进程，写 stdin 并逐字节检查 stdout、stderr、退出码和时限。
- 手工验收：真人在真实 Claude Code 和桌面 UI 中完成 `scripts/manual-acceptance.ps1` 的十步清单。脚本只记录回答，不会替人操作 Claude。

发布布局测试 `PublishedLayoutTests` 位于 Infrastructure 测试项目；`scripts/publish.ps1` 生成自包含 App/CLI 后设置受控环境变量，再验证文件版本和无真实用户状态副作用的 `status` 命令。安装脚本由 Windows CI 的 Inno Setup 编译，但仍不能代替真实安装/卸载人工冒烟。

## fail-safe 决策矩阵

| 场景 | 关键检查 | 结果 |
|---|---|---|
| Hook 未安装 | Claude 不会调用本程序 | Claude 正常询问 |
| App 已停止或 runtime 缺失 | `IRuntimeStateStore` 无有效状态 | Ask |
| App 运行但未开启/已暂停 | `RuntimeState.Enabled == false` | Ask |
| 请求目录不在所选目录树 | `WindowsProjectMatcher` 不匹配 | Ask |
| 进程 ID/启动时间不匹配 | `WindowsProcessIdentityValidator` 无效 | Ask |
| heartbeat 达到 10 秒或时间异常 | `RuntimeStateValidator` 无效 | Ask |
| JSON 损坏、读取/判断异常 | 捕获异常或无法形成完整响应 | Ask |
| provider 超时、取消或报错 | `ApprovalCoordinator` 捕获失败 | Ask |
| App 有效、目录匹配、Hook 健康 | `AlwaysAllowDecisionProvider` | Allow |

这里的 Ask 不是向 stdout 输出一个“Ask JSON”。官方 Hook 的安全降级是零字节 Allow 输出，让 Claude 沿用正常人工询问。v1 不会自动返回 Deny。

## 怎样运行

```powershell
dotnet restore CCAutoApprove.sln
dotnet build CCAutoApprove.sln -c Release --no-restore
dotnet test CCAutoApprove.sln -c Release --no-build
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1 -Version 0.1.0
```

人工验收另行运行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/manual-acceptance.ps1
```

人工验收需要真实 Claude 和设置副作用，因此必须由人备份、触发并回答；仓库当前没有把十步标成已通过。自动化绿色、发布成功和 CI 编译安装器分别提供不同证据，不能互相冒充。
