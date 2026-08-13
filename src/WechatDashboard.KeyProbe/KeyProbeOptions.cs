using System.Globalization;
using System.Text.RegularExpressions;

namespace WechatDashboard.KeyProbe;

/// <summary>KeyProbe 仅接受非敏感参数；密钥通过命名管道返回。</summary>
public sealed record KeyProbeOptions(
    int ProcessId,
    string PipeName,
    string DllPath,
    TimeSpan Timeout)
{
    private static readonly Regex PipeNamePattern = new(
        "^[A-Za-z0-9_.-]{1,200}$",
        RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AllowedArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        "--pid",
        "--pipe-name",
        "--dll-path",
        "--timeout-seconds"
    };

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out KeyProbeOptions? options,
        out string error)
    {
        options = null;
        error = "";
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < arguments.Count; index += 2)
        {
            if (index + 1 >= arguments.Count || !arguments[index].StartsWith("--", StringComparison.Ordinal))
            {
                error = "Arguments must use --name value pairs.";
                return false;
            }

            if (!AllowedArguments.Contains(arguments[index]))
            {
                error = $"Unsupported argument: {arguments[index]}";
                return false;
            }

            if (!values.TryAdd(arguments[index], arguments[index + 1]))
            {
                error = $"Duplicate argument: {arguments[index]}";
                return false;
            }
        }

        if (!values.TryGetValue("--pid", out var processIdText) ||
            !int.TryParse(processIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) ||
            processId <= 0)
        {
            error = "--pid must be a positive integer.";
            return false;
        }

        if (!values.TryGetValue("--pipe-name", out var pipeName) || !PipeNamePattern.IsMatch(pipeName))
        {
            error = "--pipe-name contains unsupported characters or length.";
            return false;
        }

        if (!values.TryGetValue("--dll-path", out var dllPath) || string.IsNullOrWhiteSpace(dllPath))
        {
            error = "--dll-path is required.";
            return false;
        }

        if (!values.TryGetValue("--timeout-seconds", out var timeoutText) ||
            !int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out var timeoutSeconds) ||
            timeoutSeconds is < 1 or > 600)
        {
            error = "--timeout-seconds must be between 1 and 600.";
            return false;
        }

        string normalizedDllPath;
        try
        {
            normalizedDllPath = Path.GetFullPath(dllPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "--dll-path is invalid.";
            return false;
        }

        options = new KeyProbeOptions(
            processId,
            pipeName,
            normalizedDllPath,
            TimeSpan.FromSeconds(timeoutSeconds));
        return true;
    }
}
