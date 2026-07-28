using System.Text;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 解析石化通 CoreIMMessageContent 的文本正文。仅实现消息读取所需的
/// protobuf wire-format 子集，避免把石化通私有协议程序集引入本项目。
/// </summary>
public static class ShihuatongMessageContentDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// 文本消息位于外层字段 2，图文消息位于外层字段 3；两者的正文均为内层字段 1。
    /// 解析失败返回 null，由调用方使用 search_text/hint_content 兜底。
    /// </summary>
    public static string? TryDecodeText(byte[]? payload)
    {
        if (payload is null || payload.Length == 0)
        {
            return null;
        }

        var data = payload.AsSpan();
        var offset = 0;
        while (offset < data.Length && TryReadVarint(data, ref offset, out var tag))
        {
            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 0x07);
            if (wireType == 2 && TryReadBytes(data, ref offset, out var value))
            {
                if (fieldNumber is 2 or 3 && TryReadNestedText(value, out var text))
                {
                    return Normalize(text);
                }

                continue;
            }

            if (!TrySkip(data, ref offset, wireType))
            {
                return null;
            }
        }

        return TryDecodePlainUtf8(data);
    }

    private static bool TryReadNestedText(ReadOnlySpan<byte> data, out string text)
    {
        var offset = 0;
        while (offset < data.Length && TryReadVarint(data, ref offset, out var tag))
        {
            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 0x07);
            if (wireType == 2 && TryReadBytes(data, ref offset, out var value))
            {
                if (fieldNumber == 1)
                {
                    try
                    {
                        text = StrictUtf8.GetString(value);
                        return !string.IsNullOrWhiteSpace(text);
                    }
                    catch (DecoderFallbackException)
                    {
                        break;
                    }
                }

                continue;
            }

            if (!TrySkip(data, ref offset, wireType))
            {
                break;
            }
        }

        text = "";
        return false;
    }

    private static string? TryDecodePlainUtf8(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            if (value < 0x20 && value is not (byte)'\t' and not (byte)'\r' and not (byte)'\n')
            {
                return null;
            }
        }
        try
        {
            var text = StrictUtf8.GetString(data);
            return string.IsNullOrWhiteSpace(text) ? null : Normalize(text);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool TryReadBytes(ReadOnlySpan<byte> data, ref int offset, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (!TryReadVarint(data, ref offset, out var rawLength) || rawLength > int.MaxValue)
        {
            return false;
        }

        var length = (int)rawLength;
        if (length < 0 || offset > data.Length - length)
        {
            return false;
        }

        value = data.Slice(offset, length);
        offset += length;
        return true;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && offset < data.Length; shift += 7)
        {
            var current = data[offset++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0) return true;
        }
        return false;
    }

    private static bool TrySkip(ReadOnlySpan<byte> data, ref int offset, int wireType)
    {
        return wireType switch
        {
            0 => TryReadVarint(data, ref offset, out _),
            1 when offset <= data.Length - 8 => (offset += 8) >= 0,
            2 => TryReadBytes(data, ref offset, out _),
            5 when offset <= data.Length - 4 => (offset += 4) >= 0,
            _ => false
        };
    }

    private static string Normalize(string value) => value.Replace("\0", "").Trim();
}
