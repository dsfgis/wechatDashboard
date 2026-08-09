using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Reminders;

public interface IReminderRepository
{
    Task<TodoReminder?> GetByIdAsync(long id, CancellationToken cancellationToken);

    Task<IReadOnlyList<TodoReminder>> GetForTodoAsync(long todoId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReminderDispatchItem>> GetDueAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken);

    Task<int> RecoverStaleClaimsAsync(
        DateTimeOffset staleBefore,
        DateTimeOffset recoveredAt,
        CancellationToken cancellationToken);

    Task<bool> TryClaimAsync(long id, DateTimeOffset claimedAt, CancellationToken cancellationToken);

    Task MarkDeliveredAsync(long id, DateTimeOffset deliveredAt, CancellationToken cancellationToken);

    Task RescheduleAfterFailureAsync(
        long id,
        DateTimeOffset retryAt,
        string sanitizedError,
        CancellationToken cancellationToken);
}

public sealed record ReminderDispatchItem(TodoReminder Reminder, string TodoTitle, string? TodoDescription);
