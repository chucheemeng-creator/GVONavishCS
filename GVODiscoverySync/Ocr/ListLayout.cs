// ListLayout.cs
// 遊戲「發現物一覽」畫面上，分類標題和清單的位置。
//
// 單位是遊戲原本的像素（不受 Windows 顯示縮放影響），而且是相對於遊戲畫面「正中央」的距離，
// 因為這個視窗顯示在畫面中央；遊戲解析度不同時，位置應該也對得上（目前只在 800 × 600 量過）。
// 量法：800 × 600 的畫面，中央是 (400, 300)；清單第一列從 (79, 165) 開始 → (-321, -135)。

using System.Text.Json.Serialization;

namespace GVODiscoverySync.Ocr;

public sealed class ListLayout
{
    // 分類標題（例如「史跡」，在清單左上角的小圖示右邊）
    public int TitleX { get; set; } = -317;
    public int TitleY { get; set; } = -197;
    public int TitleWidth { get; set; } = 90;
    public int TitleHeight { get; set; } = 22;

    // 清單的文字部分（不含左邊的卡片圖示和右邊的捲軸）
    public int ListX { get; set; } = -321;
    public int ListY { get; set; } = -135;
    public int ListWidth { get; set; } = 260;
    public int RowHeight { get; set; } = 20;
    public int RowCount { get; set; } = 10;

    // 每一列左邊的卡片圖示（有圖示＝已取得卡片）
    public int IconX { get; set; } = -335;
    public int IconWidth { get; set; } = 8;

    [JsonIgnore]
    public int ListHeight => RowHeight * RowCount;
}
