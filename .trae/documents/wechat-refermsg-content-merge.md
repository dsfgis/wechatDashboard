# 微信 refermsg 引用正文合并计划

## Context

微信原生采集当前只总结当前消息的 XML，`appmsg type=57` 中嵌入的 `refermsg/content` 没有进入 `CapturedMessage.Content`。因此，只存在于引用消息中的会议主题、时间和行动要求无法参与后续展示、自动待办 Agent 摘要、本地紧急度及截止日期解析。目标是在采集解码层补齐引用正文，使其与石化通使用相同的“当前正文 + 引用消息”文本边界，同时不改变普通微信消息及 Agent 协议。

## 实施方案

### 1. 在微信 XML 解码器中合并引用正文

修改 `src/WechatDashboard.Infrastructure/Capture/WeChatMessageContentDecoder.cs`：

- 保留现有 XML 安全设置、8MiB 限制、异常回退，以及图片、视频、表情、文件、链接和非 XML 文本的既有摘要行为。
- 在 appmsg 分支中复用已读取的字符串类型，仅当 `type == "57"` 时读取直接子节点 `refermsg/content`；LINQ to XML 的 `XElement.Value` 负责 XML 实体解码。
- 先按原规则生成当前回复摘要，再追加引用正文，格式为 `当前摘要\n\n引用消息：\n引用正文`。
- 在本类增加私有合并方法，行为与石化通 `CombineWithReference` 对齐：引用正文 Trim；空白引用不追加；当前摘要已包含相同引用正文时不重复追加。不新增跨采集器依赖或共享抽象。
- 添加必要中文注释，新增代码注释率保持 20% 以上。不修改 `WeChatV4MessageQueryService`，因为其已经把完整解码文本交给 `Summarize`。

### 2. 补充原生微信解码回归测试

修改 `tests/WechatDashboard.Tests/Program.cs`，在现有 `TestNativeWeChatReaderAsync` 的 `WeChatMessageContentDecoder` 断言区域加入独立 XML 用例，不修改同时用于 Python 对照的共享数据库夹具：

- `type=57` 保留当前回复标题，并合并多行、含 XML 转义字符的引用正文。
- 缺失、空白或已包含的引用正文不追加、不重复拼接。
- 普通 `type=5` 链接、`type=6` 文件和非 XML 文本仍保持原摘要。
- 现有原生 V4 查询及适配器断言继续验证生产读取链路仍调用该解码逻辑。

## 边界

- 本次只解析当前 XML 已嵌入的 `refermsg/content`，不增加按 `svrid` 联查数据库，也不批量回填历史消息。
- 不修改大模型提示词、JSON 协议、消息流本地评分或自动待办截止日期规则；合并后的 `Content` 会自然进入现有共用管线。
- 不改动石化通实现，避免扩大本次修改范围。

## 验证

1. 执行 `dotnet build WechatDashboard.sln --configuration Release --no-restore -m:1 -v minimal`，确认 0 编译错误。
2. 执行 `dotnet run --project tests\WechatDashboard.Tests\WechatDashboard.Tests.csproj --configuration Release --no-restore --no-build`，重点检查原生微信读取测试及既有石化通、自动待办测试。
3. 若完整 runner 仍出现既有 `MessageFeedViewModel` 并发刷新/导航竞态，单独记录，不将其误报为本次引用解析失败，也不宣称完整测试全绿。
