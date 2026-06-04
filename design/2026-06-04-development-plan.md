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
- `DefaultMentionAliases` sets the current user's aliases to `白驹过隙` and `戴少峰`.
- WPF shell with refresh, seed sample data, and one-shot capture button.
- Tests covering core rules, SQLite round-trip, JSONL capture, visible-window capture, OCR snapshot fallback, live WeChat source registration, and capture pipeline.
- Project targets .NET 10 to match the installed desktop runtime.

Known gaps:

- Real Windows UI Automation + OCR snapshot provider is wired into WPF live capture, but still needs validation against the user's actual WeChat desktop window and selected chats.
- Current WeChat capture is limited to visible UIA text. It does not read hidden chats, encrypted message databases, or full historical WeChat data.
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
- Modify: `design/message-capture-extension.md`

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
- Modify: `design/message-capture-extension.md`

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
- Modify: `design/message-capture-extension.md`
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
- Modify: `src/WechatDashboard.Infrastructure/Capture/SystemWindowsAutomationReader.cs`
- Modify: `src/WechatDashboard.Infrastructure/Capture/WindowAutomationElement.cs`
- Modify: `src/WechatDashboard.App/MainWindow.xaml`
- Modify: `src/WechatDashboard.App/MainWindow.xaml.cs`
- Modify: `tests/WechatDashboard.Tests/Program.cs`

- [x] Step 1: Add failing tests for real user aliases `白驹过隙` and `戴少峰`.
- [x] Step 2: Add failing tests for an OCR snapshot provider when UIA exposes only `微信 Weixin ...` window chrome.
- [x] Step 3: Preserve native window handles and use UIA Raw View traversal.
- [x] Step 4: Add OCR snapshot provider and screen OCR reader.
- [x] Step 5: Wire WPF capture and diagnostics to UIA + OCR snapshots.
- [x] Step 6: Run tests and full solution build.

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

The next executable milestone is still real-desktop validation: restart the app, open the target WeChat chat window, run "扫描微信窗口", document the observed UIA + OCR text preview, and tune parsing if the OCR line order differs from the currently supported single-line or split-block formats. After validation, continue with Milestone 3 so capture sources can be enabled and disabled from persisted settings instead of code defaults.
