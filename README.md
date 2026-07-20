# LineBotLogger

LINE 群組訊息記錄 Bot(MVP)。接收 LINE Messaging API webhook,把群組訊息原樣存進 SQL Server。不解析、不做前端。另有每日統整功能:每天早上把前一天的群組訊息摘要(Ollama)+ 條列,push 回群組。

## 技術棧

- .NET 8 Minimal API(單一 `Program.cs`)
- Dapper + Microsoft.Data.SqlClient
- SQL Server Express(`localhost\SQLEXPRESS`,Windows 驗證)

## 設定(appsettings.json)

| 設定 | 說明 |
|---|---|
| `ConnectionStrings:Default` | SQL Server 連線字串,預設指向 `localhost\SQLEXPRESS` 的 `LineBotLogger` database |
| `Line:ChannelSecret` | LINE Developers Console → Basic settings → Channel secret,把 `REPLACE_WITH_CHANNEL_SECRET` 換掉 |
| `Line:ChannelAccessToken` | LINE Developers Console → Messaging API → Channel access token。留 placeholder 時日報功能停用(啟動時 log warning),webhook 不受影響 |
| `Report:TriggerTime` | 日報觸發時間(台灣時間,`HH:mm`),預設 `08:00` |
| `Ollama:BaseUrl` | Ollama server 位址,預設 `http://localhost:11434` |
| `Ollama:Model` | 摘要用模型,預設 `qwen2.5:3b` |
| `Ollama:TimeoutSeconds` | Ollama 呼叫逾時秒數,預設 `120` |

## 建立 Database

```
sqlcmd -S localhost\SQLEXPRESS -E -Q "CREATE DATABASE LineBotLogger"
sqlcmd -S localhost\SQLEXPRESS -E -d LineBotLogger -i sql\001_create_tables.sql
sqlcmd -S localhost\SQLEXPRESS -E -d LineBotLogger -i sql\002_create_daily_reports.sql
```

## 啟動

```
dotnet run
```

預設監聽 `http://localhost:5000`。健康檢查:`GET /health`(DB 連得上回 200,連不上回 503)。

## 本機測試

用 Git Bash 執行(server 要先跑起來,腳本內 `CHANNEL_SECRET` 需與 appsettings.json 一致):

```
bash test/webhook-test.sh
```

## 接上 LINE(ngrok)

1. 啟動 server 後,另開視窗跑 `ngrok http 5000`,取得 `https://xxx.ngrok.io` 網址。
2. LINE Developers Console → Messaging API → Webhook URL 填 `https://xxx.ngrok.io/webhook`,按 Verify(應顯示 Success)。
3. Console 三個開關:
   - **Use webhook**:開啟
   - **自動回應訊息(Auto-reply messages)**:關閉
   - **允許加入群組(群組聊天 / Allow bot to join group chats)**:開啟
4. 把 Bot 加進群組,群組內發訊息即會寫入 `GroupMessages`。

## 每日統整(日報)

Server 內建 background service,每 5 分鐘檢查一次:台灣時間過了 `Report:TriggerTime`(預設 08:00)後,對「昨天有 text 訊息且尚未發過日報」的每個群組:

1. 撈昨天(台灣日期)的 text 訊息,依時間升冪。
2. 呼叫 Ollama(`/api/chat`)產生最多 600 字繁中摘要;Ollama 失敗只 log warning,摘要段整段省略,條列照發。
3. 組報文:標題 + AI 摘要 + 匿名條列(用戶A、用戶B…;內文超過 50 字截斷),整體上限 5000 字。
4. LINE Push 到群組;成功才寫入 `DailyReports`(有 row = 已發),失敗不寫、5 分鐘後自動重試。

發送前後皆有 log(含組好的報文全文),可直接看 console 確認內容。

### 測試方式

1. 塞一筆昨天(台灣時間)的 text 訊息到 `GroupMessages`(注意 `LineTimestamp` 是 UTC,台灣時間要減 8 小時)。
2. `Report:TriggerTime` 改成已過的時間,`Line:ChannelAccessToken` 填有效 token。
3. `dotnet run`,5 分鐘內應看到報文 log 與 push 結果;成功後 `DailyReports` 會多一筆。
4. 測完把 TriggerTime 改回 `08:00`,並清掉測試資料。

## 登入保護

`/inbox` 與 `/blacklist` 需登入(Cookie 驗證 + 單一密碼,密碼讀 `Auth:Password`)。未登入會導向 `/login`;登出走 `/logout`。
`Auth:Password` 為空時**啟動即失敗**,不會靜默開放。
`/health` 與 `/webhook` 維持公開(webhook 自身有 HMAC 驗簽)。

## 黑名單

`/inbox` 每張卡片有「加入黑名單」:寫入 `Blacklist` 並把該對話的待回訊息標成 `skipped`(卡片消失)。
之後 webhook 收到該對話的事件會**直接跳過、不寫 DB**(只留一行 log),request 仍回 200 給 LINE。

誤封到 `/blacklist` 按「解除」即可,解除後的新訊息會照常進待回清單。

相關建表 SQL:`sql/004_create_line_users.sql`(客人顯示名稱快取)、`sql/005_create_blacklist.sql`。
兩張表同步內嵌在 `Program.cs` 的 `SchemaSql`,啟動時自動建立。

## 部署到 Render(Docker)

Root Directory 設 `LineBotLogger`,Environment 選 Docker。容器聽 Render 給的 `PORT`(本機沒給時退回 8080)。

設定一律走環境變數,`:` 換成雙底線(.NET 內建行為,code 不需改):

| 環境變數 | 對應設定 |
|---|---|
| `ConnectionStrings__Default` | Supabase Postgres 連線字串 |
| `Line__ChannelSecret` | LINE Channel secret |
| `Line__ChannelAccessToken` | LINE Channel access token |
| `Auth__Password` | `/inbox` 登入密碼 |
| `Ai__BaseUrl` / `Ai__Model` / `Ai__ApiKey` | AI 起草(OpenAI 相容 API) |
| `Report__TriggerTime` | 日報觸發時間,預設 `08:00` |
| `Ollama__BaseUrl` / `Ollama__Model` | 日報摘要用,Render 上連不到本機 Ollama,摘要會略過 |

本機 Docker 測試:

```
docker build -t linebotlogger .
docker run -p 8080:8080 -e ConnectionStrings__Default=... -e Line__ChannelSecret=... -e Auth__Password=... linebotlogger
```

## 已知限制

- LINE 逾時重送 webhook 時可能寫入重複資料,MVP 不做去重。
