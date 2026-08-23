using System.Text;

namespace WechatDashboard.Application.Todos;

public static class TodoGroupNamePolicy
{
    public static string NormalizeDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("分组名称不能为空。", nameof(name));
        }

        return name.Trim().Normalize(NormalizationForm.FormC);
    }

    public static string NormalizeKey(string displayName) =>
        NormalizeDisplayName(displayName).ToUpperInvariant();
}
