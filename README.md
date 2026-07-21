# LineBotLogger

名為「工作台」的單人後台平台。首頁(`/`)是工作台入口,底下掛兩個工具:

- **LINE 一對一客服** — 收 LINE 私訊、整理成待回清單、AI 起草、人工審核後送出。
- **審片小幫手** — 純前端影片審片工具,影片在瀏覽器本地開、不上傳伺服器。

## 技術棧

- .NET 8 **Razor Pages**(`/webhook`、`/health` 為 Minimal API endpoint)
- Dapper + Npgsql
- **Postgres(Supabase)**
- 部署:**Render(Docker)**
- AI 起草:**Google Gemini 免費層**(OpenAI 相容 API)

## LINE 一對一客服流程

1. **webhook 進來** — 手刻 **HMAC-SHA256** 驗簽(不使用 LINE SDK),簽章不符回 401。
2. **一對一訊息入庫** — 事件寫入 `Messages`,黑名單對話直接跳過不寫。
3. **`/inbox` 卡片式待回清單** — 每張卡片是一位客人,顯示歷史對話 + 待回訊息,依「等最久的」排在最上。
4. **AI 起草** — 把 System Prompt(定義客服角色、語氣、不得編造資訊)連同該對話最近的訊息送給 Gemini,產生草稿回填輸入框。
5. **人工編輯** — 草稿可在輸入框直接修改。
6. **送出** — 走 LINE **Push** API 發給客人;送出成功才寫 `Replies` 並把該對話標為已回。

每張卡片另有「全部略過」與「加入黑名單」;黑名單對話之後的 webhook 事件直接跳過、不寫 DB(request 仍回 200 給 LINE)。

## 審片小幫手

純前端單檔工具(`Protected/review.html`),路由 `/review`,需登入。影片以 `URL.createObjectURL` 在瀏覽器本地播放,**不上傳伺服器**;留言自帶時間碼、可在暫停畫面上「畫記」圈重點,資料存瀏覽器 `localStorage`(依檔名分開),最後匯出 `.md` 改片意見。

## 設定(appsettings.json)

| 設定 | 說明 |
|---|---|
| `ConnectionStrings:Default` | Supabase Postgres 連線字串,例:`Host=xxx.supabase.co;Port=5432;Database=postgres;Username=postgres;Password=...` |
| `Line:ChannelSecret` | LINE Developers Console → Basic settings → Channel secret(webhook 驗簽用) |
| `Line:ChannelAccessToken` | LINE Developers Console → Messaging API → Channel access token(查客人名稱、Push 送出用) |
| `Auth:Password` | `/`、`/inbox` 等頁面的登入密碼。為空時**啟動即失敗**,不會靜默開放 |
| `Ai:BaseUrl` | AI 服務位址(OpenAI 相容,`/chat/completions` 前綴) |
| `Ai:Model` | 起草用模型 |
| `Ai:ApiKey` | AI 服務金鑰 |

## 建立 Database

不需手動跑建表腳本:schema 內嵌在 `Program.cs` 的 `SchemaSql`,啟動時對連上的 Postgres 自動 `CREATE TABLE IF NOT EXISTS`。建表失敗程式直接終止,不會連不上 DB 還假裝正常。

## 啟動

```
dotnet run
```

預設監聽 `http://localhost:5000`。健康檢查:`GET /health`(DB 連得上回 200,連不上回 503)。

## 接上 LINE(ngrok)

1. 啟動 server 後,另開視窗跑 `ngrok http 5000`,取得 `https://xxx.ngrok.io` 網址。
2. LINE Developers Console → Messaging API → Webhook URL 填 `https://xxx.ngrok.io/webhook`,按 Verify(應顯示 Success)。
3. Console 開關:**Use webhook** 開啟、**自動回應訊息(Auto-reply messages)** 關閉。

## 登入保護

`/`、`/inbox`、`/blacklist`、`/review` 需登入(Cookie 驗證 + 單一密碼,密碼讀 `Auth:Password`)。未登入導向 `/login`,登出走 `/logout`。
內建**防暴力破解鎖定**:密碼錯 5 次鎖定 10 分鐘,每次失敗延遲 1 秒拖慢嘗試,密碼比對用固定時間比較。
`/health` 與 `/webhook` 維持公開(webhook 自身有 HMAC 驗簽)。

## 設計

iOS 深色設計語言,樣式集中在 `wwwroot/site.css` 的 CSS variables,主色為暖橘 `--accent`(`#ff9f0a`)。導覽列毛玻璃、按鈕點按縮放,並尊重 `prefers-reduced-motion` / `prefers-reduced-transparency`。

## 部署到 Render(Docker)

Root Directory 設 `LineBotLogger`,Environment 選 Docker。容器聽 Render 給的 `PORT`(本機沒給時退回 8080)。

設定一律走環境變數,`:` 換成雙底線(.NET 內建行為,Linux 上一樣,code 不需改):

| 環境變數 | 對應設定 |
|---|---|
| `ConnectionStrings__Default` | Supabase Postgres 連線字串 |
| `Line__ChannelSecret` | LINE Channel secret |
| `Line__ChannelAccessToken` | LINE Channel access token |
| `Auth__Password` | 登入密碼 |
| `Ai__BaseUrl` / `Ai__Model` / `Ai__ApiKey` | AI 起草(OpenAI 相容 API) |

本機 Docker 測試:

```
docker build -t linebotlogger .
docker run -p 8080:8080 -e ConnectionStrings__Default=... -e Line__ChannelSecret=... -e Auth__Password=... linebotlogger
```

## 已知限制

- **Render 免費方案冷啟動**:服務閒置休眠後,睡醒前的第一則 webhook 訊息會漏收。
- **刻意不做輪詢 / SSE**:待回清單不自動更新,規定人工每天看兩次、用 F5 手動刷新。這是有意識的取捨,不是沒做完。
- LINE 逾時重送 webhook 時可能寫入重複資料,不做去重。
