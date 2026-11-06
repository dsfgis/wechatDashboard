using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.TodoRules;

/// <summary>
/// 用户自定义的自动待办规则（Agent 记忆）。
/// 一条规则是一组关键词，用逗号、顿号、分号、斜杠或空白分隔；
/// 命中任一关键词的消息只入库，不再自动创建待办。
/// </summary>
/// <param name="Id">数据库主键。</param>
/// <param name="Text">规则原文，例如“验证码、OTP、动态码”。</param>
/// <param name="IsEnabled">是否参与匹配；停用的规则保留在库中但不生效。</param>
/// <param name="CreatedAt">创建时间，仅用于界面排序。</param>
public sealed record TodoRule(long Id, string Text, bool IsEnabled, DateTimeOffset CreatedAt);

/// <summary>自动待办规则的持久化入口。</summary>
public interface ITodoRuleRepository
{
    /// <summary>按创建时间正序读取全部规则，包含已停用项。</summary>
    Task<IReadOnlyList<TodoRule>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>新增一条规则并返回主键；文本会在写入前归一化并校验。</summary>
    Task<long> AddAsync(string text, CancellationToken cancellationToken);

    /// <summary>启用或停用指定规则；记录不存在时不做任何修改。</summary>
    Task SetEnabledAsync(long id, bool isEnabled, CancellationToken cancellationToken);

    /// <summary>删除指定规则；记录不存在时不做任何修改。</summary>
    Task DeleteAsync(long id, CancellationToken cancellationToken);
}

/// <summary>
/// 规则匹配：把规则文本切分为关键词，并判断消息正文或群名是否命中。
/// 这里的判定是确定性的，不依赖模型；模型侧只负责识别语义相近但用词不同的表达。
/// </summary>
public static class TodoRuleMatcher
{
    /// <summary>关键词分隔符：中英文逗号、顿号、分号、斜杠、竖线与空白。</summary>
    private static readonly char[] Separators =
        { '，', ',', '、', '；', ';', '/', '|', ' ', '\t', '\r', '\n' };

    /// <summary>过短的关键词（单字）会产生大量误命中，因此不参与匹配。</summary>
    public const int MinKeywordLength = 2;

    /// <summary>单条规则允许的最大字符数，避免异常输入进入提示词和匹配循环。</summary>
    public const int MaxRuleTextLength = 200;

    /// <summary>把规则文本拆分为去重后的关键词集合，忽略空白与单字词。</summary>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        return text
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= MinKeywordLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// 返回命中的第一条启用规则；没有命中返回 null。
    /// 群名也参与匹配，便于按“某个群的消息都不建待办”这类规则生效。
    /// </summary>
    public static TodoRule? Match(Message? message, IReadOnlyList<TodoRule>? rules) =>
        Match(message?.Content, message?.ChatName, rules);

    /// <summary>按正文与群名匹配规则；内容为空时仍可用群名命中。</summary>
    public static TodoRule? Match(
        string? content,
        string? chatName,
        IReadOnlyList<TodoRule>? rules)
    {
        if (rules is null || rules.Count == 0)
        {
            return null;
        }

        var body = content ?? string.Empty;
        var chat = chatName ?? string.Empty;
        foreach (var rule in rules)
        {
            if (!rule.IsEnabled)
            {
                continue;
            }

            foreach (var keyword in Tokenize(rule.Text))
            {
                if (body.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || chat.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return rule;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 读取当前生效的关键词集合，供系统提示词说明用户已排除的内容。
    /// 同一关键词在多条规则中重复出现时只保留一次。
    /// </summary>
    public static IReadOnlyList<string> ActiveKeywords(IReadOnlyList<TodoRule>? rules) =>
        rules is null
            ? Array.Empty<string>()
            : rules
                .Where(rule => rule.IsEnabled)
                .SelectMany(rule => Tokenize(rule.Text))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    /// <summary>归一化用户输入：去除首尾空白并压缩内部连续空白，空文本按非法输入拒绝。</summary>
    public static string Normalize(string? text)
    {
        var normalized = string.Join(
            ' ',
            (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
        {
            throw new ArgumentException("规则内容不能为空。", nameof(text));
        }

        if (normalized.Length > MaxRuleTextLength)
        {
            throw new ArgumentException($"规则内容不能超过 {MaxRuleTextLength} 个字符。", nameof(text));
        }

        if (Tokenize(normalized).Count == 0)
        {
            throw new ArgumentException($"规则至少包含一个不少于 {MinKeywordLength} 个字符的关键词。", nameof(text));
        }

        return normalized;
    }
}
