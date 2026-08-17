# Task 7 Report: Claude PermissionRequest JSON Contract

## Implementation and files

- Added `ClaudePermissionRequestParser`, which reads stdin in 81,920-byte blocks, rejects payloads over 1,048,576 bytes with `InvalidDataException("HookInputTooLarge")`, deserializes with the shared JSON defaults, validates all required hook fields, ignores unknown fields, and clones both JSON elements before returning an `ApprovalRequest`.
- Added `ClaudePermissionResponseWriter`, which uses `Utf8JsonWriter` to emit the allow response directly to the supplied stream.
- Added literal two-option and three-option Claude PermissionRequest fixtures.
- Added 13 focused contract cases covering both prompt shapes, JSON lifetime safety, unknown fields, every required-field validation, malformed JSON, oversize input, and exact response bytes.
- Updated the Infrastructure test project to copy JSON fixtures to its output directory.

Files:

- `src/CCAutoApprove.Infrastructure/Claude/ClaudePermissionRequestParser.cs`
- `src/CCAutoApprove.Infrastructure/Claude/ClaudePermissionResponseWriter.cs`
- `tests/CCAutoApprove.Infrastructure.Tests/Claude/ClaudePermissionContractTests.cs`
- `tests/CCAutoApprove.Infrastructure.Tests/Fixtures/permission-two-option.json`
- `tests/CCAutoApprove.Infrastructure.Tests/Fixtures/permission-three-option.json`
- `tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj`

## TDD evidence

### RED

Command:

```powershell
D:\cc_auto_approve\.superpowers\dotnet10\dotnet.exe test tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj --filter FullyQualifiedName~ClaudePermissionContractTests
```

Result: exit code 1. Compilation failed with `CS0234` because `CCAutoApprove.Infrastructure.Claude` did not exist. This was the expected missing-feature failure before production code was added.

### GREEN: focused contract

Same command after implementation.

Result: exit code 0; 13 passed, 0 failed, 0 skipped.

### GREEN: full Infrastructure regression suite

Command:

```powershell
D:\cc_auto_approve\.superpowers\dotnet10\dotnet.exe test tests/CCAutoApprove.Infrastructure.Tests/CCAutoApprove.Infrastructure.Tests.csproj
```

Result: exit code 0; 54 passed, 0 failed, 0 skipped.

All test commands normalized `Path`/`PATH`, set `MSBUILDDISABLENODEREUSE=1`, and invoked the pinned SDK directly.

## Byte-level contract review

The allow writer produces exactly 92 UTF-8 bytes:

```json
{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}
```

The test compares the real output stream byte-for-byte against that hand-derived literal, then parses those same bytes and verifies `hookEventName` and `behavior`. There is no BOM, indentation, trailing newline, diagnostic text, or `permission_suggestions`/"Yes and" response. The writer exposes only `WriteAllowAsync`; an Ask decision does not call it and therefore writes zero stdout bytes in the later CLI orchestration contract.

## Self-review and concerns

- Checked each required validation against the brief: exact event name, non-blank session/cwd/tool, fully qualified cwd, and object-valued `tool_input`.
- Checked the strict `>` size boundary and the exact `HookInputTooLarge` value.
- Checked that parsing exceptions are converted to a sanitized `InvalidDataException` without embedding stdin data.
- Checked `JsonElement.Clone()` for both tool input and optional suggestions.
- Checked `git diff --check`; no whitespace errors.
- No implementation concerns. Ask's zero-byte behavior is intentionally enforced by not calling this allow-only writer; the CLI task owns and tests that branch.
