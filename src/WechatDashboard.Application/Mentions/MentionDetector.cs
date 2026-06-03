namespace WechatDashboard.Application.Mentions;

public sealed class MentionDetector
{
    private readonly string[] _aliases;

    public MentionDetector(IEnumerable<string> aliases)
    {
        _aliases = aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool IsMentioned(string content, bool hasWechatMentionHint = false)
    {
        if (hasWechatMentionHint)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        if (content.Contains("@我", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("@你", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _aliases.Any(alias => content.Contains("@" + alias, StringComparison.OrdinalIgnoreCase));
    }
}
