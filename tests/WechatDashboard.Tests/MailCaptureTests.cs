using System.IO;
using MimeKit;
using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Common;
using WechatDashboard.Application.Todos;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;
using WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 石化邮采集链路的回归测试：适配器字段映射、游标推进、主题清洗、待办标题来源与凭据密文存储。
/// 全部使用假读取器与假保护器，不触碰真实邮件服务器。
/// </summary>
internal static class MailCaptureTests
{
    private static readonly DateTimeOffset SampleSentAt =
        new(2026, 10, 30, 9, 15, 0, TimeSpan.FromHours(8));

    // CHECK-070 适配器把一个 MailEnvelope 映射为统一消息，并推进 uidValidity:nextUid 游标。
    internal static Task TestSinopecMailAdapterMapsEnvelopeAndCursorAsync()
    {
        var envelope = new MailEnvelope(
            Uid: 17,
            MessageId: "<ABC123@sinopec.com>",
            Subject: "Re: Fw: 关于银河麒麟V10适配的确认",
            FromDisplayName: "赵江龙",
            FromAddress: "zhaojianglong@sinopec.com",
            SentAt: SampleSentAt,
            Body: "操作系统：银河麒麟V10\nCPU架构：ARM 海思鲲鹏920\n规格：8C16G500G",
            AttachmentCount: 2,
            IsUnread: true);

        var reader = new FakeMailReader(new MailFetchResult([envelope], 4321, 18, CursorReset: false));
        var adapter = new SinopecMailCaptureAdapter(
            reader,
            new SinopecMailOptions { Account = "shaofeng.dai", MaxBodyLength = 4000 });

        return RunAsync();

        async Task RunAsync()
        {
            var batch = await adapter.CaptureAsync(
                new CaptureContext(new Dictionary<string, string>()),
                CancellationToken.None);

            AssertEqual(MailSourcePolicy.SinopecMail, adapter.Name, "适配器名称应使用石化邮来源标识。");
            AssertEqual(1, batch.Messages.Count, "本批应只有一封邮件。");
            AssertEqual("4321:18", batch.NextOffset, "游标应写回 uidValidity 与下一个 UID。");

            var message = batch.Messages[0];
            AssertEqual(MailSourcePolicy.SinopecMail, message.Source, "来源标识应写入 CapturedMessage。");
            AssertEqual("ABC123@sinopec.com", message.SourceMessageKey, "Message-ID 应去掉尖括号作为去重键。");
            AssertEqual("关于银河麒麟V10适配的确认", message.ChatName, "会话名应为剥离 Re/Fw 前缀后的主题。");
            AssertEqual("zhaojianglong@sinopec.com", message.ChatId, "发件人地址应作为会话 ID。");
            AssertEqual("赵江龙", message.SenderName, "发送人应使用发件人显示名。");
            AssertEqual(SampleSentAt, message.SentAt, "发送时间应取邮件的 Date 头。");
            AssertEqual(MessageType.Text, message.MessageType, "邮件统一按文本消息入库。");
            AssertTrue(message.Content.Contains("银河麒麟V10", StringComparison.Ordinal), "正文应保留原文。");
            AssertTrue(message.Content.Contains("[附件 2 个]", StringComparison.Ordinal), "附件应以计数占位追加。");

            // 第二次采集必须把上一轮游标作为增量条件传回读取器。
            await adapter.CaptureAsync(
                new CaptureContext(new Dictionary<string, string> { [adapter.Name] = batch.NextOffset }),
                CancellationToken.None);

            AssertEqual<long?>(4321, reader.LastRequest?.UidValidity, "增量请求应带上 UIDVALIDITY。");
            AssertEqual<uint?>(18, reader.LastRequest?.LastSeenUid, "增量请求应从下一个 UID 开始。");
            return;
        }
    }

    // CHECK-071 缺少 Message-ID 时回退 UID 去重键，正文超长按配置截断。
    internal static Task TestSinopecMailAdapterFallbacksAsync()
    {
        var envelope = new MailEnvelope(
            Uid: 5,
            MessageId: "   ",
            Subject: string.Empty,
            FromDisplayName: string.Empty,
            FromAddress: string.Empty,
            SentAt: SampleSentAt,
            Body: new string('长', 120),
            AttachmentCount: 0,
            IsUnread: false);

        var reader = new FakeMailReader(new MailFetchResult([envelope], 99, 6, CursorReset: true));
        var adapter = new SinopecMailCaptureAdapter(
            reader,
            new SinopecMailOptions { MaxBodyLength = 60 });

        return RunAsync();

        async Task RunAsync()
        {
            var batch = await adapter.CaptureAsync(
                new CaptureContext(new Dictionary<string, string>()),
                CancellationToken.None);
            var message = batch.Messages[0];

            AssertEqual("uid:5", message.SourceMessageKey, "缺少 Message-ID 时应回退到 UID 去重键。");
            AssertEqual("(无主题)", message.ChatName, "主题为空时应使用占位标题。");
            AssertEqual("unknown-sender", message.ChatId, "发件人缺失时应使用稳定占位值。");
            AssertTrue(message.Content.Contains("正文已截断", StringComparison.Ordinal), "超长正文应被截断并标注。");
            AssertTrue(batch.NextOffset.StartsWith("99:", StringComparison.Ordinal), "游标应使用新的 UIDVALIDITY。");
            return;
        }
    }

    // CHECK-072 主题前缀剥离可叠加，并按长度上限截断。
    internal static Task TestMailSubjectCleaningAsync()
    {
        AssertEqual("关于方案", MailSourcePolicy.CleanSubject("Re: Fw: 关于方案"), "应剥离叠加的 Re/Fw 前缀。");
        AssertEqual("测试通知", MailSourcePolicy.CleanSubject("答复：RE：测试通知"), "应同时处理全角冒号前缀。");
        AssertEqual("测试", MailSourcePolicy.CleanSubject("转发：测试"), "单独的“转发：”前缀应被剥离。");
        AssertEqual(string.Empty, MailSourcePolicy.CleanSubject("   "), "纯空白主题应返回空字符串。");

        var longSubject = new string('标', 120);
        var cleaned = MailSourcePolicy.CleanSubject(longSubject);
        AssertEqual(MailSourcePolicy.SubjectMaxLength, cleaned.Length, "超长主题应按上限截断。");
        AssertTrue(cleaned.EndsWith('…'), "超长主题截断后应带省略号。");
        return Task.CompletedTask;
    }

    // CHECK-073 邮件待办用邮件标题，聊天消息仍从正文提炼短标题。
    internal static Task TestTodoTitleSourceAsync()
    {
        var now = SampleSentAt;
        var mail = new Message(
            Id: 1,
            Source: MailSourcePolicy.SinopecMail,
            SourceMessageKey: "k1",
            ChatSessionId: 1,
            ChatName: "关于银河麒麟V10适配的确认",
            SenderName: "赵江龙",
            Content: "操作系统：银河麒麟V10 CPU架构：ARM 规格：8C16G500G",
            SentAt: now,
            CapturedAt: now,
            MessageType: MessageType.Text,
            IsMentionMe: false);

        var mailDraft = TodoFactory.CreateFromMessage(mail, null, PriorityLevel.P2, now);
        AssertEqual("关于银河麒麟V10适配的确认", mailDraft.Todo.Title, "邮件待办标题应直接使用邮件主题。");

        var chat = mail with
        {
            Source = "WeChat",
            ChatName = "智能管线-产品孵化",
            Content = "@戴少峰 请确认域名配置。另外注意证书有效期。"
        };
        var chatDraft = TodoFactory.CreateFromMessage(chat, null, PriorityLevel.P2, now);
        AssertEqual("@戴少峰 请确认域名配置", chatDraft.Todo.Title, "聊天待办标题应保留开头提及并截到首句。");

        var longChat = chat with { Content = new string('文', 80) };
        var longDraft = TodoFactory.CreateFromMessage(longChat, null, PriorityLevel.P2, now);
        AssertEqual(TodoFactory.TitleMaxLength, longDraft.Todo.Title.Length, "超长聊天标题应按上限截断。");
        AssertTrue(longDraft.Todo.Title.EndsWith('…'), "超长聊天标题应带省略号。");
        return Task.CompletedTask;
    }

    // CHECK-074 IMAP 凭据经保护器加密落盘，可往返读取，损坏文件按未配置处理。
    internal static Task TestProtectedImapCredentialStoreAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "protected-imap-credential.bin");

        try
        {
            var store = new ProtectedImapCredentialStore(path, new FakeCredentialProtector());
            AssertFalse(store.Exists, "初始状态不应存在凭据文件。");
            AssertTrue(store.Load() is null, "无文件时应返回 null。");

            store.Save(new ImapCredential("shaofeng.dai", "p@ss$word"));
            AssertTrue(store.Exists, "保存后凭据文件应存在。");

            var loaded = store.Load();
            AssertNotNull(loaded, "应能读回凭据。");
            AssertEqual("shaofeng.dai", loaded!.Account, "账号应往返一致。");
            AssertEqual("p@ss$word", loaded.Password, "密码应往返一致。");

            // 文件内容不得包含明文密码。
            var raw = File.ReadAllText(path, System.Text.Encoding.UTF8);
            AssertFalse(raw.Contains("p@ss", StringComparison.Ordinal), "密文文件中不得出现明文密码。");

            File.WriteAllBytes(path, [1, 2, 3, 4]);
            AssertTrue(store.Load() is null, "损坏文件应回退为未配置。");

            store.Clear();
            AssertFalse(store.Exists, "清除后凭据文件应被删除。");
            return Task.CompletedTask;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    // CHECK-075 mbox 增量读取：首次锚定末尾、追加后只读新增、中文主题正确解码。
    internal static Task TestMboxIncrementalReadAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "INBOX");

        try
        {
            File.WriteAllBytes(path, BuildMboxEntry(
                "<first@pcitc.com>",
                "关于银河麒麟V10适配的确认",
                "操作系统：银河麒麟V10\nCPU架构：ARM 海思鲲鹏920",
                SampleSentAt));
            File.WriteAllBytes(path, File.ReadAllBytes(path).Concat(BuildMboxEntry(
                "<second@pcitc.com>",
                "Re: 本周进度同步",
                "本周完成接口联调。",
                SampleSentAt.AddHours(1))).ToArray());

            var reader = new MboxMailArchiveReader(path);
            var lengthAfterTwo = reader.ReadLength();

            // 首次运行只采新增：游标锚定在文件末尾，因此读到 0 封。
            var adapter = new MboxMailCaptureAdapter(reader, MailSourcePolicy.SinopecMail);
            var firstBatch = adapter.CaptureAsync(
                new CaptureContext(new Dictionary<string, string>()),
                CancellationToken.None).GetAwaiter().GetResult();

            AssertEqual(0, firstBatch.Messages.Count, "首次运行不应回溯已有邮件。");
            AssertEqual("v1:" + lengthAfterTwo, firstBatch.NextOffset, "首次运行应把游标锚定在文件末尾。");

            // 追加第三封后，增量读取只能得到这一封。
            File.AppendAllText(path, string.Empty);
            var third = BuildMboxEntry(
                "<third@pcitc.com>",
                "Re: Fw: 请确认域名配置",
                "请确认生产域名解析是否已经生效。",
                SampleSentAt.AddHours(2));
            using (var appender = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                appender.Write(third, 0, third.Length);
            }

            var secondBatch = adapter.CaptureAsync(
                new CaptureContext(new Dictionary<string, string> { [adapter.Name] = firstBatch.NextOffset }),
                CancellationToken.None).GetAwaiter().GetResult();

            AssertEqual(1, secondBatch.Messages.Count, "增量读取应只返回新增的那一封。");
            var message = secondBatch.Messages[0];
            AssertEqual(MailSourcePolicy.SinopecMail, message.Source, "来源应写成石化邮，便于管线按邮件处理。");
            AssertEqual("third@pcitc.com", message.SourceMessageKey, "去重键应为去掉尖括号的 Message-ID。");
            AssertEqual("请确认域名配置", message.ChatName, "主题应剥离叠加的 Re/Fw 前缀。");
            AssertEqual("赵江龙", message.SenderName, "发送人应取发件人显示名。");
            AssertTrue(message.Content.Contains("生产域名解析", StringComparison.Ordinal), "正文应被正确解析。");

            // 再读一次不应重复返回同一封邮件。
            var thirdBatch = adapter.CaptureAsync(
                new CaptureContext(new Dictionary<string, string> { [adapter.Name] = secondBatch.NextOffset }),
                CancellationToken.None).GetAwaiter().GetResult();
            AssertEqual(0, thirdBatch.Messages.Count, "游标推进后不应重复返回已读邮件。");
            return Task.CompletedTask;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    // CHECK-076 游标损坏或文件被压缩后回退到从头读取，而不是永久漏读。
    internal static Task TestMboxCursorAndResetAsync()
    {
        AssertTrue(MboxCursor.Parse(null) is null, "空游标应按首次运行处理。");
        AssertTrue(MboxCursor.Parse("坏数据") is null, "非法游标应回退为首次运行。");
        AssertEqual<long?>(128, MboxCursor.Parse("v1:128"), "游标应解析出字节偏移。");
        AssertEqual("v1:256", MboxCursor.Format(256), "游标应带版本前缀写出。");

        var directory = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "INBOX");
        try
        {
            File.WriteAllBytes(path, BuildMboxEntry(
                "<only@pcitc.com>",
                "单封邮件",
                "正文",
                SampleSentAt));

            var reader = new MboxMailArchiveReader(path);
            // 游标大于文件长度 = 文件被客户端压缩重写过，应重置并从头读。
            var result = reader.ReadNew(offset: 999_999_999, maxMessages: 10);
            AssertTrue(result.Reset, "游标超出文件长度时应标记为重置。");
            AssertEqual(1, result.Messages.Count, "重置后应从文件头读到那一封邮件。");
            return Task.CompletedTask;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    /// <summary>构造一条标准 mbox 记录：From_ 分隔行 + MIME 消息 + 结尾空行。</summary>
    private static byte[] BuildMboxEntry(string messageId, string subject, string body, DateTimeOffset date)
    {
        var message = new MimeMessage();
        message.MessageId = messageId;
        message.Subject = subject;
        message.From.Add(new MailboxAddress("赵江龙", "zhaojianglong@pcitc.com"));
        message.To.Add(new MailboxAddress("戴少峰", "shaofeng.dai@pcitc.com"));
        message.Date = date;
        message.Body = new TextPart("plain") { Text = body };

        using var buffer = new MemoryStream();
        buffer.Write(System.Text.Encoding.ASCII.GetBytes("From zhaojianglong@pcitc.com Thu Oct 30 09:15:00 2026\n"));
        message.WriteTo(new FormatOptions { NewLineFormat = NewLineFormat.Unix }, buffer);
        buffer.Write(System.Text.Encoding.ASCII.GetBytes("\n"));
        return buffer.ToArray();
    }

    // CHECK-077 集成验证：真实客户端缓存可被定位并按 mbox 解析出末尾邮件。
    // 未安装石化邮客户端时直接跳过，不构成环境失败。
    internal static Task TestRealClientInboxAsync()
    {
        var inbox = SinopecMailArchiveLocator.FindInbox();
        if (inbox is null)
        {
            return Task.CompletedTask;
        }

        var length = new FileInfo(inbox).Length;
        AssertTrue(length > 0, "定位到的 INBOX 应有内容。");

        var offset = FindLastFromSeparator(inbox, length, window: 512 * 1024);
        AssertTrue(offset >= 0, "应能在文件尾部 512 KB 内找到 mbox 分隔行。");

        var reader = new MboxMailArchiveReader(inbox);
        var result = reader.ReadNew(offset, maxMessages: 5);
        AssertTrue(result.Messages.Count >= 1, "应能从真实缓存尾部解析出至少一封邮件。");

        var newest = result.Messages[^1];
        AssertTrue(!string.IsNullOrWhiteSpace(newest.MessageId), "真实邮件应带有 Message-ID。");
        AssertTrue(!string.IsNullOrWhiteSpace(newest.FromAddress), "真实邮件应带有发件人地址。");
        AssertTrue(newest.SentAt.Year >= 2000, "发送时间应能解析为合理日期。");
        AssertTrue(result.NextOffset > offset, "解析后游标应向前推进。");

        // 真实邮件要能走通“主题→待办标题”的链路：标题取清洗后的主题，不能带 Re:/回复: 前缀。
        var mapped = MailMessageMapper.Map(MailSourcePolicy.SinopecMail, newest, maxBodyLength: 4000);
        AssertEqual(MailSourcePolicy.SinopecMail, mapped.Source, "真实邮件的来源标识应为石化邮。");
        AssertTrue(mapped.ChatName.Length > 0, "真实邮件应能得出非空标题。");
        AssertTrue(mapped.ChatName.Length <= MailSourcePolicy.SubjectMaxLength + 1, "标题长度应受主题上限约束。");
        AssertTrue(
            !mapped.ChatName.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            && !mapped.ChatName.StartsWith("回复", StringComparison.Ordinal),
            $"真实邮件标题应剥离回复前缀，实际为“{mapped.ChatName}”。");
        AssertEqual(
            newest.MessageId.Trim().Trim('<', '>'),
            mapped.SourceMessageKey,
            "去重键应取 Message-ID，跨源与文件重写都稳定。");
        AssertTrue(mapped.Content.Length > 0, "真实邮件应解析出非空正文。");

        return Task.CompletedTask;
    }

    /// <summary>从文件尾部窗口内反向查找最后一个 mbox 分隔行的起始字节。</summary>
    private static long FindLastFromSeparator(string path, long length, int window)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var start = Math.Max(0, length - window);
        var buffer = new byte[length - start];
        stream.Seek(start, SeekOrigin.Begin);
        stream.ReadExactly(buffer);

        var marker = "\nFrom "u8.ToArray();
        for (var index = buffer.Length - marker.Length; index >= 0; index--)
        {
            if (buffer.AsSpan(index, marker.Length).SequenceEqual(marker))
            {
                return start + index + 1;
            }
        }

        return -1;
    }

    private sealed class FakeMailReader : IMailInboxReader    {
        private readonly MailFetchResult _result;

        public FakeMailReader(MailFetchResult result) => _result = result;

        public MailFetchRequest? LastRequest { get; private set; }

        public Task<MailFetchResult> FetchAsync(MailFetchRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_result);
        }
    }

    /// <summary>可逆的假保护器：不引入 DPAPI 依赖，仅验证密文格式与往返。</summary>
    private sealed class FakeCredentialProtector : IImapCredentialProtector
    {
        public byte[] Protect(byte[] plaintext)
            => plaintext.Select(value => (byte)(value ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] protectedBytes)
            => protectedBytes.Select(value => (byte)(value ^ 0x5A)).ToArray();
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertFalse(bool condition, string message) => AssertTrue(!condition, message);

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} 期望 {expected}，实际 {actual}。");
        }
    }

    private static void AssertNotNull<T>(T? value, string message) where T : class
    {
        if (value is null) throw new InvalidOperationException(message);
    }
}
