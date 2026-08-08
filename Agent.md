# Agent Guide

## Project

WechatDashboard is a Windows WPF desktop application for collecting collaboration messages, detecting `@我`, creating pending Todo items, classifying messages by project, ranking urgency, and showing a local SQLite dashboard. The solution currently targets .NET 10.

## Current Snapshot (2026-08-08)

The active branch is `main` at `b020aa5` (`feat:dashboard-navigation-and-capture-safety`) and matched `origin/main` at the last read-only inspection. Unrelated untracked files exist in the workspace; preserve them and stage only files intentionally changed for the current task.

Current verified state:

- WPF can call the project-local `wx_key` helper through `tools\wx-key-tools\run-wx-key-probe.ps1` to write a DB Key file under `tools\result\wechat-local-reader`.
- WPF can initialize the local reader with a DB Key file and read local WeChat messages through `tools\wechat-local-reader\wechat_local_reader.py`.
- The `微信消息` and `消息流` pages provide first/previous/next/last/jump pagination and configurable page size.
- The reader supports date-bounded reads through `--start-timestamp`, `--end-timestamp`, `--offset`, and `--limit`.
- Non-text WeChat XML payloads are summarized before reaching the UI: `[图片]`, `[视频]`, `[表情]`, `[文件]`, `[链接] 标题 - 描述`, `[位置]`.
- Python JSON output is UTF-8/ASCII-safe to avoid Windows GBK console failures.
- Shihuatong local encrypted messages can be captured through `ShihuatongLocalDatabaseCaptureAdapter`; source identities and incremental offsets are preserved without using visible-window OCR.
- Pending Todos move persistently to the completed page, and both pages display facts from the original source message. Source labels distinguish 微信、石化通、飞书、钉钉 and 示例.
- The sidebar exposes project dashboard, pending/completed Todos, message stream, WeChat messages, follow settings, capture source settings and diagnostics.
- The chart dashboard supports project, time and group-name dimensions with bar, ring and line presentation. Followed projects can contain multiple matching keywords and merge multiple chats into one project bucket.
- Verification on 2026-08-07: full solution build succeeded with 0 warnings and 0 errors; all 32 .NET regression tests passed.
- Python verification ran 47 tests with 1 failing expectation in `test_read_messages_aggregates_across_shards`: implementation sorts newest-first, while the test expects the older Alice row first. Restore a green Python baseline before claiming the next implementation complete.

Sensitive state:

- Never print or commit DB Keys, `wx-key-found.txt`, `all_keys.json`, decrypted databases, capture JSON containing real messages, or files under `tools\result`.
- `tools\result/` is git-ignored. `tools\wx-key-tools/` contains the local external key tool package and must be reviewed before committing or distributing.

## Repository Layout

```text
src/WechatDashboard.Domain/          Domain records and enums
src/WechatDashboard.Application/     Business rules, capture contracts, pipeline services
src/WechatDashboard.Infrastructure/  SQLite repositories and capture adapter implementations
src/WechatDashboard.App/             WPF UI
tests/WechatDashboard.Tests/         Console-based regression test runner
tools/wechat-local-reader/           Python local WeChat database reader and tests
tools/wx-key-tools/                  Local external DB Key helper wrapper and tool files
tools/result/                        Generated local outputs; ignored; may contain secrets
design/                              Canonical design, progress, and system-test documents
```

## Current Architecture

- All normalized ingestion still goes through `IMessageCaptureAdapter`.
- Adapters return `CaptureBatch` with `CapturedMessage` records.
- `MessageCapturePipeline` owns deduplication, persistence, `@我` detection, project classification, urgency ranking, Todo creation, and offset saving.
- WeChat capture priority is now local database/local file first. UIA/OCR visible-window capture remains a fallback and diagnostics path.
- `WeChatLocalExportCaptureAdapter` reads WeChat-like JSONL/JSON export files and maps fields such as `msgId`, `talker`, `roomName`, `sender`, `message`, `createTime`, and `msgType`.
- `WeChatLocalCommandCaptureAdapter` invokes the isolated Python reader, passes offsets through `WECHAT_DASHBOARD_OFFSET`, forces UTF-8 Python IO, parses structured JSON, and does not expose DB Keys to the WPF process logs.
- `WeChatLocalReaderService` manages local reader paths, DB Key extraction command execution, initialization, and paged date-bounded reads for the `微信消息` tab.
- `ProjectToolPaths` resolves project-local tool paths so generated files stay under `tools\result` instead of temporary external directories.
- `tools\wechat-local-reader\wechat_local_reader.py` handles key import, database validation, snapshot/decryption, V4 `SessionTable`/`Name2Id`/`Msg_<md5(username)>` reads, pagination, and non-text XML summaries.
- `tools\wx-key-tools\run-wx-key-probe.ps1` wraps the local `wx_key` tool and writes detected DB Keys to `tools\result\wechat-local-reader\wx-key-found.txt`.
- `WindowTextCaptureAdapter`, `SystemWindowsAutomationReader`, `WindowsOcrWindowTextSnapshotProvider`, and `WindowsScreenOcrReader` remain in the codebase for visible-window diagnostics and fallback capture.
- WPF `采集一次` and `开始微信监听` use the capture pipeline and saved source settings. Reading the dedicated `微信消息` page also passes returned messages through `MessageCapturePipeline.ProcessAsync`, so persistence, `@我` Todo creation and deduplication share the same behavior.
- Default current-user mention aliases are `白驹过隙` and `戴少峰` in `DefaultMentionAliases`.
- SQLite is accessed through repository classes in `Infrastructure/Persistence`.
- WPF should call application/infrastructure services and avoid embedding platform-specific capture code.

Current technical gaps:

- `MessageCapturePipeline.ProcessAsync` does not yet wrap message, classification, urgency and automatic Todo writes in one transaction. A failure after message persistence can cause the next dedup pass to skip a missing Todo.
- Adapter exceptions are currently reduced to debug output inside the pipeline; the diagnostics page does not yet persist true per-Adapter success/failure history.
- Some diagnostic timestamps are refresh timestamps rather than recorded capture timestamps.
- Top counters are calculated from a recent-message sample and can undercount high-volume days.
- Todo fields for description, due time and five statuses exist in the domain, but the WPF UI currently exposes only completion and clearing completed records.
- Message FTS search, reminders, tray mode, report export, versioned migrations, backup/restore and retention controls remain unimplemented.
- `MainWindow.xaml.cs` still combines UI state, capture orchestration, repositories, pagination, rules and analytics; split it incrementally rather than rewriting the application at once.

## Commands

Use these from the repository root:

```powershell
dotnet restore WechatDashboard.sln
dotnet build WechatDashboard.sln --no-restore -v minimal
dotnet run --project tests\WechatDashboard.Tests\WechatDashboard.Tests.csproj --no-restore
python -m unittest discover -v -s tools\wechat-local-reader -p test_wechat_local_reader.py
dotnet run --project src\WechatDashboard.App\WechatDashboard.App.csproj --no-restore
```

If the WPF app is already running and locks `bin\Debug`, build the app into the ignored result directory:

```powershell
dotnet build src\WechatDashboard.App\WechatDashboard.App.csproj --no-restore -o tools\result\build-check\WechatDashboard.App
```

In the Codex sandbox, `dotnet build`, `dotnet run`, Python tests, and Git writes may require escalation because `.git`, `obj`, temp, NuGet, and process-access paths can be restricted.

## Development Rules

- Use TDD for behavior changes: write the failing test, run it, implement, then rerun.
- Keep platform-specific message reading inside adapters or infrastructure services.
- Do not collect account passwords, session tokens, or unrelated credentials.
- Reading `Weixin.exe` process memory or running a DB Key helper is allowed only after explicit user authorization and only for the current user's local database key.
- Keep key extraction isolated from WPF business logic. Never print keys, salts, memory addresses, or chat content to application logs or command summaries.
- Store generated key/config/decrypted files under `tools\result`, keep the directory git-ignored, and never commit generated secrets or decrypted databases.
- WPF must not load third-party hook DLLs in-process. External key helpers must run as separate local processes and remain replaceable.
- Preserve `Source` and `SourceMessageKey`; they are required for cross-source deduplication.
- Add or update tests when changing capture, classification, urgency, Todo generation, SQLite persistence, local reader behavior, or user-visible message formatting.
- Run build and tests before claiming completion.

## Capture Input

The current concrete file adapter is `JsonlDirectoryCaptureAdapter`.

Default WPF capture directory:

```text
tools\result\capture-inbox
```

Default source subdirectories:

```text
tools\result\capture-inbox\WeChat
tools\result\capture-inbox\WeChatLocalExport
tools\result\capture-inbox\Feishu
tools\result\capture-inbox\Shihuatong
tools\result\capture-inbox\DingTalk
```

Each `.jsonl` line is one message:

```json
{"id":"wx-1","platform":"WeChat","chatId":"crm","chatName":"CRM项目群","senderName":"王经理","content":"@张三 今天处理线上故障","sentAt":"2026-06-03T10:00:00+08:00","messageType":"Text"}
```

The WeChat local export directory also accepts local-database export shaped records:

```json
{"msgId":"10001","chatId":"room-digital","chatName":"数字石化（二期）","senderName":"国科 王建辉","content":"@戴少峰 请确认域名配置","createTime":"2026-06-05T09:10:00+08:00","msgType":"Text"}
```

## WeChat Local Database Path

Project-local tool paths:

```text
tools\wechat-local-reader\wechat_local_reader.py
tools\wechat-local-reader\test_wechat_local_reader.py
tools\wx-key-tools\run-wx-key-probe.ps1
tools\wx-key-tools\wx_key-windows-v2.1.8\wx_key.exe
tools\result\wechat-local-reader\config.json
tools\result\wechat-local-reader\wx-key-found.txt
tools\result\wechat-local-reader\all_keys.json
tools\result\wechat-local-reader\decrypted\
```

Only the source reader, tests, and wrapper script are safe to review in normal code review. Files under `tools\result` are generated and may contain secrets or decrypted message databases.

## Next Planned Work

Follow `design/2026-06-04-development-plan.md`. Keep design changes in `design/wechat-message-monitor-wpf-design.md`; avoid creating extra design documents for the same方案 unless the user asks for a separate handoff artifact.

Immediate next work, in priority order:

1. Restore the Python test baseline by making the cross-shard ordering test match the documented newest-first contract, then rerun all verification commands serially.
2. Make dedup/upsert, message, classification, urgency and automatic Todo persistence one transaction; advance offsets only after a successful batch commit.
3. Add structured per-Adapter run results and persist real last-success, last-failure, duration, counts, error stage and sanitized error summary for diagnostics.
4. Replace recent-message-sample top counters with accurate SQL aggregate queries.
5. Build the Todo workbench: manual creation, details, project, priority, due time, description, five states, reopen, source-message navigation, reminders and snooze.
6. Add FTS5 full-text search and combined source/chat/sender/project/date/mention/Todo filters.
7. Unify project keywords, priority contacts, urgency terms and weights in a rule center; persist classification/urgency reasons and support test input and correction.
8. Add deterministic local project daily/weekly reports, followed by backup/restore, integrity checks and retention controls.
9. Incrementally extract ViewModels and application services from `MainWindow.xaml.cs`, with discoverable tests and CI.

Secondary capture work remains valid but follows the reliability baseline: decide the `tools\wx-key-tools` distribution policy, finish manual DB Key/data-directory/reinitialization UI, and complete minimized-WeChat system validation using privacy-safe counts.

Do not implement hidden in-process key extraction in WPF. If self-developed key extraction is revisited, keep it as a separate optional local tool with explicit user action and sanitized outputs.
