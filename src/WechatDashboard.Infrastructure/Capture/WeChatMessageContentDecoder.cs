using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using WechatDashboard.Domain.Enums;
using ZstdSharp;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>微信 V4 正文、zstd、群聊发送者和 XML 摘要解码。</summary>
public sealed class WeChatMessageContentDecoder
{
    public const int MaxDecompressedBytes = 8 * 1024 * 1024;

    public string DecodeContent(object? content, int contentType)
    {
        if (content is null || content is DBNull)
        {
            return "";
        }

        if (content is byte[] bytes)
        {
            if (contentType == 1 || HasZstdMagic(bytes))
            {
                try
                {
                    using var decompressor = new Decompressor();
                    return Encoding.UTF8.GetString(
                        decompressor.Unwrap(bytes, MaxDecompressedBytes));
                }
                catch
                {
                    return "";
                }
            }

            return Encoding.UTF8.GetString(bytes);
        }

        var text = Convert.ToString(content) ?? "";
        if (contentType != 1)
        {
            return text;
        }

        try
        {
            var latin1 = Encoding.Latin1.GetBytes(text);
            using var decompressor = new Decompressor();
            return Encoding.UTF8.GetString(
                decompressor.Unwrap(latin1, MaxDecompressedBytes));
        }
        catch
        {
            return text;
        }
    }

    public (string SenderName, string Content) ExtractSenderAndContent(
        string? rawContent,
        bool isGroup,
        IReadOnlyDictionary<string, string> nameMap)
    {
        var text = rawContent ?? "";
        if (!isGroup)
        {
            return ("", text);
        }

        var separator = text.IndexOf(":\n", StringComparison.Ordinal);
        if (separator < 0)
        {
            return ("", text);
        }

        var senderId = text[..separator];
        var senderName = nameMap.TryGetValue(senderId, out var resolved)
            ? resolved
            : senderId;
        return (senderName, text[(separator + 2)..]);
    }

    public string Summarize(string? text, int localType)
    {
        var cleaned = (text ?? "").Trim();
        if (cleaned.Length == 0)
        {
            return "";
        }
        if (!IsXmlMessage(cleaned))
        {
            return cleaned;
        }

        var xmlSummary = TrySummarizeXml(cleaned, localType);
        if (xmlSummary.Length > 0)
        {
            return xmlSummary;
        }

        return ToMessageType(localType) switch
        {
            MessageType.Image => "[图片]",
            MessageType.Link => "[链接]",
            MessageType.File => "[文件]",
            MessageType.System => "[系统消息]",
            _ => "[非文本消息]"
        };
    }

    public static MessageType ToMessageType(int localType)
    {
        var (baseType, subType) = SplitMessageType(localType);
        if (baseType == 1)
        {
            return MessageType.Text;
        }
        if (baseType == 3)
        {
            return MessageType.Image;
        }
        if (baseType == 49 && subType == 5)
        {
            return MessageType.Link;
        }
        if (baseType == 49 || baseType is 34 or 43 or 47 or 48 or 50)
        {
            return MessageType.File;
        }
        if (baseType is 10000 or 10002)
        {
            return MessageType.System;
        }

        return MessageType.Text;
    }

    public static (int BaseType, int SubType) SplitMessageType(int localType)
    {
        return localType is 10000 or 10002
            ? (localType, 0)
            : (localType & 0xFF, (localType >> 8) & 0xFF);
    }

    private static bool HasZstdMagic(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 &&
        bytes[0] == 0x28 &&
        bytes[1] == 0xB5 &&
        bytes[2] == 0x2F &&
        bytes[3] == 0xFD;

    private static bool IsXmlMessage(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("<?xml", StringComparison.Ordinal) ||
               trimmed.StartsWith("<msg", StringComparison.Ordinal) ||
               trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrySummarizeXml(string text, int localType)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxDecompressedBytes
            };
            using var stringReader = new StringReader(text.TrimStart());
            using var xmlReader = XmlReader.Create(stringReader, settings);
            var document = XDocument.Load(xmlReader, LoadOptions.None);
            if (document.Descendants("videomsg").Any())
            {
                return "[视频]";
            }
            if (document.Descendants("emoji").Any())
            {
                return "[表情]";
            }
            if (document.Descendants("img").Any())
            {
                return "[图片]";
            }

            var appMessage = document.Descendants("appmsg").FirstOrDefault();
            if (appMessage is not null)
            {
                var title = ChildText(appMessage, "title");
                var description = ChildText(appMessage, "des");
                var url = ChildText(appMessage, "url");
                var type = ChildText(appMessage, "type");
                if (type == "6")
                {
                    return "[文件]";
                }

                var parts = new List<string> { "[链接]" };
                if (title.Length > 0)
                {
                    parts.Add(title);
                }
                if (description.Length > 0)
                {
                    parts.Add("- " + description);
                }
                if (url.Length > 0)
                {
                    parts.Add(url);
                }

                var summary = string.Join(" ", parts);
                if (type != "57")
                {
                    return summary;
                }

                // 引用回复的当前正文仍按原规则摘要，仅补充 refermsg 中嵌入的被引用正文。
                var quotedContent = appMessage.Element("refermsg")?.Element("content")?.Value;
                return CombineWithReference(summary, quotedContent);
            }

            var location = document.Descendants("location").FirstOrDefault();
            if (location is not null)
            {
                var label = ((string?)location.Attribute("label") ?? "").Trim();
                return label.Length == 0 ? "[位置]" : "[位置] " + label;
            }

            var (baseType, subType) = SplitMessageType(localType);
            if (baseType == 3)
            {
                return "[图片]";
            }
            if (baseType == 43)
            {
                return "[视频]";
            }
            if (baseType == 47)
            {
                return "[表情]";
            }
            if (baseType == 49 && subType == 5)
            {
                return "[链接]";
            }
            if (baseType == 49)
            {
                return "[文件]";
            }

            return "[非文本消息]";
        }
        catch (XmlException)
        {
            return "";
        }
    }

    /// <summary>
    /// 合并微信引用正文，并保持与石化通相同的去重规则和文本边界。
    /// </summary>
    private static string CombineWithReference(string content, string? quotedContent)
    {
        var normalizedQuote = quotedContent?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuote)
            || content.Contains(normalizedQuote, StringComparison.Ordinal))
        {
            return content;
        }

        // 明确的引用标记会随 Content 进入消息展示、自动待办摘要和本地日期解析。
        return $"{content}\n\n引用消息：\n{normalizedQuote}";
    }

    private static string ChildText(XElement parent, string name) =>
        (parent.Element(name)?.Value ?? "").Trim();
}
