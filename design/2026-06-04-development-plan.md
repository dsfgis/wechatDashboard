# WechatDashboard Development Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the current WPF/SQLite MVP into a practical desktop message dashboard with extensible capture sources for WeChat first, then Feishu, Shihuatong, DingTalk, and similar collaboration tools.

**Architecture:** Keep message ingestion behind `IMessageCaptureAdapter`. All adapters emit `CapturedMessage`, and `MessageCapturePipeline` performs deduplication, persistence, `@我` detection, project classification, urgency ranking, Todo creation, and offset updates. WPF consumes repositories and pipeline services rather than embedding platform-specific capture logic.

**Tech Stack:** .NET 9, WPF, SQLite via `Microsoft.Data.Sqlite`, C# records/services, console test runner.

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
- WPF shell with refresh, seed sample data, and one-shot capture button.
- Tests covering core rules, SQLite round-trip, JSONL capture, and capture pipeline.
- GitHub remote `origin` configured and pushed through commit `524687f`.

Known gaps:

- Real WeChat UI Automation capture is not implemented.
- Windows notification listener is not implemented.
- Feishu, Shihuatong, and DingTalk adapters are not implemented.
- Capture sources are still wired directly in WPF instead of being configured through a source registry.
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

- [ ] Step 1: Add tests around text normalization and stable source keys using injected window text snapshots.
- [ ] Step 2: Implement a generic visible-window text adapter behind `IMessageCaptureAdapter`.
- [ ] Step 3: Add a WeChat profile that matches WeChat window titles and emits `Source = "WeChat"`.
- [ ] Step 4: Keep the adapter disabled by default until tested on the real desktop app.

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

Milestone 1 has been completed. The next executable milestone is Milestone 2: add a generic visible-window text adapter and then specialize it for WeChat UI Automation while keeping the adapter disabled by default until validated on the real desktop app.
