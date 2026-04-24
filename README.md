# 血壓 LINE Bot

傳血壓計照片到 LINE,自動辨識數值、存進 DB,並提供儀表板頁面查看歷史紀錄與趨勢。

## 架構

- **Backend** (`backend/`) — ASP.NET Core 8 Web API
  - `POST /webhook` 接收 LINE webhook,驗證簽章後背景處理
  - 圖片辨識: Google Gemini API (vision, 免費 tier)
  - 儲存: PostgreSQL (Supabase)
  - `GET /api/records`、`GET /api/stats` 給前端用
- **Frontend** (`frontend/`) — Vite + React + TypeScript + Tailwind + Tremor
  - 統計卡片 / 趨勢折線圖 / 紀錄表格

## 需要先準備

1. **LINE Messaging API Channel** — 記下 `Channel secret` 與 `Channel access token (long-lived)`
2. **Supabase 專案** (免費) — 在 Project Settings → Database 取得 `Connection string` (URI,使用 pooler,6543 port)
3. **Google Gemini API Key** — https://aistudio.google.com/ (免費,每天 1500 次請求)

## 本機執行

### Backend

```bash
cd backend
dotnet restore

# 設定機密 (或直接編輯 appsettings.Development.json)
dotnet user-secrets init
dotnet user-secrets set "Line:ChannelSecret" "..."
dotnet user-secrets set "Line:ChannelAccessToken" "..."
dotnet user-secrets set "Gemini:ApiKey" "..."
dotnet user-secrets set "Database:ConnectionString" "Host=...;Port=6543;Username=...;Password=...;Database=postgres;SSL Mode=Require;Trust Server Certificate=true"

dotnet run
```

後端會在 `http://localhost:8080` 啟動(或 `ASPNETCORE_URLS` 指定的 port),並自動建立資料表。

用 [ngrok](https://ngrok.com/) 或 [Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/) 對外開啟 webhook URL:

```bash
ngrok http 8080
```

把 `https://xxx.ngrok.io/webhook` 貼到 LINE Developers → Messaging API 的 Webhook URL,並開啟 "Use webhook"。

### Frontend

```bash
cd frontend
cp .env.example .env   # 視需要填 VITE_USER_ID (LINE userId) 過濾單一使用者
npm install
npm run dev
```

開啟 http://localhost:5173

## 免費部署

### Backend → Render

1. 把專案推到 GitHub
2. 在 Render 建立新的 **Web Service**,選 "Deploy from GitHub"
3. Root directory 填 `backend`,Runtime 選 **Docker**
4. Environment Variables (注意雙底線):
   - `Line__ChannelSecret`
   - `Line__ChannelAccessToken`
   - `Gemini__ApiKey`
   - `Database__ConnectionString`
   - `Cors__AllowedOrigins__0` = 你的前端網址
5. 部署完成後把 `https://xxx.onrender.com/webhook` 設為 LINE webhook URL

> Render 免費方案閒置 15 分鐘後休眠,冷啟動 30–60 秒。webhook 收到後會先回 200 再背景處理,所以第一次呼叫可能錯過訊息,可以讓對方再傳一次。

### Frontend → Vercel 或 Cloudflare Pages

1. 連接 GitHub repo,框架選 Vite,root 選 `frontend`
2. 環境變數:
   - `VITE_API_BASE_URL` = 你 Render 後端的 URL
   - `VITE_USER_ID` (選填) = 你的 LINE userId
3. Build command: `npm run build`,Output: `dist`

## 使用方式

加 bot 為好友 → 直接傳血壓計照片 → bot 回覆辨識結果並存入 DB → 前端 dashboard 自動顯示。
