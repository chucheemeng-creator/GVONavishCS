// Found.cs
// 讀到的一筆結果（清單上的一列），和同名組的列的身分。不依賴畫面，測試專案也能用。

using System.Collections.Generic;
using GVODiscoverySync.Excel;
using GVODiscoverySync.Matching;
using GVODiscoverySync.Ocr;

namespace GVODiscoverySync.Recognition;

/// <summary>讀到的一筆結果。</summary>
public sealed class Found
{
    public required string Category { get; set; }
    // 可以改：先推斷出來（讀不出來）的列，之後讀到字時換成讀到的結果
    public required string ScreenText { get; set; }
    public required MatchResult Match { get; set; }
    public bool HasCard { get; set; }
    /// <summary>畫面上顯示這一筆的那一列（由畫面設定，這裡不使用）。</summary>
    public object? View { get; set; }

    /// <summary>同名的一組、或手動指定時，實際對應到哪一個（其他時候是 null）。</summary>
    public DiscoveryItem? Assigned { get; set; }

    /// <summary>使用者手動指定的。</summary>
    public bool Manual { get; set; }

    /// <summary>放在「對不到」那一邊時的索引鍵（從那邊移走時要用）。</summary>
    public string? UnmatchedKey { get; set; }

    /// <summary>同名的一組還沒全部看到，不確定是組裡的哪一個。</summary>
    public bool VariantUnsure { get; set; }

    /// <summary>
    /// 同名組的列：是用上下鄰居的功績判斷出是組裡哪一個的（依價值排列時）。
    /// 這種不靠「同一頁看齊整組」，使用者移除組裡別筆時不用改回請確認。
    /// </summary>
    public bool ByMerit { get; set; }

    /// <summary>畫面上量到的字寬（遊戲像素，0 表示沒量到）。讀不出來、對不到的列用來在選擇視窗只列出字數對得上的。</summary>
    public double TextWidth { get; init; }

    /// <summary>這一列每一格的字形（使用者確認這一筆之後，用來學還不認得的字）。</summary>
    public IReadOnlyList<Glyph>? Glyphs { get; set; }

    /// <summary>這一筆是比對字形認出來的（文字辨識讀不出來或讀錯，但每個字的字形都和學過的一樣）。</summary>
    public bool ByGlyph { get; set; }

    /// <summary>這一列有字但讀不出來（Note 說明它在哪兩個發現物之間）。</summary>
    public bool Unreadable { get; init; }

    /// <summary>讀到這一列時，這一頁是不是依價值排列（手動指定時才用功績範圍縮小候選）。</summary>
    public bool PageByValue { get; init; }

    /// <summary>同名組的列：它是看過的哪一個（用上下鄰居辨識的身分），還不知道時是 null。</summary>
    public Occurrence? Occurrence { get; set; }

    /// <summary>這一筆（原本屬於同名組）已經從組的筆數裡扣掉了（被移除、或手動改指定到組外）。</summary>
    public bool VariantSuppressed { get; set; }

    /// <summary>扣的時候它沒有身分，所以也算進了「沒有身分被移除」的筆數（扣回來時要一起扣回）。</summary>
    public bool AnonymousCounted { get; set; }

    public string Note { get; init; } = "";

    /// <summary>
    /// 對不到、讀不出來的列：上一列、下一列「已確定」的名稱（Excel 的寫法）。
    /// 用來判斷之後讀對的列是不是同一個位置（是的話這一筆就過時了），手動指定時也用來捲到附近。
    /// </summary>
    public string Above { get; init; } = "";
    public string Below { get; init; } = "";

    /// <remarks>
    /// 不確定的列（好幾個名稱一樣像，例如「天座」之於天兔座、天貓座、天鵝座……）不給答案：
    /// 列出最像的第一個只是剛好排在前面，容易誤導（看起來像程式的判斷，打勾就寫進 Excel），要使用者自己指定。
    /// 同名組裡配對成功的列有 Assigned，不受影響。
    /// </remarks>
    public DiscoveryItem? Item => Assigned ?? (Match.Ambiguous ? null : Match.Item);

    /// <summary>不確定、還沒指定的列（好幾個名稱一樣像，不給答案）。</summary>
    // 同名組裡配對成功的列一定有 Assigned；Assigned 是 null 又 IsVariant 的，是讀到的字不確定、最像的剛好是一組同名的
    public bool Undecided => Assigned is null && Match.Ambiguous && !Unreadable;

    /// <summary>要使用者確認的（同名分不出是哪一個、不確定、用排序推斷的）；手動指定的已經確認過。</summary>
    public bool NeedsConfirm => !Manual && (VariantUnsure || Match.Ambiguous || Match.Score == 0);
}

/// <summary>
/// 同名組裡看過的一列的身分：它上一列、下一列已確定的名稱（Excel 的寫法）。
/// 捲動時鄰居不會變，只是到了頁面邊緣只看得到一邊；之後看到另一邊時補上。
/// 用參考相等當作身分（同一個物件＝同一列）。
/// </summary>
public sealed class Occurrence
{
    public string Above { get; set; } = "";
    public string Below { get; set; } = "";

    // 和「上一列是 above、下一列是 below」是不是同一列：規則和 SamePosition 一樣
    public bool Matches(string above, string below)
    {
        bool aboveKnown = above != "" && Above != "";
        bool belowKnown = below != "" && Below != "";
        if (aboveKnown && belowKnown)
        {
            return Above == above && Below == below;
        }
        if (aboveKnown)
        {
            return Above == above;
        }
        return belowKnown && Below == below;
    }
}
