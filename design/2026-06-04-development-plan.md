# WechatDashboard Development Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the current WPF/SQLite MVP into a practical desktop message dashboard with extensible capture sources for WeChat first, then Feishu, Shihuatong, DingTalk, and similar collaboration tools.

**Architecture:** Keep message ingestion behind `IMessageCaptureAdapter`. All adapters emit `CapturedMessage`, and `MessageCapturePipeline` performs deduplication, persistence, `@我` detection, project classification, urgency ranking, Todo creation, and offset updates. WPF consumes repositories and pipeline services rather than embedding platform-specific capture logic.

**Tech Stack:** .NET 10, WPF, SQLite via `Microsoft.Data.Sqlite`, C# records/services, console test runner.

---

## Current State

Completed:

- WPF solution and layered project structure.
- Domain models for messages, Todo items, classifications, urgency scores, and project rules.
- Rule-based `@我` detection, project classification, urgency ranking, and Todo creation.
- SQLite schema initialization and repositories for messages, Todo items, and processing offsets.
- Generic capture contracts: `IMessageCaptureAdapter`, `CaptureContext`, `CaptureBatch`, `CapturedMessage`.
- `MessageCapturePipeline` that processes captured messages end to end.
- `JsonlDirectoryCaptureAdapter` for local JSONL ingestion with offset handling and append-safe reads.
- `CaptureSourceDefinition`, `CaptureSourceKind`, and `CaptureAdapterFactory` for multi-source registration.
- Default JSONL source registration for WeChat, Feishu, Shihuatong, and DingTalk.
- `WindowTextCaptureAdapter` core for parsing injected visible-window text snapshots.
- `WindowsUiAutomationSnapshotProvider` and `SystemWindowsAutomationReader` for reading Windows UI Automation top-level window snapshots.
- WPF capture diagnostics can scan WeChat visible windows and show UIA text previews without persisting messages.
- Disabled WeChat window-text source profile through `CaptureAdapterFactory.CreateWeChatWindowTextSource()`.
- Default live capture source registration through `CaptureAdapterFactory.CreateDefaultLiveSources(...)`, enabling `WeChat.WindowText` while preserving JSONL sources for WeChat, Feishu, Shihuatong, and DingTalk.
- WPF "采集一次" now runs the live capture source set, including WeChat visible-window UI Automation capture.
- WPF "开始微信监听" and "停止监听" run the same capture pipeline on a 5-second polling interval.
- `WindowTextCaptureAdapter` supports both single-line `HH:mm Sender: Content` rows and UIA split blocks such as `HH:mm / Sender / Content`.
- `SystemWindowsAutomationReader` now uses UIA Raw View traversal by default.
- `WindowsOcrWindowTextSnapshotProvider` and `WindowsScreenOcrReader` add a Windows OCR fallback for WeChat windows that expose only window chrome through UIA.
- `WindowOcrCropCalculator` crops WeChat OCR to the right-side chat panel and skips the left navigation/conversation list.
- WeChat capture options ignore this application's own window title, `微信项目消息看板`, so diagnostics and capture do not ingest dashboard text.
- `WeChatLocalExportCaptureAdapter` reads local WeChat database/export JSONL or JSON files from the default live capture pipeline.
- Default live capture now enables `WeChat.LocalExport`; `WeChat.WindowText` remains available as a fallback/diagnostic source instead of the primary live source.
- `WeChatLocalCommandCaptureAdapter` invokes an isolated local reader process, passes the pipeline offset through `WECHAT_DASHBOARD_OFFSET`, parses structured JSON, and keeps external failures out of the WPF process.
- `tools/wechat-local-reader/wechat_local_reader.py` queries every changed session and emits complete structured message rows instead of using a last-message-only session summary.
- The packaged reader executable is installed under `%LOCALAPPDATA%\WechatDashboard\tools\wechat-local-reader`; the source remains disabled until its local config and authorized database keys exist.
- `DefaultMentionAliases` sets the current user's aliases to `白驹过隙` and `戴少峰`.
- WPF shell with refresh, seed sample data, and one-shot capture button.
- Tests covering core rules, SQLite round-trip, JSONL capture, visible-window capture, OCR snapshot fallback, live WeChat source registration, and capture pipeline.
- Project targets .NET 10 to match the installed desktop runtime.

Known gaps:

- Real Windows UI Automation + OCR snapshot provider is wired into WPF live capture, but still needs validation against the user's actual WeChat desktop window and selected chats.
- Current WeChat window capture is limited to visible UIA/OCR text. It does not read hidden chats, encrypted message databases, or full historical WeChat data.
- The next WeChat direction is local file/local database capture, because it can keep working when WeChat is minimized or covered. The canonical design is `design/wechat-message-monitor-wpf-design.md`.
- Windows notification listener is not implemented.
- Feishu, Shihuatong, and DingTalk adapters are not implemented.
- Project rules are hardcoded.
- Statistics are basic counts, not full charted dashboards.
- No installer, tray app, or background service mode yet.

## Milestone 1: Capture Source Registration

Purpose: make multiple message sources first-class without changing the pipeline each time a new software source is added.

**Files:**

- Create: `src/WechatDashboard.Application/Capture/CaptureSourceDefinition.cs`
- Create: `src/WechatDashboard.Application/Capture/CaptureSourceKind.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/CaptureAdapterFactory.cs`
- Modify: `src/WechatDashboard.App/MainWindow.xaml.cs`
- Modify: `tests/WechatDashboard.Tests/Program.cs`
- Modify: `design/wechat-message-monitor-wpf-design.md`

- [x] Step 1: Write failing tests proving the factory creates adapters for WeChat, Feishu, Shihuatong, and DingTalk.
- [x] Step 2: Implement source definitions and a factory that maps `JsonlDirectory` definitions to `JsonlDirectoryCaptureAdapter`.
- [x] Step 3: Wire WPF to build the pipeline from the factory instead of hardcoding one adapter.
- [x] Step 4: Re-run tests and full solution build.

## Milestone 2: WeChat Visible Window Adapter

Purpose: capture user-visible WeChat desktop text without process injection or database decryption.

**Files:**

- Create: `src/WechatDashboard.Infrastructure/Capture/WindowTextCaptureAdapter.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/WindowTextCaptureOptions.cs`
- Modify: `src/WechatDashboard.Infrastructure/WechatDashboard.Infrastructure.csproj` if Windows UI Automation references are required.
- Modify: `tests/WechatDashboard.Tests/Program.cs`
- Modify: `design/wechat-message-monitor-wpf-design.md`

- [x] Step 1: Add tests around text normalization and stable source keys using injected window text snapshots.
- [x] Step 2: Implement a generic visible-window text adapter behind `IMessageCaptureAdapter`.
- [x] Step 3: Add a WeChat profile that matches WeChat window titles and emits `Source = "WeChat"`.
- [x] Step 4: Keep the standalone source profile disabled by default, while enabling it only through the explicit WPF live source set.

Note: this milestone now includes the testable visible-window adapter core, Windows UI Automation snapshot provider, and WPF live capture entry. The remaining work is real-desktop validation and parser tuning using actual WeChat UIA snapshots.

## Milestone 2.1: WeChat Live Capture Wiring

Purpose: make the existing WeChat visible-window adapter usable from WPF without breaking future multi-source expansion.

**Files:**

- Modify: `src/WechatDashboard.Infrastructure/Capture/CaptureAdapterFactory.cs`
- Modify: `src/WechatDashboard.Infrastructure/Capture/WindowTextCaptureAdapter.cs`
- Modify: `src/WechatDashboard.Infrastructure/Capture/SystemWindowsAutomationReader.cs`
- Modify: `src/WechatDashboard.App/MainWindow.xaml`
- Modify: `src/WechatDashboard.App/MainWindow.xaml.cs`
- Modify: `tests/WechatDashboard.Tests/Program.cs`
- Modify: `design/wechat-message-monitor-wpf-design.md`
- Modify: `Agent.md`

- [x] Step 1: Add failing tests proving live source registration includes enabled `WeChat.WindowText`.
- [x] Step 2: Add failing tests proving UIA split message blocks can be parsed and persisted through the pipeline.
- [x] Step 3: Add `CreateDefaultLiveSources(...)` and wire WPF one-shot capture to live sources.
- [x] Step 4: Add WPF start/stop listener buttons using the same capture pipeline on a 5-second polling interval.
- [x] Step 5: Run tests and full solution build.

## Milestone 2.2: WeChat OCR Fallback

Purpose: handle current WeChat desktop windows where UI Automation exposes only window chrome instead of chat message text.

**Files:**

- Create: `src/WechatDashboard.Application/Mentions/DefaultMentionAliases.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/IScreenOcrReader.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/WindowsOcrWindowTextSnapshotProvider.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/WindowsScreenOcrReader.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/WindowOcrCropCalculator.cs`
- Modify: `src/WechatDashboard.Infrastructure/Capture/SystemWindowsAutomationReader.cs`
- Modify: `src/WechatDashboard.Infrastructure/Capture/WindowAutomationElement.cs`
- Modify: `src/WechatDashboard.Infrastructure/Capture/WindowTextCaptureOptions.cs`
- Modify: `src/WechatDashboard.App/MainWindow.xaml`
- Modify: `src/WechatDashboard.App/MainWindow.xaml.cs`
- Modify: `tests/WechatDashboard.Tests/Program.cs`

- [x] Step 1: Add failing tests for real user aliases `白驹过隙` and `戴少峰`.
- [x] Step 2: Add failing tests for an OCR snapshot provider when UIA exposes only `微信 Weixin ...` window chrome.
- [x] Step 3: Preserve native window handles and use UIA Raw View traversal.
- [x] Step 4: Add OCR snapshot provider and screen OCR reader.
- [x] Step 5: Wire WPF capture and diagnostics to UIA + OCR snapshots.
- [x] Step 6: Run tests and full solution build.
- [x] Step 7: Prioritize OCR chat text, crop OCR to the chat panel, and exclude the dashboard window from WeChat capture.

## Milestone 2.3: WeChat Local File Capture Spike

Purpose: move WeChat live collection away from OCR as the primary path and validate local file/local database ingestion behind the existing adapter pipeline.

**Files:**

- Create: `src/WechatDashboard.Infrastructure/Capture/WeChatLocalExportCaptureAdapter.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/WeChatLocalExportOptions.cs`
- Modify: `src/WechatDashboard.Infrastructure/Capture/CaptureAdapterFactory.cs`
- Modify: `src/WechatDashboard.App/MainWindow.xaml`
- Modify: `src/WechatDashboard.App/MainWindow.xaml.cs`
- Modify: `tests/WechatDashboard.Tests/Program.cs`
- Modify: `design/wechat-message-monitor-wpf-design.md`

- [x] Step 1: Add tests for reading WeChat-like local export JSONL into `CapturedMessage`.
- [x] Step 2: Add offset tests proving repeated exports do not duplicate messages.
- [x] Step 3: Add source registration for `WeChat.LocalExport` or `WeChat.LocalDatabase`.
- [x] Step 4: Add diagnostics rows for data directory, external command status, last success, and parsed message count.
- [x] Step 5: Keep `WeChat.WindowText` available as a disabled or fallback source.
- [ ] Step 6: With explicit user authorization, initialize the encrypted database reader and validate with real desktop WeChat while the chat window is minimized.

## Milestone 2.4: WeChat V4 Database Capture Rework

Purpose: replace the current single-path memory scan and five-minute query with a diagnosable pipeline inspired by the verified WeTrace data flow: pluggable key acquisition, complete database decryption, V4 shard indexing, historical bootstrap, and incremental capture.

Implementation direction updated after the WeTrace comparison:

- Treat WeTrace as evidence for the data chain, not as a dependency to embed. The reusable part is `db_storage` discovery, full SQLCipher V4 database decryption, `SessionTable` loading, `Msg_<md5(username)>` table matching, and `Name2Id` sender resolution.
- Stop treating the read-only memory scanner as the only path to success. It remains a low-intrusion provider, but failure to find a key should route to `ImportedKeyProvider` or a user-configured `ExternalHookKeyProvider`.
- Add an explicit external key command contract: stdout may return JSON with `ok`, `provider`, `version`, `wechat_version`, and `db_key`, a plain-text 64-hex DB key, or a configured key file such as `dbkey.txt`. The reader must extract only the key and must not log raw stdout/stderr.
- Prefer adapting `gzygood/DbkeyHook`'s `DbkeyHookCMD.exe -pid {pid}` style command for Windows WeChat 4.1.x, because its documented approach targets the post-4.0.3.39 behavior where dbkey is released after use and old memory search patterns stop working. Keep `ylytdeng/wechat-decrypt` as a reference for SQLCipher4 and export behavior, not as the first replacement for key acquisition.
- Validate any imported or externally extracted DB key against `session/session.db`, `contact/contact.db`, and at least one `message/message_N.db` before saving initialization state.
- Keep third-party Hook tools outside the WPF process and outside this repository. The app can invoke a user-provided command after explicit authorization, but it must not bundle unreviewed DLLs or copy WeTrace's absent `wx_key.dll`.
- Consider the WeChat local database capture incomplete until the same key drives full database decryption, V4 shard indexing, historical bootstrap, one real incremental message, deduplication, and Todo creation.

Reference reviewed on 2026-06-10:

- [afumu/wetrace documentation](https://github.com/afumu/wetrace/tree/main/docs)
- Reuse only architecture and independently verified format knowledge.
- Do not copy or redistribute its `wx_key.dll`; the DLL is excluded from the source repository.
- WeTrace is licensed under `CC BY-NC-SA 4.0`, so direct source reuse requires separate license review.

**Planned files:**

- Create: `src/WechatDashboard.Application/Capture/IWeChatDatabaseKeyProvider.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/ReadOnlyMemoryKeyProvider.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/ExternalHookKeyProvider.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/ImportedKeyProvider.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/WeChatDatabaseSnapshotService.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/WeChatDatabaseDecryptor.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/WeChatV4MessageReader.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/WeChatCaptureDiagnostics.cs`
- Modify: `tools/wechat-local-reader/wechat_local_reader.py`
- Modify: `src/WechatDashboard.Infrastructure/Capture/WeChatLocalReaderService.cs`
- Modify: `src/WechatDashboard.Infrastructure/Capture/WeChatLocalCommandCaptureAdapter.cs`
- Modify: `src/WechatDashboard.App/MainWindow.xaml`
- Modify: `src/WechatDashboard.App/MainWindow.xaml.cs`
- Modify: `tests/WechatDashboard.Tests/Program.cs`

- [ ] Step 1: Add stage-result contracts for path discovery, key acquisition, key validation, snapshot, decryption, schema inspection, shard indexing, message query, and pipeline persistence.
- [ ] Step 2: Stop collapsing adapter exceptions into `0 messages`; expose a sanitized per-stage error in WPF diagnostics.
- [ ] Step 3: Add `IWeChatDatabaseKeyProvider` and preserve the current read-only scanner as one provider instead of the only initialization path.
- [ ] Step 4: Add an imported-key provider for controlled testing with a trusted 64-character hexadecimal DB master key.
- [ ] Step 5: Define an external Hook provider protocol using a separate process, explicit per-run authorization, tool version and SHA-256 reporting, and no key output in logs.
- [ ] Step 6: Add offline fixtures proving the DB master key is separately derived against each database salt using PBKDF2-HMAC-SHA512 and page HMAC validation.
- [ ] Step 7: Discover all required databases, copy stable snapshots, decrypt only changed files, preserve `session/`, `contact/`, and `message/` paths, and atomically publish verified SQLite files.
- [ ] Step 8: Implement V4 schema reading directly: `SessionTable`, `Timestamp`/`DBInfo`, `Msg_<md5(username)>`, `Name2Id`, plain/zstd message content, and non-text summaries.
- [ ] Step 9: Add a configurable first-run bootstrap range of 7 days, 30 days, or all history; use per-shard high-water offsets only after bootstrap.
- [ ] Step 10: Add tests where key validation succeeds but a required database, table, shard, or message table is missing, and assert the exact diagnostic stage.
- [ ] Step 11: Validate on Weixin 4.1.10.31 using only counts, schema names, time ranges, and a known test message; do not log real message content.
- [ ] Step 12: Minimize WeChat and verify incremental capture, deduplication, mention detection, Todo creation, and dashboard refresh.

## Milestone 3: Capture Source Settings UI

Purpose: let the user enable, disable, and inspect capture sources without editing code.

**Files:**

- Create: `src/WechatDashboard.Domain/Entities/CaptureSourceSettings.cs`
- Create: `src/WechatDashboard.Infrastructure/Persistence/SqliteCaptureSourceSettingsRepository.cs`
- Modify: `src/WechatDashboard.Infrastructure/Persistence/SqliteDatabaseInitializer.cs`
- Modify: `src/WechatDashboard.App/MainWindow.xaml`
- Modify: `src/WechatDashboard.App/MainWindow.xaml.cs`
- Modify: `tests/WechatDashboard.Tests/Program.cs`

- [ ] Step 1: Add tests for saving and loading enabled capture sources.
- [ ] Step 2: Add a `capture_source_settings` table.
- [ ] Step 3: Add a simple WPF settings view showing source name, adapter kind, path, and enabled state.
- [ ] Step 4: Build the capture pipeline from saved settings.

## Milestone 4: Project Rules and Alias Management

Purpose: remove hardcoded user aliases and project rules.

**Files:**

- Create: `src/WechatDashboard.Infrastructure/Persistence/SqliteProjectRuleRepository.cs`
- Create: `src/WechatDashboard.Infrastructure/Persistence/SqliteUserAliasRepository.cs`
- Modify: `src/WechatDashboard.Infrastructure/Persistence/SqliteDatabaseInitializer.cs`
- Modify: `src/WechatDashboard.App/MainWindow.xaml`
- Modify: `src/WechatDashboard.App/MainWindow.xaml.cs`
- Modify: `tests/WechatDashboard.Tests/Program.cs`

- [ ] Step 1: Add repository tests for user aliases and project rules.
- [ ] Step 2: Store aliases in `user_aliases` and rules in `project_rules`.
- [ ] Step 3: Load aliases and rules into `MentionDetector` and `ProjectClassifier`.
- [ ] Step 4: Add WPF controls to add and remove aliases and rules.

## Milestone 5: Dashboard Expansion

Purpose: move from simple counters to usable multi-dimensional views.

**Files:**

- Create: `src/WechatDashboard.Application/Analytics/DashboardSummaryService.cs`
- Create: `src/WechatDashboard.Application/Analytics/DashboardSummary.cs`
- Modify: `src/WechatDashboard.Infrastructure/Persistence/SqliteMessageRepository.cs`
- Modify: `src/WechatDashboard.Infrastructure/Persistence/SqliteTodoRepository.cs`
- Modify: `src/WechatDashboard.App/MainWindow.xaml`
- Modify: `tests/WechatDashboard.Tests/Program.cs`

- [ ] Step 1: Add tests for project, time, category, priority, and Todo status aggregations.
- [ ] Step 2: Implement dashboard summary queries or application aggregation.
- [ ] Step 3: Replace hardcoded WPF project summary logic with the analytics service.
- [ ] Step 4: Add charts after the data contract is stable.

## Verification Commands

Run these before committing any implementation change:

```powershell
dotnet build WechatDashboard.sln --no-restore -v minimal
dotnet run --project tests\WechatDashboard.Tests\WechatDashboard.Tests.csproj --no-restore
git status --short
```

Expected result:

- Build exits with code 0.
- Tests report all tests passed.
- The existing `MSB3101` warning about `obj` cache write permissions may appear in this environment; it is not a compile error.

## Immediate Next Work

Milestones 1, 2, 2.1, and 2.2 have been completed at the framework level. The app now runs WeChat visible-window UIA + OCR capture from "采集一次" and supports 5-second polling from "开始微信监听".

Milestone 2.3 is implemented through the real-reader bridge. The app has located WeChat 4.1.10.31 data under `D:\cache\xwechat_files\dsfgis_84f8\db_storage`, and the packaged reader is installed. The current reader still does not produce real chat messages, so Milestone 2.4 now takes priority over further UI work.

### 当前进度更新（2026-06-10）

- 已完成本地导出文件适配器、外部命令适配器、WPF 采集诊断和本地读取器打包基础。
- 已定位微信 `4.1.10.31` 数据目录：`D:\cache\xwechat_files\dsfgis_84f8\db_storage`。
- 已获得用户明确授权，可只读扫描本机 `Weixin.exe` 进程内存提取本人数据库密钥。
- 已排除旧版固定十六进制文本模式、固定 `0x2F` 指针结构和无包装十六进制候选。
- 已实现可变容量指针结构、直接页密钥验证、PBKDF2 原始口令验证和敏感输出抑制，离线单元测试通过。
- 已调研 WeTrace 的完整链路：Hook 获取 DB 主密钥、按数据库 salt 派生、全库解密、`SessionTable` 会话读取、`Msg_<md5(username)>` 消息表和 `Name2Id` 发送人映射。
- 已确认当前方案存在三个结构性问题：密钥获取只有一个 Provider、首次采集固定回看五分钟、采集异常会被折叠成“0 条消息”。
- 真实初始化尚未成功，`WeChat.LocalDatabase` 仍应保持未启用状态。

下一阶段按以下顺序执行：

1. 先实现阶段化诊断，使路径、密钥、解密、schema、分片、查询和 pipeline 错误可以区分。
2. 抽象 `IWeChatDatabaseKeyProvider`，将现有只读扫描降为一个可替换 Provider。
3. 增加受控的导入密钥 Provider，用离线 fixture 验证多数据库 salt 派生和全库解密。
4. 定义隔离的外部 Hook Provider 协议；在来源、哈希、许可证和安全审查完成前不分发第三方 DLL。
5. 自主实现 V4 会话和消息读取，不继续依赖 `wechat_cli` 的内部私有函数。
6. 首次初始化提供 7 天、30 天或全部历史导入，之后再启用按分片高水位增量。
7. 使用真实微信只验证非敏感计数和一条已知测试消息，再验证最小化窗口采集、去重、`@白驹过隙`/`@戴少峰` Todo 和看板刷新。
8. 完成回归构建、测试、许可证检查和隐私清理后，再标记本地数据库采集为完成。
