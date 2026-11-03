# 项目周报生成与私聊识别

## Context

实施前 `WeeklyReportCollector` 只能归集消息，尚未接入生产界面、模型周报总结或 Excel 导出。本次改造把项目待办、项目消息与成员直接发给当前用户的周报内容合并，按指定模板生成新项目周报。

已确认的实施选择：
- 成员以“关注设置”中的项目成员和 `IsReporting` 为准；为“智能化管线管理系统”补齐沈佳奇、张欣宇、张彬、赵岑东，已有配置不覆盖，导出器不硬编码四人。
- 不做内容预览窗口；生成统计和异常摘要确认后直接导出 Excel，由用户在 Excel 中继续修改。
- 系统只生成 Excel，不新增或接入邮件草稿、自动发信和周报收件人功能，最终周报由用户人工发送；石化邮仅作为接收成员周报的采集来源，成员邮箱仅用于匹配发件人。
- 成员周报先补齐可靠的“私聊/直接发给我”识别；无法证明路由的历史或来源消息标记 Unknown，不猜测、不混入成员周报。
- 默认模板为 `D:\work\00_部门文档\周报月报\周报\10月周报\【智能化管线管理系统】项目周报20261031.xlsx`，原文件绝不修改。

## 一、用户流程与界面

1. 在项目看板现有“项目简报”下增加“项目周报”折叠区，不新增一级菜单。
2. 提供项目、周开始/结束日期、模板路径和浏览按钮；默认最近一个完整工作周（周一至周五），报告日期取生成日。
3. 点击“生成 Excel”后通过 `SaveFileDialog` 选择输出位置，默认模板同目录，文件名为 `【项目名称】项目周报yyyyMMdd.xlsx`，禁止覆盖模板。
4. 后台执行取数、总结和导出，复用现有 `Task.Run`、按钮防重入和状态文本模式。
5. 导出前显示各成员采用条数、缺失 wxid/邮箱、Unknown/群聊/非直接邮件排除数、项目消息与待办数、模型或本地回退状态；存在缺成员素材等异常时由用户确认后继续。
6. 完全无成员周报且无项目/待办素材时不生成空报告；模型不可用时仍使用有依据的本地摘要导出，并醒目标注回退。
7. 首次为“智能化管线管理系统”生成时仅补齐缺失的四个成员姓名并启用周报；已有别名、邮箱、wxid 和周报开关不修改。

## 二、补齐消息路由与成员身份

### 统一模型和数据库

- 为 `CapturedMessage`、`Message` 增加 `SenderExternalId`、`ChatType`、`Direction`，使用 `Unknown/Private/Group` 和 `Unknown/Incoming/Outgoing` 枚举；未知是显式状态，不得默认私聊或接收。
- 追加数据库迁移，不修改既有迁移及校验和：`messages` 增加上述路由列，历史行填 Unknown；`project_members` 增加可空 `wechat_user_id`。
- 扩展 `ProjectMember`、成员仓储和设置页：维护微信 wxid、邮箱及是否参与周报。微信按 wxid 精确归人，邮件按现有邮箱精确归人；姓名/别名只用于显示和诊断，不能替代 DirectToMe 证据。
- 消息仓储完整读写新增字段。重复采集到已有消息时，若旧记录为 Unknown 而新记录路由已知，仅补路由元数据，不重新分类、不重建待办；用户可通过重采原始来源回填历史。

### 各来源严格规则

- **微信本地库**：在 `WeChatV4MessageQueryService` 读取会话用户名、`status`、`real_sender_id` 和 `Name2Id`。只有非 `@chatroom` 单聊、经真实 fixture/原库确认值的 Incoming、且解析到 wxid 时才认定 DirectToMe；Outgoing、群聊、status 或 wxid 不明均排除。先补已知收发方向的 fixture 锁定 `status` 映射，不凭经验猜值。
- **石化邮**：扩展 `MailEnvelope`、IMAP/Mbox Reader 和 `MailMessageMapper`，保留 To/Cc/Bcc。仅 To 规范化后唯一且等于当前采集账号、Cc/Bcc 为空时映射为 Private+Incoming，`SenderExternalId=FromAddress`；多收件人、仅 Cc/Bcc 或账号未配置均不采用。
- **石化通、飞书/钉钉 JSONL、窗口 OCR**：本期保留 Unknown，不进入成员周报；仍可参与既有项目消息和待办汇总。后续取得可靠会话类型、方向和发送人 ID 后再启用。
- 成员归属只使用当前正文；“引用消息：”部分仅作上下文，不得把被引用人的事项记到回复者名下。

## 三、周报归集与结构化总结

### 完整取数

- 扩展 `IMessageRepository` 为周报提供完整时间窗分页读取，按 `(sent_at,id)` 稳定翻页，消除当前“最新 1000 条后再过滤”的漏数风险；既有简报接口行为保持不变。
- 改造 `WeeklyReportCollector`：成员素材只保留可靠 Private+Incoming 且 wxid/邮箱精确匹配的消息；不再用姓名子串认定成员私聊。
- 项目总体素材继续用 `FollowedProjectMatcher`，并校验命中的 `ProjectId`；排除当前用户发出内容。
- 在 `ITodoRepository`/`SqliteTodoRepository` 增加只读周报查询：按 `ManagedProjectId` 取本周完成、当前活动及下周到期的待办，返回标题、说明、状态、截止/完成时间和来源消息 ID，不改变状态或优先级。

### 周报契约与编排

- 在 `Application/Weekly` 新增通用契约：报告元数据、总体进展、工作项、问题、风险、下周目标和安排；工作项按事项分组并含责任人列表，进度/工时均为可空，保留输入来源 ID 供校验。
- 新增 `WeeklyReportService`，统一编排成员周报、项目消息和待办，输出 `WeeklyReportDraft` 及归集诊断。
- 新增独立 `IWeeklyReportModel`，在现有 OpenAI 兼容实现上复用配置、HTTP/JSON 和严格响应校验。提示词要求区分本周完成、下周计划、问题和风险，禁止编造负责人、进度、工时或来源。
- 成员私聊内容不必包含项目名；由模型从成员直接消息中识别周报内容与闲聊。按成员分批送模并汇总，避免长周报被单次条数或字符上限静默截断。
- 校验模型返回的成员和来源 ID 必须属于输入，工时/进度没有输入依据时置空；无效结果整次回退。
- 本地回退按成员保留有依据的原文要点，并附加待办统计；模型未配置、超时或格式错误不得阻断导出，用户主动取消仍正常传播。

## 四、Open XML 模板导出

- 在 Infrastructure 引入 `DocumentFormat.OpenXml`，新增通用 `OpenXmlWeeklyReportExporter`；直接修改模板副本的单元格，避免重建工作簿，保留批注、VML、数据验证、合并和样式。
- 校验唯一工作表“项目周报”及关键合并区域。先复制到用户选择的输出路径，再写同目录临时文件，成功后替换目标，失败清理临时文件；任何情况下不写原模板。
- 清除模板上一周业务内容，但保留固定项目元数据（项目经理、报告人、阶段）；更新项目名称、周期和报告日期。无新依据的阶段/总体进度及所有未知工时留空。
- 字段映射：
  - `C6`：本周执行进度说明，综合成员进展和待办。
  - 11—15 行：本周工作项，按事项/工作流分组，填内容、责任人及有明确依据的进度和工时。
  - 17—21 行：问题及解决方案；23—27 行：风险及缓解措施。
  - `C28`：下周工作目标；33—36 行：下周任务、期望结果、责任人和有计划依据的工时。
- 不固定成员或行数。超过模板行数时在单元格内继续按编号和分组换行，不插入行列、不静默丢弃；导出前验证所有草稿条目均已映射且仅映射一次。

## 五、主要修改文件

- 路由与身份：`Application/Capture/CapturedMessage.cs`、`Domain/Entities/Message.cs`、`Domain/Entities/ProjectMember.cs`、`Infrastructure/Capture/WeChatV4MessageQueryService.cs`、`MailInboxContracts.cs`、`ImapMailInboxReader.cs`、`MboxMailArchiveReader.cs`、`MailMessageMapper.cs`。
- 持久化：`SqliteDatabaseInitializer.cs`、`SqliteMessageRepository.cs`、`SqliteProjectMemberRepository.cs`、`SqliteTodoRepository.cs` 及对应 Application 接口。
- 周报：`Application/Weekly/WeeklyReportCollector.cs`，新增周报契约、`WeeklyReportService`、`IWeeklyReportModel`；扩展 `OpenAiCompatibleMessageActionModel`；新增 `Infrastructure/Weekly/OpenXmlWeeklyReportExporter.cs`。
- 界面：`App/MainWindow.xaml`、`App/MainWindow.xaml.cs`，扩展项目成员设置和项目看板周报区。
- 测试：`tests/WechatDashboard.Tests/Program.cs`、`MailCaptureTests.cs` 和微信 fixture。

所有新增及修改代码保持中文注释且注释率超过 20%；不修改自动采集/自动待办的本地优先级规则，不实现此前讨论的待办状态闭环或项目记忆。

## 六、验证

1. **路由测试**：微信单聊收发、群聊、未知 status、缺失 wxid；邮件唯一 To、多人 To、Cc/Bcc、账号缺失；石化通/JSONL/OCR 保持 Unknown；历史迁移及重采只补元数据。
2. **归集测试**：成员 wxid/邮箱精确匹配、项目隔离、引用防误归属、排除本人、超过 1000 条完整分页、四位默认成员幂等补齐、0/多位成员及闲聊过滤。
3. **总结测试**：结构化 JSON 校验、未知进度/工时留空、长周报分批、模型失败本地回退、取消传播、待办时间窗和来源 ID 合法性。
4. **Excel 测试**：使用真实模板副本执行端到端导出；检查旧业务内容清除、所有草稿条目无遗漏、合并/样式/批注/VML/数据验证保留、Excel 可正常打开、原模板哈希不变。
5. 手工完成“微信私聊成员周报 + 石化邮直接周报 + 项目待办”的生成，核对周期、责任人、问题风险和下周安排；确认 Unknown 历史消息未被冒充采用。
6. 执行 `dotnet build WechatDashboard.sln -c Release` 和 `dotnet run --project tests/WechatDashboard.Tests -c Release`，确保全量测试通过。

## 七、实施结果

- 已完成项目成员配置、可靠消息路由、完整时间窗分页、周报结构化总结、本地回退和 Open XML 模板导出，并接入项目看板。
- 周报功能只生成 Excel 文件，不包含邮件草稿生成或自动发送；成员周报采集仅使用满足 DirectToMe 约束的微信私聊和石化邮消息。
- 真实模板副本导出验证通过，原模板哈希保持不变；最终与项目记忆共同执行 Release 全量回归，112 项测试全部通过。
