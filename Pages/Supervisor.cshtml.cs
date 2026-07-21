using Dapper;
using Npgsql;

namespace LineBotLogger.Pages;

// 主管處理區:沿用待審核頁整套回覆/AI 草稿/推送邏輯,只把處理的狀態換成 escalated,
// 並改用自己的對話清單查詢(轉交過來、且沒有新待回的對話)。
// logger 沿用 InboxModel 分類,因為共用的就是那條管線。
public class SupervisorModel : InboxModel
{
    public SupervisorModel(IConfiguration config, IHttpClientFactory httpFactory, ILogger<InboxModel> logger)
        : base(config, httpFactory, logger) { }

    protected override string SourceStatus => "escalated";

    // 有 escalated、且已無 new 的對話才在這頁(有 new 代表客人又來訊,該回待審核頁)
    protected override async Task<List<ConversationHead>> LoadHeadsAsync(NpgsqlConnection conn) =>
        (await conn.QueryAsync<ConversationHead>("""
            SELECT ConversationId,
                   COUNT(*) FILTER (WHERE Status = 'escalated')::int AS NewCount,
                   MAX(LineTimestamp) AS LastTs
            FROM Messages
            WHERE SourceType = 'user'
            GROUP BY ConversationId
            HAVING COUNT(*) FILTER (WHERE Status = 'new') = 0
               AND COUNT(*) FILTER (WHERE Status = 'escalated') > 0
            ORDER BY LastTs ASC
            """)).AsList();
}
