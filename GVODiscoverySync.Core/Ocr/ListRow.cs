// ListRow.cs
// 辨識的結果：清單上的一列、一頁（不依賴 Windows 的文字辨識，Core 和測試專案也能用）。

using System.Collections.Generic;

namespace GVODiscoverySync.Ocr;

/// <summary>
/// 清單上的一列。Index 是畫面上的第幾列（0 起算）。
/// Readings 是用不同放大倍率讀到的結果（同一列換個倍率常常就讀對了，由呼叫端挑最像的）；Text 是第一個。
/// 這一列有字、但怎麼讀都讀不出來時（常見於一、兩個字的短名稱，例如「蠶」），Readings 是空的、Text 是 ""。
/// TextWidth 是這一列的字從畫面上量到的寬度（遊戲原本的像素，0 表示沒量到）。
/// 清單的字型每個中文字一樣寬，所以就算辨識漏了字，也看得出名稱原本有幾個字。
/// Glyphs 是這一列每一格（一個中文字寬）的字形，用來比對字形（見 Glyph）；讀不到時是 null。
/// GlyphsExact：字形是不是逐點精確的。沒有放大顯示（縮放 1）時是；放大顯示（150% 等）時畫面是糊的，
/// 每一格是「最像的已知字形」，不是百分之百可靠，只用來認出整列，不用來排除別的名稱、也不學。
/// </summary>
public sealed record ListRow(int Index, string Text, bool HasCard, IReadOnlyList<string> Readings, double TextWidth,
    IReadOnlyList<Glyph>? Glyphs = null, bool GlyphsExact = true);

/// <summary>一次辨識的結果。</summary>
public sealed class ListSnapshot
{
    public string TitleText { get; init; } = "";
    public List<ListRow> Rows { get; init; } = [];

    /// <summary>標題是用幾倍放大讀到的（給除錯用）。</summary>
    public double TitleScale { get; init; }

    /// <summary>這一頁每一列都是用字形讀出來的，沒有做文字辨識（標題也沒讀，分類由清單內容判斷）。</summary>
    public bool ByGlyphs { get; init; }
}
