-- PostgreSQL
CREATE TABLE IF NOT EXISTS Blacklist (
    ConversationId TEXT PRIMARY KEY,
    DisplayName    TEXT        NULL,     -- 封鎖當下的名稱,只為了列表好認
    CreatedAt      TIMESTAMPTZ NOT NULL DEFAULT now()
);
