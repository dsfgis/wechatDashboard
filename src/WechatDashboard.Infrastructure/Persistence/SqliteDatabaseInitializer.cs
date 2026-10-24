using Microsoft.Data.Sqlite;
using System.Text;

namespace WechatDashboard.Infrastructure.Persistence;

/// <summary>
/// SQLite 数据库初始化器：创建表结构与索引（幂等）。
/// 启用 WAL 模式与外键约束。
/// </summary>
public sealed class SqliteDatabaseInitializer
{
    private readonly string _databasePath;

    public SqliteDatabaseInitializer(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <summary>
    /// 执行数据库初始化：设置 PRAGMA 并建表建索引。
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        // WAL 模式提升并发读写性能
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
        // 启用外键约束
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken);
        // 建表建索引
        await ExecuteAsync(connection, SchemaSql, cancellationToken);
        await ExecuteAsync(connection, MigrationTableSql, cancellationToken);
        await ApplyMigrationAsync(
            connection,
            version: 1,
            name: "todo-reminder-lifecycle",
            checksum: "todo-reminder-lifecycle-v1",
            sql: TodoReminderMigrationSql,
            cancellationToken);
        await ApplyMigrationAsync(
            connection,
            version: 2,
            name: "todo-pinning",
            checksum: "todo-pinning-v1",
            sql: TodoPinningMigrationSql,
            cancellationToken);
        await ApplyMigrationAsync(
            connection,
            version: 3,
            name: "todo-groups",
            checksum: "todo-groups-v1",
            sql: TodoGroupsMigrationSql,
            cancellationToken);
        await ApplyMigrationAsync(
            connection,
            version: 4,
            name: "todo-group-colors",
            checksum: "todo-group-colors-v1",
            sql: TodoGroupColorsMigrationSql,
            cancellationToken);
        await ApplyProjectAssignmentMigrationAsync(connection, cancellationToken);
    }

    /// <summary>执行一条无返回 SQL。</summary>
    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ApplyMigrationAsync(
        SqliteConnection connection,
        int version,
        string name,
        string checksum,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT checksum FROM schema_migrations WHERE version = $version;";
        check.Parameters.AddWithValue("$version", version);
        var existingChecksum = await check.ExecuteScalarAsync(cancellationToken) as string;
        if (existingChecksum is not null)
        {
            if (!string.Equals(existingChecksum, checksum, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Schema migration {version} checksum mismatch.");
            }

            return;
        }

        await using var transaction = connection.BeginTransaction();
        await using var migration = connection.CreateCommand();
        migration.Transaction = transaction;
        migration.CommandText = sql;
        await migration.ExecuteNonQueryAsync(cancellationToken);

        await using var record = connection.CreateCommand();
        record.Transaction = transaction;
        record.CommandText = "INSERT INTO schema_migrations(version, name, checksum, applied_at) VALUES ($version, $name, $checksum, $appliedAt);";
        record.Parameters.AddWithValue("$version", version);
        record.Parameters.AddWithValue("$name", name);
        record.Parameters.AddWithValue("$checksum", checksum);
        record.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
        await record.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ApplyProjectAssignmentMigrationAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const int version = 5;
        const string name = "todo-project-assignment";
        const string checksum = "todo-project-assignment-v1";

        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT checksum FROM schema_migrations WHERE version = $version;";
        check.Parameters.AddWithValue("$version", version);
        var existingChecksum = await check.ExecuteScalarAsync(cancellationToken) as string;
        if (existingChecksum is not null)
        {
            if (!string.Equals(existingChecksum, checksum, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Schema migration {version} checksum mismatch.");
            }

            return;
        }

        await using var transaction = connection.BeginTransaction();
        try
        {
            await EnsureColumnAsync(
                connection,
                transaction,
                "followed_projects",
                "color",
                "TEXT NOT NULL DEFAULT '#4B5563'",
                cancellationToken);
            await EnsureColumnAsync(
                connection,
                transaction,
                "todo_items",
                "managed_project_id",
                "INTEGER NULL REFERENCES followed_projects(id) ON DELETE SET NULL",
                cancellationToken);
            await ExecuteAsync(connection, transaction, TodoProjectAssignmentMigrationSql, cancellationToken);

            var followedProjects = await ReadNamedIdsAsync(
                connection,
                transaction,
                "SELECT id, project_name FROM followed_projects;",
                cancellationToken);
            var projectsByName = followedProjects
                .GroupBy(item => NormalizeName(item.Name), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Select(item => item.Id).ToArray(), StringComparer.Ordinal);
            var groups = await ReadNamedIdsAsync(
                connection,
                transaction,
                "SELECT id, name FROM todo_groups;",
                cancellationToken);

            var mappedGroupTodos = 0;
            var nameConflicts = 0;
            foreach (var group in groups)
            {
                if (!projectsByName.TryGetValue(NormalizeName(group.Name), out var matchingProjectIds))
                {
                    continue;
                }

                if (matchingProjectIds.Length != 1)
                {
                    nameConflicts++;
                    continue;
                }

                mappedGroupTodos += await AssignProjectAsync(
                    connection,
                    transaction,
                    "group_id = $sourceId",
                    group.Id,
                    matchingProjectIds[0],
                    cancellationToken);
            }

            var legacyProjects = await ReadNamedIdsAsync(
                connection,
                transaction,
                "SELECT id, name FROM projects;",
                cancellationToken);
            var mappedLegacyProjectTodos = 0;
            foreach (var legacyProject in legacyProjects)
            {
                if (!projectsByName.TryGetValue(NormalizeName(legacyProject.Name), out var matchingProjectIds))
                {
                    continue;
                }

                if (matchingProjectIds.Length != 1)
                {
                    nameConflicts++;
                    continue;
                }

                mappedLegacyProjectTodos += await AssignProjectAsync(
                    connection,
                    transaction,
                    "group_id IS NULL AND project_id = $sourceId",
                    legacyProject.Id,
                    matchingProjectIds[0],
                    cancellationToken);
            }

            var unmatchedGroupTodos = await CountAsync(
                connection,
                transaction,
                "SELECT COUNT(*) FROM todo_items WHERE group_id IS NOT NULL AND managed_project_id IS NULL;",
                cancellationToken);

            await using var report = connection.CreateCommand();
            report.Transaction = transaction;
            report.CommandText = """
                INSERT INTO schema_migration_reports (
                    version, mapped_group_todos, unmatched_group_todos,
                    mapped_legacy_project_todos, name_conflicts, created_at)
                VALUES ($version, $mappedGroups, $unmatchedGroups, $mappedLegacy, $conflicts, $createdAt);
                """;
            report.Parameters.AddWithValue("$version", version);
            report.Parameters.AddWithValue("$mappedGroups", mappedGroupTodos);
            report.Parameters.AddWithValue("$unmatchedGroups", unmatchedGroupTodos);
            report.Parameters.AddWithValue("$mappedLegacy", mappedLegacyProjectTodos);
            report.Parameters.AddWithValue("$conflicts", nameConflicts);
            report.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            await report.ExecuteNonQueryAsync(cancellationToken);

            await using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO schema_migrations(version, name, checksum, applied_at) VALUES ($version, $name, $checksum, $appliedAt);";
            record.Parameters.AddWithValue("$version", version);
            record.Parameters.AddWithValue("$name", name);
            record.Parameters.AddWithValue("$checksum", checksum);
            record.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
            await record.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name = $columnName;";
        check.Parameters.AddWithValue("$columnName", columnName);
        if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) > 0)
        {
            return;
        }

        await ExecuteAsync(
            connection,
            transaction,
            $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};",
            cancellationToken);
    }

    private static async Task<IReadOnlyList<(long Id, string Name)>> ReadNamedIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        var rows = new List<(long Id, string Name)>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return rows;
    }

    private static async Task<int> AssignProjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePredicate,
        long sourceId,
        long projectId,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = $"UPDATE todo_items SET managed_project_id = $projectId WHERE managed_project_id IS NULL AND {sourcePredicate};";
        update.Parameters.AddWithValue("$projectId", projectId);
        update.Parameters.AddWithValue("$sourceId", sourceId);
        return await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string NormalizeName(string value) =>
        value.Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant();

    private const string MigrationTableSql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            checksum TEXT NOT NULL,
            applied_at TEXT NOT NULL
        );
        """;

    private const string TodoReminderMigrationSql = """
        CREATE TABLE todo_reminders (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            todo_id INTEGER NOT NULL,
            scheduled_at TEXT NOT NULL,
            status TEXT NOT NULL,
            parent_reminder_id INTEGER NULL,
            delivered_at TEXT NULL,
            snoozed_at TEXT NULL,
            dismissed_at TEXT NULL,
            attempt_count INTEGER NOT NULL DEFAULT 0,
            last_error TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY(todo_id) REFERENCES todo_items(id) ON DELETE CASCADE,
            FOREIGN KEY(parent_reminder_id) REFERENCES todo_reminders(id)
        );

        CREATE INDEX idx_todo_reminders_due ON todo_reminders(status, scheduled_at);
        CREATE INDEX idx_todo_reminders_todo ON todo_reminders(todo_id, created_at DESC);
        """;

    private const string TodoPinningMigrationSql = """
        ALTER TABLE todo_items ADD COLUMN is_pinned INTEGER NOT NULL DEFAULT 0;
        CREATE INDEX idx_todo_active_pinned_due
            ON todo_items(status, is_pinned DESC, due_at, updated_at DESC);
        """;

    private const string TodoGroupsMigrationSql = """
        CREATE TABLE todo_groups (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            normalized_name TEXT NOT NULL,
            created_at TEXT NOT NULL,
            UNIQUE(normalized_name)
        );

        ALTER TABLE todo_items
            ADD COLUMN group_id INTEGER NULL REFERENCES todo_groups(id);

        CREATE INDEX idx_todo_items_group_id ON todo_items(group_id);
        """;

    private const string TodoGroupColorsMigrationSql = """
        ALTER TABLE todo_groups
            ADD COLUMN color TEXT NOT NULL DEFAULT '#4B5563';
        """;

    private const string TodoProjectAssignmentMigrationSql = """
        CREATE INDEX IF NOT EXISTS idx_todo_items_managed_project_id
            ON todo_items(managed_project_id);

        CREATE TABLE IF NOT EXISTS schema_migration_reports (
            version INTEGER PRIMARY KEY,
            mapped_group_todos INTEGER NOT NULL,
            unmatched_group_todos INTEGER NOT NULL,
            mapped_legacy_project_todos INTEGER NOT NULL,
            name_conflicts INTEGER NOT NULL,
            created_at TEXT NOT NULL
        );
        """;

    /// <summary>
    /// 数据库模式 SQL：会话、消息、项目、分类、紧急度、待办、规则、别名、偏移量、采集源设置、审计日志。
    /// 所有对象均使用 CREATE ... IF NOT EXISTS，保证可重复执行。
    /// </summary>
    private const string SchemaSql = """
        -- 会话表：一个群聊/私聊对应一行
        CREATE TABLE IF NOT EXISTS chat_sessions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            source TEXT NOT NULL,
            source_chat_key TEXT NOT NULL,
            name TEXT NOT NULL,
            chat_type TEXT NOT NULL,
            project_id INTEGER NULL,
            is_priority INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(source, source_chat_key)
        );

        -- 消息表：核心表，按 (source, source_message_key) 去重
        CREATE TABLE IF NOT EXISTS messages (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            source TEXT NOT NULL,
            source_message_key TEXT NOT NULL,
            chat_session_id INTEGER NOT NULL,
            chat_name TEXT NOT NULL,
            sender_name TEXT NOT NULL,
            content TEXT NOT NULL,
            message_type TEXT NOT NULL,
            sent_at TEXT NOT NULL,
            captured_at TEXT NOT NULL,
            is_mention_me INTEGER NOT NULL DEFAULT 0,
            raw_excerpt TEXT NULL,
            UNIQUE(source, source_message_key)
        );

        -- 项目表
        CREATE TABLE IF NOT EXISTS projects (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL UNIQUE,
            code TEXT NULL,
            color TEXT NULL,
            is_active INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        -- 消息分类结果表
        CREATE TABLE IF NOT EXISTS message_classifications (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            message_id INTEGER NOT NULL,
            project_id INTEGER NULL,
            category TEXT NOT NULL,
            confidence REAL NOT NULL,
            reason TEXT NOT NULL,
            classifier TEXT NOT NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY(message_id) REFERENCES messages(id)
        );

        -- 紧急度评分表
        CREATE TABLE IF NOT EXISTS urgency_scores (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            message_id INTEGER NOT NULL,
            score INTEGER NOT NULL,
            priority TEXT NOT NULL,
            reason TEXT NOT NULL,
            calculated_at TEXT NOT NULL,
            FOREIGN KEY(message_id) REFERENCES messages(id)
        );

        -- 模型判断独立于采集事务；按消息唯一保存，待办必须经用户确认。
        CREATE TABLE IF NOT EXISTS todo_suggestions (
            message_id INTEGER PRIMARY KEY,
            decision TEXT NOT NULL,
            priority TEXT NOT NULL,
            title TEXT NULL,
            description TEXT NULL,
            due_at TEXT NULL,
            reason TEXT NOT NULL,
            question TEXT NULL,
            review_status TEXT NOT NULL,
            analyzed_at TEXT NOT NULL,
            FOREIGN KEY(message_id) REFERENCES messages(id)
        );

        -- 待办表
        CREATE TABLE IF NOT EXISTS todo_items (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            source_message_id INTEGER NULL,
            project_id INTEGER NULL,
            title TEXT NOT NULL,
            description TEXT NULL,
            status TEXT NOT NULL,
            priority TEXT NOT NULL,
            due_at TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            completed_at TEXT NULL,
            is_auto_created INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY(source_message_id) REFERENCES messages(id)
        );

        -- 项目分类规则表
        CREATE TABLE IF NOT EXISTS project_rules (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id INTEGER NOT NULL,
            rule_type TEXT NOT NULL,
            pattern TEXT NOT NULL,
            weight INTEGER NOT NULL DEFAULT 10,
            is_active INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        -- 用户别名表（@我 检测用）
        CREATE TABLE IF NOT EXISTS user_aliases (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            alias TEXT NOT NULL UNIQUE,
            is_active INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL
        );

        -- 处理偏移量表（增量采集进度）
        CREATE TABLE IF NOT EXISTS processing_offsets (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            adapter_name TEXT NOT NULL UNIQUE,
            offset_value TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        -- 采集源设置表
        CREATE TABLE IF NOT EXISTS capture_source_settings (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            source TEXT NOT NULL,
            display_name TEXT NOT NULL,
            kind TEXT NOT NULL,
            location TEXT NOT NULL,
            is_enabled INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(source, kind)
        );

        -- 关注群表（只展示关注群的消息）
        CREATE TABLE IF NOT EXISTS followed_chats (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            chat_name TEXT NOT NULL UNIQUE,
            is_active INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL
        );

        -- 关注项目表（群名包含项目名时，该群消息重点关注）
        CREATE TABLE IF NOT EXISTS followed_projects (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            project_name TEXT NOT NULL UNIQUE,
            is_active INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL
        );


        -- 关注项目关键字表：同一个项目可配置多个匹配关键字
        CREATE TABLE IF NOT EXISTS followed_project_keywords (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id INTEGER NOT NULL,
            keyword TEXT NOT NULL COLLATE NOCASE,
            created_at TEXT NOT NULL,
            UNIQUE(project_id, keyword),
            FOREIGN KEY(project_id) REFERENCES followed_projects(id) ON DELETE CASCADE
        );
        -- 应用设置表（key-value 存储，用于关注群过滤模式等开关）
        CREATE TABLE IF NOT EXISTS app_settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        -- 审计日志表
        CREATE TABLE IF NOT EXISTS audit_logs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            entity_type TEXT NOT NULL,
            entity_id INTEGER NOT NULL,
            action TEXT NOT NULL,
            detail TEXT NULL,
            created_at TEXT NOT NULL
        );

        -- 消息常用查询索引
        CREATE INDEX IF NOT EXISTS idx_messages_sent_at ON messages(sent_at);
        CREATE INDEX IF NOT EXISTS idx_messages_chat_session ON messages(chat_session_id);
        CREATE INDEX IF NOT EXISTS idx_messages_mention ON messages(is_mention_me, sent_at);
        CREATE INDEX IF NOT EXISTS idx_followed_project_keywords_project ON followed_project_keywords(project_id);
        CREATE INDEX IF NOT EXISTS idx_messages_sent_at_id ON messages(sent_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_messages_chat_name ON messages(chat_name);
        CREATE INDEX IF NOT EXISTS idx_messages_source_key ON messages(source, source_message_key);
        CREATE INDEX IF NOT EXISTS idx_todo_status_priority ON todo_items(status, priority, due_at);
        CREATE INDEX IF NOT EXISTS idx_classification_project ON message_classifications(project_id, category);
        """;
}
