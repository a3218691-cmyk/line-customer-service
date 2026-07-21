-- PostgreSQL
CREATE TABLE IF NOT EXISTS KnowledgeBase (
    Id        BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    Question  TEXT        NOT NULL,
    Answer    TEXT        NOT NULL,
    CreatedAt TIMESTAMPTZ NOT NULL DEFAULT now()
);
