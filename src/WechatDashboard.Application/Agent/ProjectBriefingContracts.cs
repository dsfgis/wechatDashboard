using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Agent;

/// <summary>
/// 参与项目简报的单条消息。
/// 由应用层按项目关键词过滤并截断正文后送入模型，正文长度受服务层约束。
/// </summary>
/// <param name="Source">消息来源标识（微信、石化通、石化邮等）。</param>
/// <param name="Chat">群聊或会话名称。</param>
/// <param name="Sender">发送人昵称。</param>
/// <param name="Content">截断后的消息正文。</param>
/// <param name="SentAt">消息发送时间。</param>
/// <param name="Id">数据库消息 ID；兼容旧测试构造时允许缺省为 0，真实服务必须传正数。</param>
public sealed record BriefingMessage(
    string Source,
    string Chat,
    string Sender,
    string Content,
    DateTimeOffset SentAt,
    long Id = 0);

/// <summary>送入模型的既有事项最小视图，避免暴露无关持久化字段。</summary>
public sealed record ProjectBriefingExistingItem(
    long Id,
    ProjectMemoryCategory Category,
    string Title,
    ProjectMemoryStatus Status);

/// <summary>模型返回的结构化事项；稳定既有 ID 只能来自请求白名单。</summary>
public sealed record ProjectBriefingItem(
    long? ItemId,
    ProjectMemoryCategory Category,
    string Title,
    ProjectMemoryStatus ProposedStatus,
    string Reason,
    IReadOnlyList<long> SourceMessageIds);

/// <summary>项目简报请求：项目名、时间窗口与已按项目过滤的跨源消息（按时间正序）。</summary>
/// <param name="ProjectName">项目名称。</param>
/// <param name="From">时间窗口起点（含）。</param>
/// <param name="To">时间窗口终点（含）。</param>
/// <param name="Messages">跨来源的项目相关消息，按发送时间正序排列。</param>
public sealed record ProjectBriefingRequest(
    string ProjectName,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<BriefingMessage> Messages)
{
    /// <summary>本项目已有记忆事项；旧调用方未提供时保持空集合。</summary>
    public IReadOnlyList<ProjectBriefingExistingItem> ExistingItems { get; init; } =
        Array.Empty<ProjectBriefingExistingItem>();
}

/// <summary>项目简报结果：总体摘要、关键进展、风险与待行动事项。</summary>
/// <param name="Summary">两三句话的总体进展摘要。</param>
/// <param name="Highlights">关键进展或结论，每条一句话。</param>
/// <param name="Risks">故障、阻塞、延期或争议；没有时为空。</param>
/// <param name="Actions">仍需要当前用户跟进或确认的事项；没有时为空。</param>
public sealed record ProjectBriefing(
    string Summary,
    IReadOnlyList<string> Highlights,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> Actions)
{
    /// <summary>供用户批量确认的结构化事项，旧模型实现未返回时为空集合。</summary>
    public IReadOnlyList<ProjectBriefingItem> Items { get; init; } = Array.Empty<ProjectBriefingItem>();
}

/// <summary>
/// 项目简报模型：把跨微信、石化通、石化邮等来源的项目消息汇总为结构化中文简报。
/// 实现方必须对响应做严格校验，格式无效时抛出异常，由调用方回退本地统计摘要。
/// </summary>
public interface IProjectBriefingModel
{
    /// <summary>生成项目简报。消息列表为空时抛出 <see cref="InvalidOperationException"/>。</summary>
    Task<ProjectBriefing> SummarizeProjectAsync(
        ProjectBriefingRequest request,
        CancellationToken cancellationToken);
}
