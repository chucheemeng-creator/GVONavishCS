# GVONavish.Tests

GVONavish 的測試專案（xUnit）。改了 `Core` 裡的程式以後跑一遍，確認以前做好的沒有被弄壞。

## 怎麼跑

Visual Studio：「測試 → 測試總管」→「全部執行」。或在方案資料夾執行 `dotnet test`。

## 測了什麼

| 檔案 | 測的是 |
|---|---|
| `WorldTests.cs` | 世界座標：地圖左右相連時的距離、速度換算成節 |
| `CoordFilterTests.cs` | 擋掉讀錯的座標：讀錯一次要擋掉、真的換位置要接受、超出地圖一定是讀錯 |
| `SpeedMeterTests.cs` | 船速：各種方向的誤差、跨過地圖邊界、停船、中斷後重新算 |
| `ShipMotionTests.cs` | 航向：直線航行的誤差、轉彎後跟上、停船時維持不變 |
| `TargetLockTests.cs` | 遊戲多開：讀最前面的視窗、讀到就鎖定、讀不到或視窗關掉就解除 |
| `ShipRouteTests.cs` | 航線紀錄：加點、合併、我的最愛、超過上限刪最舊的、改動時通知 |
| `RouteProgressTests.cs` | 規劃航線的剩餘距離：偏離航線、好幾段、跨過地圖邊界、來回的航線 |
| `RoutePlannerTests.cs` | 規劃航線：用真的海陸資料規劃幾條航線，檢查不穿過陸地、距離合理、到不了時回傳 null |
| `PortsTests.cs` | 港口資料（名稱和座標不重複、每個港口都在海邊）和搜尋 |

`PortsTests.cs` 的「每個港口都在海邊」可以抓到打錯的座標：加了新港口以後跑一次。

## 沒有測的

- 讀座標（`SurveyCoordExtractor`、`ScaledCoordReader`、`GameWindowCapture`）：要有遊戲畫面的截圖才能測，目前沒有放進來。
- 介面（`UI` 資料夾）和存讀檔（`RouteStore`、`AppSettings`）。

## 測試資料

規劃航線和港口的測試用 `GVONavish/seamask.bin`（引用 GVONavish 專案時會複製到測試的輸出資料夾；
找不到時會往上層找原始碼裡的那一份）。
