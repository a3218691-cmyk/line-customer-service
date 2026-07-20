using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;

public sealed class DailyReportService : BackgroundService
{
    private const string TokenPlaceholder = "REPLACE_WITH_CHANNEL_ACCESS_TOKEN";
    private const string SummarySystemPrompt =
        "你是家庭 LINE 群組的訊息摘要助手。請用繁體中文,以 3 到 5 句話摘要當天對話重點。直接輸出摘要內容,不要開頭問候、不要逐條複述、不要加任何前後綴。";

    private readonly ILogger<DailyReportService> _logger;
    private readonly string _connStr;
    private readonly string _accessToken;
    private readonly TimeSpan _triggerTime;
    private readonly string _ollamaBaseUrl;
    private readonly string _ollamaModel;
    private readonly HttpClient _lineHttp;
    private readonly HttpClient _ollamaHttp;
    private bool _tokenWarned;

    public DailyReportService(IConfiguration config, ILogger<DailyReportService> logger)
    {
        _logger = logger;
        _connStr = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");
        _accessToken = config["Line:ChannelAccessToken"] ?? "";
        _triggerTime = TimeSpan.TryParse(config["Report:TriggerTime"], out var t) ? t : new TimeSpan(8, 0, 0);
        _ollamaBaseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";
        _ollamaModel = config["Ollama:Model"] ?? "qwen2.5:3b";
        var ollamaTimeout = config.GetValue("Ollama:TimeoutSeconds", 120);
        _lineHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ollamaHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(ollamaTimeout) };
    }

    private bool TokenMissing => string.IsNullOrWhiteSpace(_accessToken) || _accessToken == TokenPlaceholder;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (TokenMissing)
        {
            _logger.LogWarning("Line:ChannelAccessToken 未設定,日報功能停用(webhook 不受影響)");
            _tokenWarned = true;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndSendAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily report check failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CheckAndSendAsync(CancellationToken ct)
    {
        var nowTw = DateTime.UtcNow.AddHours(8); // 寫死 +8,不用 TimeZoneInfo
        var targetDate = nowTw.Date.AddDays(-1);

        if (nowTw.TimeOfDay < _triggerTime)
            return;

        if (TokenMissing)
        {
            if (!_tokenWarned)
            {
                _logger.LogWarning("Line:ChannelAccessToken 未設定,日報功能停用(webhook 不受影響)");
                _tokenWarned = true;
            }
            return;
        }

        // 台灣日期 D 的 UTC 區間 = [D-8h, D+16h),半開區間
        // timestamptz 參數必須帶 Kind=Utc,否則 Npgsql 會拒收
        var fromUtc = DateTime.SpecifyKind(targetDate.AddHours(-8), DateTimeKind.Utc);
        var toUtc = fromUtc.AddDays(1);

        await using var conn = new NpgsqlConnection(_connStr);
        var groupIds = (await conn.QueryAsync<string>("""
            SELECT DISTINCT m.ConversationId FROM Messages m
            WHERE m.SourceType='group' AND m.MessageType='text' AND m.MessageText IS NOT NULL
              AND m.LineTimestamp >= @FromUtc AND m.LineTimestamp < @ToUtc
              AND NOT EXISTS (SELECT 1 FROM DailyReports r WHERE r.GroupId=m.ConversationId AND r.ReportDate=@ReportDate)
            """, new { FromUtc = fromUtc, ToUtc = toUtc, ReportDate = targetDate })).ToList();

        foreach (var groupId in groupIds)
        {
            try
            {
                await SendReportAsync(conn, groupId, targetDate, fromUtc, toUtc, ct);
            }
            catch (Exception ex)
            {
                // 單群組失敗不影響其他群組
                _logger.LogError(ex, "Daily report failed for group {GroupId}", groupId);
            }
        }
    }

    private async Task SendReportAsync(
        NpgsqlConnection conn, string groupId, DateTime targetDate, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var messages = (await conn.QueryAsync<MessageRow>("""
            SELECT LineUserId, MessageText, LineTimestamp FROM Messages
            WHERE ConversationId=@GroupId AND SourceType='group' AND MessageType='text' AND MessageText IS NOT NULL
              AND LineTimestamp >= @FromUtc AND LineTimestamp < @ToUtc
            ORDER BY LineTimestamp
            """, new { GroupId = groupId, FromUtc = fromUtc, ToUtc = toUtc })).ToList();

        var lines = BuildLines(messages);
        var summary = await GetSummaryAsync(string.Join("\n", lines), ct);
        var report = BuildReport(targetDate, lines, summary);

        _logger.LogInformation("Daily report for group {GroupId}:\n{Report}", groupId, report);

        if (!await PushAsync(groupId, report, ct))
            return; // 失敗不寫 DailyReports,下輪自然重試

        await conn.ExecuteAsync(
            "INSERT INTO DailyReports (GroupId, ReportDate) VALUES (@GroupId, @ReportDate)",
            new { GroupId = groupId, ReportDate = targetDate });
        _logger.LogInformation("Daily report sent for group {GroupId}, date {Date:yyyy-MM-dd}", groupId, targetDate);
    }

    private static List<string> BuildLines(List<MessageRow> messages)
    {
        // 匿名代號:依首次發言順序 用戶A~Z,第27人起「用戶27」,NULL → 未知
        var aliases = new Dictionary<string, string>();
        var lines = new List<string>(messages.Count);
        foreach (var m in messages)
        {
            string alias;
            if (m.LineUserId is null)
                alias = "未知";
            else if (!aliases.TryGetValue(m.LineUserId, out alias!))
            {
                alias = aliases.Count < 26 ? $"用戶{(char)('A' + aliases.Count)}" : $"用戶{aliases.Count + 1}";
                aliases[m.LineUserId] = alias;
            }

            var text = m.MessageText.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
            if (text.Length > 50)
                text = text[..50] + "…";
            lines.Add($"{m.LineTimestamp.AddHours(8):HH\\:mm} {alias}:{text}");
        }
        return lines;
    }

    private static string BuildReport(DateTime targetDate, List<string> lines, string? summary)
    {
        var sb = new StringBuilder();
        sb.Append($"【{targetDate:yyyy-MM-dd} 日報】共 {lines.Count} 則訊息\n\n");
        if (summary is not null)
            sb.Append("■ AI 摘要\n").Append(summary).Append("\n\n");
        sb.Append("■ 訊息紀錄");

        var shown = 0;
        foreach (var line in lines)
        {
            if (sb.Length + 1 + line.Length > 4900)
                break;
            sb.Append('\n').Append(line);
            shown++;
        }
        if (shown < lines.Count)
            sb.Append($"\n(僅顯示前 {shown} 則,其餘 {lines.Count - shown} 則省略)");
        return sb.ToString();
    }

    private async Task<string?> GetSummaryAsync(string content, CancellationToken ct)
    {
        try
        {
            if (content.Length > 4000)
                content = content[..4000];

            var body = JsonSerializer.Serialize(new
            {
                model = _ollamaModel,
                stream = false,
                messages = new object[]
                {
                    new { role = "system", content = SummarySystemPrompt },
                    new { role = "user", content },
                },
            });
            using var resp = await _ollamaHttp.PostAsync(
                $"{_ollamaBaseUrl}/api/chat",
                new StringContent(body, Encoding.UTF8, "application/json"), ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Ollama returned {Status}, summary skipped", (int)resp.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var text = doc.RootElement.GetProperty("message").GetProperty("content").GetString()?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                _logger.LogWarning("Ollama returned empty summary, skipped");
                return null;
            }
            return text.Length > 600 ? text[..600] : text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama summary failed, skipped");
            return null;
        }
    }

    private async Task<bool> PushAsync(string groupId, string text, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            to = groupId,
            messages = new[] { new { type = "text", text } },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.line.me/v2/bot/message/push");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var resp = await _lineHttp.SendAsync(req, ct);
        if ((int)resp.StatusCode == 200)
            return true;

        _logger.LogError("LINE push failed for group {GroupId}: {Status} {Body}",
            groupId, (int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        return false;
    }

    public override void Dispose()
    {
        _lineHttp.Dispose();
        _ollamaHttp.Dispose();
        base.Dispose();
    }

    private sealed record MessageRow(string? LineUserId, string MessageText, DateTime LineTimestamp);
}
