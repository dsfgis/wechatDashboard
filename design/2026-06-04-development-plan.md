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
- Default live capture source registration through `CaptureAdapterFactory.CreateDefaultLiveSources(...)`, preserving local-database/local-export and JSONL sources while keeping `WeChat.WindowText` opt-in for diagnostics.
- WPF "采集一次" runs the configured live capture source set; visible-window UI Automation/OCR is not included in the default live pipeline.
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
- The reader lives under `tools\wechat-local-reader`; local key/config/decrypted outputs live under `tools\result\wechat-local-reader`; the source remains disabled until authorized database keys exist.
- `DefaultMentionAliases` sets the current user's aliases to `白驹过隙` and `戴少峰`.
- WPF shell with refresh, seed sample data, and one-shot capture button.
- Tests covering core rules, SQLite round-trip, JSONL capture, visible-window capture, OCR snapshot fallback, live WeChat source registration, and capture pipeline.
- Project targets .NET 10 to match the installed desktop runtime.

Current handoff update (2026-06-30):

- The local DB Key path has moved past the original read-only memory scanner blocker. A project-local external `wx_key` helper can produce a key file under `tools\result\wechat-local-reader` after explicit user action.
- WPF exposes `自动提取Key`, `初始化本地库`, and a dedicated `微信消息` tab.
- The `微信消息` tab reads today's local database messages with pagination: 50 rows per page, columns `消息内容`, `群名称`, `发消息人`.
- `WeChatLocalReaderService.ReadMessagesAsync` calls the Python reader with day start/end timestamps, offset, and limit.
- `tools/wechat-local-reader/wechat_local_reader.py` supports date-bounded capture, offset/limit paging, UTF-8-safe JSON output, and XML payload summaries.
- XML media payloads are no longer shown raw in the UI; they are converted to user-facing summaries before JSON output.
- Generated key/config/decrypted files are constrained to `tools\result`; `.gitignore` excludes that directory.
- The historical 2026-06-30 baseline was Python reader tests 47 passed, .NET console tests 24 passed, and a successful WPF build to `tools\result\build-check\WechatDashboard.App`. Use the later 2026-08-08 handoff for the current baseline.

Current handoff update (2026-07-03):

- Added full message pagination: `微信消息` and `消息流` tabs both support first/prev/next/last/jump-to-page navigation with configurable page sizes, plus `GetPageWithKnownCountAsync` to skip redundant `COUNT(*)` queries.
- Added configurable user mention aliases: `user_aliases` table, `SqliteUserAliasRepository`, `IUserAliasRepository`, and a `关注@人名` (Follow @Names) tab to add/remove the aliases used by `MentionDetector`.
- Added `@我` highlighting and clickable URL hyperlinks in both `消息流` and `微信消息` tabs via `HighlightTextBlock`, `MessageHighlighter`, and an improved URL regex that excludes CJK/full-width characters and strips trailing punctuation.
- Fixed WeChat link-card messages so their URLs are extracted into clickable hyperlinks instead of showing raw XML.
- Added a followed-chats filter with whitelist/blacklist toggle (checkbox + `app_settings` table + `GetFilterModeAsync`/`SetFilterModeAsync`); `SqliteMessageRepository` now supports `IN`/`NOT IN` chat-name filtering across paged reads and counts.
- Messages now sort by `sent_at DESC` (DB index `idx_messages_sent_at_id`, `ORDER BY` clauses, and the Python reader) so display order matches the shown time column.
- Added a `时间` column to the `待办理` and `微信消息` tabs; renamed `项目` to `群名` in `待办理`.
- Reading WeChat messages now syncs into the main pipeline: `MessageCapturePipeline.ProcessAsync` (extracted for reuse) dedups, persists, classifies, scores urgency, and auto-creates a Todo for every `@我` message, guarded by `_captureSemaphore` to avoid races with the live capture loop; `SourceMessageKey` dedup prevents duplicate Todos on re-reads.
- Added Chinese comments across the codebase to reach the project's 20% comment-rate target, with a `check-comments.ps1` verification script.

Current handoff update (2026-08-09):

- Active delivery baseline is `main` at `7a00f4d` (`fix: persist message processing results atomically`), matching `origin/main` at the time of inspection.
- Persistent Todo completion, completed-page display, original-source message mapping, Chinese source labels, and clearing completed Todos are implemented.
- Shihuatong local encrypted message capture is implemented through the isolated local-database adapter with persisted incremental offsets.
- The WPF shell now uses sidebar navigation and includes Todo, completed Todo, message stream, WeChat messages, chart dashboard, follow settings, capture source settings, and diagnostics pages.
- The chart dashboard supports project, time, and group-name dimensions plus bar, ring, and line presentation; multiple groups can be merged into one followed-project bucket.
- Followed projects support multiple matching keywords, while legacy default project and priority-contact rules are still hardcoded and must be removed by the rule-center milestone.
- Verified on 2026-08-07: solution build succeeded with 0 warnings and 0 errors; all 32 .NET regression tests passed.
- Python reader verification ran 47 tests with 1 failure. The reader intentionally sorts by `(sentAt, id)` descending, but `test_read_messages_aggregates_across_shards` expects the older Alice row at index 0. Treat this as a test-contract mismatch to resolve before implementation work, not as confirmed production ordering corruption.
- No tracked source changes were introduced by the inspection; unrelated untracked workspace files remain out of scope.

Current implementation update (2026-08-09):

- Added `IMessageProcessingUnitOfWork` and `SqliteMessageProcessingUnitOfWork` as the atomic boundary used by `MessageCapturePipeline`.
- Message insertion now uses `ON CONFLICT(source, source_message_key) DO NOTHING RETURNING id`, making the database unique constraint the final deduplication authority instead of a separate `ExistsAsync`/`SaveAsync` race window.
- Message and automatic Todo writes now share one SQLite connection and transaction. Any Todo factory or insert failure rolls back the message, so the same source message can succeed on a later retry.
- Added a SQLite integration regression test with a temporary failing Todo trigger. It verifies rollback, successful retry, source-message linkage, and duplicate suppression.
- Verification: full solution build passed with 0 warnings and 0 errors; all 33 .NET regression tests passed.
- Python reader remains at 46 passed / 1 failed because of the previously documented newest-first test expectation mismatch; the transaction change does not touch Python reader code.
- Classification and urgency are now written to `message_classifications` and `urgency_scores` inside the same transaction, including category, confidence, reason, classifier, score and priority. Classifier-version metadata remains open.
- Todo workbench core is now implemented from `design/wechat-message-monitor-wpf-design.md` section 5.1: arbitrary persisted-message conversion, four due buckets, persisted reminders/snooze history, application-start catch-up, editable Todo detail and source-message context navigation. Windows Toast, quiet hours, per-source controls, explicit custom snooze UI and automated WPF acceptance remain open.
- The implementation uses feature Views/ViewModels, async commands, policies, application services, repositories, a unit of work, an in-app notification Adapter and `TodoFeatureCoordinator`; `MainWindow.xaml.cs` is 489 lines smaller than the baseline for this change.
- Verification on 2026-08-09: after the Visual Studio debug process released the default DLLs, the standard solution build and WPF independent-output build both passed with 0 warnings/0 errors; all 39 .NET regression tests passed.

Known gaps:

- Real Windows UI Automation + OCR snapshot provider is wired into WPF live capture, but still needs validation against the user's actual WeChat desktop window and selected chats.
- UIA/OCR capture is limited to visible text and is no longer the preferred live path; local file/local database capture remains the primary direction.
- WeChat local-database reading exists, but fresh-machine initialization, minimized-WeChat incremental validation, per-stage diagnostics, external-tool provenance, and manual reinitialization still need formal acceptance.
- Windows notification listener is not implemented.
- Feishu and DingTalk currently rely on JSONL directory ingestion rather than native live adapters. Shihuatong has both JSONL and local-database paths.
- Project keyword configuration exists, but default project rules, priority contacts, classification reasons, and urgency reasons are not yet fully configurable or persisted.
- Charted dashboard views exist, but top counters still sample recent messages and can undercount high-volume days; category, priority, Todo-status, SLA, and full-history aggregate queries remain incomplete.
- The Todo workbench now exposes description, due time, priority and five statuses, plus due grouping, persisted reminders and source-message navigation. Explicit custom snooze UI and automated WPF UI acceptance remain open.
- Message search, FTS5, Windows Toast, tray mode, report export, backup/restore, and retention controls are not implemented. Versioned migration infrastructure exists for the Todo reminder migration but needs to be used by later schema changes.
- Message, classification, urgency and automatic Todo persistence are now atomic. Explicit classifier-version metadata and historical backfill remain open.
- Adapter failures are collapsed to debug output inside the pipeline, while parts of the diagnostics UI use refresh time as a stand-in for real last-success time.
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

## Milestone 2.4: Native C# WeChat V4 Database Capture

Purpose: replace the current Python runtime path with a diagnosable native C# pipeline: isolated `wx_key.dll` acquisition, complete database validation/reading, V4 shard indexing, historical bootstrap, and incremental capture. Python remains only as a migration oracle until parity and clean-machine acceptance are complete.

Implementation direction updated after the WeTrace comparison:

- Treat WeTrace as evidence for the data chain, not as a dependency to embed. The reusable part is `db_storage` discovery, full SQLCipher V4 database decryption, `SessionTable` loading, `Msg_<md5(username)>` table matching, and `Name2Id` sender resolution.
- Stop treating the read-only memory scanner as the only path to success. It remains a low-intrusion provider, but failure to find a key should route to `NativeHookKeyProvider` or `ImportedKeyProvider`.
- Add a self-owned x64 `WechatDashboard.KeyProbe.exe`. It loads the authorized `wx_key.dll` through P/Invoke, returns only sanitized status on stdout, and transfers the 32-byte master key through a current-user-only random named pipe.
- Use `NativeHookKeyProvider` as the target WeChat 4.1.x provider because post-4.0.3.39 clients may release dbkey after use. Keep the C# read-only memory scanner as a diagnostic provider and imported keys as a controlled recovery provider.
- Validate any imported or externally extracted DB key against `session/session.db`, `contact/contact.db`, and at least one `message/message_N.db` before saving initialization state.
- Keep `wx_key.dll` outside the WPF process and package it with `WechatDashboard.KeyProbe.exe`. The project owner confirmed on 2026-08-11 that the selected DLL is authorized for external distribution; each release must archive the authorization evidence, source, version, SHA-256, dependency closure, and security scan result.
- Put installed binaries under the application directory and all mutable databases, settings, logs, DPAPI material, snapshots, and decrypted working data under `%LocalAppData%\WechatDashboard`.
- Remove Python, PyInstaller artifacts, `.py` files, PowerShell probing, and plaintext key files from the final Release package only after C# parity is proven.
- Consider the WeChat local database capture incomplete until the same key drives full database decryption, V4 shard indexing, historical bootstrap, one real incremental message, deduplication, and Todo creation.

Reference reviewed on 2026-06-10:

- [afumu/wetrace documentation](https://github.com/afumu/wetrace/tree/main/docs)
- Reuse only architecture and independently verified format knowledge.
- Do not copy the DLL absent from WeTrace. The selected distributable `wx_key.dll` must be traced to the separately authorized source recorded by this project.
- WeTrace is licensed under `CC BY-NC-SA 4.0`, so direct source reuse requires separate license review.

**Planned files:**

- Create: `src/WechatDashboard.Application/Capture/IWeChatDatabaseKeyProvider.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/ReadOnlyMemoryKeyProvider.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/NativeHookKeyProvider.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/ImportedKeyProvider.cs`
- Create: `src/WechatDashboard.KeyProbe/WechatDashboard.KeyProbe.csproj`
- Create: `src/WechatDashboard.KeyProbe/Program.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/WeChatDatabaseSnapshotService.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/WeChatDatabaseKeyDeriver.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/WeChatDatabaseDecryptor.cs` only if direct SQLCipher V4 access fails compatibility fixtures
- Create: `src/WechatDashboard.Infrastructure/Capture/WeChatV4MessageReader.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/WeChatMessageContentDecoder.cs`
- Create: `src/WechatDashboard.Infrastructure/Capture/WeChatCaptureDiagnostics.cs`
- Modify: `src/WechatDashboard.Infrastructure/Capture/WeChatLocalReaderService.cs`
- Modify: `src/WechatDashboard.Infrastructure/Capture/WeChatLocalCommandCaptureAdapter.cs`
- Modify: `src/WechatDashboard.Infrastructure/Capture/ProjectToolPaths.cs`
- Modify: `src/WechatDashboard.App/MainWindow.xaml`
- Modify: `src/WechatDashboard.App/MainWindow.xaml.cs`
- Modify: `tests/WechatDashboard.Tests/Program.cs`
- Retain temporarily: `tools/wechat-local-reader/wechat_local_reader.py` and tests as migration oracle; remove from final Release after Step 15

- [ ] Step 1: Add stage-result contracts for path discovery, key acquisition, key validation, snapshot, decryption, schema inspection, shard indexing, message query, and pipeline persistence.
- [ ] Step 2: Stop collapsing adapter exceptions into `0 messages`; expose a sanitized per-stage error in WPF diagnostics.
- [ ] Step 3: Add `IWeChatDatabaseKeyProvider` with `NativeHookKeyProvider`, `ReadOnlyMemoryKeyProvider`, and `ImportedKeyProvider`.
- [ ] Step 4: Add an imported-key provider for controlled testing with a trusted 64-character hexadecimal DB master key.
- [ ] Step 5: Implement x64 `WechatDashboard.KeyProbe.exe`, P/Invoke the authorized `wx_key.dll`, require explicit per-run user action, and transfer the key over a current-user-only random named pipe without key output in arguments, environment, stdout/stderr, or logs.
- [ ] Step 6: Add offline fixtures proving the DB master key is separately derived against each database salt using PBKDF2-HMAC-SHA512 and page HMAC validation.
- [ ] Step 7: Discover all required databases and copy stable DB/WAL/SHM snapshots. First prove direct read-only SQLCipher V4 access; use managed page decryption only if compatibility fixtures demonstrate it is necessary.
- [ ] Step 8: Implement V4 schema reading directly: `SessionTable`, `Timestamp`/`DBInfo`, `Msg_<md5(username)>`, `Name2Id`, plain/zstd message content, and non-text summaries.
- [ ] Step 9: Add a configurable first-run bootstrap range of 7 days, 30 days, or all history; use per-shard high-water offsets only after bootstrap.
- [ ] Step 10: Add tests where key validation succeeds but a required database, table, shard, or message table is missing, and assert the exact diagnostic stage.
- [ ] Step 11: Validate on Weixin 4.1.10.31 using only counts, schema names, time ranges, and a known test message; do not log real message content.
- [ ] Step 12: Minimize WeChat and verify incremental capture, deduplication, mention detection, Todo creation, and dashboard refresh.
- [ ] Step 13: Dual-run C# and Python on offline fixtures and approved real-machine aggregate checks; compare database counts, message IDs, timestamps, chat/sender mapping, content summaries, ordering, pagination, and offsets.
- [ ] Step 14: Split install-root paths from `%LocalAppData%\WechatDashboard`, produce a self-contained `win-x64` staging bundle, and verify installed operation from paths containing spaces and Chinese characters.
- [ ] Step 15: Validate on a clean Windows x64 machine with no Python and no Python `PATH`; after acceptance, remove Python, PowerShell probing, plaintext key files, and PyInstaller artifacts from Release packaging.
- [ ] Step 16: Archive `wx_key.dll` authorization evidence, source, version, SHA-256, dependency list, security scan, and Authenticode signing results for the exact released binary.

## Milestone 3: Capture Source Settings UI

Purpose: let the user enable, disable, and inspect capture sources without editing code.

**Files:**

- Create: `src/WechatDashboard.Domain/Entities/CaptureSourceSettings.cs`
- Create: `src/WechatDashboard.Infrastructure/Persistence/SqliteCaptureSourceSettingsRepository.cs`
- Modify: `src/WechatDashboard.Infrastructure/Persistence/SqliteDatabaseInitializer.cs`
- Modify: `src/WechatDashboard.App/MainWindow.xaml`
- Modify: `src/WechatDashboard.App/MainWindow.xaml.cs`
- Modify: `tests/WechatDashboard.Tests/Program.cs`

- [x] Step 1: Add tests for saving and loading enabled capture sources.
- [x] Step 2: Add a `capture_source_settings` table.
- [x] Step 3: Add a simple WPF settings view showing source name, adapter kind, path, and enabled state.
- [x] Step 4: Build the capture pipeline from saved settings.

## Milestone 4: Project Rules and Alias Management

Purpose: remove hardcoded user aliases and project rules.

> Progress note (2026-07-03): User-alias management is complete — `user_aliases` table, `SqliteUserAliasRepository` + tests, and the `关注@人名` tab wired into `MentionDetector`. Project rules remain hardcoded in `CreateCapturePipeline`; `SqliteProjectRuleRepository` and the rules UI are still pending.

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

> Progress note (2026-08-08): chart presentation and project/time/group dimensions are implemented, but the analytics service, accurate SQL aggregates, category/priority/Todo/SLA dimensions, and removal of recent-message sampling remain open. Do not mark this milestone complete yet.

## Milestone 6: Reliability Baseline and Truthful Diagnostics

Purpose: remove data-loss windows and make the application's status claims reflect persisted facts.

**Primary files:**

- Modify: `src/WechatDashboard.Application/Capture/MessageCapturePipeline.cs`
- Modify: `src/WechatDashboard.Application/Capture/CaptureRunResult.cs`
- Modify: `src/WechatDashboard.Infrastructure/Persistence/SqliteDatabaseInitializer.cs`
- Create: capture-run repositories and migration infrastructure under `src/WechatDashboard.Infrastructure/Persistence/`
- Modify: `src/WechatDashboard.App/MainWindow.xaml` and `MainWindow.xaml.cs`
- Modify: both .NET and Python regression tests

- [ ] Step 1: Resolve the Python newest-first test contract and restore a fully green baseline.
- [x] Step 2a: Add a database transaction covering unique-key dedup/upsert, message and automatic Todo writes, with rollback/retry/duplicate regression coverage.
- [x] Step 2b: Persist classification and urgency result rows in the same transaction, with field round-trip, rollback and duplicate-suppression coverage.
- [ ] Step 3: Advance an Adapter offset only after the entire batch commits; retain a replayable offset on failure.
- [ ] Step 4: Return structured per-Adapter results including status, duration, counts, error stage and sanitized summary.
- [ ] Step 5: Persist real capture-run history and replace refresh-time diagnostic placeholders.
- [ ] Step 6: Replace recent-100 top counters with accurate aggregate queries and regression-test days containing more than 100 messages.
- [ ] Step 7: Run build, .NET tests, Python tests and `git diff --check` serially.

## Milestone 7: Todo Workbench and Reminder Lifecycle

Purpose: turn generated mentions into a complete personal work-management flow.

**Planned components:**

- Add `TodoApplicationService` and `ReminderApplicationService`; ViewModels do not orchestrate repositories.
- Add Todo list/detail/message-feed ViewModels, async commands and `ShellNavigationService` routes.
- Add `TodoFactory`, `TodoDueBucketPolicy`, `ReminderSchedulePolicy` and `TodoStatusTransitionPolicy`.
- Add versioned migration support, `todo_reminders`, reminder repository and Todo unit of work.
- Add reminder worker and notification publisher behind interfaces; Windows Toast remains an Adapter detail.
- Incrementally extract the affected Todo/message responsibilities from `MainWindow.xaml.cs`; do not perform an all-at-once shell rewrite.

- [x] Step 0: Complete the detailed architecture, persistence, interaction, failure and test design in `design/wechat-message-monitor-wpf-design.md` section 5.1.
- [x] Step 1: Add `schema_migrations` and a transactional migration runner; migrate `todo_reminders` and reminder indexes without requiring a fresh database.
- [ ] Step 2: Introduce `AsyncRelayCommand`, `IDialogService`, the application event bus and `ShellNavigationService`; extract Todo list/detail and message-feed ViewModels without adding new business branches to `MainWindow.xaml.cs`. ViewModels, commands, dialog abstraction and a feature Coordinator are implemented; the event bus and full shell navigation abstraction remain open.
- [x] Step 3: Add failing tests and implement `TodoFactory` plus `TodoApplicationService.CreateFromMessageAsync`; carry the canonical database `MessageId` in every convertible row and make repeated conversion return the existing Todo.
- [ ] Step 4: Implement persisted editing of title, description, project, priority, due time and transitions among Pending, InProgress, Waiting, Done and Ignored; support reopening without altering the source message. Title, description, priority, due time and five-state editing are implemented; project selection UI remains open.
- [ ] Step 5: Implement `TodoDueBucketPolicy` and grouped active-Todo queries for 已逾期、今日到期、后续到期、无截止时间, including midnight, time-zone and resume refresh behavior. The four groups and minute refresh are implemented; explicit resume and system-time-zone change events remain open.
- [ ] Step 6: Implement reminder creation, completion cancellation, due claiming, application-start catch-up, delivery deduplication and snooze presets/custom time. Snooze changes reminder time only, never `DueAt`. Core lifecycle, stale-claim recovery and presets are implemented; explicit custom snooze UI and Windows notification delivery remain open.
- [x] Step 7: Add Todo detail original-message facts and `MessageContextRoute`; load anchor context by ID and `(sent_at, id)`, temporarily bypass list filters, select/scroll/highlight through a WPF Behavior, and expose return-to-list navigation.
- [ ] Step 8: Add per-source/per-chat notification controls and quiet hours after the core reminder lifecycle is stable.
- [ ] Step 9: Run migration/SQLite/unit/ViewModel/UI acceptance tests, then build, .NET tests, Python tests and `git diff --check` serially. Reject the milestone if `MainWindow.xaml.cs` gains repository calls or has net line growth from these features.

Implementation checkpoint (2026-08-09): Steps 1, 3 and 7 are complete. Steps 2, 4, 5 and 6 have their core paths implemented with the remaining boundaries stated inline. The standard solution and WPF independent-output builds both pass with 0 warnings/0 errors, 39 .NET tests pass, and `MainWindow.xaml.cs` has net negative line growth. Step 9 remains open until Python and UI acceptance are green.

## Milestone 8: Search, Rule Center, and Explainability

Purpose: make accumulated messages discoverable and make automated decisions inspectable and correctable.

- [ ] Step 1: Add versioned migration support and create an FTS5 index for content, chat name and sender name.
- [ ] Step 2: Implement full-text search combined with source, chat, sender, project, date, mention, priority and Todo-status filters.
- [ ] Step 3: Reuse Milestone 7's message-to-Todo command and `MessageContextRoute` from FTS/search results; do not build a second conversion or navigation path.
- [ ] Step 4: Replace legacy hardcoded project and priority-contact rules with repository-backed configuration.
- [x] Step 5a: Persist `message_classifications` and `urgency_scores`, including classification reason, classifier, urgency reason and priority.
- [ ] Step 5b: Add explicit classifier/ranker version metadata and a migration path for historical rows.
- [ ] Step 6: Add rule test input, reason preview, user correction, candidate-rule capture and controlled historical recomputation.

## Milestone 9: Reports and Local Data Management

Purpose: convert project activity into useful summaries while preserving local privacy and recoverability.

- [ ] Step 1: Define deterministic project daily/weekly report contracts: new work, completed work, open work, risks, decisions and deadlines.
- [ ] Step 2: Add report preview and local Markdown export, followed by Excel and Word export.
- [ ] Step 3: Keep optional AI summarization disabled by default; require explicit scope preview, consent and redaction before cloud use.
- [ ] Step 4: Add backup, restore, WAL checkpoint, schema-version validation and `PRAGMA integrity_check`.
- [ ] Step 5: Add retention policies and time-range deletion with preview, confirmation and audit records.
- [ ] Step 6: Add optional encrypted backups and document recovery limitations.

## Milestone 10: UI Architecture and Automated Quality

Purpose: reduce the 2,000-line main-window coupling and make future feature work independently testable.

- [ ] Step 1: Introduce application composition/DI without changing capture security boundaries.
- [ ] Step 2: Extract Shell, Todo, Message Feed, Dashboard, Rules, Diagnostics and Settings ViewModels incrementally.
- [ ] Step 3: Move aggregation, filtering and command behavior out of `MainWindow.xaml.cs` into application services.
- [ ] Step 4: Replace or wrap the console test runner with discoverable unit/integration test projects while preserving existing cases.
- [ ] Step 5: Add ViewModel tests, selected WPF UI automation tests and CI for build, .NET tests, Python tests and formatting checks.
- [ ] Step 6: Measure keyset pagination and aggregation performance against a generated 1-million-message database before optimizing further.

## Verification Commands

Run these before committing any implementation change:

```powershell
dotnet build WechatDashboard.sln --no-restore -v minimal
dotnet run --project tests\WechatDashboard.Tests\WechatDashboard.Tests.csproj --no-restore
python -m unittest discover -v -s tools\wechat-local-reader -p test_wechat_local_reader.py
git diff --check
git status --short
```

Expected result:

- Build exits with code 0.
- All .NET and Python tests report passed; a red Python suite is a blocker even if the failure is only an outdated expectation.
- `git diff --check` exits with code 0.
- Build and tests run serially to avoid `CS2012` DLL contention in this repository.

## Immediate Next Work

Current priority was Milestone 6 as of 2026-08-08. The user decision on 2026-08-11 explicitly adds Milestone 2.4 native C# WeChat migration and no-Python installation readiness as the active capture-system priority; unrelated Feishu/DingTalk or chart expansion remains deferred.

Immediate sequence:

1. Correct the Python newest-first test contract and freeze it as the migration oracle.
2. Implement and test isolated x64 `WechatDashboard.KeyProbe.exe` with the authorized `wx_key.dll` and protected named-pipe transfer.
3. Port WeChat V4 key validation, snapshots, SQLCipher/zstd/schema parsing and incremental reads to C# with offline fixture parity.
4. Dual-run C# and Python, complete privacy-safe real-machine validation, then pass the clean-machine no-Python installation test.
5. Add explicit offset failure coverage and persist truthful per-Adapter run diagnostics.
6. Continue the remaining reliability, Todo, search and rule-center milestones after the capture migration slice is green.

Milestones 1, 2, 2.1, 2.2, and 2.3 are implemented at the framework level. Milestone 2.4 is partially implemented through the Python reader and WPF service path: DB Key file import, database initialization, V4 local message reading, date-bounded pagination, UTF-8-safe JSON output, and XML media summary display all exist.

### 当前进度更新（2026-06-30）

Completed since the previous 2026-06-10 snapshot:

- Added project-local path resolution through `ProjectToolPaths` so external tools and generated outputs stay under `tools` and `tools/result`.
- Added `tools\wx-key-tools\run-wx-key-probe.ps1` and WPF `自动提取Key` integration for the local external `wx_key` helper.
- Added `WeChatLocalReaderService` initialization and paged message reading path for WPF.
- Added WPF `微信消息` tab with `读取当天消息`, `上一页`, `下一页`, and page status text.
- Implemented default read of today's messages with 50 rows per page.
- Implemented Python reader capture arguments `--start-timestamp`, `--end-timestamp`, `--offset`, and `--limit`.
- Fixed Windows GBK output failures by forcing/safely escaping Python JSON output.
- Added non-text XML summarization so image/video/emoji/file/link/location messages do not display raw XML in the table.
- Added/updated tests for key-file initialization, reader paging behavior, UTF-8-safe output, local reader service date paging, and XML summaries.

Historical validation baseline on 2026-06-30:

- Python reader: `python -m unittest discover -v -s tools\wechat-local-reader -p test_wechat_local_reader.py` -> 47 tests passed.
- .NET regression runner: `dotnet run --project tests\WechatDashboard.Tests\WechatDashboard.Tests.csproj` -> 24 tests passed.
- WPF build while app is locked: `dotnet build src\WechatDashboard.App\WechatDashboard.App.csproj --no-restore -o tools\result\build-check\WechatDashboard.App` -> succeeded with existing SQLitePCLRaw vulnerability warnings only.
- `git diff --check` -> passed; line-ending warnings may appear.

Still open:

1. Closed by the 2026-08-11 project decision: the selected `wx_key.dll` may be distributed with KeyProbe. The remaining release task is to archive the exact binary's authorization evidence, source URL, version, SHA-256, dependency closure, security review, and signature.
2. Finish user-facing configuration for manually selecting `db_storage`, importing a DB Key file, and clearing/reinitializing local reader state.
3. Unify the dedicated `微信消息` read path with the main pipeline experience where appropriate: persistence, `@我` Todo creation, and dashboard refresh semantics.
4. Improve diagnostics so WPF clearly separates key extraction, key validation, database copy/decrypt, schema query, message query, JSON parse, and UI display errors.
5. Re-run the system test on a fresh worktree without printing real message content.
6. Validate minimized-WeChat incremental capture using a known test message and non-sensitive counts.
7. Keep raw DB Keys, `wx-key-found.txt`, `all_keys.json`, decrypted DBs, and capture JSON out of Git and logs.

Recommended next branch/worktree handoff:

1. Read `Agent.md` first for the current state and safety boundaries.
2. Run the code-level tests from this file.
3. Follow `design/wechat-local-database-system-test.md` for local DB validation.
4. If the UI shows XML again, check `summarize_message_content` in `tools\wechat-local-reader\wechat_local_reader.py` and confirm `query_messages_from_shard` calls it before appending messages.
5. If DB Key extraction fails, do not debug OCR first; inspect the key tool path, `Weixin` PID selection, key file creation, and key/database-directory match.

### 当前进度更新（2026-07-03）

Completed since the 2026-06-30 snapshot:

- Message pagination: first/prev/next/last/jump-to-page navigation for both `微信消息` and `消息流` tabs, with page-size controls and `GetPageWithKnownCountAsync` to skip redundant `COUNT(*)` between page turns (`c653788`, `f4eb402`).
- Configurable user mention aliases (Milestone 4 partial): `user_aliases` table, `SqliteUserAliasRepository` + `IUserAliasRepository`, `TestUserAliasRepositoryAsync`, and the `关注@人名` tab that drives `MentionDetector` at runtime (`feb0d66`).
- `@我` highlighting and clickable links: `HighlightTextBlock` custom control + `MessageHighlighter` render configured aliases in red bold on a light-red background, and URLs as blue clickable hyperlinks in both `消息流` and `微信消息` (`bfa9a9a`).
- WeChat link-card fix: link-card messages now extract the real URL into a clickable hyperlink instead of displaying raw XML; URL regex hardened to exclude CJK/full-width characters and strip trailing punctuation (`db18cfa`).
- Followed-chats filter with blacklist/whitelist toggle: `app_settings` table + `GetFilterModeAsync`/`SetFilterModeAsync`, an `排除列表中的群` checkbox, and `IN`/`NOT IN` chat-name filtering across paged reads and counts (`c3166e4`).
- `sent_at DESC` ordering: DB index `idx_messages_sent_at_id`, `ORDER BY sent_at DESC, id DESC` in `GetRecentAsync`/`ReadPageAsync`, and matching sort in the Python reader so display order matches the shown time column.
- Time/group-name columns: `时间` column added to `待办理` (via `SourceMessageId` -> `SentAt` fallback) and `微信消息`; `项目` renamed to `群名` in `待办理`.
- Read-path pipeline unification: `MessageCapturePipeline.ProcessAsync` extracted for reuse and invoked from `LoadTodayWeChatMessagesAsync` so reading WeChat messages now persists messages, classifies, scores urgency, and auto-creates a Todo for every `@我` message. Guarded by `_captureSemaphore` against the live capture loop; `SourceMessageKey` dedup prevents duplicate Todos on re-reads. This closes prior "Still open" item #3 (`3f0e7b5`).
- Code-comment coverage: Chinese comments added across the codebase to meet the 20% comment-rate target, with `check-comments.ps1` for verification (`7c169ce`).

Milestone status:

- Milestone 3 (Capture Source Settings UI): complete — `capture_source_settings` table, `SqliteCaptureSourceSettingsRepository` + test, WPF source list, and pipeline built from saved settings.
- Milestone 4 (Project Rules and Alias Management): alias half complete; project rules remain hardcoded (`SqliteProjectRuleRepository` and rules UI pending).

Still open (updated):

1. (Unchanged) Decide `tools\wx-key-tools` tooling commit policy.
2. (Unchanged) Finish manual `db_storage` selection, DB Key import, and reinit UI.
3. (Closed by `3f0e7b5`) ~~Unify `微信消息` read path with the pipeline.~~
4. (Unchanged) Improve per-stage WeChat diagnostics.
5. (Unchanged) Re-run system test on a fresh worktree without printing real message content.
6. (Unchanged) Validate minimized-WeChat incremental capture with a known test message.
7. (Unchanged) Keep raw DB Keys / decrypted DBs / capture JSON out of Git and logs.
8. (New) Implement `SqliteProjectRuleRepository` + rules UI so project rules are configurable rather than hardcoded.
9. (New) Validate dedup correctness when the same @me message is seen both via live capture and via the read button within one session.
