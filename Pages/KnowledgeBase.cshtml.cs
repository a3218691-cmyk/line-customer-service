using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace LineBotLogger.Pages;

public class KnowledgeBaseModel : PageModel
{
    private readonly string _connStr;
    private readonly ILogger<KnowledgeBaseModel> _logger;

    public KnowledgeBaseModel(IConfiguration config, ILogger<KnowledgeBaseModel> logger)
    {
        _connStr = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");
        _logger = logger;
    }

    // Id 用 long 對應 BIGINT
    public record FaqItem(long Id, string Question, string Answer, DateTime CreatedAt);

    public IReadOnlyList<FaqItem> Items { get; private set; } = Array.Empty<FaqItem>();

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        await using var conn = new NpgsqlConnection(_connStr);
        var rows = await conn.QueryAsync<FaqItem>("""
            SELECT Id, Question, Answer, CreatedAt
            FROM KnowledgeBase
            ORDER BY CreatedAt DESC
            """);
        Items = rows.AsList();
    }

    public async Task<IActionResult> OnPostAddAsync(string question, string answer)
    {
        question = question?.Trim() ?? "";
        answer = answer?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer))
        {
            ErrorMessage = "問題與答案都不可空白";
            return RedirectToPage();
        }

        await using var conn = new NpgsqlConnection(_connStr);
        await conn.ExecuteAsync(
            "INSERT INTO KnowledgeBase (Question, Answer) VALUES (@Question, @Answer)",
            new { Question = question, Answer = answer });
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateAsync(long id, string question, string answer)
    {
        question = question?.Trim() ?? "";
        answer = answer?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer))
        {
            ErrorMessage = "問題與答案都不可空白";
            return RedirectToPage();
        }

        await using var conn = new NpgsqlConnection(_connStr);
        await conn.ExecuteAsync(
            "UPDATE KnowledgeBase SET Question = @Question, Answer = @Answer WHERE Id = @Id",
            new { Id = id, Question = question, Answer = answer });
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(long id)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.ExecuteAsync("DELETE FROM KnowledgeBase WHERE Id = @Id", new { Id = id });
        _logger.LogInformation("KnowledgeBase entry {Id} removed", id);
        return RedirectToPage();
    }
}
