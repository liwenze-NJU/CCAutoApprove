# Four Residual Blockers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the four user-authorized residual blockers without expanding the v1 feature scope.

**Architecture:** Keep Hook decisions fail-closed while separating repair commands from decision settings. Strengthen Claude settings writes with ownership-aware compare/commit/rollback, clear only resolved Hook-specific presentation errors, and correct the Inno Setup percent escaping helper.

**Tech Stack:** .NET 10, C#, xUnit, WPF, JSON, Windows named synchronization primitives, Inno Setup Pascal Script, PowerShell static checks.

**Spec:** `docs/superpowers/specs/2026-08-16-cc-auto-approve-design.md`

## Global Constraints

- Windows-only v1; do not touch real user Claude settings, registry, startup entries, or audit data in tests.
- Hook decision handling remains strict fail-closed when local settings are missing, malformed, or unreadable.
- Hook stdout remains exactly one JSON response and never contains logs or diagnostics.
- Preserve unrelated Claude settings bytes/fields and cross-process serialization semantics.
- **Binding user-approved ruling (2026-08-24):** For an existing Claude settings file, preventing silent loss of external edits takes priority over crash/power-loss atomic replacement. Existing-file mutation must use an exclusive same-handle exact-byte compare/write, restore the exact original bytes before releasing ownership after any catchable write failure, use a conflict-preserving conditional-delete protocol, and document backup/quarantine recovery for abrupt termination. Missing-file creation remains atomic and non-overwriting.
- Do not claim Inno Setup compilation because ISCC is unavailable locally.
- Use strict RED-GREEN TDD for every production behavior change.

---

### Task 1: Close the four residual blockers

**Files:**
- Modify: `src/CCAutoApprove.Cli/CliApplication.cs`
- Modify: `src/CCAutoApprove.Infrastructure/Claude/ClaudeSettingsCommitter.cs`
- Modify: `src/CCAutoApprove.Infrastructure/Claude/ClaudeHookManager.cs`
- Modify: `src/CCAutoApprove.App/ViewModels/StatusViewModel.cs` and/or the shared status mapping/controller boundary
- Modify: `installer/CCAutoApprove.iss`
- Test: existing CLI, Infrastructure, App, and installer/static contract test projects

**Interfaces:**
- Consumes: existing `status`, `uninstall`, Hook settings manager/committer, controller error state, and Inno Setup helper contracts.
- Produces: repair commands independent of local decision settings; ownership-safe Claude settings commit and rollback; recovered Hook status presentation; compile-credible percent escaping.

- [ ] **Step 1: Reproduce non-Hook repair command failures**

Add focused process/application tests proving `status` and `uninstall` remain usable when the local settings file is malformed or unreadable. Verify RED is caused by production composition loading `JsonSettingsStore` before routing the repair command.

- [ ] **Step 2: Make repair composition settings-independent**

Route `status` and `uninstall` without loading local decision settings. Keep the Hook command on the existing strict settings path. Run the focused CLI tests to GREEN.

- [ ] **Step 3: Reproduce both Claude settings interleavings**

Add deterministic tests for: (a) an external edit after optimistic comparison but before replacement move, and (b) an external edit after this attempt writes but before rollback/delete. Verify RED demonstrates overwrite/deletion of the external edit.

- [ ] **Step 4: Make commit and rollback ownership-aware**

Prepare and flush the complete replacement bytes before final ownership acquisition. For an existing settings file, acquire an exclusive no-sharing read/write handle, compare exact bytes through that handle, and keep the same handle open through truncate/write/flush so in-place writers, sibling-temp rename writers, and readers cannot enter the successful mutation interval.

If a catchable failure occurs after truncation, restore and flush the exact pre-comparison bytes while the handle is still exclusive before propagating the failure. Conditional delete must preserve conflicts: quarantine the compared object, delete only when the moved bytes still match, restore a raced external replacement when possible, and otherwise retain both versions while reporting the quarantine recovery path. Missing-file creation must remain an atomic non-overwriting sibling move.

Preserve named cross-process serialization, bounded cancellable contention retries, byte-safe backup, unrelated JSON fields, and prepared-file cleanup. Document that abrupt kill, process/system crash, or power loss can leave a partial/missing primary or staging/quarantine sibling requiring manual recovery; do not claim crash-atomic replacement for an existing file. Run focused in-place writer, sibling-atomic writer, reader-isolation, caught-failure recovery, and Claude manager tests to GREEN.

- [ ] **Step 5: Reproduce stale Hook error presentation**

Add an exact ViewModel/presentation sequence: Hook health false produces `AppController.HookNotOperational`; Hook health later becomes true without a new controller error; status text/glyph/tray mapping must stop reporting Error. Verify RED.

- [ ] **Step 6: Clear only resolved Hook-specific error state**

At the smallest appropriate state boundary, clear or override the resolved Hook-specific error while preserving unrelated runtime errors. Run focused App tests to GREEN.

- [ ] **Step 7: Correct and statically verify Inno escaping**

Add/update a static contract test requiring `EscapePercent` to copy its const input to a mutable result and call `StringChangeEx(Result, '%', '%%', True);` without assigning the Integer return value. Update the script and run the test to GREEN. Do not report compilation.

- [ ] **Step 8: Verify and commit**

Run focused suites, Release solution build/test, publish/layout checks because CLI and Infrastructure composition changed, Task 14/static/PowerShell checks, and `git diff --check`. Record exact commands/results and any skipped environmental gate in the report, self-review the full diff, and commit the changes.

---

## Progress Ledger

- **Fix Round 1 (2026-08-21):** Added held-handle comparison ownership for in-place writers and corrected Hook recovery ordering in `db0059c48e2b45ea968e6ba3cf3d42f6dd7c0725` and `b5c71fe8cdb095dad63129d7a18ab2997f66c7bc`; follow-up review identified that delete sharing still admitted sibling-temp atomic replacement.
- **Fix Round 2 (2026-08-24):** Reproduced sibling-temp `File.Replace` loss for normal commit, backup restore, and conditional delete; implemented exclusive same-handle existing-file rewrite, exact caught-failure recovery, conflict-preserving quarantine delete, reader isolation, and documented abrupt-termination limits in `9b62c66cabfd200c851a1daf5b7bf3fc725bf4af`.
- **Ruling (2026-08-24, user-approved and binding):** Safety-first behavior is accepted: preventing silent external-edit loss outranks crash/power-loss atomic replacement for an existing Claude settings file; missing-file creation remains atomic/non-overwriting. **Cost if wrong:** abrupt kill, crash, or power loss can leave the primary missing or partial and require exact backup or quarantine recovery.
