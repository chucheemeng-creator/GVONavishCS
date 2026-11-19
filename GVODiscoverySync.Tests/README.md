# GVODiscoverySync.Tests

發現物同步工具中需要 Windows 的部分的測試（xUnit）：用實際的遊戲截圖（`TestData/Screens`）測字形比對（`GlyphTests.cs`）和卡片圖示（`CardIconTests.cs`）。
不依賴 Windows 的規則（Excel 讀寫、名稱比對、讀取結果的推斷）在 `GVODiscoverySync.Core.Tests`。

## 怎麼跑

Visual Studio：「測試 → 測試總管」→「全部執行」。或在方案資料夾執行 `dotnet test`（要在 Windows 上）。

## 測試資料

`TestData/Screens` 是 800×600、縮放 1（和 150%）的遊戲截圖，`TestData/Glyphs-round1.json` 是學過一輪的字形。
發現物名稱清單 `Discoveries.tsv` 和 `GVODiscoverySync.Core.Tests` 共用（見 csproj 的連結）。
