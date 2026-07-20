using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace LineBotLogger.Pages;

public class BlacklistModel : PageModel
{
    private readonly string _connStr;
    private readonly ILogger<BlacklistModel> _logger;

    public BlacklistModel(IConfiguration config, ILogger<BlacklistModel> logger)
    {
        _connStr = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");
        _logger = logger;
    }

    public record BlockedItem(string ConversationId, string? DisplayName, DateTime CreatedAt);

    public IReadOnlyList<BlockedItem> Items { get; private set; } = Array.Empty<BlockedItem>();

    public async Task OnGetAsync()
    {
        await using var conn = new NpgsqlConnection(_connStr);
        var rows = await conn.QueryAsync<BlockedItem>("""
            SELECT ConversationId, DisplayName, CreatedAt
            FROM Blacklist
            ORDER BY CreatedAt DESC
            """);
        Items = rows.AsList();
    }

    public async Task<IActionResult> OnPostRemoveAsync(string conversationId)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.ExecuteAsync(
            "DELETE FROM Blacklist WHERE ConversationId = @Cid", new { Cid = conversationId });
        _logger.LogInformation("Conversation {ConversationId} unblocked", conversationId);
        return RedirectToPage();
    }
}
