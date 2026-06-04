# Agent Guide

## Project

WechatDashboard is a Windows WPF desktop application for collecting collaboration messages, detecting `@我`, creating pending Todo items, classifying messages by project, ranking urgency, and showing a local SQLite dashboard. The solution currently targets .NET 10.

## Repository Layout

```text
src/WechatDashboard.Domain/          Domain records and enums
src/WechatDashboard.Application/     Business rules, capture contracts, pipeline services
src/WechatDashboard.Infrastructure/  SQLite repositories and capture adapter implementations
src/WechatDashboard.App/             WPF UI
tests/WechatDashboard.Tests/         Console-based regression test runner
design/                              Design, extension, and development plan documents
```

## Current Architecture

- Message capture must go through `IMessageCaptureAdapter`.
- Adapters return `CaptureBatch` containing `CapturedMessage` records.
- `MessageCapturePipeline` owns deduplication, persistence, `@我` detection, project classification, urgency ranking, Todo creation, and offset saving.
- `WindowTextCaptureAdapter` parses visible-window text snapshots.
- `WindowsUiAutomationSnapshotProvider` plus `SystemWindowsAutomationReader` can read Windows UI Automation top-level windows.
- `CaptureAdapterFactory.CreateDefaultLiveSources(...)` enables `WeChat.WindowText` while preserving JSONL sources for WeChat, Feishu, Shihuatong, and DingTalk.
- WPF "采集一次" uses the live source set, so it runs JSONL import and WeChat visible-window UIA capture together.
- WPF "开始微信监听" / "停止监听" runs the same capture pipeline on a 5-second polling loop.
- WPF capture diagnostics includes a "扫描微信窗口" action that previews UIA window text without persisting messages.
- SQLite is accessed through repository classes in `Infrastructure/Persistence`.
- WPF should call application/infrastructure services and avoid embedding platform-specific capture code.

## Commands

Use these from the repository root:

```powershell
dotnet restore WechatDashboard.sln
dotnet build WechatDashboard.sln --no-restore -v minimal
dotnet run --project tests\WechatDashboard.Tests\WechatDashboard.Tests.csproj --no-restore
dotnet run --project src\WechatDashboard.App\WechatDashboard.App.csproj --no-restore
```

In the Codex sandbox, `dotnet build`, `dotnet run`, and Git writes may require escalation because `.git`, `obj`, and NuGet cache paths can be restricted.

## Development Rules

- Use TDD for behavior changes: write the failing test, run it, implement, then rerun.
- Keep platform-specific message reading inside adapters.
- Do not implement WeChat, Feishu, Shihuatong, or DingTalk collection by process injection, Hook, database cracking, credential capture, or bypassing encryption.
- Preserve `Source` and `SourceMessageKey`; they are required for cross-source deduplication.
- Add or update tests when changing capture, classification, urgency, Todo generation, or SQLite persistence.
- Run build and tests before claiming completion.

## Capture Input

The current concrete adapter is `JsonlDirectoryCaptureAdapter`.

Default WPF capture directory:

```text
%LOCALAPPDATA%\WechatDashboard\capture-inbox
```

Default source subdirectories:

```text
%LOCALAPPDATA%\WechatDashboard\capture-inbox\WeChat
%LOCALAPPDATA%\WechatDashboard\capture-inbox\Feishu
%LOCALAPPDATA%\WechatDashboard\capture-inbox\Shihuatong
%LOCALAPPDATA%\WechatDashboard\capture-inbox\DingTalk
```

Each `.jsonl` line is one message:

```json
{"id":"wx-1","platform":"WeChat","chatId":"crm","chatName":"CRM项目群","senderName":"王经理","content":"@张三 今天处理线上故障","sentAt":"2026-06-03T10:00:00+08:00","messageType":"Text"}
```

## Next Planned Work

Follow `design/2026-06-04-development-plan.md`.

Immediate next step: validate `SystemWindowsAutomationReader` against the actual WeChat desktop window, record the observed UIA text format, and tune parsing if the preview differs from the supported single-line or split-block formats. Current WeChat capture only reads visible UIA text; it does not read hidden chats, encrypted databases, or full historical messages.
