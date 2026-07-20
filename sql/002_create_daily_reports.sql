-- PostgreSQL
CREATE TABLE IF NOT EXISTS DailyReports (
    Id         BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    GroupId    TEXT        NOT NULL,
    ReportDate DATE        NOT NULL,
    SentAt     TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT UQ_DailyReports_Group_Date UNIQUE (GroupId, ReportDate)
);
