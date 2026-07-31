CREATE TABLE IF NOT EXISTS echo_messages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    supporter_identifier TEXT NOT NULL,
    supporter_name TEXT NOT NULL,
    plan_name TEXT NOT NULL,
    subject TEXT NOT NULL,
    message TEXT NOT NULL,
    contact TEXT NOT NULL,
    app_version TEXT NOT NULL,
    language TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_echo_messages_created_at
    ON echo_messages(created_at DESC);
