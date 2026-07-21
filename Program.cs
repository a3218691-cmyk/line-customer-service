using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Authentication.Cookies;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHostedService<DailyReportService>();
// /Index、/Inbox、/Blacklist 需登入;webhook / health 是 Minimal API,不受影響
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizePage("/Index");
    o.Conventions.AuthorizePage("/Inbox");
    o.Conventions.AuthorizePage("/Blacklist");
    o.Conventions.AuthorizePage("/KnowledgeBase");
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Login";
        o.AccessDeniedPath = "/Login";
    });
builder.Services.AddAuthorization();
builder.Services.AddHttpClient();
// 讓 Razor 直接輸出中文,不要跳脫成 &#x...;
builder.Services.Configure<Microsoft.Extensions.WebEncoders.WebEncoderOptions>(o =>
    o.TextEncoderSettings = new System.Text.Encodings.Web.TextEncoderSettings(
        System.Text.Unicode.UnicodeRanges.All));
var app = builder.Build();

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

// 審片小幫手:純前端單檔工具,放 Protected/(非 wwwroot)靠登入把關;檔案缺失回 404 不拖垮主服務
var reviewPath = Path.Combine(app.Environment.ContentRootPath, "Protected", "review.html");
app.MapGet("/review", () => File.Exists(reviewPath)
        ? Results.Content(File.ReadAllText(reviewPath), "text/html; charset=utf-8")
        : Results.NotFound())
    .RequireAuthorization();

var connStr = app.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");
var channelSecret = app.Configuration["Line:ChannelSecret"]
    ?? throw new InvalidOperationException("Missing Line:ChannelSecret");
// 沒設密碼就不准啟動,避免 /inbox 靜默開放
if (string.IsNullOrWhiteSpace(app.Configuration["Auth:Password"]))
    throw new InvalidOperationException("Missing Auth:Password");

const string InsertSql = """
    INSERT INTO Messages (ConversationId, SourceType, LineUserId, MessageType, MessageText, LineTimestamp, RawJson)
    VALUES (@ConversationId, @SourceType, @LineUserId, @MessageType, @MessageText, @LineTimestamp, @RawJson);
    """;

app.MapGet("/health", async () =>
{
    try
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.ExecuteScalarAsync<int>("SELECT 1");
        return Results.Ok("OK");
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Health check failed: DB unreachable");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/webhook", async (HttpRequest request) =>
{
    // 驗簽必須用原始字串,不能反序列化再重組
    string rawBody;
    using (var reader = new StreamReader(request.Body, Encoding.UTF8))
        rawBody = await reader.ReadToEndAsync();

    if (!VerifySignature(request, rawBody, channelSecret))
    {
        app.Logger.LogWarning("Webhook signature verification failed");
        return Results.Unauthorized();
    }

    JsonDocument doc;
    try
    {
        doc = JsonDocument.Parse(rawBody);
    }
    catch (JsonException ex)
    {
        app.Logger.LogWarning(ex, "Webhook body is not valid JSON");
        return Results.BadRequest();
    }

    using (doc)
    {
        if (!doc.RootElement.TryGetProperty("events", out var events) ||
            events.ValueKind != JsonValueKind.Array)
        {
            app.Logger.LogWarning("Webhook body has no events array");
            return Results.BadRequest();
        }

        var total = 0;
        var skipped = 0;
        await using var conn = new NpgsqlConnection(connStr);

        foreach (var ev in events.EnumerateArray())
        {
            total++;

            // 只處理 group(群組)與 user(一對一)來源,room 等其他來源略過
            if (!ev.TryGetProperty("source", out var source) ||
                !source.TryGetProperty("type", out var sourceTypeProp))
            {
                skipped++;
                continue;
            }

            var sourceType = sourceTypeProp.GetString();
            var conversationIdProp = sourceType switch
            {
                "group" => "groupId",
                "user" => "userId",
                _ => null,
            };
            if (conversationIdProp is null)
            {
                skipped++;
                continue;
            }

            try
            {
                var eventType = ev.GetProperty("type").GetString()!;
                var messageType = eventType;
                string? messageText = null;

                if (eventType == "message" && ev.TryGetProperty("message", out var msg))
                {
                    messageType = msg.GetProperty("type").GetString()!;
                    if (messageType == "text")
                        messageText = msg.GetProperty("text").GetString();
                }

                var conversationId = source.GetProperty(conversationIdProp).GetString()!;

                // 黑名單的對話直接不寫 DB
                if (await conn.ExecuteScalarAsync<bool>(
                        "SELECT EXISTS (SELECT 1 FROM Blacklist WHERE ConversationId = @Cid)",
                        new { Cid = conversationId }))
                {
                    app.Logger.LogInformation("Blacklisted conversation, event not saved");
                    continue;
                }

                await conn.ExecuteAsync(InsertSql, new
                {
                    ConversationId = conversationId,
                    SourceType = sourceType,
                    LineUserId = source.TryGetProperty("userId", out var uid) ? uid.GetString() : null,
                    MessageType = messageType,
                    MessageText = messageText,
                    LineTimestamp = DateTimeOffset
                        .FromUnixTimeMilliseconds(ev.GetProperty("timestamp").GetInt64())
                        .UtcDateTime,
                    RawJson = ev.GetRawText(),
                });
            }
            catch (Exception ex)
            {
                // 單筆失敗不擋整個 request,LINE 重送反而會重複
                app.Logger.LogError(ex, "Failed to save event: {RawJson}", ev.GetRawText());
            }
        }

        app.Logger.LogInformation(
            "Webhook processed: {Total} events, {Skipped} unsupported source skipped", total, skipped);
    }

    return Results.Ok();
});

// 啟動時建表。內容與 sql/001 ~ sql/005 同步(內嵌避免容器漏複製檔案)
// 失敗直接讓程式起不來,不要連不上 DB 還假裝正常
const string SchemaSql = """
    CREATE TABLE IF NOT EXISTS Messages (
        Id             BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        ConversationId TEXT        NOT NULL,
        SourceType     TEXT        NOT NULL,
        Status         TEXT        NOT NULL DEFAULT 'new',
        LineUserId     TEXT        NULL,
        MessageType    TEXT        NOT NULL,
        MessageText    TEXT        NULL,
        LineTimestamp  TIMESTAMPTZ NOT NULL,
        RawJson        TEXT        NOT NULL,
        CreatedAt      TIMESTAMPTZ NOT NULL DEFAULT now()
    );
    CREATE INDEX IF NOT EXISTS IX_Messages_Conversation_Ts ON Messages (ConversationId, LineTimestamp);

    CREATE TABLE IF NOT EXISTS DailyReports (
        Id         BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        GroupId    TEXT        NOT NULL,
        ReportDate DATE        NOT NULL,
        SentAt     TIMESTAMPTZ NOT NULL DEFAULT now(),
        CONSTRAINT UQ_DailyReports_Group_Date UNIQUE (GroupId, ReportDate)
    );

    CREATE TABLE IF NOT EXISTS Replies (
        Id        BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        MessageId BIGINT      NOT NULL,
        FinalText TEXT        NOT NULL,
        SentAt    TIMESTAMPTZ NOT NULL DEFAULT now()
    );

    CREATE TABLE IF NOT EXISTS LineUsers (
        ConversationId TEXT PRIMARY KEY,
        DisplayName    TEXT        NOT NULL,
        CreatedAt      TIMESTAMPTZ NOT NULL DEFAULT now()
    );

    CREATE TABLE IF NOT EXISTS Blacklist (
        ConversationId TEXT PRIMARY KEY,
        DisplayName    TEXT        NULL,
        CreatedAt      TIMESTAMPTZ NOT NULL DEFAULT now()
    );

    CREATE TABLE IF NOT EXISTS KnowledgeBase (
        Id        BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        Question  TEXT        NOT NULL,
        Answer    TEXT        NOT NULL,
        CreatedAt TIMESTAMPTZ NOT NULL DEFAULT now()
    );
    """;

try
{
    await using var initConn = new NpgsqlConnection(connStr);
    await initConn.ExecuteAsync(SchemaSql);
    app.Logger.LogInformation("Database schema ready");
}
catch (Exception ex)
{
    app.Logger.LogCritical(ex, "建表失敗,程式終止");
    throw;
}

app.Run();

static bool VerifySignature(HttpRequest request, string rawBody, string channelSecret)
{
    if (!request.Headers.TryGetValue("x-line-signature", out var header))
        return false;

    byte[] provided;
    try
    {
        provided = Convert.FromBase64String(header.ToString());
    }
    catch (FormatException)
    {
        return false;
    }

    var computed = HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(channelSecret),
        Encoding.UTF8.GetBytes(rawBody));

    return CryptographicOperations.FixedTimeEquals(computed, provided);
}
