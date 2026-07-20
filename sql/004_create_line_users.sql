-- PostgreSQL
CREATE TABLE IF NOT EXISTS LineUsers (
    ConversationId TEXT PRIMARY KEY,     -- 一對一對話的 userId
    DisplayName    TEXT        NOT NULL, -- LINE Profile API 取回的顯示名稱
    CreatedAt      TIMESTAMPTZ NOT NULL DEFAULT now()
);
