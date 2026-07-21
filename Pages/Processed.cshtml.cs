using Dapper;
using Npgsql;

namespace LineBotLogger.Pages;

// 已處理:唯讀看板,列出已回覆 / 已略過(處理完)的對話,只看完整往來,不提供任何操作。
public class ProcessedModel : InboxModel
{
    public ProcessedModel(IConfiguration config, IHttpClientFactory httpFactory, ILogger<InboxModel> logger)
        : base(config, httpFactory, logger) { }

    // 用一個不對應任何真實狀態的值:讓歷史查詢(Status <> @Src)撈出全部往來,且「待處理」訊息恆為空
    protected override string SourceStatus => "none";

    // 已無 new、也無 escalated,但有處理過(replied / skipped)紀錄的對話;最近處理的排前面
    protected override async Task<List<ConversationHead>> LoadHeadsAsync(NpgsqlConnection conn) =>
        (await conn.QueryAsync<ConversationHead>("""
            SELECT ConversationId, 0 AS NewCount, MAX(LineTimestamp) AS LastTs
            FROM Messages
            WHERE SourceType = 'user'
            GROUP BY ConversationId
            HAVING COUNT(*) FILTER (WHERE Status = 'new') = 0
               AND COUNT(*) FILTER (WHERE Status = 'escalated') = 0
               AND COUNT(*) FILTER (WHERE Status IN ('replied','skipped')) > 0
            ORDER BY LastTs DESC
            """)).AsList();

    // 已處理頁沒有「待處理訊息」區
    protected override Task<List<TimelineEntry>> LoadFreshAsync(NpgsqlConnection conn, string cid) =>
        Task.FromResult(new List<TimelineEntry>());
}
