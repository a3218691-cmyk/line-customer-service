-- PostgreSQL
CREATE TABLE IF NOT EXISTS Replies (
    Id        BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    MessageId BIGINT      NOT NULL,   -- 對應 Messages.Id
    FinalText TEXT        NOT NULL,   -- 實際送出去的文字
    SentAt    TIMESTAMPTZ NOT NULL DEFAULT now()
);
