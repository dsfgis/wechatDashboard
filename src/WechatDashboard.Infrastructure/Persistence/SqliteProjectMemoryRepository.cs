using System.Globalization;
using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Briefing;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Persistence;

/// <summary>
/// SQLite 项目记忆仓储。所有选中建议在同一事务中重新读取并校验，
/// 防止确认窗口过期后覆盖其他操作已经写入的状态。
/// </summary>
public sealed class SqliteProjectMemoryRepository : IProjectMemoryRepository
{
    private const string ItemColumns = """
        id, project_id, category, title, normalized_title, status,
        created_at, updated_at, last_mentioned_at, last_status_reason
        """;

    private readonly string _databasePath;

    public SqliteProjectMemoryRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <summary>读取项目全部记忆，终端状态也返回，由界面决定是否折叠。</summary>
    public async Task<IReadOnlyList<ProjectMemoryItem>> GetByProjectAsync(
        long projectId,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ItemColumns}
            FROM project_memory_items
            WHERE project_id = $projectId
            ORDER BY updated_at DESC, id DESC;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);

        var items = new List<ProjectMemoryItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    /// <summary>读取一个事项及其证据和时间线；项目 ID 同时参与查询以阻止跨项目访问。</summary>
    public async Task<ProjectMemoryDetails?> GetDetailsAsync(
        long projectId,
        long itemId,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        var item = await ReadItemAsync(connection, null, projectId, itemId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var sources = new List<ProjectMemorySource>();
        await using (var sourceCommand = connection.CreateCommand())
        {
            sourceCommand.CommandText = """
                SELECT id, item_id, message_id, source, source_message_key,
                       chat_name, sender_name, content_snapshot, sent_at, created_at
                FROM project_memory_sources
                WHERE item_id = $itemId
                ORDER BY sent_at DESC, id DESC;
                """;
            sourceCommand.Parameters.AddWithValue("$itemId", itemId);
            await using var reader = await sourceCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sources.Add(new ProjectMemorySource(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    ParseTime(reader.GetString(8)),
                    ParseTime(reader.GetString(9))));
            }
        }

        var history = new List<ProjectMemoryStatusChange>();
        await using (var historyCommand = connection.CreateCommand())
        {
            historyCommand.CommandText = """
                SELECT id, item_id, previous_status, new_status, reason,
                       evidence_message_id, change_source, changed_at
                FROM project_memory_status_history
                WHERE item_id = $itemId
                ORDER BY changed_at DESC, id DESC;
                """;
            historyCommand.Parameters.AddWithValue("$itemId", itemId);
            await using var reader = await historyCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                history.Add(new ProjectMemoryStatusChange(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : Enum.Parse<ProjectMemoryStatus>(reader.GetString(2)),
                    Enum.Parse<ProjectMemoryStatus>(reader.GetString(3)),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    Enum.Parse<ProjectMemoryChangeSource>(reader.GetString(6)),
                    ParseTime(reader.GetString(7))));
            }
        }

        return new ProjectMemoryDetails(item, sources, history);
    }

    /// <summary>
    /// 批量应用建议。事项、来源、历史要么全部提交，要么在任一校验失败时全部回滚。
    /// </summary>
    public async Task<ProjectMemoryApplyResult> ApplyAsync(
        long projectId,
        IReadOnlyList<ProjectMemoryProposal> proposals,
        IReadOnlyDictionary<long, Message> sourceMessages,
        ProjectMemoryChangeSource changeSource,
        DateTimeOffset appliedAt,
        CancellationToken cancellationToken)
    {
        if (proposals.Count == 0)
        {
            return new ProjectMemoryApplyResult(0, 0, 0, Array.Empty<long>());
        }

        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var transaction = connection.BeginTransaction();
        try
        {
            var created = 0;
            var updated = 0;
            var skipped = 0;
            var itemIds = new List<long>();

            foreach (var proposal in proposals)
            {
                // 每条建议必须有至少一条真实消息，快照只能来自服务层重新读取的实体。
                var messages = ResolveMessages(proposal, sourceMessages);
                var mentionedAt = messages.Max(message => message.SentAt);
                ProjectMemoryItem? item;
                var isNew = false;

                if (proposal.ItemId is long requestedItemId)
                {
                    item = await ReadItemAsync(
                        connection, transaction, projectId, requestedItemId, cancellationToken);
                    if (item is null)
                    {
                        throw new ProjectMemoryConcurrencyException("事项已删除或不属于当前项目，请刷新项目记忆。");
                    }

                    // 基准状态来自生成建议时的快照，不一致说明确认窗口已经过期。
                    if (proposal.BaseStatus != item.Status)
                    {
                        throw new ProjectMemoryConcurrencyException(
                            $"事项“{item.Title}”状态已变化，请刷新后重新确认。");
                    }
                }
                else
                {
                    // 新事项先按活跃标题归并，避免同一问题在多期简报中重复创建。
                    var normalizedTitle = ProjectMemoryTitle.Normalize(proposal.Title);
                    item = await FindActiveByTitleAsync(
                        connection, transaction, projectId, normalizedTitle, cancellationToken);
                    // 终态事项仅在证据完全重复时归并，以兼顾重复应用幂等和合法新建同事项。
                    item ??= await FindTerminalByEvidenceAsync(
                        connection, transaction, projectId, normalizedTitle, messages, cancellationToken);
                    if (item is null)
                    {
                        item = await InsertItemAsync(
                            connection,
                            transaction,
                            projectId,
                            proposal,
                            normalizedTitle,
                            appliedAt,
                            mentionedAt,
                            cancellationToken);
                        created++;
                        isNew = true;
                    }
                }

                // 模型建议不得重开终态；人工修正入口可通过 Manual 明确绕过此限制。
                if (!isNew
                    && changeSource == ProjectMemoryChangeSource.Briefing
                    && IsTerminal(item.Status)
                    && proposal.ProposedStatus != item.Status)
                {
                    throw new InvalidOperationException($"事项“{item.Title}”已完成或已废弃，不能通过简报重新打开。");
                }

                var statusChanged = item.Status != proposal.ProposedStatus;
                var addedSources = await InsertSourcesAsync(
                    connection, transaction, item.Id, messages, appliedAt, cancellationToken);

                if (isNew || statusChanged || addedSources > 0)
                {
                    if (!isNew)
                    {
                        await UpdateItemAsync(
                            connection,
                            transaction,
                            item.Id,
                            proposal.ProposedStatus,
                            statusChanged ? proposal.Reason.Trim() : item.LastStatusReason,
                            appliedAt,
                            mentionedAt,
                            cancellationToken);
                        updated++;
                    }

                    // 初次创建和真实状态变化才写历史；仅补充来源不会生成伪状态记录。
                    if (isNew || statusChanged)
                    {
                        await InsertHistoryAsync(
                            connection,
                            transaction,
                            item.Id,
                            isNew ? null : item.Status,
                            proposal.ProposedStatus,
                            proposal.Reason.Trim(),
                            messages[0].Id,
                            changeSource,
                            appliedAt,
                            cancellationToken);
                    }
                }
                else
                {
                    skipped++;
                }

                if (!itemIds.Contains(item.Id))
                {
                    itemIds.Add(item.Id);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return new ProjectMemoryApplyResult(created, updated, skipped, itemIds);
        }
        catch
        {
            // 明确回滚后再向界面报告，避免部分建议已经写入而其余建议失败。
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>按建议来源顺序解析消息，任何缺失 ID 都视为无效证据。</summary>
    private static IReadOnlyList<Message> ResolveMessages(
        ProjectMemoryProposal proposal,
        IReadOnlyDictionary<long, Message> sourceMessages)
    {
        var ids = proposal.SourceMessageIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            throw new InvalidOperationException("项目记忆建议至少需要一条来源消息。");
        }

        var messages = new List<Message>(ids.Length);
        foreach (var id in ids)
        {
            if (!sourceMessages.TryGetValue(id, out var message) || message.Id != id)
            {
                throw new InvalidOperationException($"来源消息 {id} 不存在或已删除，请重新生成简报。");
            }

            messages.Add(message);
        }

        return messages;
    }

    /// <summary>创建新事项，初始状态历史在来源插入成功后统一补齐。</summary>
    private static async Task<ProjectMemoryItem> InsertItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long projectId,
        ProjectMemoryProposal proposal,
        string normalizedTitle,
        DateTimeOffset appliedAt,
        DateTimeOffset mentionedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO project_memory_items (
                project_id, category, title, normalized_title, status,
                created_at, updated_at, last_mentioned_at, last_status_reason)
            VALUES (
                $projectId, $category, $title, $normalizedTitle, $status,
                $createdAt, $updatedAt, $mentionedAt, $reason);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$category", proposal.Category.ToString());
        command.Parameters.AddWithValue("$title", proposal.Title.Trim());
        command.Parameters.AddWithValue("$normalizedTitle", normalizedTitle);
        command.Parameters.AddWithValue("$status", proposal.ProposedStatus.ToString());
        command.Parameters.AddWithValue("$createdAt", FormatTime(appliedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatTime(appliedAt));
        command.Parameters.AddWithValue("$mentionedAt", FormatTime(mentionedAt));
        command.Parameters.AddWithValue("$reason", proposal.Reason.Trim());
        var id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        return new ProjectMemoryItem(
            id,
            projectId,
            proposal.Category,
            proposal.Title.Trim(),
            normalizedTitle,
            proposal.ProposedStatus,
            appliedAt,
            appliedAt,
            mentionedAt,
            proposal.Reason.Trim());
    }

    /// <summary>更新现有事项；只有状态变化才替换最近状态原因。</summary>
    private static async Task UpdateItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long itemId,
        ProjectMemoryStatus status,
        string? reason,
        DateTimeOffset appliedAt,
        DateTimeOffset mentionedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE project_memory_items
            SET status = $status,
                updated_at = $updatedAt,
                last_mentioned_at = $mentionedAt,
                last_status_reason = $reason
            WHERE id = $itemId;
            """;
        command.Parameters.AddWithValue("$itemId", itemId);
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", FormatTime(appliedAt));
        command.Parameters.AddWithValue("$mentionedAt", FormatTime(mentionedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>逐条插入来源快照，INSERT OR IGNORE 保证重复应用不会产生重复证据。</summary>
    private static async Task<int> InsertSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long itemId,
        IReadOnlyList<Message> messages,
        DateTimeOffset appliedAt,
        CancellationToken cancellationToken)
    {
        var inserted = 0;
        foreach (var message in messages)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO project_memory_sources (
                    item_id, message_id, source, source_message_key,
                    chat_name, sender_name, content_snapshot, sent_at, created_at)
                VALUES (
                    $itemId, $messageId, $source, $sourceMessageKey,
                    $chatName, $senderName, $content, $sentAt, $createdAt);
                """;
            command.Parameters.AddWithValue("$itemId", itemId);
            command.Parameters.AddWithValue("$messageId", message.Id);
            command.Parameters.AddWithValue("$source", message.Source);
            command.Parameters.AddWithValue("$sourceMessageKey", message.SourceMessageKey);
            command.Parameters.AddWithValue("$chatName", message.ChatName);
            command.Parameters.AddWithValue("$senderName", message.SenderName);
            command.Parameters.AddWithValue("$content", message.Content);
            command.Parameters.AddWithValue("$sentAt", FormatTime(message.SentAt));
            command.Parameters.AddWithValue("$createdAt", FormatTime(appliedAt));
            inserted += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return inserted;
    }

    /// <summary>写入完整状态历史，证据消息日后删除不会改变历史文本。</summary>
    private static async Task InsertHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long itemId,
        ProjectMemoryStatus? previousStatus,
        ProjectMemoryStatus newStatus,
        string reason,
        long evidenceMessageId,
        ProjectMemoryChangeSource changeSource,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO project_memory_status_history (
                item_id, previous_status, new_status, reason,
                evidence_message_id, change_source, changed_at)
            VALUES (
                $itemId, $previousStatus, $newStatus, $reason,
                $evidenceMessageId, $changeSource, $changedAt);
            """;
        command.Parameters.AddWithValue("$itemId", itemId);
        command.Parameters.AddWithValue(
            "$previousStatus",
            previousStatus.HasValue ? previousStatus.Value.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("$newStatus", newStatus.ToString());
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$evidenceMessageId", evidenceMessageId);
        command.Parameters.AddWithValue("$changeSource", changeSource.ToString());
        command.Parameters.AddWithValue("$changedAt", FormatTime(changedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>按项目内非终态规范化标题查找可归并事项。</summary>
    private static async Task<ProjectMemoryItem?> FindActiveByTitleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long projectId,
        string normalizedTitle,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {ItemColumns}
            FROM project_memory_items
            WHERE project_id = $projectId
              AND normalized_title = $normalizedTitle
              AND status NOT IN ('Completed', 'Discarded')
            ORDER BY id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$normalizedTitle", normalizedTitle);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
    }

    /// <summary>查找具有相同来源证据的终态事项，只用于阻止重复应用创建副本。</summary>
    private static async Task<ProjectMemoryItem?> FindTerminalByEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long projectId,
        string normalizedTitle,
        IReadOnlyList<Message> messages,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var evidencePredicates = new List<string>(messages.Count);
        for (var index = 0; index < messages.Count; index++)
        {
            evidencePredicates.Add($"(s.source = $source{index} AND s.source_message_key = $key{index})");
            command.Parameters.AddWithValue($"$source{index}", messages[index].Source);
            command.Parameters.AddWithValue($"$key{index}", messages[index].SourceMessageKey);
        }

        command.CommandText = $"""
            SELECT {ItemColumns}
            FROM project_memory_items AS i
            WHERE i.project_id = $projectId
              AND i.normalized_title = $normalizedTitle
              AND i.status IN ('Completed', 'Discarded')
              AND EXISTS (
                  SELECT 1 FROM project_memory_sources AS s
                  WHERE s.item_id = i.id AND ({string.Join(" OR ", evidencePredicates)}))
            ORDER BY i.id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$normalizedTitle", normalizedTitle);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
    }

    /// <summary>在指定事务可见性内读取事项，应用建议时使用最新数据库状态。</summary>
    private static async Task<ProjectMemoryItem?> ReadItemAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long projectId,
        long itemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {ItemColumns}
            FROM project_memory_items
            WHERE project_id = $projectId AND id = $itemId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$itemId", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
    }

    /// <summary>将数据库行映射为事项实体，枚举只来自受 CHECK 约束的持久化值。</summary>
    private static ProjectMemoryItem ReadItem(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        Enum.Parse<ProjectMemoryCategory>(reader.GetString(2)),
        reader.GetString(3),
        reader.GetString(4),
        Enum.Parse<ProjectMemoryStatus>(reader.GetString(5)),
        ParseTime(reader.GetString(6)),
        ParseTime(reader.GetString(7)),
        ParseTime(reader.GetString(8)),
        reader.IsDBNull(9) ? null : reader.GetString(9));

    private static bool IsTerminal(ProjectMemoryStatus status) =>
        status is ProjectMemoryStatus.Completed or ProjectMemoryStatus.Discarded;

    private static string FormatTime(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
