using Microsoft.Data.Sqlite;

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
    }

    /// <summary>执行一条无返回 SQL。</summary>
    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

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
        CREATE INDEX IF NOT EXISTS idx_messages_sent_at_id ON messages(sent_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_messages_chat_name ON messages(chat_name);
        CREATE INDEX IF NOT EXISTS idx_messages_source_key ON messages(source, source_message_key);
        CREATE INDEX IF NOT EXISTS idx_todo_status_priority ON todo_items(status, priority, due_at);
        CREATE INDEX IF NOT EXISTS idx_classification_project ON message_classifications(project_id, category);
        """;
}
