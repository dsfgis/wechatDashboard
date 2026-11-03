using System.IO;
using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Agent;
using WechatDashboard.Application.Briefing;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;
using WechatDashboard.Infrastructure.Persistence;

namespace WechatDashboard.Tests;

/// <summary>
/// 项目记忆集成测试：使用真实 SQLite 验证迁移、跨期稳定 ID、事务边界和证据保留。
/// 模型均由本地替身返回，测试不会访问网络，也不会把建议自动写入数据库。
/// </summary>
public static class ProjectMemoryTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(8));

    /// <summary>v9 必须可重复初始化，并且旧库升级不会改变任何既有迁移校验和。</summary>
    public static async Task SchemaV9UpgradesAndIsIdempotentAsync()
    {
        var databasePath = await CreateDatabaseAsync();
        var initializer = new SqliteDatabaseInitializer(databasePath);
        var originalChecksums = await ReadChecksumsAsync(databasePath);

        // 重复初始化模拟应用多次启动，v9 只能保留一条迁移记录。
        await initializer.InitializeAsync(CancellationToken.None);
        Equal(1L, await ScalarAsync<long>(
            databasePath,
            "SELECT COUNT(*) FROM schema_migrations WHERE version = 9 AND checksum = 'project-memory-briefing-tracking-v1';"),
            "Migration v9 should be recorded exactly once.");
        Equal(originalChecksums, await ReadChecksumsAsync(databasePath),
            "Existing migration checksums must remain byte-for-byte compatible.");

        // 删除 v9 对象和记录模拟 v8 旧库，再初始化应只追加项目记忆结构。
        await ExecuteAsync(databasePath, """
            DROP TABLE IF EXISTS project_memory_status_history;
            DROP TABLE IF EXISTS project_memory_sources;
            DROP TABLE IF EXISTS project_memory_items;
            DELETE FROM schema_migrations WHERE version = 9;
            """);
        await initializer.InitializeAsync(CancellationToken.None);

        Equal(3L, await ScalarAsync<long>(databasePath, """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name IN (
                'project_memory_items', 'project_memory_sources', 'project_memory_status_history');
            """), "An old database should gain all three project-memory tables.");
        Equal(originalChecksums, await ReadChecksumsAsync(databasePath),
            "Upgrading an old database must not rewrite earlier migrations.");
    }

    /// <summary>首次入库、标题归并、跨期模型关联和重复应用都必须保持同一个稳定 ID。</summary>
    public static async Task RepositoryTracksItemsAcrossBriefingsAsync()
    {
        var databasePath = await CreateDatabaseAsync();
        var project = await new SqliteFollowedProjectRepository(databasePath)
            .SaveAsync("智能管线", CancellationToken.None);
        var messages = new SqliteMessageRepository(databasePath);
        var memory = new SqliteProjectMemoryRepository(databasePath);
        var service = new ProjectMemoryService(memory, messages);
        var first = await SaveMessageAsync(messages, "管线接口联调开始", BaseTime);
        var second = await SaveMessageAsync(messages, "管线接口联调补充记录", BaseTime.AddHours(1));
        var completed = await SaveMessageAsync(messages, "管线接口联调已完成并通过验收", BaseTime.AddHours(2));

        var initialProposal = NewProposal("接口  联调", ProjectMemoryStatus.Open, first);
        var created = await service.ApplySelectedAsync(project.Id, new[] { initialProposal }, CancellationToken.None);
        Equal(1, created.Created, "The first confirmed proposal should create one item.");
        var itemId = created.ItemIds.Single();

        // 完全重复的新事项建议按标题和来源幂等归并，不增加假状态历史。
        var repeated = await service.ApplySelectedAsync(project.Id, new[] { initialProposal }, CancellationToken.None);
        Equal(1, repeated.Skipped, "Repeating an identical proposal should be skipped.");
        var details = await memory.GetDetailsAsync(project.Id, itemId, CancellationToken.None);
        Equal(1, details!.Sources.Count, "Repeated evidence must not create duplicate source rows.");
        Equal(1, details.History.Count, "A repeated state must not create fake history.");

        // 同一活跃事项即使空白排版不同，也只补充新来源，不创建第二个事项。
        var normalizedDuplicate = NewProposal("  接口 联调 ", ProjectMemoryStatus.Open, second);
        var supplemented = await service.ApplySelectedAsync(project.Id, new[] { normalizedDuplicate }, CancellationToken.None);
        Equal(1, supplemented.Updated, "A new source should update the existing active item.");
        Equal(itemId, supplemented.ItemIds.Single(), "Normalized active titles should share one stable ID.");

        // 下一期简报只能关联请求中暴露的既有 ID；完成状态有明确验收正文证据。
        var model = new StubBriefingModel((request, _) =>
        {
            Equal(itemId, request.ExistingItems.Single().Id,
                "The next briefing should receive the existing stable item ID.");
            return Task.FromResult(BriefingWith(new ProjectBriefingItem(
                itemId,
                ProjectMemoryCategory.Progress,
                "接口联调",
                ProjectMemoryStatus.Completed,
                "联调已经交付并完成验收。",
                new[] { completed.Id })));
        });
        var briefingService = new ProjectBriefingService(messages, model, memory);
        var keywords = new[]
        {
            new FollowedProjectKeyword(1, project.Id, project.ProjectName, "管线", BaseTime)
        };
        var outcome = await briefingService.GenerateAsync(
            project.Id, project.ProjectName, keywords, BaseTime.AddDays(-1), BaseTime.AddDays(1), CancellationToken.None);
        True(outcome.FromModel, "A valid structured model result should not fall back.");
        Equal(itemId, outcome.MemoryProposals.Single().ItemId,
            "The model proposal should retain the stable existing item ID.");

        var updated = await service.ApplySelectedAsync(project.Id, outcome.MemoryProposals, CancellationToken.None);
        Equal(1, updated.Updated, "A confirmed completion should update the existing item.");
        details = await memory.GetDetailsAsync(project.Id, itemId, CancellationToken.None);
        Equal(ProjectMemoryStatus.Completed, details!.Item.Status, "Completion should be persisted.");
        Equal(3, details.Sources.Count, "Every distinct source snapshot should be retained.");
        Equal(2, details.History.Count, "Creation and completion should produce exactly two history rows.");

        // 再生成一次相同完成建议时基准状态已刷新，重复应用只跳过，不写第三条历史。
        var repeatedOutcome = await briefingService.GenerateAsync(
            project.Id, project.ProjectName, keywords, BaseTime.AddDays(-1), BaseTime.AddDays(1), CancellationToken.None);
        var skipped = await service.ApplySelectedAsync(project.Id, repeatedOutcome.MemoryProposals, CancellationToken.None);
        Equal(1, skipped.Skipped, "A repeated completion with the same evidence should be idempotent.");
        details = await memory.GetDetailsAsync(project.Id, itemId, CancellationToken.None);
        Equal(2, details!.History.Count, "Idempotent completion must not append history.");

        // 阻塞、搁置和废弃是不同生命周期语义，不能统一折叠成完成。
        var lifecycle = new[]
        {
            NewProposal("上线窗口", ProjectMemoryStatus.Blocked, first, category: ProjectMemoryCategory.Risk),
            NewProposal("旧报表迁移", ProjectMemoryStatus.Shelved, first),
            NewProposal("临时演示环境", ProjectMemoryStatus.Discarded, first)
        };
        await service.ApplySelectedAsync(project.Id, lifecycle, CancellationToken.None);
        var statuses = (await memory.GetByProjectAsync(project.Id, CancellationToken.None))
            .ToDictionary(item => item.Title, item => item.Status, StringComparer.Ordinal);
        Equal(ProjectMemoryStatus.Blocked, statuses["上线窗口"], "Blocked should remain distinct.");
        Equal(ProjectMemoryStatus.Shelved, statuses["旧报表迁移"], "Shelved should remain distinct.");
        Equal(ProjectMemoryStatus.Discarded, statuses["临时演示环境"], "Discarded should remain distinct.");
    }

    /// <summary>过期确认和批量失败不得覆盖新状态或留下半批数据，原消息删除后证据仍可读。</summary>
    public static async Task RepositoryRejectsStaleBatchesAndRetainsEvidenceAsync()
    {
        var databasePath = await CreateDatabaseAsync();
        var projectRepository = new SqliteFollowedProjectRepository(databasePath);
        var project = await projectRepository.SaveAsync("智能管线", CancellationToken.None);
        var otherProject = await projectRepository.SaveAsync("其他项目", CancellationToken.None);
        var messages = new SqliteMessageRepository(databasePath);
        var memory = new SqliteProjectMemoryRepository(databasePath);
        var service = new ProjectMemoryService(memory, messages);
        var first = await SaveMessageAsync(messages, "管线方案等待确认", BaseTime);
        var second = await SaveMessageAsync(messages, "管线方案已完成并验收", BaseTime.AddHours(1));
        var third = await SaveMessageAsync(messages, "管线方案恢复推进", BaseTime.AddHours(2));
        var created = await service.ApplySelectedAsync(project.Id,
            new[] { NewProposal("方案确认", ProjectMemoryStatus.Open, first) }, CancellationToken.None);
        var itemId = created.ItemIds.Single();

        // 建议携带的基准状态不是当前状态，必须明确报告过期而非覆盖。
        var stale = NewProposal("方案确认", ProjectMemoryStatus.InProgress, second,
            itemId, ProjectMemoryStatus.Blocked);
        await ThrowsAsync<ProjectMemoryConcurrencyException>(
            () => service.ApplySelectedAsync(project.Id, new[] { stale }, CancellationToken.None),
            "A stale base status should be rejected.");

        // 同一事务中第一条本可成功、第二条过期，最终两条都不能部分提交。
        var batch = new[] { NewProposal("事务回滚事项", ProjectMemoryStatus.Open, second), stale };
        await ThrowsAsync<ProjectMemoryConcurrencyException>(
            () => service.ApplySelectedAsync(project.Id, batch, CancellationToken.None),
            "Any stale row should roll back the complete batch.");
        Equal(1, (await memory.GetByProjectAsync(project.Id, CancellationToken.None)).Count,
            "A failed batch must not leave a partially created item.");
        True(await memory.GetDetailsAsync(otherProject.Id, itemId, CancellationToken.None) is null,
            "Project ID must participate in detail queries to block cross-project reads.");

        // 完成后再通过 Manual 重开，验证终态只能由明确人工修正绕过简报限制。
        var completion = NewProposal("方案确认", ProjectMemoryStatus.Completed, second,
            itemId, ProjectMemoryStatus.Open);
        await service.ApplySelectedAsync(project.Id, new[] { completion }, CancellationToken.None);
        var manualReopen = NewProposal("方案确认", ProjectMemoryStatus.InProgress, third,
            itemId, ProjectMemoryStatus.Completed);
        await memory.ApplyAsync(
            project.Id,
            new[] { manualReopen },
            new Dictionary<long, Message> { [third.Id] = third },
            ProjectMemoryChangeSource.Manual,
            DateTimeOffset.Now,
            CancellationToken.None);
        var details = await memory.GetDetailsAsync(project.Id, itemId, CancellationToken.None);
        Equal(ProjectMemoryStatus.InProgress, details!.Item.Status, "A manual correction may reopen a terminal item.");
        Equal(ProjectMemoryChangeSource.Manual, details.History[0].ChangeSource,
            "The reopening history should identify its manual origin.");

        // 删除原始消息只置空外键，快照正文、来源键和状态原因仍然保留。
        await ExecuteAsync(databasePath, $"DELETE FROM messages WHERE id = {first.Id};");
        details = await memory.GetDetailsAsync(project.Id, itemId, CancellationToken.None);
        var snapshot = details!.Sources.Single(source => source.SourceMessageKey == first.SourceMessageKey);
        True(snapshot.MessageId is null, "Deleted source messages should leave a null message foreign key.");
        Equal(first.Content, snapshot.ContentSnapshot, "The evidence snapshot must survive source deletion.");
        Equal(3, details.History.Count, "Source deletion must not delete status history.");
    }

    /// <summary>模型伪造 ID、非法状态或无证据强状态均回退；用户取消则继续向上传播。</summary>
    public static async Task BriefingValidatesStructuredItemsAndFallsBackAsync()
    {
        var databasePath = await CreateDatabaseAsync();
        var projectRepository = new SqliteFollowedProjectRepository(databasePath);
        var project = await projectRepository.SaveAsync("智能管线", CancellationToken.None);
        var otherProject = await projectRepository.SaveAsync("其他项目", CancellationToken.None);
        var messages = new SqliteMessageRepository(databasePath);
        var memory = new SqliteProjectMemoryRepository(databasePath);
        var service = new ProjectMemoryService(memory, messages);
        var first = await SaveMessageAsync(messages, "管线联调正在推进", BaseTime);
        await SaveMessageAsync(messages, "管线上线被阻塞", BaseTime.AddMinutes(1));
        await SaveMessageAsync(messages, "管线请跟进验收安排", BaseTime.AddMinutes(2), isMentionMe: true);
        var otherMessage = await SaveMessageAsync(messages, "其他项目消息", BaseTime, chatName: "其他项目群");
        var created = await service.ApplySelectedAsync(project.Id,
            new[] { NewProposal("接口联调", ProjectMemoryStatus.Open, first) }, CancellationToken.None);
        var otherCreated = await service.ApplySelectedAsync(otherProject.Id,
            new[] { NewProposal("其他事项", ProjectMemoryStatus.Open, otherMessage) }, CancellationToken.None);
        var itemId = created.ItemIds.Single();

        // 合法既有 ID 和本次来源 ID 才能形成模型建议。
        var valid = await GenerateWithModelAsync(messages, memory, project, BriefingWith(
            new ProjectBriefingItem(itemId, ProjectMemoryCategory.Action, "接口联调",
                ProjectMemoryStatus.Blocked, "当前联调被阻断。", new[] { first.Id })));
        True(valid.FromModel, "A valid existing item and evidence whitelist should be accepted.");
        Equal(ProjectMemoryStatus.Open, valid.MemoryProposals.Single().BaseStatus,
            "The proposal should capture the current status for optimistic concurrency.");

        // 伪造、跨项目和未知来源都不能进入可确认建议，统一回退本地规则。
        var invalidItems = new[]
        {
            new ProjectBriefingItem(long.MaxValue, ProjectMemoryCategory.Action, "伪造事项",
                ProjectMemoryStatus.Open, "伪造 ID。", new[] { first.Id }),
            new ProjectBriefingItem(otherCreated.ItemIds.Single(), ProjectMemoryCategory.Action, "跨项目事项",
                ProjectMemoryStatus.Open, "跨项目 ID。", new[] { first.Id }),
            new ProjectBriefingItem(itemId, ProjectMemoryCategory.Action, "未知来源",
                ProjectMemoryStatus.Open, "来源不在白名单。", new[] { otherMessage.Id }),
            new ProjectBriefingItem(itemId, ProjectMemoryCategory.Action, "无证据完成",
                ProjectMemoryStatus.Completed, "正文没有完成证据。", new[] { first.Id }),
            new ProjectBriefingItem(itemId, ProjectMemoryCategory.Action, "无证据废弃",
                ProjectMemoryStatus.Discarded, "正文没有取消证据。", new[] { first.Id }),
            new ProjectBriefingItem(itemId, ProjectMemoryCategory.Action, "非法状态",
                (ProjectMemoryStatus)999, "非法枚举。", new[] { first.Id })
        };
        foreach (var invalidItem in invalidItems)
        {
            var fallback = await GenerateWithModelAsync(messages, memory, project, BriefingWith(invalidItem));
            True(!fallback.FromModel, $"Invalid model item should fall back: {invalidItem.Title}.");
            True(fallback.MemoryProposals.All(proposal =>
                    proposal.ItemId is null
                    && proposal.ProposedStatus is ProjectMemoryStatus.Blocked or ProjectMemoryStatus.Open),
                "Fallback proposals should only contain conservative local Blocked/Open suggestions.");
        }

        // 已完成事项不允许被模型重开，即使用户消息仍在持续推进其他工作。
        var terminal = await service.ApplySelectedAsync(project.Id,
            new[] { NewProposal("已结束事项", ProjectMemoryStatus.Completed, first) }, CancellationToken.None);
        var reopen = await GenerateWithModelAsync(messages, memory, project, BriefingWith(
            new ProjectBriefingItem(terminal.ItemIds.Single(), ProjectMemoryCategory.Action, "已结束事项",
                ProjectMemoryStatus.Open, "模型试图重开终态。", new[] { first.Id })));
        True(!reopen.FromModel, "A model must not reopen a completed item.");

        // 模型未配置、超时等异常继续沿用本地摘要和保守建议。
        var failedService = new ProjectBriefingService(messages,
            new StubBriefingModel((_, _) => throw new InvalidOperationException("模拟模型超时")), memory);
        var failed = await failedService.GenerateAsync(project.Id, project.ProjectName,
            Keywords(project), BaseTime.AddDays(-1), BaseTime.AddDays(1), CancellationToken.None);
        True(!failed.FromModel, "A model exception should use the local fallback.");
        True(failed.MemoryProposals.Count >= 2, "Risk and mention-me messages should produce local suggestions.");

        // 只有调用方令牌确实取消时才传播 OperationCanceledException，不伪装成模型失败。
        using var cancellation = new CancellationTokenSource();
        var cancellingService = new ProjectBriefingService(messages,
            new StubBriefingModel((_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<ProjectBriefing>(token);
            }), memory);
        await ThrowsAsync<OperationCanceledException>(
            () => cancellingService.GenerateAsync(project.Id, project.ProjectName,
                Keywords(project), BaseTime.AddDays(-1), BaseTime.AddDays(1), cancellation.Token),
            "Caller cancellation must propagate through briefing generation.");
    }

    /// <summary>用固定模型结果生成一次当前项目简报，减少各非法用例的重复编排。</summary>
    private static Task<ProjectBriefingOutcome> GenerateWithModelAsync(
        SqliteMessageRepository messages,
        SqliteProjectMemoryRepository memory,
        FollowedProject project,
        ProjectBriefing briefing)
    {
        var model = new StubBriefingModel((_, _) => Task.FromResult(briefing));
        return new ProjectBriefingService(messages, model, memory).GenerateAsync(
            project.Id,
            project.ProjectName,
            Keywords(project),
            BaseTime.AddDays(-1),
            BaseTime.AddDays(1),
            CancellationToken.None);
    }

    /// <summary>当前项目只匹配“管线”，其他项目消息不会进入模型来源白名单。</summary>
    private static IReadOnlyCollection<FollowedProjectKeyword> Keywords(FollowedProject project) =>
        new[] { new FollowedProjectKeyword(1, project.Id, project.ProjectName, "管线", BaseTime) };

    /// <summary>创建只含结构化事项差异的简报，文本字段保持固定以减少测试噪音。</summary>
    private static ProjectBriefing BriefingWith(params ProjectBriefingItem[] items) =>
        new("项目简报", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())
        {
            Items = items
        };

    /// <summary>构造建议时默认使用行动类别；测试可按需覆盖类别、既有 ID 和基准状态。</summary>
    private static ProjectMemoryProposal NewProposal(
        string title,
        ProjectMemoryStatus status,
        Message source,
        long? itemId = null,
        ProjectMemoryStatus? baseStatus = null,
        ProjectMemoryCategory category = ProjectMemoryCategory.Action) =>
        new(itemId, category, title, baseStatus, status, "测试确认的状态原因。", new[] { source.Id });

    /// <summary>保存一条具有真实自增 ID 的项目消息，来源键在每次调用中保持唯一。</summary>
    private static Task<Message> SaveMessageAsync(
        SqliteMessageRepository repository,
        string content,
        DateTimeOffset sentAt,
        string chatName = "管线项目群",
        bool isMentionMe = false) =>
        repository.SaveAsync(new Message(
            Id: 0,
            Source: "WeChat",
            SourceMessageKey: $"memory-{Guid.NewGuid():N}",
            ChatSessionId: 1,
            ChatName: chatName,
            SenderName: "测试成员",
            Content: content,
            SentAt: sentAt,
            CapturedAt: sentAt,
            MessageType: MessageType.Text,
            IsMentionMe: isMentionMe), CancellationToken.None);

    /// <summary>创建独立临时数据库并完成全部迁移，避免测试之间相互污染。</summary>
    private static async Task<string> CreateDatabaseAsync()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);
        return databasePath;
    }

    /// <summary>读取旧迁移版本和校验和，顺序排序后可直接比较升级前后快照。</summary>
    private static async Task<IReadOnlyDictionary<int, string>> ReadChecksumsAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, checksum FROM schema_migrations WHERE version < 9 ORDER BY version;";
        await using var reader = await command.ExecuteReaderAsync();
        var values = new Dictionary<int, string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetInt32(0), reader.GetString(1));
        }

        return values;
    }

    /// <summary>执行用于查询表数量或迁移数量的标量 SQL。</summary>
    private static async Task<T> ScalarAsync<T>(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Scalar result was null."));
    }

    /// <summary>执行旧库准备或原消息删除等测试专用 SQL。</summary>
    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>简洁断言异常类型，确保回退和取消语义不会被意外交换。</summary>
    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>字典比较同时验证键集合和值，防止旧迁移被遗漏或篡改。</summary>
    private static void Equal<T>(T expected, T actual, string message)
    {
        if (expected is IReadOnlyDictionary<int, string> expectedDictionary
            && actual is IReadOnlyDictionary<int, string> actualDictionary)
        {
            if (expectedDictionary.Count == actualDictionary.Count
                && expectedDictionary.All(pair => actualDictionary.TryGetValue(pair.Key, out var value) && value == pair.Value))
            {
                return;
            }
        }
        else if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            return;
        }

        throw new InvalidOperationException($"{message} Expected: {expected}; Actual: {actual}.");
    }

    /// <summary>可控简报模型替身，可检查请求并模拟正常、异常和取消响应。</summary>
    private sealed class StubBriefingModel(
        Func<ProjectBriefingRequest, CancellationToken, Task<ProjectBriefing>> respond) : IProjectBriefingModel
    {
        public Task<ProjectBriefing> SummarizeProjectAsync(
            ProjectBriefingRequest request,
            CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
