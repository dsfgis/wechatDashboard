using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Briefing;

/// <summary>
/// 项目记忆应用服务：重新读取证据消息、合并完全重复的选中行，再委托仓储事务写入。
/// 生成简报和生成建议不会调用本服务，只有用户明确应用时才产生持久化变更。
/// </summary>
public sealed class ProjectMemoryService
{
    private readonly IProjectMemoryRepository _memoryRepository;
    private readonly IMessageRepository _messageRepository;

    public ProjectMemoryService(
        IProjectMemoryRepository memoryRepository,
        IMessageRepository messageRepository)
    {
        _memoryRepository = memoryRepository ?? throw new ArgumentNullException(nameof(memoryRepository));
        _messageRepository = messageRepository ?? throw new ArgumentNullException(nameof(messageRepository));
    }

    /// <summary>读取项目全部记忆事项，状态筛选由调用方完成。</summary>
    public Task<IReadOnlyList<ProjectMemoryItem>> GetByProjectAsync(
        long projectId,
        CancellationToken cancellationToken) =>
        _memoryRepository.GetByProjectAsync(projectId, cancellationToken);

    /// <summary>读取来源快照和状态时间线。</summary>
    public Task<ProjectMemoryDetails?> GetDetailsAsync(
        long projectId,
        long itemId,
        CancellationToken cancellationToken) =>
        _memoryRepository.GetDetailsAsync(projectId, itemId, cancellationToken);

    /// <summary>
    /// 应用用户选中的建议。所有来源在应用时重新查询，不能使用确认窗口中缓存的正文伪造快照。
    /// </summary>
    public async Task<ProjectMemoryApplyResult> ApplySelectedAsync(
        long projectId,
        IReadOnlyList<ProjectMemoryProposal> selectedProposals,
        CancellationToken cancellationToken)
    {
        if (projectId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(projectId), "项目 ID 必须为正数。");
        }

        // 合并同事项、同目标状态的重复行，避免一次批量应用被自己误判为并发冲突。
        var proposals = MergeDuplicateProposals(selectedProposals);
        if (proposals.Count == 0)
        {
            return new ProjectMemoryApplyResult(0, 0, 0, Array.Empty<long>());
        }

        var sourceIds = proposals
            .SelectMany(proposal => proposal.SourceMessageIds)
            .Distinct()
            .ToArray();
        var messages = await _messageRepository.GetByIdsAsync(sourceIds, cancellationToken);
        var messagesById = messages.ToDictionary(message => message.Id);
        if (messagesById.Count != sourceIds.Length)
        {
            throw new InvalidOperationException("部分来源消息已删除或不存在，请重新生成简报后再应用。");
        }

        return await _memoryRepository.ApplyAsync(
            projectId,
            proposals,
            messagesById,
            ProjectMemoryChangeSource.Briefing,
            DateTimeOffset.Now,
            cancellationToken);
    }

    /// <summary>校验边界输入并合并重复建议；冲突状态不能由仓储猜测先后顺序。</summary>
    private static IReadOnlyList<ProjectMemoryProposal> MergeDuplicateProposals(
        IReadOnlyList<ProjectMemoryProposal> proposals)
    {
        var merged = new Dictionary<string, ProjectMemoryProposal>(StringComparer.Ordinal);
        foreach (var proposal in proposals)
        {
            if (!Enum.IsDefined(proposal.Category) || !Enum.IsDefined(proposal.ProposedStatus))
            {
                throw new InvalidOperationException("项目记忆建议包含无效类别或状态。");
            }

            var title = proposal.Title.Trim();
            var reason = proposal.Reason.Trim();
            if (title.Length == 0 || reason.Length == 0)
            {
                throw new InvalidOperationException("项目记忆标题和状态原因不能为空。");
            }

            var sourceIds = proposal.SourceMessageIds.Distinct().ToArray();
            if (sourceIds.Length == 0 || sourceIds.Any(id => id <= 0))
            {
                throw new InvalidOperationException("项目记忆建议必须包含有效的来源消息 ID。");
            }

            var normalizedTitle = ProjectMemoryTitle.Normalize(title);
            // 新事项按规范化标题归并，既有事项按稳定 ID 归并。
            var itemKey = proposal.ItemId?.ToString() ?? $"new:{normalizedTitle}";
            var key = $"{itemKey}|{proposal.ProposedStatus}|{proposal.BaseStatus}";
            if (merged.TryGetValue(key, out var existing))
            {
                merged[key] = existing with
                {
                    SourceMessageIds = existing.SourceMessageIds.Union(sourceIds).ToArray()
                };
                continue;
            }

            // 同一事项在同一批次出现不同目标状态属于冲突建议，应让用户重新选择。
            var hasConflict = merged.Keys.Any(existingKey =>
                existingKey.StartsWith($"{itemKey}|", StringComparison.Ordinal));
            if (hasConflict)
            {
                throw new InvalidOperationException($"事项“{title}”存在冲突的目标状态，请刷新后重新确认。");
            }

            merged.Add(key, proposal with
            {
                Title = title,
                Reason = reason,
                SourceMessageIds = sourceIds
            });
        }

        return merged.Values.ToArray();
    }
}
