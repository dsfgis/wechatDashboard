using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WechatDashboard.KeyProbe;

Console.OutputEncoding = Encoding.UTF8;

if (!KeyProbeOptions.TryParse(args, out var options, out var parseError))
{
    WriteStatus(new { ok = false, error_code = "invalid_arguments", message = parseError });
    return 2;
}

if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
{
    WriteStatus(new { ok = false, error_code = "unsupported_process", message = "KeyProbe requires a 64-bit Windows process." });
    return 3;
}

var keyBytes = Array.Empty<byte>();
try
{
    var dllDirectory = Path.GetDirectoryName(options!.DllPath);
    if (!string.IsNullOrWhiteSpace(dllDirectory))
    {
        Environment.CurrentDirectory = dllDirectory;
    }

    using var native = new WxKeyNativeLibrary(options.DllPath);
    native.Initialize(options.ProcessId);

    using var timeoutCts = new CancellationTokenSource(options.Timeout);
    while (true)
    {
        timeoutCts.Token.ThrowIfCancellationRequested();
        native.DrainStatusMessages();
        if (native.TryPollKey(out var keyText))
        {
            try
            {
                if (!Regex.IsMatch(keyText, "^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant))
                {
                    throw new InvalidOperationException("Native helper returned a key with an invalid format.");
                }

                keyBytes = Convert.FromHexString(keyText);
            }
            finally
            {
                keyText = "";
            }

            break;
        }

        await Task.Delay(100, timeoutCts.Token);
    }

    await using (var pipe = new NamedPipeClientStream(
                     ".",
                     options.PipeName,
                     PipeDirection.Out,
                     PipeOptions.Asynchronous))
    {
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.ConnectAsync(connectCts.Token);
        await pipe.WriteAsync(keyBytes, connectCts.Token);
        await pipe.FlushAsync(connectCts.Token);
    }

    WriteStatus(new
    {
        ok = true,
        provider = "native-hook",
        version = "1",
        key_delivered = true
    });
    return 0;
}
catch (OperationCanceledException)
{
    WriteStatus(new { ok = false, error_code = "timeout", message = "Timed out while waiting for the database key." });
    return 4;
}
catch (Exception exception)
{
    WriteStatus(new
    {
        ok = false,
        error_code = "key_probe_failed",
        message = Sanitize(exception.Message)
    });
    return 5;
}
finally
{
    if (keyBytes.Length > 0)
    {
        CryptographicOperations.ZeroMemory(keyBytes);
    }
}

static void WriteStatus(object value)
{
    Console.Out.WriteLine(JsonSerializer.Serialize(value));
}

static string Sanitize(string value)
{
    var redacted = Regex.Replace(
        value ?? "",
        "(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])",
        "<REDACTED_KEY>",
        RegexOptions.CultureInvariant);
    return redacted.Length <= 500 ? redacted : redacted[..500];
}
