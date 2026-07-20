using Dapper;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace LineBotLogger.Pages;

public class IndexModel : PageModel
{
    private readonly string _connStr;

    public IndexModel(IConfiguration config)
    {
        _connStr = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");
    }

    /// <summary>待回對話數,口徑須與 Inbox 卡片數一致。</summary>
    public int PendingConversations { get; private set; }

    public async Task OnGetAsync()
    {
        await using var conn = new NpgsqlConnection(_connStr);
        // Postgres COUNT 回 bigint,一定要 ::int 才接得住
        PendingConversations = await conn.ExecuteScalarAsync<int>("""
            SELECT COUNT(DISTINCT ConversationId)::int
            FROM Messages
            WHERE SourceType = 'user' AND Status = 'new'
            """);
    }
}
