using Microsoft.Data.Sqlite;

namespace WechatDashboard.Infrastructure.Persistence;

public sealed class SqliteDatabaseInitializer
{
    private readonly string _databasePath;

    public SqliteDatabaseInitializer(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken);
        await ExecuteAsync(connection, SchemaSql, cancellationToken);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string SchemaSql = """
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

        CREATE TABLE IF NOT EXISTS projects (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL UNIQUE,
            code TEXT NULL,
            color TEXT NULL,
            is_active INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

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

        CREATE TABLE IF NOT EXISTS urgency_scores (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            message_id INTEGER NOT NULL,
            score INTEGER NOT NULL,
            priority TEXT NOT NULL,
            reason TEXT NOT NULL,
            calculated_at TEXT NOT NULL,
            FOREIGN KEY(message_id) REFERENCES messages(id)
        );

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

        CREATE TABLE IF NOT EXISTS user_aliases (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            alias TEXT NOT NULL UNIQUE,
            is_active INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS processing_offsets (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            adapter_name TEXT NOT NULL UNIQUE,
            offset_value TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

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

        CREATE TABLE IF NOT EXISTS audit_logs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            entity_type TEXT NOT NULL,
            entity_id INTEGER NOT NULL,
            action TEXT NOT NULL,
            detail TEXT NULL,
            created_at TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_messages_sent_at ON messages(sent_at);
        CREATE INDEX IF NOT EXISTS idx_messages_chat_session ON messages(chat_session_id);
        CREATE INDEX IF NOT EXISTS idx_messages_mention ON messages(is_mention_me, sent_at);
        CREATE INDEX IF NOT EXISTS idx_messages_captured_at_id ON messages(captured_at DESC, id DESC);
        CREATE INDEX IF NOT EXISTS idx_messages_source_key ON messages(source, source_message_key);
        CREATE INDEX IF NOT EXISTS idx_todo_status_priority ON todo_items(status, priority, due_at);
        CREATE INDEX IF NOT EXISTS idx_classification_project ON message_classifications(project_id, category);
        """;
}
