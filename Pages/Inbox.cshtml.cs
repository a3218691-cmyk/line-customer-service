using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace LineBotLogger.Pages;

public class InboxModel : PageModel
{
    private readonly string _connStr;
    private readonly string _accessToken;
    private readonly string _aiBaseUrl;
    private readonly string _aiModel;
    private readonly string _aiApiKey;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<InboxModel> _logger;

    public InboxModel(IConfiguration config, IHttpClientFactory httpFactory, ILogger<InboxModel> logger)
    {
        _connStr = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");
        _accessToken = config["Line:ChannelAccessToken"] ?? "";
        _aiBaseUrl = config["Ai:BaseUrl"] ?? "";
        _aiModel = config["Ai:Model"] ?? "";
        _aiApiKey = config["Ai:ApiKey"] ?? "";
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public record InboxItem(long Id, string ConversationId, string? MessageText, DateTime LineTimestamp);

    public IReadOnlyList<InboxItem> Items { get; private set; } = Array.Empty<InboxItem>();

    [TempData]
    public string? ErrorMessage { get; set; }

    // 草稿只走 TempData 回填畫面,不落庫(TempData 序列化器不支援 long,用字串存 Id)
    [TempData]
    public string? DraftForId { get; set; }

    [TempData]
    public string? DraftText { get; set; }

    public async Task OnGetAsync()
    {
        await using var conn = new NpgsqlConnection(_connStr);
        var rows = await conn.QueryAsync<InboxItem>("""
            SELECT Id, ConversationId, MessageText, LineTimestamp
            FROM Messages
            WHERE SourceType = 'user' AND Status = 'new'
            ORDER BY LineTimestamp DESC
            """);
        Items = rows.AsList();
    }

    public async Task<IActionResult> OnPostAsync(long id, string? replyText)
    {
        replyText = replyText?.Trim();
        if (string.IsNullOrEmpty(replyText))
        {
            ErrorMessage = "回覆內容不可空白";
            return RedirectToPage();
        }

        await using var conn = new NpgsqlConnection(_connStr);
        var conversationId = await conn.ExecuteScalarAsync<string?>(
            "SELECT ConversationId FROM Messages WHERE Id = @Id AND Status = 'new'", new { Id = id });
        if (conversationId is null)
        {
            ErrorMessage = $"找不到待回訊息(Id={id})";
            return RedirectToPage();
        }

        var error = await PushAsync(conversationId, replyText);
        if (error is not null)
        {
            // 送失敗就維持 new、不寫 Replies
            ErrorMessage = error;
            return RedirectToPage();
        }

        // 送出成功才落庫,兩件事一起成立
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync(
            "INSERT INTO Replies (MessageId, FinalText) VALUES (@MessageId, @FinalText)",
            new { MessageId = id, FinalText = replyText }, tx);
        await conn.ExecuteAsync(
            "UPDATE Messages SET Status = 'replied' WHERE Id = @Id", new { Id = id }, tx);
        await tx.CommitAsync();

        _logger.LogInformation("Reply sent for message {MessageId}", id);
        return RedirectToPage();
    }

    /// <summary>成功回 null,失敗回要顯示的錯誤訊息。</summary>
    private async Task<string?> PushAsync(string to, string text)
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
            return "Line:ChannelAccessToken 未設定,無法送出";

        var body = JsonSerializer.Serialize(new
        {
            to,
            messages = new[] { new { type = "text", text } },
        });

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.line.me/v2/bot/message/push");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        try
        {
            using var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
                return null;

            var respBody = await resp.Content.ReadAsStringAsync();
            _logger.LogError("LINE push failed: {Status} {Body}", (int)resp.StatusCode, respBody);
            return $"LINE 回覆送出失敗(HTTP {(int)resp.StatusCode}),訊息維持待回。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LINE push threw");
            return $"LINE 回覆送出失敗:{ex.Message}";
        }
    }

    public async Task<IActionResult> OnPostDraftAsync(long id)
    {
        if (string.IsNullOrWhiteSpace(_aiBaseUrl) || string.IsNullOrWhiteSpace(_aiApiKey))
        {
            // 未設定就地返回,不發任何 HTTP
            ErrorMessage = "AI 尚未設定(Ai:BaseUrl / Ai:ApiKey),無法起草";
            return RedirectToPage();
        }

        await using var conn = new NpgsqlConnection(_connStr);
        var target = await conn.QuerySingleOrDefaultAsync<DraftTarget>(
            "SELECT ConversationId, LineTimestamp FROM Messages WHERE Id = @Id AND Status = 'new'",
            new { Id = id });
        if (target is null)
        {
            ErrorMessage = $"找不到待回訊息(Id={id})";
            return RedirectToPage();
        }

        // 同對話最近 10 則(含這次要回的那則),依時間升冪;同時間時客戶訊息排在回覆之前
        var history = await conn.QueryAsync<HistoryRow>("""
            SELECT Role, Text FROM (
                SELECT m.LineTimestamp AS Ts, 0 AS Seq, 'user' AS Role, m.MessageText AS Text
                FROM Messages m
                WHERE m.ConversationId = @Cid AND m.SourceType = 'user'
                  AND m.MessageText IS NOT NULL AND m.LineTimestamp <= @Ts
                UNION ALL
                SELECT m.LineTimestamp AS Ts, 1 AS Seq, 'assistant' AS Role, r.FinalText AS Text
                FROM Replies r
                JOIN Messages m ON m.Id = r.MessageId
                WHERE m.ConversationId = @Cid AND m.LineTimestamp <= @Ts
            ) h
            ORDER BY Ts DESC, Seq DESC
            LIMIT 10
            """, new { Cid = target.ConversationId, Ts = target.LineTimestamp });

        var messages = new List<object>
        {
            new { role = "system", content = SystemPrompt },
        };
        foreach (var row in history.Reverse())
            messages.Add(new { role = row.Role, content = row.Text });

        var (draft, error) = await DraftAsync(messages);
        if (error is not null)
        {
            ErrorMessage = error;
            return RedirectToPage();
        }

        DraftForId = id.ToString();
        DraftText = draft;
        return RedirectToPage();
    }

    private record DraftTarget(string ConversationId, DateTime LineTimestamp);

    private record HistoryRow(string Role, string Text);

    private const string SystemPrompt =
        "你是繁體中文客服人員。語氣禮貌簡潔,回覆控制在 200 字以內。" +
        "不要編造訂單、金額、時程等資訊;資訊不足時主動向客戶詢問。只輸出回覆內容本身。";

    /// <summary>成功回 (草稿, null),失敗回 (null, 錯誤訊息)。</summary>
    private async Task<(string? Draft, string? Error)> DraftAsync(IReadOnlyList<object> messages)
    {
        var body = JsonSerializer.Serialize(new { model = _aiModel, messages });

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"{_aiBaseUrl.TrimEnd('/')}/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _aiApiKey);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        try
        {
            using var resp = await http.SendAsync(req);
            var respBody = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("AI draft failed: {Status} {Body}", (int)resp.StatusCode, respBody);
                return (null, $"AI 起草失敗(HTTP {(int)resp.StatusCode}),請手動回覆。");
            }

            using var doc = JsonDocument.Parse(respBody);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()?.Trim();

            return string.IsNullOrEmpty(content)
                ? (null, "AI 沒有產生內容,請手動回覆。")
                : (content, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI draft threw");
            return (null, $"AI 起草失敗:{ex.Message}");
        }
    }

    // 台灣無日光節約,固定 UTC+8
    public static string ToTaipei(DateTime utc) =>
        utc.ToUniversalTime().AddHours(8).ToString("yyyy-MM-dd HH:mm");

    public static string ShortId(string id) =>
        id.Length <= 8 ? id : id[..8] + "…";
}
