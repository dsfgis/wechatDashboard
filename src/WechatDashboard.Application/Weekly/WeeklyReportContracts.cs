using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Weekly;

/// <summary>周报元数据：项目名称、统计周期和实际报告日期。</summary>
public sealed record WeeklyReportMetadata(
    string ProjectName,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset ReportDate);

/// <summary>
/// 本周工作项。进度使用 0 到 1 的小数，工时使用小时；
/// 原始素材没有明确依据时对应字段必须为 null，不能由模型猜测。
/// </summary>
public sealed record WeeklyWorkItem(
    string Description,
    IReadOnlyList<string> Owners,
    double? ExpectedProgress,
    double? CurrentProgress,
    double? PlannedHours,
    double? ActualHours,
    IReadOnlyList<long> SourceMessageIds,
    IReadOnlyList<long> SourceTodoIds);

/// <summary>周报问题项：问题、解决方案、责任人和处理状态。</summary>
public sealed record WeeklyIssue(
    string Description,
    string Solution,
    IReadOnlyList<string> Owners,
    string Status,
    IReadOnlyList<long> SourceMessageIds,
    IReadOnlyList<long> SourceTodoIds);

/// <summary>周报风险项：风险、缓解措施、责任人和监控状态。</summary>
public sealed record WeeklyRisk(
    string Description,
    string Mitigation,
    IReadOnlyList<string> Owners,
    string Status,
    IReadOnlyList<long> SourceMessageIds,
    IReadOnlyList<long> SourceTodoIds);

/// <summary>下周工作安排；计划工时没有原文依据时保留为空。</summary>
public sealed record WeeklyPlan(
    string Task,
    string ExpectedResult,
    IReadOnlyList<string> Owners,
    double? PlannedHours,
    IReadOnlyList<long> SourceMessageIds,
    IReadOnlyList<long> SourceTodoIds);

/// <summary>
/// 可直接映射到项目周报模板的结构化草稿。
/// 各条目保留来源 ID，供服务层校验模型没有虚构输入。
/// </summary>
public sealed record WeeklyReportDraft(
    WeeklyReportMetadata Metadata,
    string Summary,
    IReadOnlyList<WeeklyWorkItem> WorkItems,
    IReadOnlyList<WeeklyIssue> Issues,
    IReadOnlyList<WeeklyRisk> Risks,
    string NextWeekGoal,
    IReadOnlyList<WeeklyPlan> Plans);

/// <summary>单个成员的归集诊断：采用消息数及是否配置可靠直接身份。</summary>
public sealed record WeeklyMemberDiagnostic(
    string MemberName,
    int AdoptedCount,
    bool HasDirectIdentity);

/// <summary>周报取数诊断，用于导出前向用户说明缺素材和路由排除情况。</summary>
public sealed record WeeklyCollectionDiagnostics(
    IReadOnlyList<WeeklyMemberDiagnostic> Members,
    int UnknownRouteExcluded,
    int NonPrivateExcluded,
    int ProjectMessageCount);

/// <summary>周报生成结果：正文、模型/回退状态、取数诊断及待办数量。</summary>
public sealed record WeeklyReportOutcome(
    WeeklyReportDraft Draft,
    bool FromModel,
    string? FallbackReason,
    WeeklyCollectionDiagnostics Diagnostics,
    int TodoCount);

/// <summary>送入周报模型的完整素材，成员内容、项目消息和待办分别保留。</summary>
public sealed record WeeklyReportModelRequest(
    WeeklyReportMetadata Metadata,
    WeeklyReportMaterial Material,
    IReadOnlyList<Message> ProjectMessages,
    IReadOnlyList<TodoItem> Todos);

/// <summary>项目周报模型：把已归集素材整理成严格结构化中文周报。</summary>
public interface IWeeklyReportModel
{
    /// <summary>生成周报草稿；响应无效时抛出异常，由服务层回退本地摘要。</summary>
    Task<WeeklyReportDraft> GenerateAsync(
        WeeklyReportModelRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Excel 周报导出抽象，界面只依赖草稿和文件路径。</summary>
public interface IWeeklyReportExporter
{
    /// <summary>把草稿写入模板副本；实现方不得修改模板原文件。</summary>
    void Export(
        WeeklyReportDraft draft,
        string templatePath,
        string outputPath);
}
