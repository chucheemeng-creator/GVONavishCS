# GVONavishCS：給 Claude 的專案說明

這個 repo 有兩個獨立發佈的 Windows 工具，共用一些程式碼。回覆和 commit 訊息、PR 說明一律用**繁體中文**。

## 專案結構

| 資料夾 | 內容 |
|---|---|
| `GVONavish/` | 航海地圖輔助（`GVONavish.exe`），GitHub Issue 標籤 `navish` |
| `GVODiscoverySync/` | 發現物同步（`GVODiscoverySync.exe`），標籤 `discovery-sync` |
| `GVODiscoverySync.Core/` | 發現物同步中不依賴 Windows 的部分（Excel、名稱比對、字形、推斷規則） |
| `GVO.Common/` | 兩個工具共用：找遊戲視窗、顯示縮放、截圖（僅 Windows）、安全存檔 |
| `*.Tests/` | 測試：`GVONavish.Tests`、`GVODiscoverySync.Tests`、`GVODiscoverySync.Core.Tests` |

- 兩個對話分開處理時，各管各的範圍；改到 `GVO.Common` 要同時顧到兩邊。
- 不讀取、不修改遊戲記憶體，也不對遊戲送按鍵或滑鼠；只看畫面。這條不能破壞。

## 環境與編譯

- **目標框架固定 .NET 10**（`global.json`、各 csproj、CI 都是 10.0.x）。.NET 11 已評估過，**暫時不升**，不要主動建議升級或改語法。
- 編譯與測試：`dotnet build -c Release`、`dotnet test`（方案檔 `GVONavishCS.slnx`）。
- WinForms、Windows 文字辨識、GDI+ 都需要 Windows；Linux 容器通常沒有 dotnet，Core 與 Core.Tests 在 Linux 也能跑。完整的編譯與測試以 GitHub Actions 的「編譯和測試」為準（PR 到 master 時自動跑）。

## 發佈新版

在 Actions 頁面執行「發佈新版」，選要發佈的程式。發佈前要先一起合併到 master：

1. `<程式>/<程式>.csproj` 的 `<Version>`
2. `<程式>/ReleaseNotes.md`：這一版的說明，`<!-- version: … -->` 要和 `<Version>` 一樣

Tag 前綴：GVONavish 是 `Navish-v<版本>`，GVODiscoverySync 是 `DiscoverySync-v<版本>`（發佈時標成 Pre-release）。同一個版本已有 Release 時 workflow 會停下來。

## 慣例

- 開發用分支加 PR，合併到 `master`；不要直接推 `master`，也不要在沒被要求時開 PR。
- Commit 訊息用中文；改版本時用「程式名 v版本：重點」的格式，例如 `GVODiscoverySync v0.2.69: …`。
- Issue 有兩個範本（`.github/ISSUE_TEMPLATE/`），會自動加對應標籤；一般問題與建議導向 Discussions。
- 雲端對話無法刪除遠端分支（GitHub 回 HTTP 403）；已合併的分支請使用者自己刪，或在 repo 設定開「合併後自動刪除分支」。

## 待辦

- [ ] **下一次發佈 GVODiscoverySync 時**，修改 `GVODiscoverySync/ReleaseNotes.md` 第 80 行「請不要放在 `C:\Program Files`」：新版已支援在不能寫入的資料夾執行，設定、字形、對照表會改存在 `%LocalAppData%\GVODiscoverySync`。建議改成「放在自己的資料夾最方便，設定和對照表都在 exe 旁邊，也好直接編輯；放在 `C:\Program Files` 這類不能寫入的地方也能用，設定、字形、對照表會改存在 `%LocalAppData%\GVODiscoverySync`」。改完把這項刪掉。
