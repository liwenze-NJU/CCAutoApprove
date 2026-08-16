# CCAutoApprove v1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows desktop application that uses Claude Code's official `PermissionRequest` Hook to auto-approve requests only for one selected project while a live, explicitly enabled tray process maintains a valid heartbeat.

**Architecture:** A modular monolith with platform-neutral decisions in `CCAutoApprove.Core`, Windows/JSON/Claude integrations in `CCAutoApprove.Infrastructure`, a short-lived console Hook host in `CCAutoApprove.Cli`, and a WPF/tray host in `CCAutoApprove.App`. The Hook fails safely to `Ask` for every invalid, stale, mismatched, timed-out, or exceptional condition.

**Tech Stack:** C# 14, .NET 10 LTS, WPF/XAML, System.Text.Json, Windows Forms `NotifyIcon`, xUnit, Inno Setup, GitHub Actions on Windows.

## Global Constraints

- Target Windows 10/11 x64 and `net10.0` / `net10.0-windows`; ARM64, WSL, cloud, and non-interactive `claude -p` are out of scope.
- Require .NET 10 SDK; use `global.json` with `10.0.100` and `rollForward: latestFeature`.
- Do not add third-party runtime or MVVM packages in v1; use BCL APIs, manual composition, and small local command/ViewModel helpers.
- `CCAutoApprove.Core` must not reference WPF, Windows Registry, Claude settings paths, or concrete AI/network clients.
- Auto-approval starts disabled on every App process start and is enabled only from the live App/tray process.
- Heartbeat interval is 2 seconds; age `>= 10 seconds` is stale; a heartbeat more than 2 seconds in the future is invalid.
- Hook input maximum is 1 MiB UTF-8; local Hook total timeout is 1000 milliseconds.
- Exact selected directory and true descendants match case-insensitively; prefix siblings such as `my-app-old` never match `my-app`.
- Every uncertain or erroneous condition returns internal `Ask`; never emit `Allow` from an exception path.
- Hook stdout contains only valid Claude decision JSON for `Allow`; `Ask` produces zero stdout bytes.
- Default logging is `PrivacySafe`, retention is 7 days, and raw commands/`tool_input` never enter default logs.
- Existing Claude settings and unrelated Hooks must be preserved; Hook install/uninstall is atomic and idempotent.
- All user-facing text is Simplified Chinese and stored in centralized WPF resources.
- Follow TDD: write a failing test, confirm the expected failure, implement the minimum behavior, rerun focused tests, then run the affected project suite.
- Design reference: `docs/superpowers/specs/2026-08-16-cc-auto-approve-design.md`.

---

## Planned File Structure

```text
CCAutoApprove.sln
Directory.Build.props
global.json
README.md
src/
  CCAutoApprove.Core/
    Models/
      ApprovalDecision.cs
      ApprovalRequest.cs
      AuditDetailLevel.cs
      AuditRecord.cs
      PersistentSettings.cs
      RuntimeState.cs
      RuntimeValidationResult.cs
    Abstractions/
      IAuditLog.cs
      IClock.cs
      ICurrentProcessInfo.cs
      IDecisionProvider.cs
      IDirectoryService.cs
      IHookHealthService.cs
      IProcessIdentityValidator.cs
      IProjectMatcher.cs
      IRuntimeStateStore.cs
      ISettingsStore.cs
      IStartupManager.cs
    Decisions/
      AlwaysAllowDecisionProvider.cs
      ApprovalCoordinator.cs
      RuntimeStateValidator.cs
  CCAutoApprove.Infrastructure/
    Claude/
      ClaudeDoctor.cs
      ClaudeHookManager.cs
      ClaudePermissionRequestParser.cs
      ClaudePermissionResponseWriter.cs
      DoctorCheck.cs
    Configuration/
      AppPaths.cs
      AtomicFileWriter.cs
      JsonDefaults.cs
      JsonRuntimeStateStore.cs
      JsonSettingsStore.cs
    Logging/
      JsonLineAuditLog.cs
      NullAuditLog.cs
    Windows/
      SystemClock.cs
      WindowsCurrentProcessInfo.cs
      WindowsDirectoryService.cs
      WindowsProcessIdentityValidator.cs
      WindowsProjectMatcher.cs
      WindowsStartupManager.cs
  CCAutoApprove.Cli/
    CliApplication.cs
    HookCommand.cs
    Program.cs
  CCAutoApprove.App/
    App.xaml
    App.xaml.cs
    MainWindow.xaml
    MainWindow.xaml.cs
    Commands/
      AsyncRelayCommand.cs
      RelayCommand.cs
    Resources/
      Strings.zh-CN.xaml
    Services/
      AppController.cs
      HeartbeatService.cs
      IHeartbeatTimer.cs
      PeriodicHeartbeatTimer.cs
      SingleInstanceGuard.cs
      TrayIconService.cs
    ViewModels/
      MainViewModel.cs
      RecordsViewModel.cs
      SettingsViewModel.cs
      StatusViewModel.cs
    Views/
      RecordsView.xaml
      SettingsView.xaml
      StatusView.xaml
tests/
  CCAutoApprove.Core.Tests/
  CCAutoApprove.Infrastructure.Tests/
  CCAutoApprove.Cli.Tests/
  CCAutoApprove.App.Tests/
installer/
  CCAutoApprove.iss
scripts/
  publish.ps1
  manual-acceptance.ps1
.github/workflows/ci.yml
docs/
  learning/architecture.md
  testing.md
```

## Task 1: Scaffold the Solution and Decision Contract

**Files:**
- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `CCAutoApprove.sln`
- Create: `src/CCAutoApprove.Core/CCAutoApprove.Core.csproj`
- Create: `src/CCAutoApprove.Infrastructure/CCAutoApprove.Infrastructure.csproj`
- Create: `src/CCAutoApprove.Cli/CCAutoApprove.Cli.csproj`
- Create: `src/CCAutoApprove.App/CCAutoApprove.App.csproj`
- Create: `tests/CCAutoApprove.Core.Tests/CCAutoApprove.Core.Tests.csproj`
- Create: `tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj`
- Create: `tests/CCAutoApprove.Cli.Tests/CCAutoApprove.Cli.Tests.csproj`
- Create: `tests/CCAutoApprove.App.Tests/CCAutoApprove.App.Tests.csproj`
- Create: `src/CCAutoApprove.Core/Models/ApprovalDecision.cs`
- Create: `src/CCAutoApprove.Core/Abstractions/IDecisionProvider.cs`
- Create: `src/CCAutoApprove.Core/Decisions/AlwaysAllowDecisionProvider.cs`
- Test: `tests/CCAutoApprove.Core.Tests/Decisions/AlwaysAllowDecisionProviderTests.cs`

**Interfaces:**
- Produces: `ApprovalDecision`, `ApprovalDecisionKind`, `DecisionSource`, and `IDecisionProvider.DecideAsync(ApprovalRequest, CancellationToken)`.
- Consumes: no earlier task.

- [ ] **Step 1: Install or verify the .NET 10 SDK**

Run:

```powershell
dotnet --list-sdks
```

If no `10.0.x` SDK appears, run with user approval:

```powershell
winget install --id Microsoft.DotNet.SDK.10 -e --source winget
```

Expected: `dotnet --version` reports a `10.0.x` SDK after opening a new shell.

- [ ] **Step 2: Create the solution and projects**

Run:

```powershell
dotnet new sln -n CCAutoApprove
dotnet new classlib -n CCAutoApprove.Core -o src/CCAutoApprove.Core -f net10.0
dotnet new classlib -n CCAutoApprove.Infrastructure -o src/CCAutoApprove.Infrastructure -f net10.0-windows
dotnet new console -n CCAutoApprove.Cli -o src/CCAutoApprove.Cli -f net10.0-windows
dotnet new wpf -n CCAutoApprove.App -o src/CCAutoApprove.App -f net10.0-windows
dotnet new xunit -n CCAutoApprove.Core.Tests -o tests/CCAutoApprove.Core.Tests -f net10.0
dotnet new xunit -n CCAutoApprove.Infrastructure.Tests -o tests/CCAutoApprove.Infrastructure.Tests -f net10.0-windows
dotnet new xunit -n CCAutoApprove.Cli.Tests -o tests/CCAutoApprove.Cli.Tests -f net10.0-windows
dotnet new xunit -n CCAutoApprove.App.Tests -o tests/CCAutoApprove.App.Tests -f net10.0-windows
dotnet sln CCAutoApprove.sln add src/CCAutoApprove.Core/CCAutoApprove.Core.csproj src/CCAutoApprove.Infrastructure/CCAutoApprove.Infrastructure.csproj src/CCAutoApprove.Cli/CCAutoApprove.Cli.csproj src/CCAutoApprove.App/CCAutoApprove.App.csproj tests/CCAutoApprove.Core.Tests/CCAutoApprove.Core.Tests.csproj tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj tests/CCAutoApprove.Cli.Tests/CCAutoApprove.Cli.Tests.csproj tests/CCAutoApprove.App.Tests/CCAutoApprove.App.Tests.csproj
dotnet add src/CCAutoApprove.Infrastructure/CCAutoApprove.Infrastructure.csproj reference src/CCAutoApprove.Core/CCAutoApprove.Core.csproj
dotnet add src/CCAutoApprove.Cli/CCAutoApprove.Cli.csproj reference src/CCAutoApprove.Core/CCAutoApprove.Core.csproj src/CCAutoApprove.Infrastructure/CCAutoApprove.Infrastructure.csproj
dotnet add src/CCAutoApprove.App/CCAutoApprove.App.csproj reference src/CCAutoApprove.Core/CCAutoApprove.Core.csproj src/CCAutoApprove.Infrastructure/CCAutoApprove.Infrastructure.csproj
dotnet add tests/CCAutoApprove.Core.Tests/CCAutoApprove.Core.Tests.csproj reference src/CCAutoApprove.Core/CCAutoApprove.Core.csproj
dotnet add tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj reference src/CCAutoApprove.Core/CCAutoApprove.Core.csproj src/CCAutoApprove.Infrastructure/CCAutoApprove.Infrastructure.csproj
dotnet add tests/CCAutoApprove.Cli.Tests/CCAutoApprove.Cli.Tests.csproj reference src/CCAutoApprove.Cli/CCAutoApprove.Cli.csproj
dotnet add tests/CCAutoApprove.App.Tests/CCAutoApprove.App.Tests.csproj reference src/CCAutoApprove.App/CCAutoApprove.App.csproj
```

Set `global.json` to:

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

Set `Directory.Build.props` to:

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
    <LangVersion>14.0</LangVersion>
  </PropertyGroup>
</Project>
```

Delete template `Class1.cs` files. Add `<UseWindowsForms>true</UseWindowsForms>` to `CCAutoApprove.App.csproj` for `NotifyIcon` support.

- [ ] **Step 3: Write the failing decision-provider test**

```csharp
using CCAutoApprove.Core.Decisions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Core.Tests.Decisions;

public sealed class AlwaysAllowDecisionProviderTests
{
    [Fact]
    public async Task DecideAsync_ReturnsLocalAlwaysAllow()
    {
        var provider = new AlwaysAllowDecisionProvider();
        var request = ApprovalRequest.CreateForTest(@"D:\projects\my-app", "Bash");

        ApprovalDecision decision = await provider.DecideAsync(request, CancellationToken.None);

        Assert.Equal(ApprovalDecisionKind.Allow, decision.Kind);
        Assert.Equal(DecisionSource.LocalAlwaysAllow, decision.Source);
    }
}
```

- [ ] **Step 4: Run the focused test and verify the expected failure**

Run:

```powershell
dotnet test tests/CCAutoApprove.Core.Tests/CCAutoApprove.Core.Tests.csproj --filter FullyQualifiedName~AlwaysAllowDecisionProviderTests
```

Expected: FAIL to compile because `ApprovalDecision`, `ApprovalRequest`, and `AlwaysAllowDecisionProvider` do not exist.

- [ ] **Step 5: Implement the minimal decision contract**

`ApprovalDecision.cs`:

```csharp
namespace CCAutoApprove.Core.Models;

public enum ApprovalDecisionKind { Allow, Deny, Ask }
public enum DecisionSource { LocalAlwaysAllow, LocalRule, AI, RemotePhone, HumanDesktop }

public sealed record ApprovalDecision(
    ApprovalDecisionKind Kind,
    DecisionSource Source,
    string? Reason = null)
{
    public static ApprovalDecision Allow(DecisionSource source) => new(ApprovalDecisionKind.Allow, source);
    public static ApprovalDecision Ask(string reason) => new(ApprovalDecisionKind.Ask, DecisionSource.HumanDesktop, reason);
}
```

Create `ApprovalRequest.cs` now because the provider contract consumes it:

```csharp
using System.Text.Json;

namespace CCAutoApprove.Core.Models;

public sealed record ApprovalRequest(
    Guid RequestId,
    string SessionId,
    string ProjectPath,
    string ToolName,
    JsonElement ToolInput,
    string PermissionMode,
    JsonElement? PermissionSuggestions,
    DateTimeOffset ReceivedAtUtc)
{
    public static ApprovalRequest CreateForTest(string projectPath, string toolName)
    {
        using JsonDocument document = JsonDocument.Parse("{}");
        return new(Guid.NewGuid(), "test-session", projectPath, toolName,
            document.RootElement.Clone(), "default", null, DateTimeOffset.UtcNow);
    }
}
```

`IDecisionProvider.cs`:

```csharp
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Core.Abstractions;

public interface IDecisionProvider
{
    Task<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken cancellationToken);
}
```

`AlwaysAllowDecisionProvider.cs`:

```csharp
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Core.Decisions;

public sealed class AlwaysAllowDecisionProvider : IDecisionProvider
{
    public Task<ApprovalDecision> DecideAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow));
}
```

- [ ] **Step 6: Run the solution build and focused test**

```powershell
dotnet build CCAutoApprove.sln
dotnet test tests/CCAutoApprove.Core.Tests/CCAutoApprove.Core.Tests.csproj --filter FullyQualifiedName~AlwaysAllowDecisionProviderTests
```

Expected: build succeeds and the focused test passes.

- [ ] **Step 7: Commit the scaffold and decision contract**

```powershell
git add global.json Directory.Build.props CCAutoApprove.sln src tests
git commit -m "feat: scaffold solution and decision contract"
```

## Task 2: Implement Windows Project Matching

**Files:**
- Create: `src/CCAutoApprove.Core/Abstractions/IProjectMatcher.cs`
- Create: `src/CCAutoApprove.Infrastructure/Windows/WindowsProjectMatcher.cs`
- Test: `tests/CCAutoApprove.Infrastructure.Tests/Windows/WindowsProjectMatcherTests.cs`

**Interfaces:**
- Produces: `bool IProjectMatcher.IsMatch(string selectedProject, string requestDirectory)`.
- Consumes: Core project from Task 1.

- [ ] **Step 1: Write exhaustive path theory tests**

```csharp
using CCAutoApprove.Infrastructure.Windows;

namespace CCAutoApprove.Infrastructure.Tests.Windows;

public sealed class WindowsProjectMatcherTests
{
    [Theory]
    [InlineData(@"D:\projects\my-app", @"D:\projects\my-app", true)]
    [InlineData(@"D:\projects\my-app\", @"d:\PROJECTS\MY-APP", true)]
    [InlineData(@"D:\projects\my-app", @"D:\projects\my-app\src", true)]
    [InlineData(@"D:\projects\my-app", @"D:\projects\my-app-old", false)]
    [InlineData(@"D:\projects\my-app", @"D:\projects", false)]
    [InlineData(@"D:\projects\my-app", @"E:\projects\my-app", false)]
    public void IsMatch_UsesDirectoryBoundaries(string selected, string request, bool expected)
    {
        var matcher = new WindowsProjectMatcher();
        Assert.Equal(expected, matcher.IsMatch(selected, request));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative\\path")]
    [InlineData("bad\0path")]
    public void IsMatch_InvalidRequestPath_ReturnsFalse(string request)
    {
        var matcher = new WindowsProjectMatcher();
        Assert.False(matcher.IsMatch(@"D:\projects\my-app", request));
    }
}
```

- [ ] **Step 2: Run the tests and verify failure**

```powershell
dotnet test tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj --filter FullyQualifiedName~WindowsProjectMatcherTests
```

Expected: FAIL because the matcher and interface do not exist.

- [ ] **Step 3: Implement boundary-safe matching**

`IProjectMatcher.cs`:

```csharp
namespace CCAutoApprove.Core.Abstractions;

public interface IProjectMatcher
{
    bool IsMatch(string selectedProject, string requestDirectory);
}
```

`WindowsProjectMatcher.cs`:

```csharp
using CCAutoApprove.Core.Abstractions;

namespace CCAutoApprove.Infrastructure.Windows;

public sealed class WindowsProjectMatcher : IProjectMatcher
{
    public bool IsMatch(string selectedProject, string requestDirectory)
    {
        try
        {
            if (!Path.IsPathFullyQualified(selectedProject) || !Path.IsPathFullyQualified(requestDirectory))
                return false;

            string selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedProject));
            string request = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestDirectory));
            string relative = Path.GetRelativePath(selected, request);

            return relative == "." ||
                (!relative.Equals("..", StringComparison.OrdinalIgnoreCase) &&
                 !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                 !Path.IsPathFullyQualified(relative));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
```

- [ ] **Step 4: Run focused and project tests**

```powershell
dotnet test tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj --filter FullyQualifiedName~WindowsProjectMatcherTests
dotnet test tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj
```

Expected: all tests pass.

- [ ] **Step 5: Commit project matching**

```powershell
git add src/CCAutoApprove.Core/Abstractions/IProjectMatcher.cs src/CCAutoApprove.Infrastructure/Windows/WindowsProjectMatcher.cs tests/CCAutoApprove.Infrastructure.Tests/Windows/WindowsProjectMatcherTests.cs
git commit -m "feat: add boundary-safe project matching"
```

## Task 3: Validate Runtime State and Heartbeats

**Files:**
- Create: `src/CCAutoApprove.Core/Models/RuntimeState.cs`
- Create: `src/CCAutoApprove.Core/Models/RuntimeValidationResult.cs`
- Create: `src/CCAutoApprove.Core/Abstractions/IClock.cs`
- Create: `src/CCAutoApprove.Core/Abstractions/IProcessIdentityValidator.cs`
- Create: `src/CCAutoApprove.Core/Decisions/RuntimeStateValidator.cs`
- Test: `tests/CCAutoApprove.Core.Tests/Decisions/RuntimeStateValidatorTests.cs`

**Interfaces:**
- Produces: `RuntimeStateValidator.Validate(RuntimeState)` and exact heartbeat constants.
- Consumes: no platform implementation; tests use fakes.

- [ ] **Step 1: Write heartbeat boundary tests with a fake clock/process validator**

```csharp
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Decisions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Core.Tests.Decisions;

public sealed class RuntimeStateValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 2, 30, 10, TimeSpan.Zero);

    [Theory]
    [InlineData(true, -2.0, true)]
    [InlineData(true, 0.0, true)]
    [InlineData(true, 9.999, true)]
    [InlineData(true, 10.0, false)]
    [InlineData(false, 0.0, false)]
    public void Validate_EnforcesEnabledAndHeartbeatAge(bool enabled, double ageSeconds, bool expected)
    {
        var state = RuntimeState.CreateForTest(enabled, Now.AddSeconds(-ageSeconds));
        var validator = new RuntimeStateValidator(new FakeClock(Now), new FakeProcessValidator(true));
        Assert.Equal(expected, validator.Validate(state).IsValid);
    }

    [Fact]
    public void Validate_RejectsHeartbeatMoreThanTwoSecondsInFuture()
    {
        var state = RuntimeState.CreateForTest(true, Now.AddSeconds(2.001));
        var validator = new RuntimeStateValidator(new FakeClock(Now), new FakeProcessValidator(true));
        Assert.False(validator.Validate(state).IsValid);
    }

    [Fact]
    public void Validate_RejectsDeadOrReusedProcess()
    {
        var state = RuntimeState.CreateForTest(true, Now);
        var validator = new RuntimeStateValidator(new FakeClock(Now), new FakeProcessValidator(false));
        Assert.False(validator.Validate(state).IsValid);
    }

    private sealed record FakeClock(DateTimeOffset UtcNow) : IClock;
    private sealed record FakeProcessValidator(bool Result) : IProcessIdentityValidator
    {
        public bool IsValid(RuntimeState state) => Result;
    }
}
```

- [ ] **Step 2: Run the focused tests and verify failure**

```powershell
dotnet test tests/CCAutoApprove.Core.Tests/CCAutoApprove.Core.Tests.csproj --filter FullyQualifiedName~RuntimeStateValidatorTests
```

Expected: FAIL because runtime types do not exist.

- [ ] **Step 3: Implement runtime models and validation**

```csharp
namespace CCAutoApprove.Core.Models;

public sealed record RuntimeState(
    int SchemaVersion,
    bool Enabled,
    int ProcessId,
    DateTimeOffset ProcessStartUtc,
    Guid InstanceId,
    DateTimeOffset HeartbeatUtc,
    string SelectedProject)
{
    public static RuntimeState CreateForTest(bool enabled, DateTimeOffset heartbeatUtc) =>
        new(1, enabled, 1234, heartbeatUtc.AddMinutes(-1), Guid.NewGuid(), heartbeatUtc,
            @"D:\projects\my-app");
}

public sealed record RuntimeValidationResult(bool IsValid, string? ErrorCode)
{
    public static RuntimeValidationResult Valid() => new(true, null);
    public static RuntimeValidationResult Invalid(string code) => new(false, code);
}
```

```csharp
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Core.Decisions;

public sealed class RuntimeStateValidator(IClock clock, IProcessIdentityValidator processValidator)
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan HeartbeatLifetime = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromSeconds(2);

    public RuntimeValidationResult Validate(RuntimeState state)
    {
        if (!state.Enabled) return RuntimeValidationResult.Invalid("Disabled");
        if (string.IsNullOrWhiteSpace(state.SelectedProject)) return RuntimeValidationResult.Invalid("MissingProject");
        if (!processValidator.IsValid(state)) return RuntimeValidationResult.Invalid("InvalidProcess");

        TimeSpan age = clock.UtcNow - state.HeartbeatUtc;
        if (age < -FutureTolerance) return RuntimeValidationResult.Invalid("FutureHeartbeat");
        if (age >= HeartbeatLifetime) return RuntimeValidationResult.Invalid("StaleHeartbeat");
        return RuntimeValidationResult.Valid();
    }
}
```

Define `IClock.UtcNow` and `IProcessIdentityValidator.IsValid(RuntimeState)` with the exact signatures used by the tests.

- [ ] **Step 4: Run all Core tests**

```powershell
dotnet test tests/CCAutoApprove.Core.Tests/CCAutoApprove.Core.Tests.csproj
```

Expected: all Core tests pass.

- [ ] **Step 5: Commit runtime validation**

```powershell
git add src/CCAutoApprove.Core tests/CCAutoApprove.Core.Tests
git commit -m "feat: validate runtime heartbeat state"
```

## Task 4: Build the Fail-Safe Approval Coordinator

**Files:**
- Create: `src/CCAutoApprove.Core/Abstractions/IRuntimeStateStore.cs`
- Create: `src/CCAutoApprove.Core/Decisions/ApprovalCoordinator.cs`
- Test: `tests/CCAutoApprove.Core.Tests/Decisions/ApprovalCoordinatorTests.cs`

**Interfaces:**
- Consumes: `RuntimeStateValidator`, `IProjectMatcher`, and `IDecisionProvider`.
- Produces: `Task<ApprovalDecision> ApprovalCoordinator.DecideAsync(ApprovalRequest, CancellationToken)`.

- [ ] **Step 1: Write the decision-matrix tests**

Use local fakes and cover every row below in `ApprovalCoordinatorTests.cs`:

```csharp
[Theory]
[InlineData(false, true, true, ApprovalDecisionKind.Ask)]
[InlineData(true, false, true, ApprovalDecisionKind.Ask)]
[InlineData(true, true, false, ApprovalDecisionKind.Ask)]
[InlineData(true, true, true, ApprovalDecisionKind.Allow)]
public async Task DecideAsync_RequiresAllGuards(
    bool stateExists, bool runtimeValid, bool projectMatches, ApprovalDecisionKind expected)
{
    var coordinator = CreateCoordinator(stateExists, runtimeValid, projectMatches,
        new AlwaysAllowDecisionProvider());

    ApprovalDecision decision = await coordinator.DecideAsync(
        ApprovalRequest.CreateForTest(@"D:\projects\my-app", "Bash"), CancellationToken.None);

    Assert.Equal(expected, decision.Kind);
}
```

Add separate tests whose provider throws, delays longer than 750 milliseconds, returns `Deny`, and returns `Ask`. Expected results are `Ask`, `Ask`, `Deny`, and `Ask` respectively.

- [ ] **Step 2: Run focused tests and verify failure**

```powershell
dotnet test tests/CCAutoApprove.Core.Tests/CCAutoApprove.Core.Tests.csproj --filter FullyQualifiedName~ApprovalCoordinatorTests
```

Expected: FAIL because the coordinator/store interface is missing.

- [ ] **Step 3: Implement the store contract and coordinator**

```csharp
namespace CCAutoApprove.Core.Abstractions;

public interface IRuntimeStateStore
{
    Task<RuntimeState?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(RuntimeState state, CancellationToken cancellationToken);
}
```

```csharp
using CCAutoApprove.Core.Abstractions;
using CCAutoApprove.Core.Models;

namespace CCAutoApprove.Core.Decisions;

public sealed class ApprovalCoordinator(
    IRuntimeStateStore stateStore,
    RuntimeStateValidator stateValidator,
    IProjectMatcher projectMatcher,
    IDecisionProvider decisionProvider)
{
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromMilliseconds(750);

    public async Task<ApprovalDecision> DecideAsync(
        ApprovalRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            RuntimeState? state = await stateStore.LoadAsync(cancellationToken);
            if (state is null) return ApprovalDecision.Ask("MissingRuntimeState");
            RuntimeValidationResult validation = stateValidator.Validate(state);
            if (!validation.IsValid) return ApprovalDecision.Ask(validation.ErrorCode!);
            if (!projectMatcher.IsMatch(state.SelectedProject, request.ProjectPath))
                return ApprovalDecision.Ask("ProjectMismatch");

            return await decisionProvider
                .DecideAsync(request, cancellationToken)
                .WaitAsync(ProviderTimeout, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return ApprovalDecision.Ask(exception is TimeoutException ? "DecisionTimeout" : "DecisionError");
        }
        catch (OperationCanceledException)
        {
            return ApprovalDecision.Ask("DecisionCanceled");
        }
    }
}
```

- [ ] **Step 4: Run the Core suite**

```powershell
dotnet test tests/CCAutoApprove.Core.Tests/CCAutoApprove.Core.Tests.csproj
```

Expected: matrix, exception, cancellation, timeout, Deny, and Ask tests all pass.

- [ ] **Step 5: Commit the coordinator**

```powershell
git add src/CCAutoApprove.Core tests/CCAutoApprove.Core.Tests
git commit -m "feat: add fail-safe approval coordinator"
```

## Task 5: Add Atomic Settings and Runtime Storage

**Files:**
- Create: `src/CCAutoApprove.Core/Models/PersistentSettings.cs`
- Create: `src/CCAutoApprove.Core/Models/AuditDetailLevel.cs`
- Create: `src/CCAutoApprove.Core/Abstractions/ISettingsStore.cs`
- Create: `src/CCAutoApprove.Infrastructure/Configuration/AppPaths.cs`
- Create: `src/CCAutoApprove.Infrastructure/Configuration/AtomicFileWriter.cs`
- Create: `src/CCAutoApprove.Infrastructure/Configuration/JsonDefaults.cs`
- Create: `src/CCAutoApprove.Infrastructure/Configuration/JsonRuntimeStateStore.cs`
- Create: `src/CCAutoApprove.Infrastructure/Configuration/JsonSettingsStore.cs`
- Create: `src/CCAutoApprove.Infrastructure/Windows/SystemClock.cs`
- Create: `src/CCAutoApprove.Infrastructure/Windows/WindowsProcessIdentityValidator.cs`
- Test: `tests/CCAutoApprove.Infrastructure.Tests/Configuration/JsonStoresTests.cs`
- Test: `tests/CCAutoApprove.Infrastructure.Tests/Windows/WindowsProcessIdentityValidatorTests.cs`

**Interfaces:**
- Produces: atomic JSON stores, `AppPaths`, `SystemClock`, and the real process validator.
- Consumes: Core interfaces from Tasks 3-4.

- [ ] **Step 1: Write temporary-directory JSON store tests**

Tests must assert:

```csharp
[Fact]
public async Task RuntimeStore_RoundTripsCompleteState()
{
    using var temp = new TemporaryDirectory();
    var store = new JsonRuntimeStateStore(Path.Combine(temp.Path, "runtime.json"));
    RuntimeState expected = RuntimeState.CreateForTest(true, DateTimeOffset.UtcNow);

    await store.SaveAsync(expected, CancellationToken.None);
    RuntimeState? actual = await store.LoadAsync(CancellationToken.None);

    Assert.Equal(expected, actual);
}

[Fact]
public async Task RuntimeStore_InvalidJson_ReturnsNull()
{
    using var temp = new TemporaryDirectory();
    string path = Path.Combine(temp.Path, "runtime.json");
    await File.WriteAllTextAsync(path, "{\"enabled\":tr");
    var store = new JsonRuntimeStateStore(path);
    Assert.Null(await store.LoadAsync(CancellationToken.None));
}
```

Add a 100-iteration concurrent read/write test that asserts every non-null read deserializes to schema version 1 and never throws.

- [ ] **Step 2: Run the focused store tests and verify failure**

```powershell
dotnet test tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj --filter FullyQualifiedName~JsonStoresTests
```

Expected: FAIL because stores and models do not exist.

- [ ] **Step 3: Implement settings, paths, serializer defaults, and atomic replacement**

`PersistentSettings` defaults:

```csharp
public sealed record PersistentSettings(
    int SchemaVersion = 1,
    string? SelectedProject = null,
    bool StartWithWindows = false,
    string Language = "zh-CN",
    AuditDetailLevel AuditDetailLevel = AuditDetailLevel.PrivacySafe,
    int AuditRetentionDays = 7);

public enum AuditDetailLevel { Disabled, PrivacySafe, Detailed }
```

`AppPaths` must accept an optional base path for tests and otherwise resolve:

```csharp
string root = Environment.GetEnvironmentVariable("CCAA_DATA_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCAutoApprove");
```

`AtomicFileWriter.WriteAllTextAsync` must create the parent directory, write a unique sibling `*.tmp`, flush/close it, then call `File.Move(tempPath, targetPath, true)` in a `try/finally` that deletes a leftover temp file.

Use shared options:

```csharp
public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    PropertyNameCaseInsensitive = true
};
```

Implement `JsonRuntimeStateStore` so missing/invalid files return `null`; `JsonSettingsStore` returns new `PersistentSettings()` for a missing file but throws a typed `InvalidDataException` for malformed settings so the UI can report corruption.

- [ ] **Step 4: Implement real time and process identity adapters**

`WindowsProcessIdentityValidator.IsValid`:

```csharp
public bool IsValid(RuntimeState state)
{
    try
    {
        using Process process = Process.GetProcessById(state.ProcessId);
        DateTimeOffset actualStart = process.StartTime.ToUniversalTime();
        return Math.Abs((actualStart - state.ProcessStartUtc).TotalSeconds) <= 1;
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
    {
        return false;
    }
}
```

`SystemClock.UtcNow` returns `DateTimeOffset.UtcNow`.

- [ ] **Step 5: Run focused and Infrastructure suites**

```powershell
dotnet test tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj --filter "FullyQualifiedName~JsonStoresTests|FullyQualifiedName~WindowsProcessIdentityValidatorTests"
dotnet test tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj
```

Expected: all tests pass, including concurrent reads/writes.

- [ ] **Step 6: Commit atomic persistence**

```powershell
git add src/CCAutoApprove.Core src/CCAutoApprove.Infrastructure tests/CCAutoApprove.Infrastructure.Tests
git commit -m "feat: add atomic settings and runtime storage"
```

## Task 6: Implement Privacy-Safe and Detailed Audit Logs

**Files:**
- Create: `src/CCAutoApprove.Core/Models/AuditRecord.cs`
- Create: `src/CCAutoApprove.Core/Abstractions/IAuditLog.cs`
- Create: `src/CCAutoApprove.Infrastructure/Logging/NullAuditLog.cs`
- Create: `src/CCAutoApprove.Infrastructure/Logging/JsonLineAuditLog.cs`
- Test: `tests/CCAutoApprove.Infrastructure.Tests/Logging/JsonLineAuditLogTests.cs`

**Interfaces:**
- Produces: `IAuditLog.WriteAsync(ApprovalRequest, ApprovalDecision, CancellationToken)` and `ReadRecentAsync(int, CancellationToken)`.
- Consumes: `AppPaths`, `PersistentSettings`, and decision/request models.

- [ ] **Step 1: Write privacy and detail-level tests**

Use a request whose `toolInput.command` is `deploy --api-key secret-value-123`.

```csharp
[Theory]
[InlineData(AuditDetailLevel.Disabled, false, false)]
[InlineData(AuditDetailLevel.PrivacySafe, true, false)]
[InlineData(AuditDetailLevel.Detailed, true, true)]
public async Task WriteAsync_RespectsDetailLevel(
    AuditDetailLevel level, bool createsLog, bool containsSecret)
{
    using var temp = new TemporaryDirectory();
    IAuditLog log = CreateLog(temp.Path, level);
    ApprovalRequest request = CreateSecretRequest();

    await log.WriteAsync(request, ApprovalDecision.Allow(DecisionSource.LocalAlwaysAllow), CancellationToken.None);

    string combined = ReadAllLogText(temp.Path);
    Assert.Equal(createsLog, combined.Length > 0);
    Assert.Equal(containsSecret, combined.Contains("secret-value-123", StringComparison.Ordinal));
}
```

Add tests for one-JSON-object-per-line under 50 concurrent writes, retention deleting files older than 7 days, and `ReadRecentAsync(200)` returning newest-first records.

- [ ] **Step 2: Run audit tests and verify failure**

```powershell
dotnet test tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj --filter FullyQualifiedName~JsonLineAuditLogTests
```

Expected: FAIL because audit implementations do not exist.

- [ ] **Step 3: Implement the audit contract and records**

```csharp
public sealed record AuditRecord(
    DateTimeOffset TimeUtc,
    Guid RequestId,
    string Project,
    string Tool,
    ApprovalDecisionKind Decision,
    DecisionSource Source,
    string? SessionId = null,
    string? PermissionMode = null,
    JsonElement? ToolInput = null,
    JsonElement? PermissionSuggestions = null);

public interface IAuditLog
{
    Task WriteAsync(ApprovalRequest request, ApprovalDecision decision, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditRecord>> ReadRecentAsync(int maximumCount, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
    Task DeleteExpiredAsync(int retentionDays, CancellationToken cancellationToken);
}
```

`PrivacySafe` must set all optional detail fields to `null`. `Detailed` clones the JSON elements. `Disabled` uses `NullAuditLog` and creates no file.

Use daily file names `audit-yyyy-MM-dd.jsonl` and a named mutex `Local\CCAutoApprove.AuditLog`. Wait at most 50 milliseconds for the mutex; if not acquired, discard the audit write without changing the decision.

- [ ] **Step 4: Run focused and Infrastructure tests**

```powershell
dotnet test tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj --filter FullyQualifiedName~JsonLineAuditLogTests
dotnet test tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj
```

Expected: all tests pass and the PrivacySafe output contains neither `secret-value-123` nor `toolInput`.

- [ ] **Step 5: Commit audit logging**

```powershell
git add src/CCAutoApprove.Core src/CCAutoApprove.Infrastructure tests/CCAutoApprove.Infrastructure.Tests
git commit -m "feat: add configurable privacy-safe audit logs"
```

## Task 7: Implement the Claude PermissionRequest JSON Contract

**Files:**
- Create: `src/CCAutoApprove.Infrastructure/Claude/ClaudePermissionRequestParser.cs`
- Create: `src/CCAutoApprove.Infrastructure/Claude/ClaudePermissionResponseWriter.cs`
- Test: `tests/CCAutoApprove.Infrastructure.Tests/Claude/ClaudePermissionContractTests.cs`
- Create fixtures: `tests/CCAutoApprove.Infrastructure.Tests/Fixtures/permission-two-option.json`
- Create fixtures: `tests/CCAutoApprove.Infrastructure.Tests/Fixtures/permission-three-option.json`

**Interfaces:**
- Produces: `ParseAsync(Stream, IClock, CancellationToken)` and `WriteAllowAsync(Stream, CancellationToken)`.
- Consumes: `ApprovalRequest`, `IClock`, and JSON defaults.

- [ ] **Step 1: Add real-shaped PermissionRequest fixtures and failing tests**

`permission-three-option.json`:

```json
{
  "session_id": "abc123",
  "cwd": "D:\\projects\\my-app",
  "permission_mode": "default",
  "hook_event_name": "PermissionRequest",
  "tool_name": "Bash",
  "tool_input": { "command": "dotnet test", "description": "Run tests" },
  "permission_suggestions": [
    {
      "type": "addRules",
      "rules": [{ "toolName": "Bash", "ruleContent": "dotnet test" }],
      "behavior": "allow",
      "destination": "localSettings"
    }
  ]
}
```

The two-option fixture omits `permission_suggestions`. Tests assert both map to the same `ApprovalRequest`, unknown fields are ignored, missing `cwd` fails, wrong event fails, malformed JSON fails, and a stream larger than 1 MiB fails before parsing.

Test response JSON by parsing it and asserting:

```csharp
Assert.Equal("PermissionRequest", root.GetProperty("hookSpecificOutput").GetProperty("hookEventName").GetString());
Assert.Equal("allow", root.GetProperty("hookSpecificOutput").GetProperty("decision").GetProperty("behavior").GetString());
```

- [ ] **Step 2: Run contract tests and verify failure**

```powershell
dotnet test tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj --filter FullyQualifiedName~ClaudePermissionContractTests
```

Expected: FAIL because parser/writer types do not exist.

- [ ] **Step 3: Implement size-limited parsing**

Read in 81920-byte blocks, accumulate into `MemoryStream`, and throw `InvalidDataException("HookInputTooLarge")` when total bytes exceed `1_048_576`. Deserialize into a private DTO with `[JsonPropertyName]` attributes. Require:

```text
hook_event_name == "PermissionRequest"
session_id is non-empty
cwd is non-empty and fully qualified
tool_name is non-empty
tool_input has JSON Object kind
```

Map with cloned `JsonElement` values and generate `Guid.NewGuid()` for `RequestId`.

- [ ] **Step 4: Implement minified Allow output**

Use `Utf8JsonWriter` directly so stdout has one JSON object and no diagnostic text:

```csharp
writer.WriteStartObject();
writer.WritePropertyName("hookSpecificOutput");
writer.WriteStartObject();
writer.WriteString("hookEventName", "PermissionRequest");
writer.WritePropertyName("decision");
writer.WriteStartObject();
writer.WriteString("behavior", "allow");
writer.WriteEndObject();
writer.WriteEndObject();
writer.WriteEndObject();
await writer.FlushAsync(cancellationToken);
```

- [ ] **Step 5: Run contract and Infrastructure suites**

```powershell
dotnet test tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj --filter FullyQualifiedName~ClaudePermissionContractTests
dotnet test tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj
```

Expected: all tests pass for both UI prompt shapes and every malformed-input case.

- [ ] **Step 6: Commit the Claude JSON contract**

```powershell
git add src/CCAutoApprove.Infrastructure/Claude tests/CCAutoApprove.Infrastructure.Tests
git commit -m "feat: implement Claude permission request contract"
```

## Task 8: Install, Diagnose, and Remove the User-Level Hook

**Files:**
- Create: `src/CCAutoApprove.Core/Abstractions/IHookHealthService.cs`
- Create: `src/CCAutoApprove.Infrastructure/Claude/DoctorCheck.cs`
- Create: `src/CCAutoApprove.Infrastructure/Claude/ClaudeHookManager.cs`
- Create: `src/CCAutoApprove.Infrastructure/Claude/ClaudeDoctor.cs`
- Test: `tests/CCAutoApprove.Infrastructure.Tests/Claude/ClaudeHookManagerTests.cs`
- Test: `tests/CCAutoApprove.Infrastructure.Tests/Claude/ClaudeDoctorTests.cs`

**Interfaces:**
- Produces: `InstallAsync`, `UninstallAsync`, `IsInstalledAsync`, `ClaudeDoctor.RunAsync`, and `IHookHealthService.IsOperationalAsync`.
- Consumes: `AtomicFileWriter`; paths are constructor-injected so tests never touch real user settings.

```csharp
public interface IHookHealthService
{
    Task<bool> IsOperationalAsync(CancellationToken cancellationToken);
}
```

`ClaudeDoctor` implements this interface by running the required checks and returning false when any check has severity `Error`.

- [ ] **Step 1: Write merge/idempotency/uninstall tests**

Create an existing settings fixture with `model`, `permissions`, and a separate `PostToolUse` Hook. Tests must assert:

1. Install preserves every existing property and adds one `PermissionRequest` handler.
2. Second install produces exactly one CCAutoApprove command.
3. Install creates one timestamped backup only when content changes.
4. Malformed settings throws `InvalidDataException` and leaves original bytes unchanged.
5. Uninstall removes only the exact CCAutoApprove command and preserves sibling Hooks.
6. Repeated uninstall succeeds without modification.

Expected command for a CLI path `C:\Program Files\CCAutoApprove\cli\CCAutoApprove.Cli.exe`:

```text
"C:\Program Files\CCAutoApprove\cli\CCAutoApprove.Cli.exe" hook
```

- [ ] **Step 2: Run Hook manager tests and verify failure**

```powershell
dotnet test tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ClaudeHookManagerTests|FullyQualifiedName~ClaudeDoctorTests"
```

Expected: FAIL because manager/doctor types do not exist.

- [ ] **Step 3: Implement JSON-node merge and exact ownership matching**

Use `JsonNode.Parse` to retain unknown content. Ensure this shape:

```json
{
  "hooks": {
    "PermissionRequest": [
      {
        "matcher": "",
        "hooks": [
          {
            "type": "command",
            "command": "\"C:\\...\\CCAutoApprove.Cli.exe\" hook"
          }
        ]
      }
    ]
  }
}
```

Ownership is an exact ordinal-ignore-case match of both `type == "command"` and the normalized command string. Do not remove entries based only on containing `CCAutoApprove`.

Before an actual change, copy the original to `settings.json.ccautoapprove-backup-yyyyMMdd-HHmmssfff`, then atomically replace and reparse the result. If reparsing fails, atomically restore the backup and throw.

- [ ] **Step 4: Implement doctor checks**

```csharp
public enum DoctorSeverity { Pass, Warning, Error }
public sealed record DoctorCheck(string Code, DoctorSeverity Severity, string Message);
```

Doctor checks exactly these codes: `ClaudeSettingsReadable`, `ClaudeSettingsValid`, `HookInstalled`, `HookNotDuplicated`, `HookExecutableExists`, `HookCommandPathMatches`, `HooksNotDisabled`, `DataDirectoryWritable`, `RuntimeStateValid`, and `SelectedProjectExists`.

- [ ] **Step 5: Run all Infrastructure tests**

```powershell
dotnet test tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj
```

Expected: install/uninstall never mutate unrelated settings, and all doctor severity tests pass.

- [ ] **Step 6: Commit Hook lifecycle management**

```powershell
git add src/CCAutoApprove.Infrastructure/Claude tests/CCAutoApprove.Infrastructure.Tests/Claude
git commit -m "feat: manage and diagnose Claude hook installation"
```

## Task 9: Implement the CLI and Process Contract

**Files:**
- Create: `src/CCAutoApprove.Cli/CliApplication.cs`
- Create: `src/CCAutoApprove.Cli/HookCommand.cs`
- Modify: `src/CCAutoApprove.Cli/Program.cs`
- Test: `tests/CCAutoApprove.Cli.Tests/CliApplicationTests.cs`
- Test: `tests/CCAutoApprove.Cli.Tests/HookProcessContractTests.cs`

**Interfaces:**
- Produces commands: `hook`, `install`, `uninstall`, `status`, `doctor`.
- Consumes: all Core decision interfaces and Infrastructure adapters from Tasks 2-8.

- [ ] **Step 1: Write in-process CLI tests**

Construct `CliApplication` with injected streams/services and assert:

```csharp
[Fact]
public async Task Hook_WhenDecisionIsAsk_WritesZeroBytesAndReturnsZero()
{
    using var input = new MemoryStream(ValidFixtureBytes);
    using var output = new MemoryStream();
    using var error = new StringWriter();
    var app = CreateApp(ApprovalDecision.Ask("Disabled"));
    int exitCode = await app.RunAsync(
        ["hook"], input, output, error, CancellationToken.None);
    Assert.Equal(0, exitCode);
    Assert.Equal(0, output.Length);
}
```

Use this single signature everywhere:

```csharp
Task<int> RunAsync(
    string[] args,
    Stream input,
    Stream output,
    TextWriter error,
    CancellationToken cancellationToken);
```

Add tests for Allow JSON, unknown command exit code 2 on stderr, install/uninstall delegation, and status containing no raw `tool_input`.

- [ ] **Step 2: Run CLI tests and verify failure**

```powershell
dotnet test tests/CCAutoApprove.Cli.Tests/CCAutoApprove.Cli.Tests.csproj --filter FullyQualifiedName~CliApplicationTests
```

Expected: FAIL because CLI application classes do not exist.

- [ ] **Step 3: Implement Hook orchestration with a 1000 ms total timeout**

`HookCommand.ExecuteAsync` sequence:

```csharp
using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
timeout.CancelAfter(TimeSpan.FromMilliseconds(1000));
ApprovalRequest request = await parser.ParseAsync(input, clock, timeout.Token);
ApprovalDecision decision = await coordinator.DecideAsync(request, timeout.Token);
await auditLog.WriteAsync(request, decision, timeout.Token);
if (decision.Kind == ApprovalDecisionKind.Allow)
    await responseWriter.WriteAllowAsync(output, timeout.Token);
return 0;
```

Wrap the full Hook path in a top-level catch that writes a sanitized error record if possible, writes nothing to stdout, and returns 0. Non-Hook commands return nonzero for failure and may write human-readable stderr.

- [ ] **Step 4: Implement manual command dispatch and production composition**

```csharp
return args.FirstOrDefault()?.ToLowerInvariant() switch
{
    "hook" => await hookCommand.ExecuteAsync(input, output, cancellationToken),
    "install" => await InstallAsync(cancellationToken),
    "uninstall" => await UninstallAsync(cancellationToken),
    "status" => await StatusAsync(output, cancellationToken),
    "doctor" => await DoctorAsync(output, cancellationToken),
    _ => WriteUsageAndReturn2(error)
};
```

`Program.cs` calls `CliApplication.CreateProduction().RunAsync(args, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error, CancellationToken.None)` and assigns the returned exit code. Do not print a startup banner.

- [ ] **Step 5: Write and run real child-process contract tests**

The test sets `CCAA_DATA_DIR` and `CCAA_CLAUDE_SETTINGS_PATH` to temp paths, writes a runtime state pointing to the living test process and current UTC heartbeat, starts the built CLI with `hook`, writes fixture JSON to stdin, then asserts:

```text
matching + enabled + fresh → one valid Allow JSON object
disabled → zero stdout bytes
stale → zero stdout bytes
mismatch → zero stdout bytes
malformed → zero stdout bytes
```

Run:

```powershell
dotnet test tests/CCAutoApprove.Cli.Tests/CCAutoApprove.Cli.Tests.csproj
```

Expected: all process tests finish under 2 seconds and pass.

- [ ] **Step 6: Run all non-UI tests**

```powershell
dotnet test tests/CCAutoApprove.Core.Tests/CCAutoApprove.Core.Tests.csproj
dotnet test tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj
dotnet test tests/CCAutoApprove.Cli.Tests/CCAutoApprove.Cli.Tests.csproj
```

- [ ] **Step 7: Commit the CLI**

```powershell
git add src/CCAutoApprove.Cli tests/CCAutoApprove.Cli.Tests
git commit -m "feat: add fail-safe Claude hook CLI"
```

## Task 10: Add the App Controller, Heartbeat Loop, and ViewModels

**Files:**
- Create: `src/CCAutoApprove.Core/Abstractions/ICurrentProcessInfo.cs`
- Create: `src/CCAutoApprove.Core/Abstractions/IDirectoryService.cs`
- Create: `src/CCAutoApprove.Infrastructure/Windows/WindowsCurrentProcessInfo.cs`
- Create: `src/CCAutoApprove.Infrastructure/Windows/WindowsDirectoryService.cs`
- Create: `src/CCAutoApprove.App/Services/IHeartbeatTimer.cs`
- Create: `src/CCAutoApprove.App/Services/PeriodicHeartbeatTimer.cs`
- Create: `src/CCAutoApprove.App/Services/HeartbeatService.cs`
- Create: `src/CCAutoApprove.App/Services/AppController.cs`
- Create: `src/CCAutoApprove.App/Commands/AsyncRelayCommand.cs`
- Create: `src/CCAutoApprove.App/Commands/RelayCommand.cs`
- Create: `src/CCAutoApprove.App/ViewModels/MainViewModel.cs`
- Create: `src/CCAutoApprove.App/ViewModels/StatusViewModel.cs`
- Create: `src/CCAutoApprove.App/ViewModels/RecordsViewModel.cs`
- Create: `src/CCAutoApprove.App/ViewModels/SettingsViewModel.cs`
- Test: `tests/CCAutoApprove.App.Tests/Services/HeartbeatServiceTests.cs`
- Test: `tests/CCAutoApprove.App.Tests/Services/AppControllerTests.cs`
- Test: `tests/CCAutoApprove.App.Tests/ViewModels/StatusViewModelTests.cs`

**Interfaces:**
- Produces: testable App orchestration and ViewModels; no visual code yet.
- Consumes: stores, Hook manager/health service, audit log, clock, process info, and directory service.

- [ ] **Step 1: Write controller and heartbeat tests**

Tests must assert:

1. `InitializeAsync` writes a new runtime state with `Enabled == false`, current PID/start time, and new instance ID.
2. `EnableAsync` fails when selected project is missing/nonexistent or Hook doctor has an Error.
3. Valid enable writes `Enabled == true` and starts heartbeat.
4. `PauseAsync` immediately writes `Enabled == false`.
5. Heartbeat updates every fake timer tick without overlapping writes.
6. First write failure retries once; second failure raises `Faulted`, disables in memory, and attempts a final disabled write.
7. Shutdown cancels the loop and writes disabled before returning.

Use this injectable timer contract so tests advance ticks instantly; the production implementation wraps `PeriodicTimer(TimeSpan.FromSeconds(2))`:

```csharp
public interface IHeartbeatTimer : IAsyncDisposable
{
    ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Run App service tests and verify failure**

```powershell
dotnet test tests/CCAutoApprove.App.Tests/CCAutoApprove.App.Tests.csproj --filter "FullyQualifiedName~HeartbeatServiceTests|FullyQualifiedName~AppControllerTests"
```

Expected: FAIL because services do not exist.

- [ ] **Step 3: Implement process/directory adapters and heartbeat service**

`ICurrentProcessInfo` exposes `int ProcessId` and `DateTimeOffset ProcessStartUtc`. `IDirectoryService.Exists(string path)` wraps `Directory.Exists`. `IHookHealthService.IsOperationalAsync` is implemented by `ClaudeDoctor` and returns false if any required doctor check has severity `Error`.

Heartbeat state creation must use:

```csharp
new RuntimeState(
    1,
    enabled,
    processInfo.ProcessId,
    processInfo.ProcessStartUtc,
    instanceId,
    clock.UtcNow,
    selectedProject);
```

The service owns one loop Task and one `CancellationTokenSource`; calling Start twice is a no-op, and Stop awaits the existing loop.

- [ ] **Step 4: Implement AppController rules**

`EnableAsync` order:

```text
selected path is non-empty
path exists
ClaudeDoctor has no Error checks
save persistent selected project
write enabled runtime state
start heartbeat
raise state-changed event
```

`PauseAsync` and `ShutdownAsync` are idempotent. On heartbeat `Faulted`, set UI error code `HeartbeatWriteFailed` and transition to disabled.

- [ ] **Step 5: Write failing ViewModel tests and implement commands/properties**

Test that the main status text is `已暂停` initially, `正在自动批准` after enable, and `异常` after a heartbeat fault. Verify `CanExecute` is false while an async command is running and property-change events fire for `IsEnabled`, `StatusText`, and `SelectedProject`.

Implement `AsyncRelayCommand` without synchronous `.Result`/`.Wait()`. Exceptions are surfaced through a supplied async error handler that sets the ViewModel error message.

- [ ] **Step 6: Run all App tests**

```powershell
dotnet test tests/CCAutoApprove.App.Tests/CCAutoApprove.App.Tests.csproj
```

Expected: controller, heartbeat, and ViewModel tests pass without real two-second waits.

- [ ] **Step 7: Commit App behavior**

```powershell
git add src/CCAutoApprove.Core src/CCAutoApprove.Infrastructure src/CCAutoApprove.App tests/CCAutoApprove.App.Tests
git commit -m "feat: add app controller and heartbeat lifecycle"
```

## Task 11: Build the WPF Status Center and Tray

**Files:**
- Modify: `src/CCAutoApprove.App/App.xaml`
- Modify: `src/CCAutoApprove.App/App.xaml.cs`
- Modify: `src/CCAutoApprove.App/MainWindow.xaml`
- Modify: `src/CCAutoApprove.App/MainWindow.xaml.cs`
- Create: `src/CCAutoApprove.App/Resources/Strings.zh-CN.xaml`
- Create: `src/CCAutoApprove.App/Views/StatusView.xaml`
- Create: `src/CCAutoApprove.App/Views/RecordsView.xaml`
- Create: `src/CCAutoApprove.App/Views/SettingsView.xaml`
- Create: `src/CCAutoApprove.App/Services/SingleInstanceGuard.cs`
- Create: `src/CCAutoApprove.App/Services/TrayIconService.cs`
- Test: `tests/CCAutoApprove.App.Tests/Services/SingleInstanceGuardTests.cs`
- Test: `tests/CCAutoApprove.App.Tests/ViewModels/RecordsViewModelTests.cs`
- Test: `tests/CCAutoApprove.App.Tests/ViewModels/SettingsViewModelTests.cs`

**Interfaces:**
- Produces: the approved three-page Simplified Chinese UI and synchronized tray controls.
- Consumes: ViewModels/AppController from Task 10 and audit/Hook services from earlier tasks.

- [ ] **Step 1: Add centralized Chinese resources**

Create resource keys for every visible string, including:

```xml
<sys:String x:Key="AppName">CCAutoApprove</sys:String>
<sys:String x:Key="NavStatus">状态</sys:String>
<sys:String x:Key="NavRecords">记录</sys:String>
<sys:String x:Key="NavSettings">设置</sys:String>
<sys:String x:Key="EnableApproval">开启自动批准</sys:String>
<sys:String x:Key="PauseApproval">暂停自动批准</sys:String>
<sys:String x:Key="ChooseProject">选择项目</sys:String>
<sys:String x:Key="ExitApplication">退出</sys:String>
```

Declare `xmlns:sys="clr-namespace:System;assembly=mscorlib"` and merge the dictionary in `App.xaml`. No user-facing Chinese literals may remain in code-behind.

- [ ] **Step 2: Write Records and Settings ViewModel tests**

Assert Records loads at most 200 newest items, clears after confirmation callback returns true, and displays detail fields only for `Detailed`. Assert changing from Detailed to PrivacySafe calls a deletion-choice callback; choosing delete removes existing detailed logs.

- [ ] **Step 3: Implement the three views and navigation shell**

`MainWindow.xaml` uses a left navigation list with three items and a `ContentControl`. `StatusView` includes current state, enable/pause button, project path, folder picker, Hook health, heartbeat, and today's count. `RecordsView` contains a `DataGrid` for time/tool/decision/source and a details pane. `SettingsView` contains three audit radios, detailed-mode warning, a startup checkbox bound to a read-only `StartupEnabled` value and disabled until Task 12 supplies its command, and Hook install/doctor/uninstall actions.

Use data binding and commands; code-behind is limited to window lifetime and folder-dialog integration.

The navigation shell follows this concrete binding shape:

```xml
<Grid>
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="180" />
        <ColumnDefinition Width="*" />
    </Grid.ColumnDefinitions>
    <ListBox ItemsSource="{Binding Pages}"
             SelectedItem="{Binding SelectedPage}"
             DisplayMemberPath="Title" />
    <ContentControl Grid.Column="1"
                    Content="{Binding SelectedPage.Content}" />
</Grid>
```

The status page binds actions instead of handling button clicks:

```xml
<StackPanel Margin="24">
    <TextBlock Text="{Binding StatusText}" FontSize="28" />
    <TextBlock Text="{Binding SelectedProject}" TextWrapping="Wrap" />
    <StackPanel Orientation="Horizontal">
        <Button Content="{DynamicResource ChooseProject}"
                Command="{Binding ChooseProjectCommand}" />
        <Button Content="{Binding ToggleButtonText}"
                Command="{Binding ToggleApprovalCommand}" />
    </StackPanel>
    <TextBlock Text="{Binding HookHealthText}" />
    <TextBlock Text="{Binding HeartbeatText}" />
    <TextBlock Text="{Binding TodayApprovalCountText}" />
</StackPanel>
```

- [ ] **Step 4: Implement single-instance behavior**

Use a named mutex `Local\CCAutoApprove.App.Singleton`. `SingleInstanceGuard.TryAcquire()` returns false for the second instance. The second process shows `CCAutoApprove 已经在运行，请查看系统托盘。` and exits before creating the main window.

- [ ] **Step 5: Implement tray synchronization and close-to-tray**

Create one `NotifyIcon` with a context menu containing status/project summary, enable or pause, open main window, records, settings, and exit. Marshal actions to `Application.Current.Dispatcher`.

Window `Closing` behavior:

```csharp
if (!trayService.IsExitRequested)
{
    e.Cancel = true;
    Hide();
}
```

Tray Exit sets `IsExitRequested`, awaits `AppController.ShutdownAsync`, disposes `NotifyIcon`, and calls `Application.Shutdown()`.

- [ ] **Step 6: Run App tests and a manual UI smoke test**

```powershell
dotnet test tests/CCAutoApprove.App.Tests/CCAutoApprove.App.Tests.csproj
dotnet run --project src/CCAutoApprove.App/CCAutoApprove.App.csproj
```

Verify: Chinese text is not clipped at 100% and 150% scaling, navigation switches pages, close hides to tray, tray enable/pause updates the window, and a second launch does not create a second tray instance.

- [ ] **Step 7: Commit the WPF/tray UI**

```powershell
git add src/CCAutoApprove.App tests/CCAutoApprove.App.Tests
git commit -m "feat: add WPF status center and tray controls"
```

## Task 12: Add Current-User Startup Control

**Files:**
- Create: `src/CCAutoApprove.Core/Abstractions/IStartupManager.cs`
- Create: `src/CCAutoApprove.Infrastructure/Windows/WindowsStartupManager.cs`
- Modify: `src/CCAutoApprove.App/ViewModels/SettingsViewModel.cs`
- Modify: `src/CCAutoApprove.App/Views/SettingsView.xaml`
- Test: `tests/CCAutoApprove.Infrastructure.Tests/Windows/WindowsStartupManagerTests.cs`
- Test: `tests/CCAutoApprove.App.Tests/ViewModels/SettingsViewModelTests.cs`

**Interfaces:**
- Produces: idempotent per-user startup registration under the exact HKCU Run key.
- Consumes: installed App executable path and settings ViewModel.

```csharp
public interface IStartupManager
{
    bool IsEnabled();
    void Enable();
    void Disable();
}
```

- [ ] **Step 1: Write startup-manager tests against an injected registry adapter**

Define internal `IUserRunRegistry` with `GetValue`, `SetValue`, and `DeleteValue`. Fake it in tests and assert:

```text
Enable writes value name CCAutoApprove and quoted App path plus --minimized
Enable twice performs no duplicate semantic change
Disable removes only CCAutoApprove
IsEnabled rejects a stale/different executable path
registry exception leaves ViewModel checkbox at previous value and exposes an error
```

- [ ] **Step 2: Run focused tests and verify failure**

```powershell
dotnet test tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj --filter FullyQualifiedName~WindowsStartupManagerTests
```

Expected: FAIL because startup manager does not exist.

- [ ] **Step 3: Implement the exact current-user Run registration**

Use:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
Value name: CCAutoApprove
Value data: "C:\...\CCAutoApprove.App.exe" --minimized
```

Use `Registry.CurrentUser.OpenSubKey(..., writable: true)` and never request elevation. Quote the executable path. `Disable` ignores a missing value but does not swallow access/IO errors.

- [ ] **Step 4: Bind the setting and minimized startup behavior**

`SettingsViewModel` updates persistent settings only after registry success. On App startup, `--minimized` suppresses `MainWindow.Show()` but still initializes the tray; auto-approval remains disabled because `InitializeAsync` always writes disabled runtime state.

- [ ] **Step 5: Run Infrastructure and App tests**

```powershell
dotnet test tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj
dotnet test tests/CCAutoApprove.App.Tests/CCAutoApprove.App.Tests.csproj
```

- [ ] **Step 6: Commit startup control**

```powershell
git add src/CCAutoApprove.Infrastructure src/CCAutoApprove.App tests
git commit -m "feat: add optional per-user startup"
```

## Task 13: Publish and Package the Windows Application

**Files:**
- Create: `scripts/publish.ps1`
- Create: `installer/CCAutoApprove.iss`
- Modify: `src/CCAutoApprove.App/CCAutoApprove.App.csproj`
- Modify: `src/CCAutoApprove.Cli/CCAutoApprove.Cli.csproj`
- Create: `tests/CCAutoApprove.Infrastructure.Tests/Packaging/PublishedLayoutTests.cs`

**Interfaces:**
- Produces: `artifacts/publish/win-x64/app/CCAutoApprove.App.exe`, `artifacts/publish/win-x64/cli/CCAutoApprove.Cli.exe`, and `artifacts/installer/CCAutoApprove-Setup-<version>.exe`.
- Consumes: completed App/CLI and Hook uninstall command.

- [ ] **Step 1: Write a published-layout test**

The test accepts an `CCAA_PUBLISH_ROOT` environment variable and asserts the two EXEs exist in stable separate directories, can run `CCAutoApprove.Cli.exe status`, and the App file has the expected product/version metadata. Skip only when the environment variable is absent; CI packaging sets it.

- [ ] **Step 2: Add deterministic self-contained publish settings**

In App and CLI project files add Release-only properties:

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <SelfContained>true</SelfContained>
  <PublishSingleFile>true</PublishSingleFile>
  <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  <DebugType>embedded</DebugType>
  <PublishTrimmed>false</PublishTrimmed>
</PropertyGroup>
```

Do not trim WPF.

- [ ] **Step 3: Implement `publish.ps1`**

The script takes `-Version` defaulting to `0.1.0`, deletes only the explicit workspace `artifacts/publish/win-x64` directory after resolving and verifying it is under the repository, then runs:

```powershell
dotnet publish src/CCAutoApprove.App/CCAutoApprove.App.csproj -c Release -r win-x64 -p:Version=$Version -o artifacts/publish/win-x64/app
dotnet publish src/CCAutoApprove.Cli/CCAutoApprove.Cli.csproj -c Release -r win-x64 -p:Version=$Version -o artifacts/publish/win-x64/cli
```

Then set `CCAA_PUBLISH_ROOT` and run `PublishedLayoutTests`.

- [ ] **Step 4: Write the per-user Inno Setup script**

Required directives:

```ini
[Setup]
AppId={{6B4F8851-B57E-48C8-B922-CEAA7C0AC5AD}
AppName=CCAutoApprove
AppVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\CCAutoApprove
DefaultGroupName=CCAutoApprove
OutputDir=..\artifacts\installer
OutputBaseFilename=CCAutoApprove-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes

[Files]
Source: "..\artifacts\publish\win-x64\app\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs
Source: "..\artifacts\publish\win-x64\cli\*"; DestDir: "{app}\cli"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\CCAutoApprove"; Filename: "{app}\app\CCAutoApprove.App.exe"

[Run]
Filename: "{app}\app\CCAutoApprove.App.exe"; Description: "启动 CCAutoApprove"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\cli\CCAutoApprove.Cli.exe"; Parameters: "uninstall"; Flags: runhidden; RunOnceId: "RemoveClaudeHook"
```

Add uninstall Pascal code that asks whether to delete `%LOCALAPPDATA%\CCAutoApprove`; delete it only after explicit Yes. Always remove the `CCAutoApprove` HKCU Run value before file removal.

- [ ] **Step 5: Build and smoke-test the installer**

```powershell
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1 -Version 0.1.0
iscc /DAppVersion=0.1.0 installer/CCAutoApprove.iss
```

Expected: installer EXE exists, installs without UAC for the current user, first launch is disabled, and uninstall removes Hook/startup entries while honoring the data-retention choice.

- [ ] **Step 6: Commit packaging**

```powershell
git add src/CCAutoApprove.App/CCAutoApprove.App.csproj src/CCAutoApprove.Cli/CCAutoApprove.Cli.csproj scripts installer tests/CCAutoApprove.Infrastructure.Tests/Packaging
git commit -m "build: add self-contained Windows installer"
```

## Task 14: Add CI, Learning Documentation, and Real Claude Acceptance

**Files:**
- Create: `.github/workflows/ci.yml`
- Create: `README.md`
- Create: `docs/learning/architecture.md`
- Create: `docs/testing.md`
- Create: `scripts/manual-acceptance.ps1`
- Modify: `.gitignore`

**Interfaces:**
- Produces: reproducible CI, beginner-oriented documentation, and a safe manual acceptance checklist.
- Consumes: the complete solution and publish script.

- [ ] **Step 1: Write the Windows CI workflow**

```yaml
name: ci

on:
  push:
  pull_request:

permissions:
  contents: read

jobs:
  build-test-package:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v7
      - uses: actions/setup-dotnet@v6
        with:
          dotnet-version: 10.0.x
      - run: dotnet restore CCAutoApprove.sln
      - run: dotnet build CCAutoApprove.sln -c Release --no-restore
      - run: dotnet test CCAutoApprove.sln -c Release --no-build --logger "trx;LogFileName=tests.trx"
      - shell: pwsh
        run: ./scripts/publish.ps1 -Version 0.1.0-ci
      - shell: pwsh
        run: choco install innosetup -y --no-progress
      - shell: pwsh
        run: '& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DAppVersion=0.1.0 installer/CCAutoApprove.iss'
```

Add `artifacts/`, `TestResults/`, `.vs/`, `**/bin/`, and `**/obj/` to `.gitignore` if not already present.

- [ ] **Step 2: Write beginner-oriented README and architecture guide**

README sections, in this order:

```text
CCAutoApprove 是什么
安全警告
系统要求
安装
首次配置
日常使用
日志隐私等级
命令行诊断
卸载
从源码构建
测试
项目结构
未来方向
```

`docs/learning/architecture.md` explains Solution vs Project, EXE vs DLL, Core/Infrastructure/App/CLI, interfaces/adapters, Hook vs MCP, JSON serialization, heartbeat, sync vs async, and why this is a modular monolith rather than microservices. Use the concrete classes built in this plan for every example.

`docs/testing.md` explains Arrange/Act/Assert, unit/integration/process/manual tests, and the fail-safe decision matrix.

- [ ] **Step 3: Create the non-destructive manual acceptance script**

`manual-acceptance.ps1` must never invoke Claude or destructive commands automatically. It prints and records completion for this checklist:

```text
1. Hook absent → Claude prompts normally.
2. Hook installed, App stopped → Claude prompts normally.
3. App running, disabled → Claude prompts normally.
4. App enabled for another directory → Claude prompts normally.
5. App enabled for selected directory → harmless permission request is allowed.
6. Pause → next request prompts normally.
7. Force-close App, wait 10 seconds → next request prompts normally.
8. /clear → selected-directory approval still works while enabled.
9. Two Claude sessions in selected directory → both work.
10. Uninstall Hook → normal prompting is restored.
```

Safe commands are limited to `dotnet --version`, listing a dedicated temporary test directory, and running a no-side-effect test project. The script pauses for the human to trigger each Claude request and enter `y`/`n`; it never sends terminal input to Claude.

- [ ] **Step 4: Run the full verification suite**

```powershell
dotnet restore CCAutoApprove.sln
dotnet build CCAutoApprove.sln -c Release --no-restore
dotnet test CCAutoApprove.sln -c Release --no-build
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1 -Version 0.1.0
git diff --check
```

Expected: all commands exit 0 and the publish layout test passes.

- [ ] **Step 5: Perform real Claude acceptance and record the result**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/manual-acceptance.ps1
```

Expected: all ten human-confirmed checks are recorded as passed. If the current PowerShell still cannot locate `claude`, run acceptance from the user's normal CMD and record the resolved `where claude` path in the acceptance output without committing private user paths.

- [ ] **Step 6: Commit CI and documentation**

```powershell
git add .github README.md docs scripts/manual-acceptance.ps1 .gitignore
git commit -m "docs: add CI and beginner usage guides"
```

- [ ] **Step 7: Final branch verification**

```powershell
git status --short
git log --oneline --decorate -15
dotnet test CCAutoApprove.sln -c Release --no-build
```

Expected: clean status, one focused commit per task, and all tests passing.

---

## Specification Coverage

| Approved design area | Implementation tasks |
|---|---|
| Goals, non-goals, fail-safe safety stance | 1, 4, 9, 14 |
| First use, daily use, pause/exit, multiple sessions, `/clear` | 8, 10, 11, 14 |
| Status Center, tray, single instance, Simplified Chinese resources | 10, 11 |
| Modular-monolith boundaries and dependency direction | 1, then enforced throughout 2-14 |
| Approval models and future decision-provider extension | 1, 4 |
| Persistent settings, runtime state, heartbeat, atomic writes | 3, 5, 10 |
| Exact selected-directory matching and Windows path behavior | 2 |
| PermissionRequest parsing, timeout, Allow/Ask output contract | 4, 7, 9 |
| Hook install, merge, backup, doctor, uninstall | 8, 9 |
| Disabled/privacy-safe/detailed audit modes and retention | 6, 11 |
| Error handling, concurrency, mutexes, async rules | 3-6, 9-12 |
| Optional current-user startup, default off | 12, 13 |
| Self-contained Windows publishing, installer, uninstall | 13 |
| Unit, contract, process, ViewModel, concurrency, CI, real acceptance | every task's tests, finalized in 14 |
| AI/mobile/other-shell future seams without v1 networking | 1, 4, 6, 14 documentation |
| Complete v1 acceptance criteria | 14 manual acceptance checklist |

No v1 task introduces AI calls, MCP, a mobile server, WSL/Git Bash integration, automatic `No`, or Claude response automation. Those remain documented extension points rather than partially implemented features.

---

## Execution Notes

- At execution time, first use `superpowers:using-git-worktrees` to create an isolated feature worktree unless the environment is already an isolated worktree.
- For task-by-task implementation, use `superpowers:subagent-driven-development` with one implementer and the required specification/code-quality reviews per task, or use `superpowers:executing-plans` for inline batches with checkpoints.
- Before any completion claim, use `superpowers:verification-before-completion` and run the exact Task 14 verification commands with fresh output.
- Do not push, publish a GitHub Release, or modify real Claude user settings during automated tests without explicit user authorization. Real settings changes occur only in the manual acceptance stage after a backup and confirmation.
