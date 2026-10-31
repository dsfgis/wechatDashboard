# Agent Guide

## Todo Detail Title and SinopecMail IMAP Capture (2026-10-30)

Two changes landed together; both are contracts other code depends on.

**Todo detail title.** `TodoDetailWindow` no longer shows the literal 待办详情. The window `Title` and the header `TextBlock` bind to `TodoDetailViewModel.Title` (`FallbackValue=待办详情`, 330 DIP max width + `CharacterEllipsis`). `TodoFactory.BuildTitle` now produces a one-line title: collapse whitespace, cut at the first sentence terminator (`。；！？;!?`), then clamp to `TodoFactory.TitleMaxLength = 30` with `…`. **Leading `@mentions` are intentionally preserved** — `TestCapturePipelineAsync` asserts `Title.Contains("@张三")`, and dropping the mention broke it; do not "optimize" that away again. `TodoFactory.ResolveTitle` prefers an explicit title, then the cleaned mail subject for mail sources, then `BuildTitle(content)`.

**SinopecMail capture.** A standard IMAP4rev1 path, structurally different from the WeChat/Shihuatong adapters because it needs no client internals.

- Protocol facts measured on this machine (2026-10-30): **the account lives on `amail.sinopec.com:993`**, per the company mail client's own account settings — `imap.sinopec.com` is a *different* instance and the account domain is `@pcitc.com`, not `@sinopec.com`. Both hosts speak IMAP4rev1 (`eYou MUA v8.18.2` banner), refuse 143, and advertise only `AUTH=PLAIN` (no `AUTH=LOGIN`). `smtp.` / `owa.` / `exchange.sinopec.com` do not resolve, so there is no EWS path.
- `CaptureSourceKind.SinopecMail` + `CaptureAdapterFactory.CreateSinopecMailSource()` (registered in `CreateDefaultLiveSources`, **disabled by default**). `MailSourcePolicy` (`Application/Common`) owns the source id, subject cleaning and `IsMailSource`.
- `IMailInboxReader` / `MailFetchRequest` / `MailEnvelope` / `MailFetchResult` (`Application/Capture/MailInboxContracts.cs`) keep protocol out of the pipeline, so the adapter is testable without a server. `ImapMailInboxReader` is the MailKit 4.14.1 implementation; `HtmlTextConverter` is the hand-rolled HTML→text fallback.
- Cursor is `uidValidity:nextUid` (`MailCursor`). UIDVALIDITY change = rebuild, read only the most recent batch. Reads are always ascending by UID so a backlog is drained rather than skipped.
- Folder is opened `FolderAccess.ReadOnly`; nothing is ever moved, flagged or expunged. Attachments are counted from `BodyParts`, never downloaded.
- `ProtectedImapCredentialStore` = DPAPI `CurrentUser` + entropy `WechatDashboard.ImapCredential.v1`, magic `SMCP`, plaintext `account\npassword`. **The password must never reach the database, logs, exception messages or source files.** The account is echoed in the settings UI; the password never is.
- Pipeline: `needsAgentDecision = isMentionMe || isMailSource`. `shouldCreateTodo` splits by source — chat keeps fail-open (`isMentionMe && (analysis is null || analysis.ShouldCreate)`), mail is **fail-closed** (`analysis?.ShouldCreate == true`). This asymmetry is deliberate: a mailbox volume of unconditionally created Todos is worse than missing some.

**Local cache path added (2026-10-30, user-chosen).** The supplied IMAP password is rejected, so capture was redirected to the client's own on-disk cache, which needs no credential.
- The 石化邮 client is **Thunderbird-based**, not CEF: profile lives in `Documents\sinopec\<profile>\profile`, account prefs in `prefs.js` (`amail.sinopec.com` / `imap` / 993 / `SSL` / `shaofeng.dai@pcitc.com`, no `authMethod` override), mailbox at `ImapMail\amail.sinopec.com\INBOX` (2.2 GB mbox).
- `CaptureSourceKind.SinopecMailArchive` + `CaptureAdapterFactory.CreateSinopecMailArchiveSource()` (enabled only when the INBOX is found), located by `SinopecMailArchiveLocator`.
- `MboxMailArchiveReader` parses with `MimeParser(stream, MimeFormat.Mbox)`; `MboxCursor` is `v1:<byteOffset>`. **First run anchors the cursor at the current file length — it deliberately does not backfill history (user decision).** A cursor beyond the file length means the client compacted the mailbox: reset to 0 and rely on `Message-ID` dedup.
- A message without `Message-ID` makes the reader rewind the cursor to that message's start instead of advancing — this keeps a half-written tail from being lost or double-counted.
- `MboxMailCaptureAdapter.AdapterKey = "SinopecMail.LocalCache"` (offset key) while `CapturedMessage.Source = "SinopecMail"` (显示与邮件语义). Both mail paths share `MailMessageMapper`, so titles/keys/bodies cannot drift.
- Source display mapping `SinopecMail → 石化邮` was added in `MessageFeedViewModel.FormatSource`, `TodoWorkbenchViewModel.FormatSource` and `MainWindow` (uppercase switch). Missing it would surface the raw English id in the UI.

Open item at this checkpoint: login was rejected (`Invalid login or password`) on both hosts, for `shaofeng.dai`, `shaofeng.dai@sinopec.com`, `SHAOFENG.DAI` and `shaofeng.dai@pcitc.com`, via both `LOGIN` and `AUTHENTICATE PLAIN` (with and without authzid). The password transits intact (10 chars, ASCII, trailing `$`, no stray whitespace), so this is a server-side rejection, not an encoding bug. Do not keep retrying — the failure count is already high enough to risk a lockout. Next step is user-side: confirm the password via the mail client's 检测服务 button or the webmail, and confirm IMAP is enabled for the account. The code path is complete and unit-tested; only a valid credential (or an IMAP permission enabled on the server) is missing.

Both previously "pre-existing" failures are now fixed (2026-10-30); the full runner is **97 PASS / 0 FAIL**.

1. `Native WeChat reader matches the frozen Python oracle field by field` was **never a WeChat reading defect**. That test builds its own fixture SQLCipher database (`CreateFixtureDatabaseAsync`) and inserts a zstd-compressed body whose plaintext is the literal `压缩消息`; it then compares C# against a live Python run. This machine's Python had **no `zstandard` package**, so the oracle decoded the compressed row to an empty string while ZstdSharp decoded it to `压缩消息` — hence `Expected '', got '压缩消息'`. Installing `zstandard` (0.25.0) makes the test pass. Real user chat data is not involved at all; the assertion order (count/offset/id/chat/sender first, content third) is why it looked like a content mismatch.
2. `Capture pipeline applies immediate Agent priorities and summaries with safe fallback` was genuinely wall-clock dependent, and the earlier note blaming 17:30 was incomplete — there are **two** windows. `offReminders` fails after **23:00** (`CreatedAt + 1h` crosses the day-end 23:59 due time, and `TodoService.Schedulable` requires `candidate < dueAt`), and `onReminders` fails after **17:30** (the Agent reminder is hardcoded to 17:30 and must satisfy `candidate > now`). Fixed properly rather than by weakening assertions: `MessageCapturePipeline` now takes an optional `Func<DateTimeOffset>? clock` (default `() => DateTimeOffset.Now`) used for the Todo creation instant, and the test injects `sentAt.Date.AddHours(10)`. Do not revert this to `DateTimeOffset.Now` in that pipeline path.

## Mail Capture Verified Against Real Mailbox (2026-10-31)

The local-cache path is now proven end to end on real data, independent of the blocked IMAP credential.

- `TestRealClientInboxAsync` (CHECK-077) reads the **actual** 2.22 GB `ImapMail\amail.sinopec.com\INBOX`, seeks the last `"\nFrom "` marker inside a 512 KB tail window, parses with `MboxMailArchiveReader`, and asserts the mapping chain: `Message-ID` → `SourceMessageKey`, cleaned subject → `ChatName`, non-empty body. It skips cleanly when the client is not installed, so CI/other machines stay green. Real sample that passes: subject `回复: 协助开通防火墙策略-FHQS2026103000013` → title `协助开通防火墙策略-FHQS2026103000013`.
- First live run (app restarted 2026-10-31 09:53) wrote `processing_offsets` row `SinopecMail.LocalCache = v1:2378803586`, which is **byte-for-byte the INBOX length** — confirming the anchor-at-end (no history backfill) semantics actually hold in the shipped app, not just in tests. `capture_source_settings` stays empty because `LoadCaptureSourceSettingsAsync` falls back to `source.IsEnabled` for unsaved rows, and the archive source is enabled whenever the INBOX is found.
- Consequence to remember: nothing appears until the 石化邮 client syncs **new** mail into the mbox. If the user wants recent mail immediately, move the offset backwards (delete the row or point it at an earlier `From ` marker); do not change the adapter.
- Fixed a genuine concurrency defect surfaced as a flaky `Message feed keeps the source context when a page refresh races with navigation`: two overlapping `ShowContextAsync` calls (tab auto-refresh + explicit navigation) both mutated `Items` and one enumerated it while the other was mid-`Clear/Add` → `Collection was modified`. `ReplaceItemsAsync` now returns the materialized rows, swaps `Items` inside `_itemsGate`, and `ShowContextAsync` selects from the returned rows instead of re-enumerating the shared collection. Verified with **8 consecutive full runs, 97 PASS each**.
- The 石化邮 client must be running for the mbox to advance (it is the sync agent). A stale `bin\Debug` build also silently disables the whole feature: a running `WechatDashboard.App` locks the DLLs, so `dotnet build` fails with MSB3027 and the old process keeps executing pre-mail code. Always stop the app before rebuilding.
- IMAP was retried once more with the full address `shaofeng.dai@pcitc.com` + `Dsf980728$` on `amail.sinopec.com:993`: still `a2 NO Invalid login or password` (11th attempt, no lockout). Do **not** retry again from code or scripts. The client's stored credential differs from the supplied password; only the user can resolve this.
- Backfill procedure (used for the last 5 mails, now documented in README): stop the app → back up the db → set `processing_offsets.offset_value` for `SinopecMail.LocalCache` to `v1:<byte offset of a 'From ' separator>` → restart. Dedupe is by `Message-ID`, so a rewound cursor never double-inserts.
- **A killed process leaves a legitimate partial state, not a bug.** Each mail commits in its own transaction, but the offset advances only after the whole batch succeeds. Process death mid-batch therefore shows "3 of 5 mails present, cursor unchanged"; the next run dedupes the 3 and inserts the rest. Confirmed by running `MessageCapturePipeline` against a **copy** of the real db (captured=5, persisted=2, dup=3, todos=1, status Succeeded, cursor advanced to file end).
- Diagnosing capture failures: `SanitizedError` lives only in `CaptureSourceRunResult` and `Debug.WriteLine` output goes to the debugger, so there is no on-disk trace. Reproduce by assembling the pipeline against a db copy (`SqliteMessageProcessingUnitOfWork` + real `OpenAiCompatibleMessageActionModel` from `ProtectedDeepSeekApiKeyStore` + `MboxMailCaptureAdapter`) and printing `result.Sources`. A throwaway console project under `tools/` works and must be deleted afterwards.

## Project

WechatDashboard is a Windows WPF desktop application for collecting collaboration messages, detecting `@我` and `@所有人`, creating pending Todo items, classifying messages by project, ranking urgency, and showing a local SQLite dashboard. The solution currently targets .NET 10.

## Configurable Priority Levels and Colors (2026-10-30)

Requested feature: users must be able to choose how many priority levels exist (not just the fixed P0–P3) and pick a color per level.

Contract now implemented:

- `PriorityPalette` (`Application/Common/PriorityPalette.cs`) is an immutable palette: an ordered color array where **the array length is the level count** and the index is the level (0 → `P0`). `PriorityLevel` gained `P4`/`P5`, so the enum covers 2–6 configured levels without a schema change; `MinLevelCount = 2`, `MaxLevelCount = 6`, defaults are the historical four colors `#D92D20 / #B54708 / #3B5998 / #475467`.
- `PrioritySettings` (same file) is the single process-wide holder (`Current`, `Apply`, `ResetToDefault`, `Changed`). `PriorityColorPolicy.For` now delegates to it, so the message feed, Todo cards, completed list, calendar cards and the month-cell preview all read one palette; `P0Color`…`P3Color` remain the defaults used by tests.
- **Lowering the level count never mutates stored data.** `PriorityPalette.Normalize` clamps an out-of-range level to the lowest configured one for display, grouping, dropdowns and color lookup; the SQLite `priority` text (e.g. `P3`) is untouched, so raising the count again restores the original level. `TodoDetailViewModel` keeps `_originalPriority` and writes it back when the user saves other fields without touching the priority dropdown, so a settings change cannot silently downgrade a Todo.
- Persistence reuses `app_settings`: `IPrioritySettingsRepository` / `SqlitePrioritySettingsRepository` store the color array as JSON under `priority_level_colors`. Missing, corrupt, out-of-range or non-hex values fall back to the default four-level palette instead of throwing.
- `SqliteTodoRepository` priority sorting now maps `P0`–`P5` explicitly (both `GetPendingAsync` and `GetActiveAsync`); `UrgencyRanker.ForManualAgentAnalysis` scores `P4 = 12` / `P5 = 6` below `P3 = 20` to keep scores monotonic if an Agent ever returns a deeper level.
- UI: new 优先级设置 tab (main-window tab index 10, sidebar tag `10`, appended last so existing tab indices stay stable). It offers 层数 2–6, one row per level (swatch, code, 12-color preset dropdown, `#RRGGBB` text box, live preview), 保存设置 and 恢复默认（4 级）, plus a status line. `PrioritySettingsViewModel` lives in `App/ViewModels/Settings`; saving applies the palette and calls back into `MainWindow.OnPrioritySettingsAppliedAsync`, which refreshes workbench, message feed, desktop calendar and dashboard immediately.
- `TodoWorkbenchViewModel` replaced the four hard-coded `Priority0…Priority3` collections with `ObservableCollection<PriorityGroupViewModel> PriorityGroups`; `TodoPendingView.xaml` renders them with an `ItemsControl` (per-level 全选 passes `Items`). Group expansion state is remembered per level in a `HashSet` so the 1-minute auto refresh does not re-expand collapsed groups.
- `TodoCreateViewModel`/`TodoDetailViewModel` expose `PriorityOptions` (`PriorityOption`: level + code + color) instead of `Enum.GetValues<PriorityLevel>()`, and both ComboBoxes render a color dot; the create default is `Normalize(P2)` so a two-level setup still preselects a valid level.
- `HighPriorityTodoCount` (and the P0/P1 label next to it) now means “the top two configured levels” via `PriorityPalette.IsHighPriority`.

Verification at this checkpoint (2026-10-30):

- `dotnet build src/WechatDashboard.App/WechatDashboard.App.csproj -o .buildcheck/app-priority` → 0 errors (NU1900 warnings are network-only).
- `dotnet build tests/WechatDashboard.Tests -o .buildcheck/tests-priority2` → 0 errors. Full runner: **88 PASS**, only the pre-existing `Native WeChat reader matches the frozen Python oracle field by field` failure remains.
- New `TestPrioritySettingsAsync` covers palette validation/normalization, SQLite round-trip, corrupt-JSON fallback, the settings view model (add level → custom hex → save → global palette + one refresh), invalid hex retention and 恢复默认.
- `TestTodoWorkbenchBucketSummaryAsync` additionally applies a two-level palette: groups drop to 2, P1 group grows to 4 (P1+P2+P3 folded), the lowest group uses its configured color and a folded row displays `P1`.

## Desktop Calendar Must Not React to Show Desktop (2026-10-30)

Requested behavior change: an embedded desktop calendar must not flash when the user presses Win+D or clicks the taskbar 显示桌面 button.

Root cause measured on a real desktop (1 ms Z-order sampling, `tests/.../DesktopCalendarNativeAssertions.cs` helpers): 显示桌面 does not minimize the window. It raises the desktop icon layer (Progman) above every top-level window, so the overlay-mode calendar is covered until something puts it back. The previous guard was a 300 ms `DispatcherTimer`, which left the calendar covered for **up to 292 ms** — that gap is the flicker.

Fix in `Services/DesktopCalendarDesktopHost.cs`:

- The polling guard became an event-driven watch. While embedded in overlay mode the host installs two out-of-context WinEvent hooks: `EVENT_OBJECT_REORDER` (0x8004, delivered for the desktop window `#32769` when the shell reorders top level windows) and `EVENT_SYSTEM_FOREGROUND` (0x0003, delivered when Progman becomes foreground). Both carry `WINEVENT_SKIPOWNPROCESS`, so the host never reacts to its own `SetWindowPos`.
- `RestoreOverlayAboveDesktop` runs directly inside the callback (same UI thread, one `SetWindowPos`), instead of waiting for a timer tick. A re-entrancy flag (`_isRestoringDesktopOrder`) prevents the `WM_WINDOWPOSCHANGING` handler from overwriting the `InsertAfter` the restore just chose.
- The timer survives only as a fallback: 1 s interval when at least one hook installed, 300 ms when both hooks failed (remote sessions, restricted shells) so the old behavior is preserved rather than lost.
- `DesktopCalendarDesktopHost` is now `IDisposable`; `DesktopCalendarWindow.Closed` disposes it so the WinEvent hooks cannot outlive the window when Explorer restarts and destroys the calendar window.

Rejected alternative: parenting the calendar into the raised desktop (child mode). Forcing that path on this machine (`parent=Progman`, `WS_CHILD`) makes the window contribute no pixels — the WPF surface is not composited under a no-redirection Progman — so overlay mode plus an immediate Z-order restore remains the only workable approach.

Verification at this checkpoint (2026-10-30):

- Timeline probe before the fix: icon layer above the calendar for 292 ms after Win+D (window never minimized, `IsIconic=false`).
- After the fix, same probe with 61 255 samples over 4 s: the icon layer was above the calendar in 2 samples only, longest continuous coverage **3 ms**; the in-test measurement reports **9 ms**.
- `TestDesktopCalendarShowDesktopSurvivalAsync` (renamed to *Embedded desktop calendar survives Win+D without a visible flicker*) now samples `HasIconLayerAbove` continuously while injecting a real Win+D and fails when the longest coverage reaches `FlickerBudgetMs = 60` (one frame is ~16 ms, so 60 ms tolerates GC/scheduling but still fails a return to polling). It keeps the previous assertions: not iconic, still visible, unchanged bounds, and toggling the desktop back leaves it above the icon layer.
- App and test projects build with 0 errors (the running debug instance locks the default `bin`; builds used `tools/result/build-check`). Full runner: only the pre-existing `Native WeChat reader matches the frozen Python oracle field by field` failure remains.
- Docs: the contract is recorded in `design/wechat-message-monitor-wpf-design.md` §5.2 (宿主模式、Z 序与输入契约、显示桌面事件表、验收方法), README gained the 嵌入桌面 user steps, and `specs/manual-todo-and-groups/check_list.md` gained CHECK-059 (still unchecked until the taskbar 显示桌面 button is verified by hand).

## Priority-Colored Calendar Todo Text (2026-10-30)

Requested behavior change: the text of a Todo in the 当日日程 panel must use the same color as its priority.

- New `Application/Common/PriorityColorPolicy.cs` is now the single source of the P0–P3 palette (`#D92D20` / `#B54708` / `#3B5998` / `#475467`). `MessageFeedViewModel.PriorityColor()` delegates to it, so the message feed and the calendar cannot drift apart.
- `DesktopCalendarTodoItemViewModel.PriorityColor` exposes that palette per row; `DesktopCalendarView.xaml` binds the card title and the `P2`-style status label to it, replacing the previous `CalendarStrongText` DynamicResource that had no definition in any dictionary (it silently resolved to nothing).
- 已办理 items kept the existing `CalendarDisabledText` gray plus strikethrough at this checkpoint: color expressed priority only for still-active Todos. The ViewModel still returned the real priority color, the graying lived purely in the DataTrigger. (Superseded the same day — see the next section.)
- Scope was the right-hand 当日日程 list only; the two-line preview inside each month-grid cell still used `CalendarText`, and the 项目 row keeps its dot + muted text. (The month-cell preview now also follows the palette — see the next section.)

## Completed Todos Keep Priority Color (2026-10-30)

Follow-up request: 已办理 (completed) Todos must keep their priority color instead of turning gray, so one priority has exactly one color everywhere.

- `DesktopCalendarView.xaml` drops the `CalendarDisabledText` override from the `IsCompleted` DataTrigger on both the 当日日程 card title and its status label; completed rows keep priority color and are distinguished by strikethrough only. The status label collapsed to a direct `Foreground="{Binding PriorityColor}"` because the trigger became empty.
- The month-grid cell preview now also binds `PriorityColor` as its base foreground (previously `CalendarText`), so active and completed cell items share the same palette as the day list; completed cell items keep strikethrough and lose the gray.
- `TodoCompletedView.xaml` colors 优先级 (SemiBold) and 标题 via `DataGridTextColumn.ElementStyle`绑定到 `PriorityColor`; `TodoListItemViewModel` gained `PriorityColor => PriorityColorPolicy.For(Todo.Priority)` so 已办理 list, calendar and message feed all read the same `PriorityColorPolicy`.
- Completion state is now expressed by the checkbox + strikethrough alone; no view uses `CalendarDisabledText` for Todo titles anymore.

Verification at this checkpoint (2026-10-30):

- App project builds with 0 errors in an isolated output directory (the running debug instance locks the default `bin`).
- Full runner: only the pre-existing `Native WeChat reader matches the frozen Python oracle field by field` failure remains; every desktop-calendar and Todo test passes. The P0/P1/P0 color assertions in `TestDesktopCalendarViewModelAsync` are unchanged (the ViewModel contract did not move).

Verification at this checkpoint (2026-10-30):

- `dotnet build tests/WechatDashboard.Tests` in an isolated output directory: 0 errors (NU1900 vulnerability-feed warnings are network-only).
- `TestDesktopCalendarViewModelAsync` now asserts P1/P0/P0 colors on the three selected-day rows (including the completed one) and that `MessageFeedViewModel.PriorityColor(P1)` is still `#B54708`.
- Full runner: only the pre-existing `Native WeChat reader matches the frozen Python oracle field by field` failure remains; every desktop-calendar test passes.

## Compact Todo Detail and Reminder Cards (2026-10-29)

Current implemented behavior:

- `TodoDetailWindow` is compact by default: 560×600 (min 480×420), 12 DIP base font, 16 DIP outer margin. Reusable styles (`Card`, `SectionTitle`, `FieldLabel`, `HintText`, `CompactButton`) unify the white-card look; 保存待办 is the primary blue button.
- The detail window no longer offers the 状态 ComboBox: status changes only through explicit actions (完成 etc.). `TodoDetailViewModel.Status` still participates in save, `CanComplete` and `CanTogglePin`; saving writes the current status back unchanged. 优先级 and 项目 now share one row.
- The 查看原始消息 button has been removed from the detail window. The original message stays visible in the read-only card. The 复制原文 and 复制全部信息 buttons were also removed (2026-10-29); their code-behind handlers and `CopyToClipboard` helper were deleted, while `TodoDetailViewModel.BuildCopyText()` stays because the test suite asserts on it. `NavigateToSourceCommand`/`NavigateAsync` remain in the ViewModel but have no UI entry.
- `ReminderNotificationWindow` cards shrink from 500 to 380 DIP wide, MaxHeight 520 → 360; title font 22 → 15, body 16 → 14, buttons and margins tightened. `ReminderNotificationQueue.MinCardHeight` is now 170 to match.
- Queue stacking, badge, positioning and resolution behavior are unchanged.
- Scrollbars are now styled globally: `Styles/ScrollbarStyles.xaml` (merged in `App.xaml`) declares the implicit `ScrollBar` style — 12 DIP track, 6 DIP rounded thumb (`#C7D2DE` → `#94A3B8` hover → `#64748B` drag), transparent track that shows a `#EFF3F7` rounded trough only while hovered, no arrow buttons. This covers 消息流 / 关注设置 / 采集源设置 and every other view; the duplicate style block in `TodoDetailWindow.xaml` was removed. `CalendarControls.xaml` keeps its own theme-driven scrollbar (window-level style wins).
- Pinned Todos are no longer excluded from the due-date groups. `RefreshAsync` keeps the 已置顶 group *and* lists the same row in its 已逾期 / 今日到期 / 后续到期 / 无截止时间 group (pinned first, `[置顶]` badge unchanged). Both groups share one `TodoListItemViewModel`, so checkbox state and 批量完成 stay in sync; `ActiveRows()` uses `Distinct()` and `ActiveCount` is the de-duplicated `activeRows.Length`, so `SelectedCount`/全选 never double-count. The header now reads 活动待办 N 条（去重）and states that pinned items are also counted in their due-date group.
- `TodoService` now parses colloquial clock times from the message body (`5点半`, `下午3点`, `16点左右`, `明天上午10点`, `17:30`) via `ExplicitClockTimeRegex`, so those Todos get a real deadline instead of the end-of-day fallback; `第3点` style numbering is excluded by a negative lookbehind and messages carrying a full yyyy/M/d stamp skip this branch. Reminder derivation is now: Agent time → meeting card −5 min → body clock −15 min → explicit due −1 h → day-end fallback at 09:00 on the due day, else capture +1 h (`IsDayEndSentinel` distinguishes a real 23:59 from the synthetic day end). This kills the "due 23:59, remind 22:59" case.
- Cross-chat merge can now move a Todo's time *earlier* when the message confirms it. `ConfirmedClockTimeRegex`/`TryResolveConfirmedClockTime` (`那就` / `定在` / `改为` / `改到` / `调整到` / `挪到` / `推迟到` / `提前到` / `确认` / `确定` / `敲定` / `好吧` + an optional day/period + clock) reuses the D-036 period conversion; `请问5点半方便么` stays a question. A new `TimeConfirmed` flag flows `MessageCapturePipeline`/`MessageFeedViewModel` → `MergeTodoRequest` → `SqliteTodoMerge.ApplyAsync`, where `RescheduleAsync`/`ApplyDueAtAsync` skip the "only later" comparison and overwrite due + reminder with the confirmed instant minus `ClockTimeReminderLead`. Both paths still require the confirmed instant to be in the future, otherwise the existing reminder is kept so the Todo never loses it. Merge still attaches the message as a source and never creates a second Todo; the status text distinguishes 按确认的时间更新提醒 from 按更晚时间更新提醒.

Verification at this checkpoint (2026-10-29):

- App project builds with 0 errors in an isolated output directory (default `bin` remains locked by the running debug instance; NuGet vulnerability-data warnings are network-only).

## Desktop Calendar Todo Ordering (2026-10-15)

`DesktopCalendarViewModel` (shared by the main-window 日程概览 and the desktop calendar window) orders each day's Todos with a fixed four-level sort contract:

- First key: `Priority` — P0 first.
- Second key: has an active project (`ManagedProjectId` points at a still-enabled followed project) — with-project items first.
- Third key: reminder time — earliest still-pending (`Scheduled`/`Dispatching`) reminder, ascending; items without an active reminder go last. A completed Todo has its reminders cancelled, so the key falls back to its latest reminder time to keep the row from jumping.
- Fourth key: source-message `SentAt`, ascending; manual Todos without a source message go last.
- Tie-breakers: `DueAt`, then `Id`, for a stable order.
- Completion state no longer participates in ordering. Checking a Todo complete leaves it in place; the checkbox and strikethrough are the only difference. A completed item therefore cannot be pushed out of the two-line date-cell preview the way it could under the older "completed last" contract.

Verification at this checkpoint (2026-10-15):

- `TestDesktopCalendarViewModelAsync` asserts the new priority-first order (`completed P0 17:00 → active P0 18:00 → active P1 15:00`).
- New `TestDesktopCalendarDailyOrderingAsync` covers all four keys: priority, active project, reminder time and source-message time.
- `TestDetailCompletionKeepsTodoVisibleInCalendarAsync` now completes a Todo that has a reminder and a same-day sibling, proving the row keeps its index after completion cancels the reminder.
- Full runner: 98/98 passing.

## Reminder Notification Queue (2026-10-28)

Current implemented behavior:

- Reminder delivery no longer blocks polling: `InAppReminderNotificationPublisher.PublishAsync` only enqueues into `ReminderNotificationQueue` and returns, so `ReminderWorker` marks the reminder delivered and continues with the remaining due items of the same round.
- `ReminderNotificationQueue` shows at most three cards at the bottom-right corner of the work area; further due reminders wait in a FIFO queue. The newest card occupies the lowest slot, older cards keep their position and move up only when needed. Gaps are 12 DIP and the screen margin is 24 DIP.
- Cards carry a `+N 条待查看` badge while other reminders are still queued.
- Card height is capped at `(workArea.Height - 2*24 - 2*12) / 3`, so three stacked cards always fit; long descriptions scroll inside the card instead of pushing the buttons out of the window.
- Cards are non-modal (`Show`, not `ShowDialog`), topmost, do not steal focus (`ShowActivated="False"`), and stay out of the taskbar. Closing a card with the title-bar X counts as Acknowledge.
- User choices are applied by `ReminderResolutionHandler`: 完成 marks the Todo completed, 延后10分钟 snoozes the reminder, 知道了 only refreshes the workbench.
- The queue repositions cards on `SizeChanged`/`ContentRendered`, because a `SizeToContent="Height"` window does not have its final height when `Loaded` fires.
- `TodoFeatureCoordinator.StopAsync` disposes the queue, which closes visible cards without re-dispatching and without recording them as user decisions.
- Trade-off: enqueuing counts as delivery, so `MarkDeliveredAsync` runs before the user acts. Reminders still waiting in the in-memory queue are not re-published after an application restart; keeping them would require a persisted "delivered but not resolved" state.

Verification at this checkpoint (2026-10-28):

- App project builds with 0 errors in an isolated output directory; the default `bin` path is locked when a debug instance is running (MSB3021/MSB3027, environment only).
- WPF smoke run with five queued reminders: three visible at `left=1076, top=679/352/24`, width 500, height ≤316, bottom edge 995 = 1019−24, gaps 12 and no overlap. Closing the top card invoked the handler once (`Acknowledge`) and promoted the fourth card (pending 2 → 1). Disposing the queue produced no further handler calls.
- Full .NET runner: no new failures. `Native WeChat reader matches the frozen Python oracle field by field` fails identically on the untouched baseline (`git stash` comparison) and is pre-existing; `Message feed keeps the source context when a page refresh races with navigation` failed on the baseline and passed with this change, so it is flaky rather than caused here.

## Agent and Automatic/Manual Todo Analysis (2026-10-26)

Current implemented behavior:

- The `模型配置` page configures the built-in DeepSeek endpoint and `deepseek-flash` model. The API Key is encrypted with Windows DPAPI `CurrentUser` and stored at `%LocalAppData%\WechatDashboard\Agent\protected-deepseek-api-key.bin`; saving or clearing the Key still requires an application restart.
- The `分析消息内容` checkbox is independent of Key loading. It persists as `model_analyze_message_content` in SQLite `app_settings` and takes effect immediately for subsequently processed messages.
- `MentionDetector` recognizes configured aliases, `@我`, `@你`, and `@所有人`. Automatic Todo creation also covers `@所有人`, including WeChat's U+2005 separator after the mention.
- Message-feed refresh and automatic capture do not use an LLM to rerank the message list. Automatic message priority continues to come from local `urgency_scores` rules, including when an automatic Todo uses an Agent priority.
- The former `AI 建议`, review, and clarification workflow has been removed. Manual `转为待办` now reuses `AnalyzeMentionAsync` for any newly converted message, independently of the automatic-analysis switch and mention detection. It sends only the current message, never adjacent messages or clarification answers; an existing Todo opens directly without another model call.
- A manual click is an explicit creation instruction, so `shouldCreate=false` does not block creation. Missing configuration, request failure, timeout, or invalid JSON falls back to the default description and local priority without blocking the Todo. A blank Agent description also falls back to the default text without discarding a valid Agent priority.
- After successful manual analysis, the Agent description is written only to the Todo, while its priority is used by both the Todo and a newly appended message-feed `urgency_scores` row. P0/P1/P2/P3 map to 90/75/50/20, and the score, Todo, and initial reminder commit or roll back together. The separate `AgentPriority` request field prevents ordinary Todo priority edits from changing feed scores; the displayed source message remains unchanged.
- Legacy `todo_suggestions` tables and data in existing databases are left untouched but are no longer read or written. New databases do not create that table.
- When automatic analysis is enabled, a newly ingested `@我` or `@所有人` message triggers one `AnalyzeMentionAsync` JSON request. The Agent returns `shouldCreate`, a P0-P3 priority, and a concise Chinese description.
- A successful `shouldCreate=false` decision persists the message but skips its automatic Todo when the content is unrelated, low priority, informational, or has no concrete action. This explicit decision is different from model failure.
- Agent output controls automatic Todo creation and overrides only `TodoItem.Priority` and `TodoItem.Description`. Title, project, managed project, due time, and automatic-creation status remain rule-driven. Local urgency scores are not overwritten.
- Automatic Todo due time uses local rules: an explicit `会议时间` start takes precedence, then an explicit current-week weekday such as `本周三` selects that Wednesday at day end, and otherwise the send-day end remains the fallback. Calendar grouping reads the same `DueAt`.
- Shihuatong capture follows a current message's `reference_seq` to the quoted record with the same `cider` and `server_seq`. A non-empty, non-duplicate quoted body is appended under `引用消息：`; the merged content feeds message display, automatic-Todo analysis, local urgency, and due-time parsing. Missing or already included quoted content is not appended again.
- Automatic analysis runs before `IMessageProcessingUnitOfWork.TryProcessAsync`, so no network wait occurs inside the SQLite transaction. An existing `(source, source_message_key)` skips the model call and still uses the transactional deduplication path.
- With the automatic switch off, no automatic model call is made and the original automatic-create behavior is preserved. Missing configuration, request failure, timeout, or invalid JSON also creates the automatic Todo with local priority/default description rather than treating the message as rejected. External cancellation is propagated.
- Existing messages and Todos are not automatically backfilled or recalculated in bulk. Automated model tests use fake HTTP handlers and do not consume real API quota.

Verification at this checkpoint:

- Release solution builds succeed with 0 compile errors.
- The complete .NET runner reports 83/83 passing tests.
- Coverage includes explicit automatic `shouldCreate=false`, model-failure fallback, manual conversion despite `shouldCreate=false`, existing-Todo idempotency, Agent description/priority application, local-versus-Agent feed-score isolation, transactional score rollback, quoted-body merge/deduplication, current-week weekday resolution, meeting-start due time, and calendar grouping.
- Automated model tests use fake HTTP handlers or controllable fakes and do not consume real API quota.

## Historical Snapshot (2026-08-09)

At that inspection, the active branch was `main` at `7a00f4d` (`fix: persist message processing results atomically`) and matched `origin/main`. Unrelated untracked files exist in the workspace; preserve them and stage only files intentionally changed for the current task.

Verified state at that historical checkpoint:

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
- Todo workbench implementation is present in the current working tree: arbitrary persisted messages can become idempotent Todos, active items are grouped into overdue/today/upcoming/undated, detail editing and persisted reminders/snooze history are wired, and Todo detail can locate the original message context.
- Verification on 2026-08-09: the standard solution build and WPF independent-output build both succeeded with 0 warnings and 0 errors; all 39 .NET regression tests passed. Use the independent output only while a Visual Studio debug process locks the default app DLLs.
- Python verification now passes all 47 tests. The stale `test_read_messages_aggregates_across_shards` assertion was updated to the documented newest-first `(sentAt, id)` contract without changing production sorting.

Target architecture decision (2026-08-11; not yet the current implementation):

- The final WeChat local-database path will be native C# and will not require Python at runtime.
- A separate self-contained x64 `WechatDashboard.KeyProbe.exe` will P/Invoke the selected `wx_key.dll`; the WPF process must not load the DLL directly.
- The project owner confirmed that the selected `wx_key.dll` is authorized for external distribution. Each release must still archive the authorization evidence, source, version, SHA-256, dependency closure, security scan, and signing result for the exact binary shipped.
- KeyProbe will transfer the key through a random current-user-only named pipe. Keys must not appear in arguments, environment variables, stdout/stderr, logs, normal plaintext files, or the application SQLite database.
- The C# reader will own multi-database validation, stable DB/WAL/SHM snapshots, SQLCipher V4 access or managed page decryption, `SessionTable`/`Name2Id`/`Msg_<md5>` reads, zstd/XML decoding, pagination, and incremental offsets.
- Python remains only as a migration compatibility path and golden oracle. Remove Python, PyInstaller artifacts, `.py` files, PowerShell probing, and plaintext key files from Release only after parity and clean-machine acceptance.

Native-reader implementation checkpoint (2026-08-11):

- Added the x64 `WechatDashboard.KeyProbe` project, strict non-secret argument parsing, dynamic `wx_key.dll` exports, sanitized JSON status, and key delivery over a current-user-only random named pipe.
- Added `IWeChatDatabaseKeyProvider`, a zeroing `WeChatDatabaseKeyLease`, and `NativeHookKeyProvider`; `WeChatLocalReaderService` prefers this provider when KeyProbe is available and keeps the PowerShell path only as an explicit compatibility fallback.
- WPF no longer requires a plaintext Key file after native extraction and preserves the in-memory key until compatibility Python initialization starts; that environment-variable handoff is transitional and is cleared after init.
- Verification: full Debug/Release solution builds pass with 0 warnings/0 errors, all 44 .NET tests pass, all 47 Python tests pass, and KeyProbe invalid-argument/missing-DLL smoke tests return sanitized JSON.
- No real Weixin process Hook was executed in this checkpoint because each real extraction requires explicit per-run authorization. C# SQLCipher/schema/zstd reading, install/data-root separation, Release staging, signing, and clean-machine acceptance remain open.

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

Planned Release additions are `src/WechatDashboard.KeyProbe/` and the native C# WeChat reader components under `Infrastructure/Capture`; installed mutable state moves to `%LocalAppData%\WechatDashboard`.

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

Current technical state and gaps:

- `MessageCapturePipeline.ProcessAsync` now uses `IMessageProcessingUnitOfWork`; SQLite persists the unique-key message, `message_classifications`, `urgency_scores`, and optional automatic Todo on one connection and transaction. Any result failure rolls back all four artifacts, a later retry succeeds, and duplicate input creates no duplicate result rows.
- Classification category, confidence, reason and classifier plus urgency score, priority and reason are persisted. Explicit classifier-version metadata is not yet modeled and remains part of the rule-center work.
- Adapter exceptions are currently reduced to debug output inside the pipeline; the diagnostics page does not yet persist true per-Adapter success/failure history.
- Some diagnostic timestamps are refresh timestamps rather than recorded capture timestamps.
- Top counters are calculated from a recent-message sample and can undercount high-volume days.
- Todo list/detail/message-flow responsibilities now use feature Views/ViewModels, async commands, application services, policies, repositories, a unit of work and `TodoFeatureCoordinator`. `MainWindow.xaml.cs` still owns unrelated capture, settings, diagnostics and dashboard responsibilities, so continue extracting those incrementally.
- Reminder migration/lifecycle, in-app delivery, stale-claim recovery and snooze presets are implemented. Windows Toast, quiet hours, per-source controls, explicit custom snooze UI, project selection UI and WPF automation remain open.
- Message FTS search, tray mode, report export, backup/restore and retention controls remain unimplemented. The migration runner currently has only the Todo reminder migration.

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

Immediate next work, in priority order after the 2026-08-11 native-reader decision:

1. Restore the Python test baseline and freeze its newest-first behavior as the migration oracle.
2. Implement the isolated x64 KeyProbe and protected named-pipe contract around the authorized `wx_key.dll`.
3. Port key validation, snapshots, SQLCipher V4, schema/message parsing, zstd/XML, pagination and offsets to C# with offline fixture parity.
4. Dual-run C# and Python, complete privacy-safe real-machine validation, then pass a clean-machine installation test with no Python available.
5. Split install-root paths from `%LocalAppData%\WechatDashboard`, archive release authorization/provenance evidence, sign artifacts, and remove Python from Release.
6. Resume structured Adapter diagnostics, SQL aggregate counters, Todo acceptance, search/rule-center, reporting and remaining `MainWindow.xaml.cs` extraction after the migration slice is green.

The `wx_key.dll` distribution decision is closed by the project owner's 2026-08-11 authorization confirmation, subject to per-release evidence and binary provenance. Manual DB Key/data-directory/reinitialization UI and minimized-WeChat validation remain required fallback and acceptance work.

Do not implement hidden in-process key extraction in WPF. Use the separate KeyProbe only after explicit user action, isolate native failures, and keep all outputs sanitized.
