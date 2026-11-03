# 重复待办清理与石化邮直接收信

## Context
当前两条已办待办指向同一事项，需要仅保留最后创建的待办；石化邮实时采集仍存在本地 mbox 缓存与 IMAP 两条来源，且历史开关可能继续启用缓存或禁用 IMAP。目标是删除指定重复记录，并让石化邮只通过现有 IMAP 实现直接收信，不回退缓存。

## 实施步骤

### 1. 定向清理重复待办
- 已核对数据库 `tools/result/data/wechat-dashboard.db`：保留 ID **1305**（创建时间 2026-11-03 10:35:30，来源消息 81530），删除 ID **1298**（创建时间 2026-11-02 16:55:29，来源消息 81135）。
- 停止应用后备份数据库，再在单事务中启用外键，以 `id=1298`、状态 `Done`、`source_message_id=81135` 和创建时间为保护条件执行一次性删除；影响行数不是 1 则回滚。
- 利用现有 `ON DELETE CASCADE` 清理 1298 的来源关联及可能存在的提醒，保留原始消息 81135、81530 和待办 1305；不新增通用待办删除功能。

### 2. 石化邮只走 IMAP 直接收信
- 修改 [CaptureAdapterFactory.cs](file:///d:/study/code/wechatDashboard/src/WechatDashboard.Infrastructure/Capture/CaptureAdapterFactory.cs)：默认实时源不再注册 `SinopecMailArchive`，IMAP 默认启用。
- 在该工厂集中加入石化邮有效源策略并供适配器创建与界面加载复用：剔除历史 Archive 源、强制 SinopecMail 为启用，防止数据库中保存的 `Archive=true` 或 `IMAP=false` 让旧行为恢复。
- 继续复用现有 `SinopecMailCaptureAdapter`、`ImapMailInboxReader`、DPAPI 凭据和 UID 游标，不新写邮件协议代码；无凭据时按现有机制报告配置失败，其他采集源继续运行，禁止回退缓存。

### 3. 同步设置界面和过时测试
- 修改 [MainWindow.xaml.cs](file:///d:/study/code/wechatDashboard/src/WechatDashboard.App/MainWindow.xaml.cs)：加载、保存和创建管线时应用同一直接收信策略，保存后刷新列表，确保界面状态与实际行为一致。
- 修改 [MainWindow.xaml](file:///d:/study/code/wechatDashboard/src/WechatDashboard.App/MainWindow.xaml) 的石化邮说明：保存账号后直接通过 IMAP 收信，不再读取本地缓存，无需手动启用。
- 修改 [Program.cs](file:///d:/study/code/wechatDashboard/tests/WechatDashboard.Tests/Program.cs)：补充默认源无 Archive、IMAP 强制启用、历史缓存源不会创建适配器等回归断言；从默认测试套件移除会读取本机真实缓存的旧集成用例，保留 IMAP、映射、去重及周报测试。
- 周报仍从统一消息库归集，无需改周报业务代码。新增代码与测试均使用中文注释，注释占比保持超过 20%。

## 验证
1. 数据库清理后核对：1298 及其关联不存在，1305 与两条原始消息保留，`PRAGMA foreign_key_check` 无异常，界面仅显示保留项。
2. 执行 Release 生成和完整自定义测试程序，确认无编译错误及测试回归。
3. 检查有效采集源仅包含石化邮 IMAP；使用已保存凭据进行一次真实只读收信，确认邮件可入库、UID 游标推进且不重复，未配置凭据时只产生明确诊断且不会读取缓存。
