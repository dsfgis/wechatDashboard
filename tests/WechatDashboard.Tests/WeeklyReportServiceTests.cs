using System.IO;
using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Weekly;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;
using WechatDashboard.Infrastructure.Persistence;

namespace WechatDashboard.Tests;

/// <summary>
/// 项目周报集成测试：使用真实SQLite仓储验证完整取数、严格归集、模型校验与本地回退。
/// 模型使用可控替身，不访问网络，也不会触发任何邮件能力。
/// </summary>
public static class WeeklyReportServiceTests
{
    /// <summary>超过一千条窗口消息时仍要保留最早成员周报，并只采用可靠私聊身份。</summary>
    public static async Task CollectorUsesStrictIdentityAndCompleteWindowAsync()
    {
        var databasePath = await CreateDatabaseAsync();
        var repository = new SqliteMessageRepository(databasePath);
        var from = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(8));
        var to = from.AddDays(7);
        const long projectId = 11;

        // 有效成员消息放在窗口最早位置；若仓储只取最新1000条，这两条会首先丢失。
        await repository.SaveAsync(CreateMessage(
            "本周完成接口联调",
            from.AddHours(1),
            "wxid_shen",
            ChatType.Private,
            Direction.Incoming), CancellationToken.None);
        await repository.SaveAsync(CreateMessage(
            "本周完成数据核对",
            from.AddHours(2),
            "shen@pcitc.com",
            ChatType.Private,
            Direction.Incoming,
            source: "SinopecMail"), CancellationToken.None);

        // 同一个wxid也不能弥补Unknown、群聊或发出方向，这些消息均不得归到成员。
        await repository.SaveAsync(CreateMessage(
            "Unknown路由不能采用",
            from.AddHours(3),
            "wxid_shen"), CancellationToken.None);
        await repository.SaveAsync(CreateMessage(
            "群聊不能采用",
            from.AddHours(3).AddMinutes(1),
            "wxid_shen",
            ChatType.Group,
            Direction.Incoming), CancellationToken.None);
        await repository.SaveAsync(CreateMessage(
            "本人发出不能采用",
            from.AddHours(3).AddMinutes(2),
            "wxid_shen",
            ChatType.Private,
            Direction.Outgoing), CancellationToken.None);

        // 项目消息走独立关键词通道，群聊项目进展仍可总结，但必须隔离其他项目。
        var projectMessage = await repository.SaveAsync(CreateMessage(
            "管线提升本周完成验收安排",
            from.AddHours(4),
            "other-user",
            ChatType.Group,
            Direction.Incoming,
            sender: "项目群成员"), CancellationToken.None);
        await repository.SaveAsync(CreateMessage(
            "其他项目的内容不能混入",
            from.AddHours(4).AddMinutes(1),
            "other-user",
            ChatType.Group,
            Direction.Incoming,
            sender: "其他项目成员"), CancellationToken.None);

        // 构造1000条更晚的无关消息，验证周报查询不是“最新1000条后再过滤”。
        for (var index = 0; index < 1000; index++)
        {
            await repository.SaveAsync(CreateMessage(
                $"无关消息{index}",
                from.AddHours(5).AddSeconds(index),
                $"other-{index}",
                ChatType.Group,
                Direction.Incoming,
                sender: "同事"), CancellationToken.None);
        }

        var members = new[]
        {
            new ProjectMember(
                1,
                projectId,
                "沈佳奇",
                null,
                "shen@pcitc.com",
                true,
                from,
                "wxid_shen")
        };
        var keywords = new[]
        {
            new FollowedProjectKeyword(1, projectId, "智能管线", "管线提升", from),
            new FollowedProjectKeyword(2, 22, "其他项目", "其他项目", from)
        };
        var material = await new WeeklyReportCollector(repository).CollectAsync(
            projectId,
            "智能管线",
            members,
            keywords,
            from,
            to,
            cancellationToken: CancellationToken.None);

        var contribution = material.Contributions.Single();
        AssertEqual(2, contribution.Messages.Count, "Only verified private incoming member identities may be adopted.");
        AssertTrue(contribution.Messages.Any(message => message.Source == "WeChat"), "The wxid private message should be adopted.");
        AssertTrue(contribution.Messages.Any(message => message.Source == "SinopecMail"), "The direct member mail should be adopted.");
        AssertEqual(1, material.ProjectMessages.Count, "Project messages must be isolated by the matched project ID.");
        AssertEqual(projectMessage.Id, material.ProjectMessages[0].Id, "The current project group message should remain available.");
        AssertTrue(material.Diagnostics.UnknownRouteExcluded >= 1, "Unknown member-like routes should be diagnosed.");
        AssertTrue(material.Diagnostics.NonPrivateExcluded >= 1, "Group or outgoing member-like routes should be diagnosed.");
    }

    /// <summary>模型来源和责任人必须真实；失败时回退且引用内容不能记成回复者成果。</summary>
    public static async Task ServiceValidatesModelAndFallsBackAsync()
    {
        var databasePath = await CreateDatabaseAsync();
        var messageRepository = new SqliteMessageRepository(databasePath);
        var todoRepository = new SqliteTodoRepository(databasePath);
        var from = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(8));
        var to = from.AddDays(7);
        var message = await messageRepository.SaveAsync(CreateMessage(
            "本周完成接口联调\n引用消息：张彬已完成数据库迁移",
            from.AddHours(1),
            "wxid_shen",
            ChatType.Private,
            Direction.Incoming), CancellationToken.None);
        var members = new[]
        {
            new ProjectMember(1, 11, "沈佳奇", null, null, true, from, "wxid_shen")
        };

        // 正常模型结果应原样通过，并保留可空进度和工时。
        var validModel = new DelegateWeeklyReportModel((request, _) => new WeeklyReportDraft(
            request.Metadata,
            "本周完成接口联调。",
            new[]
            {
                new WeeklyWorkItem(
                    "完成接口联调",
                    new[] { "沈佳奇" },
                    null,
                    null,
                    null,
                    null,
                    new[] { message.Id },
                    Array.Empty<long>())
            },
            Array.Empty<WeeklyIssue>(),
            Array.Empty<WeeklyRisk>(),
            string.Empty,
            Array.Empty<WeeklyPlan>()));
        var service = new WeeklyReportService(
            new WeeklyReportCollector(messageRepository),
            todoRepository,
            validModel);
        var valid = await service.GenerateAsync(11, "智能管线", members, Array.Empty<FollowedProjectKeyword>(), from, to, to);
        AssertTrue(valid.FromModel, "A valid sourced model draft should be accepted.");
        AssertEqual(1, valid.Draft.WorkItems.Count, "The valid model work item should be preserved.");

        // 虚构来源ID必须触发整次回退，不能把无依据条目导出到正式模板。
        var invalidSourceModel = new DelegateWeeklyReportModel((request, _) => new WeeklyReportDraft(
            request.Metadata,
            "无效来源",
            new[]
            {
                new WeeklyWorkItem("虚构工作", Array.Empty<string>(), null, null, null, null, new[] { 999999L }, Array.Empty<long>())
            },
            Array.Empty<WeeklyIssue>(),
            Array.Empty<WeeklyRisk>(),
            string.Empty,
            Array.Empty<WeeklyPlan>()));
        var invalidSource = await new WeeklyReportService(
            new WeeklyReportCollector(messageRepository),
            todoRepository,
            invalidSourceModel).GenerateAsync(11, "智能管线", members, Array.Empty<FollowedProjectKeyword>(), from, to, to);
        AssertFalse(invalidSource.FromModel, "An unknown source ID must force the local fallback.");

        // 未配置责任人同样不能通过校验。
        var invalidOwnerModel = new DelegateWeeklyReportModel((request, _) => new WeeklyReportDraft(
            request.Metadata,
            "无效责任人",
            new[]
            {
                new WeeklyWorkItem("接口联调", new[] { "陌生人" }, null, null, null, null, new[] { message.Id }, Array.Empty<long>())
            },
            Array.Empty<WeeklyIssue>(),
            Array.Empty<WeeklyRisk>(),
            string.Empty,
            Array.Empty<WeeklyPlan>()));
        var invalidOwner = await new WeeklyReportService(
            new WeeklyReportCollector(messageRepository),
            todoRepository,
            invalidOwnerModel).GenerateAsync(11, "智能管线", members, Array.Empty<FollowedProjectKeyword>(), from, to, to);
        AssertFalse(invalidOwner.FromModel, "An unconfigured owner must force the local fallback.");
        AssertTrue(invalidOwner.Draft.WorkItems[0].Description.Contains("接口联调"), "Fallback should retain current message content.");
        AssertFalse(invalidOwner.Draft.WorkItems[0].Description.Contains("数据库迁移"), "Quoted work must not be attributed to the replier.");

        // 用户主动取消必须继续传播，不能被模型失败回退吞掉。
        using var cancellation = new CancellationTokenSource();
        var cancellingModel = new DelegateWeeklyReportModel((_, token) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(token);
        });
        var cancelled = false;
        try
        {
            await new WeeklyReportService(
                new WeeklyReportCollector(messageRepository),
                todoRepository,
                cancellingModel).GenerateAsync(
                    11,
                    "智能管线",
                    members,
                    Array.Empty<FollowedProjectKeyword>(),
                    from,
                    to,
                    to,
                    cancellationToken: cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        AssertTrue(cancelled, "User cancellation must propagate instead of producing a fallback report.");
    }

    /// <summary>超过单批字符上限时按成员分批，合并后来源仍通过全量素材校验。</summary>
    public static async Task ServiceBatchesLongMemberContributionsAsync()
    {
        var databasePath = await CreateDatabaseAsync();
        var messageRepository = new SqliteMessageRepository(databasePath);
        var todoRepository = new SqliteTodoRepository(databasePath);
        var from = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(8));
        var to = from.AddDays(7);
        var first = await messageRepository.SaveAsync(CreateMessage(
            new string('甲', 16001),
            from.AddHours(1),
            "wxid_one",
            ChatType.Private,
            Direction.Incoming), CancellationToken.None);
        var second = await messageRepository.SaveAsync(CreateMessage(
            new string('乙', 16001),
            from.AddHours(2),
            "wxid_two",
            ChatType.Private,
            Direction.Incoming), CancellationToken.None);
        var members = new[]
        {
            new ProjectMember(1, 11, "成员一", null, null, true, from, "wxid_one"),
            new ProjectMember(2, 11, "成员二", null, null, true, from, "wxid_two")
        };

        // 每个批次只返回当前输入支持的一项，便于验证分批次数及最终无遗漏合并。
        var model = new DelegateWeeklyReportModel((request, _) =>
        {
            var contribution = request.Material.Contributions.Single();
            var source = contribution.Messages.Single().Id;
            return new WeeklyReportDraft(
                request.Metadata,
                contribution.Member.DisplayName,
                new[]
                {
                    new WeeklyWorkItem(
                        contribution.Member.DisplayName,
                        new[] { contribution.Member.DisplayName },
                        null,
                        null,
                        null,
                        null,
                        new[] { source },
                        Array.Empty<long>())
                },
                Array.Empty<WeeklyIssue>(),
                Array.Empty<WeeklyRisk>(),
                string.Empty,
                Array.Empty<WeeklyPlan>());
        });
        var outcome = await new WeeklyReportService(
            new WeeklyReportCollector(messageRepository),
            todoRepository,
            model).GenerateAsync(11, "智能管线", members, Array.Empty<FollowedProjectKeyword>(), from, to, to);

        AssertEqual(2, model.CallCount, "Two oversized member contributions should be sent in separate batches.");
        AssertEqual(2, outcome.Draft.WorkItems.Count, "Merging batches must preserve every member work item.");
        AssertTrue(outcome.Draft.WorkItems.SelectMany(item => item.SourceMessageIds).Contains(first.Id), "The first batch source should survive merging.");
        AssertTrue(outcome.Draft.WorkItems.SelectMany(item => item.SourceMessageIds).Contains(second.Id), "The second batch source should survive merging.");
    }

    /// <summary>待办周报只取当前项目的本周完成项和活动项，不带入其他项目或历史完成项。</summary>
    public static async Task TodoRepositorySelectsWeeklyScopeAsync()
    {
        var databasePath = await CreateDatabaseAsync();
        var repository = new SqliteTodoRepository(databasePath);
        var from = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(8));
        var to = from.AddDays(7);
        // ManagedProjectId具有真实外键，先创建两个项目再验证跨项目隔离。
        var projectRepository = new SqliteFollowedProjectRepository(databasePath);
        var project = await projectRepository.SaveAsync("周报项目", ProjectColorPolicy.Blue, CancellationToken.None);
        var otherProject = await projectRepository.SaveAsync("其他项目", ProjectColorPolicy.Green, CancellationToken.None);
        var projectId = project.Id;

        var completed = await repository.SaveAsync(CreateTodo("本周完成", projectId, TodoStatus.Done, from.AddDays(1), from.AddDays(1)), CancellationToken.None);
        var active = await repository.SaveAsync(CreateTodo("继续跟进", projectId, TodoStatus.Pending, from.AddDays(8), null), CancellationToken.None);
        await repository.SaveAsync(CreateTodo("历史完成", projectId, TodoStatus.Done, from.AddDays(-2), from.AddDays(-2)), CancellationToken.None);
        await repository.SaveAsync(CreateTodo("其他项目", otherProject.Id, TodoStatus.Pending, from.AddDays(2), null), CancellationToken.None);
        await repository.SaveAsync(CreateTodo("忽略事项", projectId, TodoStatus.Ignored, from.AddDays(2), null), CancellationToken.None);

        var rows = await repository.GetWeeklyReportAsync(projectId, from, to, CancellationToken.None);
        AssertEqual(2, rows.Count, "Weekly todos should include only current-project completed-in-window and active items.");
        AssertTrue(rows.Any(todo => todo.Id == completed.Id), "The in-window completed todo should be included.");
        AssertTrue(rows.Any(todo => todo.Id == active.Id), "The active follow-up todo should be included.");
    }

    /// <summary>创建独立临时数据库，避免测试之间共享消息、待办或路由状态。</summary>
    private static async Task<string> CreateDatabaseAsync()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);
        return databasePath;
    }

    /// <summary>构造可指定路由的测试消息，默认Unknown以模拟不能证明私聊的历史数据。</summary>
    private static Message CreateMessage(
        string content,
        DateTimeOffset sentAt,
        string? senderExternalId = null,
        ChatType chatType = ChatType.Unknown,
        Direction direction = Direction.Unknown,
        string sender = "沈佳奇",
        string source = "WeChat") => new(
            0,
            source,
            $"weekly-test-{Guid.NewGuid():N}",
            1,
            chatType == ChatType.Group ? "项目群" : "私聊",
            sender,
            content,
            sentAt,
            sentAt,
            MessageType.Text,
            false,
            senderExternalId,
            chatType,
            direction);

    /// <summary>构造项目待办；ManagedProjectId是周报项目隔离的唯一依据。</summary>
    private static TodoItem CreateTodo(
        string title,
        long projectId,
        TodoStatus status,
        DateTimeOffset dueAt,
        DateTimeOffset? completedAt) => new(
            0,
            null,
            null,
            title,
            null,
            status,
            PriorityLevel.P2,
            dueAt,
            dueAt.AddDays(-1),
            dueAt.AddDays(-1),
            completedAt,
            false)
        {
            ManagedProjectId = projectId
        };

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertFalse(bool condition, string message) => AssertTrue(!condition, message);

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; Actual: {actual}");
        }
    }

    /// <summary>可控周报模型替身，记录调用次数并返回测试指定的结构化结果。</summary>
    private sealed class DelegateWeeklyReportModel : IWeeklyReportModel
    {
        private readonly Func<WeeklyReportModelRequest, CancellationToken, WeeklyReportDraft> _handler;

        public DelegateWeeklyReportModel(Func<WeeklyReportModelRequest, CancellationToken, WeeklyReportDraft> handler)
        {
            _handler = handler;
        }

        public int CallCount { get; private set; }

        public Task<WeeklyReportDraft> GenerateAsync(
            WeeklyReportModelRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_handler(request, cancellationToken));
        }
    }
}
