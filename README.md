# 訂閱管理 · YouTube / B站（Avalonia）

跨平台桌面應用（.NET 10 / Avalonia），兩個分頁：

| 分頁 | 功能 |
|------|------|
| **YouTube** | 訂閱總數、列表、勾選、全選、取消訂閱 |
| **B站** | 关注总数、列表、勾选、全选、取消关注 |

## 環境

- .NET 10 SDK
- YouTube：Google 帳號 + YouTube Data API v3
- B站：哔哩哔哩账号（扫码或 Cookie）

## 建置與執行

```powershell
cd D:\codex\YoutubeBilibiliSubscription
dotnet restore
dotnet run
```

---

## YouTube 分頁

1. Google Cloud 啟用 **YouTube Data API v3**
2. 建立 OAuth **桌面應用程式**，下載 JSON
3. 程式內 **選擇憑證檔** → **登入 / 授權**
4. OAuth 同意畫面測試使用者加入自己的 Gmail

詳見 `client_secrets.example.json`。

Token：`%LocalAppData%\YoutubeSubscription\Google.Apis.Auth\`

---

## B站 分頁

### 功能

- 计算 **关注 UP 总数**
- 列出关注列表（头像、昵称、UID、签名、关注日期）
- **勾选** / **全选** / **取消全选**
- **取消关注**已勾选 UP（确认后调用 API）

### 登录方式

**方式 A：扫码（推荐）**

1. 打开 **B站** 分页
2. 点 **扫码登录**
3. 用哔哩哔哩 App 扫描，手机确认

**方式 B：导入 cookies.txt**

1. 浏览器登录 [bilibili.com](https://www.bilibili.com)
2. 用扩展（如 *Get cookies.txt LOCALLY*）导出 Netscape 格式 `cookies.txt`
3. 程式内点 **导入 cookies.txt** / **选择 cookies.txt 并登录**
4. 选择文件后自动读取 `SESSDATA`、`bili_jct`（及 `DedeUserID`、`buvid3`）

**方式 C：手动 Cookie**

1. 浏览器登录 [bilibili.com](https://www.bilibili.com)
2. F12 → Application → Cookies → `bilibili.com`
3. 复制 `SESSDATA`、`bili_jct`
4. 程式内 **Cookie 登录** 贴上

凭证缓存：`%LocalAppData%\YoutubeSubscription\Bilibili\credential.json`

> 请勿把 Cookie / credential.json 提交到 Git。

### 说明

- B 站使用站内 Web API（非官方开放平台 OAuth）
- 接口可能变更或触发风控；批量取消关注已加短延迟
- Cookie 过期后需重新登录

---

## 專案結構（重點）

```
Views/
  MainWindow.axaml      # 分頁殼
  YouTubeView.axaml
  BilibiliView.axaml
ViewModels/
  ShellViewModel.cs
  MainViewModel.cs      # YouTube
  BilibiliViewModel.cs
Services/
  YouTubeAuthService.cs
  YouTubeSubscriptionService.cs
  BilibiliApiClient.cs
  BilibiliCredentialStore.cs
```
