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

    /// <summary>
    /// 對話氣泡一則。Role = user(客戶) / assistant(客服)。
    /// Ts 是排序用時間(一律客人來訊時間),DisplayTs 才是畫面上要顯示的時間。
    /// </summary>
    public record TimelineEntry(
        string Role, string? Text, DateTime Ts, DateTime DisplayTs, string? MessageType, string Status);

    public record ConversationCard(
        string ConversationId,
        string DisplayName,
        int NewCount,
        IReadOnlyList<TimelineEntry> History,
        IReadOnlyList<TimelineEntry> NewMessages);

    public IReadOnlyList<ConversationCard> Cards { get; private set; } = Array.Empty<ConversationCard>();

    [TempData]
    public string? ErrorMessage { get; set; }

    // 草稿只走 TempData 回填畫面,不落庫
    [TempData]
    public string? DraftForId { get; set; }

    [TempData]
    public string? DraftText { get; set; }

    public async Task OnGetAsync()
    {
        await using var conn = new NpgsqlConnection(_connStr);

        // 有待處理訊息的對話,依該對話最新時間排序(各看板的口徑在 LoadHeadsAsync)
        var heads = await LoadHeadsAsync(conn);

        if (heads.Count == 0)
            return;

        var names = await ResolveDisplayNamesAsync(conn, heads.Select(h => h.ConversationId).ToList());

        var cards = new List<ConversationCard>(heads.Count);
        foreach (var head in heads)
        {
            // 歷史:已回覆/已略過的客戶訊息 + 客服回覆,取最近 20 筆再轉回升冪
            // 同時間時客戶訊息排在客服回覆之前
            var history = (await conn.QueryAsync<TimelineEntry>("""
                SELECT Role, Text, Ts, DisplayTs, MessageType, Status FROM (
                    SELECT m.LineTimestamp AS Ts, m.LineTimestamp AS DisplayTs, 0 AS Seq, 'user' AS Role,
                           m.MessageText AS Text, m.MessageType AS MessageType, m.Status AS Status
                    FROM Messages m
                    WHERE m.ConversationId = @Cid AND m.SourceType = 'user' AND m.Status <> @Src
                    UNION ALL
                    SELECT m.LineTimestamp AS Ts, r.SentAt AS DisplayTs, 1 AS Seq, 'assistant' AS Role,
                           r.FinalText AS Text, 'text' AS MessageType, 'replied' AS Status
                    FROM Replies r
                    JOIN Messages m ON m.Id = r.MessageId
                    WHERE m.ConversationId = @Cid
                ) h
                ORDER BY Ts DESC, Seq DESC
                LIMIT 20
                """, new { Cid = head.ConversationId, Src = SourceStatus })).Reverse().ToList();

            var fresh = await LoadFreshAsync(conn, head.ConversationId);

            names.TryGetValue(head.ConversationId, out var name);
            cards.Add(new ConversationCard(
                head.ConversationId,
                string.IsNullOrWhiteSpace(name) ? ShortId(head.ConversationId) : name,
                head.NewCount, history, fresh));
        }

        Cards = cards;
    }

    /// <summary>這個看板處理的「待處理」狀態:待審核=new,主管處理區=escalated。</summary>
    protected virtual string SourceStatus => "new";

    /// <summary>撈這個看板要顯示的對話清單(各看板篩選條件不同)。</summary>
    protected virtual async Task<List<ConversationHead>> LoadHeadsAsync(NpgsqlConnection conn) =>
        (await conn.QueryAsync<ConversationHead>("""
            SELECT ConversationId, COUNT(*)::int AS NewCount, MAX(LineTimestamp) AS LastTs
            FROM Messages
            WHERE SourceType = 'user' AND Status = @Src
            GROUP BY ConversationId
            ORDER BY LastTs ASC
            """, new { Src = SourceStatus })).AsList();

    /// <summary>撈這個對話的「待處理」訊息(卡片下方新訊息區);已處理頁沒有這一區。</summary>
    protected virtual async Task<List<TimelineEntry>> LoadFreshAsync(NpgsqlConnection conn, string cid) =>
        (await conn.QueryAsync<TimelineEntry>("""
            SELECT 'user' AS Role, MessageText AS Text, LineTimestamp AS Ts,
                   LineTimestamp AS DisplayTs, MessageType, Status
            FROM Messages
            WHERE ConversationId = @Cid AND SourceType = 'user' AND Status = @Src
            ORDER BY LineTimestamp
            """, new { Cid = cid, Src = SourceStatus })).AsList();

    protected record ConversationHead(string ConversationId, int NewCount, DateTime LastTs);

    private record LineUserRow(string ConversationId, string DisplayName);

    /// <summary>查名字:先查 LineUsers,缺的才打 LINE Profile API。整批有總時限,逾時就這次不查。</summary>
    private async Task<Dictionary<string, string>> ResolveDisplayNamesAsync(
        NpgsqlConnection conn, IReadOnlyList<string> conversationIds)
    {
        var names = new Dictionary<string, string>();
        try
        {
            var known = await conn.QueryAsync<LineUserRow>(
                "SELECT ConversationId, DisplayName FROM LineUsers WHERE ConversationId = ANY(@Cids)",
                new { Cids = conversationIds.ToArray() });
            foreach (var row in known)
                names[row.ConversationId] = row.DisplayName;

            // 整批查名字的總時限,避免一堆查不到的對話把頁面拖垮
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            foreach (var cid in conversationIds)
            {
                if (names.ContainsKey(cid) || cts.IsCancellationRequested)
                    continue;

                var fetched = await FetchDisplayNameAsync(cid, cts.Token);
                if (cts.IsCancellationRequested)
                    continue; // 逾時的不寫庫,下次再試

                // 查不到就把短碼存成名字,讓它變成「已知」不再重複打 API
                var name = string.IsNullOrWhiteSpace(fetched) ? ShortId(cid) : fetched;
                names[cid] = name;
                await conn.ExecuteAsync("""
                    INSERT INTO LineUsers (ConversationId, DisplayName) VALUES (@Cid, @Name)
                    ON CONFLICT (ConversationId) DO NOTHING
                    """, new { Cid = cid, Name = name });
            }
        }
        catch (Exception ex)
        {
            // 名字只是點綴,查不到也要把頁面渲染出來
            _logger.LogWarning(ex, "Resolve display names failed");
        }
        return names;
    }

    private async Task<string?> FetchDisplayNameAsync(string userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
            return null;

        try
        {
            var http = _httpFactory.CreateClient();
            using var req = new HttpRequestMessage(
                HttpMethod.Get, $"https://api.line.me/v2/bot/profile/{Uri.EscapeDataString(userId)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("LINE profile failed: {Status}", (int)resp.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("displayName", out var prop) ? prop.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LINE profile threw");
            return null;
        }
    }

    public async Task<IActionResult> OnPostAsync(string conversationId, string? replyText)
    {
        replyText = replyText?.Trim();
        if (string.IsNullOrEmpty(replyText))
        {
            ErrorMessage = "回覆內容不可空白";
            return RedirectToPage();
        }

        await using var conn = new NpgsqlConnection(_connStr);
        // Replies 掛在該對話最新的那則待回訊息上
        var anchorId = await conn.ExecuteScalarAsync<long?>(LatestNewIdSql, new { Cid = conversationId, Src = SourceStatus });
        if (anchorId is null)
        {
            ErrorMessage = "找不到待回訊息,可能已被其他人處理";
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
        try
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync(
                "INSERT INTO Replies (MessageId, FinalText) VALUES (@MessageId, @FinalText)",
                new { MessageId = anchorId.Value, FinalText = replyText }, tx);
            await conn.ExecuteAsync(MarkNewSql, new { Cid = conversationId, Status = "replied", Src = SourceStatus }, tx);
            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            // 客人已經收到訊息了,這裡只能如實告知,不重試也不補償
            _logger.LogError(ex, "Reply pushed but DB update failed for {ConversationId}", conversationId);
            ErrorMessage = "回覆已送出,但狀態更新失敗,請勿重複送出";
            return RedirectToPage();
        }

        _logger.LogInformation("Reply sent for conversation {ConversationId}", conversationId);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSkipAsync(string conversationId)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.ExecuteAsync(MarkNewSql, new { Cid = conversationId, Status = "skipped", Src = SourceStatus });
        _logger.LogInformation("Conversation {ConversationId} skipped", conversationId);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostBlacklistAsync(string conversationId)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("""
            INSERT INTO Blacklist (ConversationId, DisplayName)
            SELECT @Cid, (SELECT DisplayName FROM LineUsers WHERE ConversationId = @Cid)
            ON CONFLICT (ConversationId) DO NOTHING
            """, new { Cid = conversationId }, tx);
        await conn.ExecuteAsync(MarkNewSql, new { Cid = conversationId, Status = "skipped", Src = SourceStatus }, tx);
        await tx.CommitAsync();

        _logger.LogInformation("Conversation {ConversationId} blacklisted", conversationId);
        return RedirectToPage();
    }

    /// <summary>轉交主管:把這個對話的待回訊息改成 escalated,丟進主管處理區。只有待審核頁提供這顆按鈕。</summary>
    public async Task<IActionResult> OnPostEscalateAsync(string conversationId)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.ExecuteAsync("""
            UPDATE Messages SET Status = 'escalated'
            WHERE ConversationId = @Cid AND SourceType = 'user' AND Status = 'new'
            """, new { Cid = conversationId });
        _logger.LogInformation("Conversation {ConversationId} escalated", conversationId);
        return RedirectToPage();
    }

    private const string LatestNewIdSql = """
        SELECT Id FROM Messages
        WHERE ConversationId = @Cid AND SourceType = 'user' AND Status = @Src
        ORDER BY LineTimestamp DESC, Id DESC
        LIMIT 1
        """;

    private const string MarkNewSql = """
        UPDATE Messages SET Status = @Status
        WHERE ConversationId = @Cid AND SourceType = 'user' AND Status = @Src
        """;

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

    public async Task<IActionResult> OnPostDraftAsync(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(_aiBaseUrl) || string.IsNullOrWhiteSpace(_aiApiKey))
        {
            // 未設定就地返回,不發任何 HTTP
            ErrorMessage = "AI 尚未設定(Ai:BaseUrl / Ai:ApiKey),無法起草";
            return RedirectToPage();
        }

        await using var conn = new NpgsqlConnection(_connStr);
        var target = await conn.QuerySingleOrDefaultAsync<DraftTarget>("""
            SELECT ConversationId, LineTimestamp FROM Messages
            WHERE ConversationId = @Cid AND SourceType = 'user' AND Status = @Src
            ORDER BY LineTimestamp DESC, Id DESC
            LIMIT 1
            """, new { Cid = conversationId, Src = SourceStatus });
        if (target is null)
        {
            ErrorMessage = "找不到待回訊息,可能已被其他人處理";
            return RedirectToPage();
        }

        // 同對話最近 100 則(含這次要回的那則),依時間升冪;同時間時客戶訊息排在回覆之前
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
            LIMIT 100
            """, new { Cid = target.ConversationId, Ts = target.LineTimestamp });

        // 知識庫全表撈,併進 system prompt(不加 LIMIT、不篩選)
        var faqs = (await conn.QueryAsync<FaqRow>(
            "SELECT Question, Answer FROM KnowledgeBase ORDER BY CreatedAt")).AsList();

        var messages = new List<object>
        {
            new { role = "system", content = BuildSystemPrompt(faqs) },
        };
        foreach (var row in history.Reverse())
            messages.Add(new { role = row.Role, content = row.Text });

        var (draft, error) = await DraftAsync(messages);
        if (error is not null)
        {
            ErrorMessage = error;
            return RedirectToPage();
        }

        DraftForId = conversationId;
        DraftText = draft;
        return RedirectToPage();
    }

    private record DraftTarget(string ConversationId, DateTime LineTimestamp);

    private record HistoryRow(string Role, string Text);

    private record FaqRow(string Question, string Answer);

    // 角色設定(基底)。硬約束與知識庫由 BuildSystemPrompt 動態接上。
    private const string SystemPromptBase =
        "你是繁體中文客服人員。語氣禮貌簡潔,回覆控制在 200 字以內。只輸出回覆內容本身。";

    /// <summary>組最終 system content:角色 + 硬約束 +(有資料才加)知識庫段落。</summary>
    private static string BuildSystemPrompt(IReadOnlyList<FaqRow> faqs)
    {
        var sb = new StringBuilder();
        sb.AppendLine(SystemPromptBase);
        sb.AppendLine();

        // 硬約束:給人審用的訊號,沒料的題目讓草稿空著/說不確定,不可省略
        sb.AppendLine("【回答規則】");
        sb.AppendLine("- 你只能根據下方「店家資料」回答客戶問題。");
        sb.AppendLine("- 店家資料裡沒有的資訊(價格、庫存、出貨/到貨日期、訂單狀態、優惠等)一律不可自行編造、不可自行承諾。");
        sb.AppendLine("- 知識庫裡沒有的商品或服務,一律視為店家沒有販售。客人問到知識庫沒有的品項時,不可假裝有賣、不可編造品項/規格/價格,要老實回覆「這個我們目前沒有販售」或「我幫您確認後再回覆」。不可為了服務熱情而無中生有。");
        sb.AppendLine("- 查不到答案時不要猜,用話術:「不確定的部分,我幫您確認後再回覆您」(或轉專人)。");

        if (faqs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("以下是店家提供的問答資料,請據此回答:");
            sb.AppendLine();
            for (var i = 0; i < faqs.Count; i++)
            {
                sb.AppendLine($"Q: {faqs[i].Question}");
                sb.AppendLine($"A: {faqs[i].Answer}");
                if (i < faqs.Count - 1)
                    sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

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

    /// <summary>氣泡下方的短時間:MM/dd 上午hh:mm</summary>
    public static string ToTaipeiShort(DateTime utc)
    {
        var t = utc.ToUniversalTime().AddHours(8);
        var hour12 = t.Hour % 12 == 0 ? 12 : t.Hour % 12;
        return $"{t:MM/dd} {(t.Hour < 12 ? "上午" : "下午")}{hour12:00}:{t.Minute:00}";
    }

    /// <summary>非文字訊息沒有內文,依型別給佔位字,避免出現空白泡泡。</summary>
    public static string BubbleText(TimelineEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Text))
            return entry.Text;

        return entry.MessageType switch
        {
            "sticker" => "[貼圖]",
            "image" => "[圖片]",
            "video" => "[影片]",
            "audio" => "[語音]",
            "file" => "[檔案]",
            "location" => "[位置]",
            _ => "[非文字訊息]",
        };
    }

    public static string ShortId(string id) =>
        id.Length <= 8 ? id : id[..8] + "…";
}
