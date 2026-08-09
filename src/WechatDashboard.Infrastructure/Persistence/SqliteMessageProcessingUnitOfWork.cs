using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Infrastructure.Persistence;

/// <summary>
/// SQLite 单消息工作单元：在同一连接和事务中完成消息去重插入、
/// 分类结果、紧急度结果及自动待办写入。
/// </summary>
public sealed class SqliteMessageProcessingUnitOfWork : IMessageProcessingUnitOfWork
{
    private readonly string _databasePath;

    public SqliteMessageProcessingUnitOfWork(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <inheritdoc />
    public async Task<MessageProcessingWriteResult> TryProcessAsync(
        Message message,
        Func<Message, MessageProcessingArtifacts> createArtifacts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(createArtifacts);

        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var transaction = connection.BeginTransaction();

        try
        {
            var savedMessage = await SqliteMessageRepository.TryInsertAsync(
                connection,
                transaction,
                message,
                cancellationToken);

            if (savedMessage is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new MessageProcessingWriteResult(false, false, null);
            }

            var artifacts = createArtifacts(savedMessage);
            var classification = artifacts.Classification with { MessageId = savedMessage.Id };
            var urgency = artifacts.Urgency with { MessageId = savedMessage.Id };
            var todo = artifacts.Todo is null
                ? null
                : artifacts.Todo with { SourceMessageId = savedMessage.Id };

            await InsertClassificationAsync(connection, transaction, classification, cancellationToken);
            await InsertUrgencyAsync(connection, transaction, urgency, cancellationToken);

            var createdTodo = todo is not null;
            if (todo is not null)
            {
                await SqliteTodoRepository.InsertAsync(connection, transaction, todo, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new MessageProcessingWriteResult(true, createdTodo, savedMessage);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // 保留最初的处理异常；释放未提交事务仍会再次尝试回滚。
            }

            throw;
        }
    }

    /// <summary>在当前事务中写入消息分类结果。</summary>
    private static async Task InsertClassificationAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        ClassificationResult classification,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO message_classifications (
                message_id,
                project_id,
                category,
                confidence,
                reason,
                classifier,
                created_at
            )
            VALUES (
                $messageId,
                $projectId,
                $category,
                $confidence,
                $reason,
                $classifier,
                $createdAt
            );
            """;
        command.Parameters.AddWithValue("$messageId", classification.MessageId);
        command.Parameters.AddWithValue("$projectId", classification.ProjectId is null ? DBNull.Value : classification.ProjectId.Value);
        command.Parameters.AddWithValue("$category", classification.Category.ToString());
        command.Parameters.AddWithValue("$confidence", classification.Confidence);
        command.Parameters.AddWithValue("$reason", classification.Reason);
        command.Parameters.AddWithValue("$classifier", classification.Classifier);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>在当前事务中写入紧急度评分结果。</summary>
    private static async Task InsertUrgencyAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        UrgencyScore urgency,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO urgency_scores (
                message_id,
                score,
                priority,
                reason,
                calculated_at
            )
            VALUES (
                $messageId,
                $score,
                $priority,
                $reason,
                $calculatedAt
            );
            """;
        command.Parameters.AddWithValue("$messageId", urgency.MessageId);
        command.Parameters.AddWithValue("$score", urgency.Score);
        command.Parameters.AddWithValue("$priority", urgency.Priority.ToString());
        command.Parameters.AddWithValue("$reason", urgency.Reason);
        command.Parameters.AddWithValue("$calculatedAt", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
