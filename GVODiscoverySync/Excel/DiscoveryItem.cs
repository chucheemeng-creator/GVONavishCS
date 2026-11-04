// DiscoveryItem.cs
// Excel 裡的一個發現物（一列）。

namespace GVODiscoverySync.Excel;

public sealed class DiscoveryItem
{
    /// <summary>分類＝工作表名稱，例如「史跡」。</summary>
    public required string Category { get; init; }

    /// <summary>發現物名稱（Excel 上的寫法）。</summary>
    public required string Name { get; init; }

    /// <summary>在工作表上的列號（從 1 開始，第 1 列是標題）。</summary>
    public required uint Row { get; init; }

    /// <summary>ID 欄的值，沒有時是 0。</summary>
    public int Id { get; init; }

    /// <summary>人物 1～5 是否已經打勾（索引 0～4）。</summary>
    public bool[] Marked { get; } = new bool[DiscoveryBook.CharacterCount];

    public override string ToString() => $"{Category}／{Name}";
}
