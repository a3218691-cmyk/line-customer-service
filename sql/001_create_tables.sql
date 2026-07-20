-- PostgreSQL。識別字一律不加引號,讓 Postgres 統一折成小寫(Dapper 對應大小寫不敏感)
CREATE TABLE IF NOT EXISTS Messages (
    Id             BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    ConversationId TEXT        NOT NULL,   -- group 放 groupId,user 放 userId
    SourceType     TEXT        NOT NULL,   -- group / user
    Status         TEXT        NOT NULL DEFAULT 'new',  -- new / drafted / replied / ignored
    LineUserId     TEXT        NULL,
    MessageType    TEXT        NOT NULL,
    MessageText    TEXT        NULL,
    LineTimestamp  TIMESTAMPTZ NOT NULL,
    RawJson        TEXT        NOT NULL,
    CreatedAt      TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS IX_Messages_Conversation_Ts ON Messages (ConversationId, LineTimestamp);
