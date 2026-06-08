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
design/                              Canonical design document and development plan
```

## Current Architecture

- Message capture must go through `IMessageCaptureAdapter`.
- Adapters return `CaptureBatch` containing `CapturedMessage` records.
- `MessageCapturePipeline` owns deduplication, persistence, `@我` detection, project classification, urgency ranking, Todo creation, and offset saving.
- WeChat capture direction is local file/local database ingestion first; visible-window OCR remains a fallback and diagnostics path.
- `WeChatLocalExportCaptureAdapter` reads WeChat local database/export JSONL or JSON files and maps common local-export fields such as `msgId`, `talker`, `roomName`, `sender`, `message`, `createTime`, and `msgType`.
- `CaptureAdapterFactory.CreateDefaultLiveSources(...)` enables `WeChat.LocalExport` by default and keeps `WeChat.WindowText` disabled unless explicitly enabled.
- `WeChatLocalCommandCaptureAdapter` runs the isolated reader, passes offsets through `WECHAT_DASHBOARD_OFFSET`, and parses structured JSON without exposing database keys to WPF.
- `tools/wechat-local-reader/wechat_local_reader.py` is packaged as `%LOCALAPPDATA%\WechatDashboard\tools\wechat-local-reader\wechat-local-reader.exe`.
- `WeChat.LocalDatabase` is enabled only when both the reader executable and its local `config.json` exist.
- `WindowTextCaptureAdapter` parses visible-window text snapshots.
- `SystemWindowsAutomationReader` reads Windows UI Automation top-level windows and uses Raw View traversal by default.
- `WindowsOcrWindowTextSnapshotProvider` combines UIA text with Windows OCR over the visible WeChat window when UIA exposes only window chrome.
- `CaptureAdapterFactory.CreateDefaultLiveSources(...)` enables `WeChat.WindowText` while preserving JSONL sources for WeChat, Feishu, Shihuatong, and DingTalk.
- WPF "采集一次" uses the live source set, so it runs JSONL import and WeChat visible-window UIA capture together.
- WPF "开始微信监听" / "停止监听" runs the same capture pipeline on a 5-second polling loop.
- WPF capture diagnostics includes a "扫描微信窗口" action that previews UIA window text without persisting messages.
- Default current-user mention aliases are `白驹过隙` and `戴少峰` in `DefaultMentionAliases`.
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
- Do not collect account passwords, session tokens, or unrelated credentials.
- Reading `Weixin.exe` process memory is allowed only after explicit user authorization and only for extracting the current user's local database key.
- Keep key extraction inside the isolated local reader. Never print keys, salts, memory addresses, or chat content to application logs or command summaries.
- Store generated key/config files outside the repository under `%LOCALAPPDATA%\WechatDashboard\tools\wechat-local-reader`, restrict access to the current Windows user, and never commit them.
- Process injection and API hooking are not part of the default design. Prefer read-only memory scanning and database-page validation.
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
%LOCALAPPDATA%\WechatDashboard\capture-inbox\WeChatLocalExport
%LOCALAPPDATA%\WechatDashboard\capture-inbox\Feishu
%LOCALAPPDATA%\WechatDashboard\capture-inbox\Shihuatong
%LOCALAPPDATA%\WechatDashboard\capture-inbox\DingTalk
```

Each `.jsonl` line is one message:

```json
{"id":"wx-1","platform":"WeChat","chatId":"crm","chatName":"CRM项目群","senderName":"王经理","content":"@张三 今天处理线上故障","sentAt":"2026-06-03T10:00:00+08:00","messageType":"Text"}
```

The WeChat local export directory also accepts local-database export shaped records:

```json
{"msgId":"10001","chatId":"room-digital","chatName":"数字石化（二期）","senderName":"国科 王建辉","content":"@戴少峰 请确认域名配置","createTime":"2026-06-05T09:10:00+08:00","msgType":"Text"}
```

## Next Planned Work

Follow `design/2026-06-04-development-plan.md`. Keep design changes in `design/wechat-message-monitor-wpf-design.md`; do not create extra design documents for new方案.

Immediate next step: validate the variable-layout WeChat 4.x memory scanner against Weixin 4.1.10.31, initialize the reader for `D:\cache\xwechat_files\dsfgis_84f8\db_storage`, then verify collection while the chat window is minimized. The fixed `x'<key><salt>'` and fixed-capacity `0x2F` patterns did not match this version. Current OCR capture only reads visible window text and remains a fallback.
