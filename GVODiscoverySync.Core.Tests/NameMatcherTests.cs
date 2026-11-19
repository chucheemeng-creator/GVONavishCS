// NameMatcherTests.cs
// 名稱比對的規則：實際讀錯過的案例（TestData/OcrCases.tsv）和各條規則的單元測試。

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GVODiscoverySync.Excel;
using GVODiscoverySync.Matching;
using Xunit;

namespace GVODiscoverySync.Tests;

public class NameMatcherTests
{
    // OcrCases.tsv 的每一行一個測試案例（測試總管裡顯示行號和說明）
    public static IEnumerable<object[]> OcrCases()
        => TestBook.ReadTsv("OcrCases.tsv").Select(r => new object[]
        {
            r.Line,
            r.Fields[0],
            r.Fields[1],
            double.Parse(r.Fields[2], CultureInfo.InvariantCulture),
            r.Fields[3],
            r.Fields.Length > 4 ? r.Fields[4] : "",
        });

    [Theory]
    [MemberData(nameof(OcrCases))]
    public void 實際案例(int line, string category, string read, double width, string expected, string note)
    {
        MatchResult m = TestBook.Matcher.Match(category, read, width);
        string got = m.Item is null ? "（找不到）" : $"{m.Item.Name}（{m.Score:0%}{(m.Ambiguous ? "，不確定" : "")}）";
        if (expected == "?")
        {
            Assert.False(m.IsMatch, $"第 {line} 行「{read}」不應該確定對到，結果是 {got}。{note}");
        }
        else
        {
            Assert.True(m.IsMatch, $"第 {line} 行「{read}」應該確定對到「{expected}」，結果是 {got}。{note}");
            Assert.True(m.Item!.Name == expected || m.Variants.Any(v => v.Name == expected),
                $"第 {line} 行「{read}」應該對到「{expected}」，結果是 {got}。{note}");
        }
    }

    [Theory]
    [InlineData("巴別塔", 46, true)]
    [InlineData("巴別塔（小）", 83, true)] // 結尾的全形右括號，量到的少半格
    [InlineData("浪子（畫家與莎斯姬亞）", 164, true)]
    [InlineData("埃努瑪·埃里什", 112, true)] // 間隔號是一個中文字寬
    [InlineData("天鵝座β", 59, true)] // β 是全形（實測）
    [InlineData("關島(港)", 30, true)] // 遊戲上是「關島」，兩個字
    [InlineData("關島(港)", 62, false)]
    [InlineData("天鵝座β", 46, false)]
    [InlineData("黑鳳蝶", 62, false)] // 3 個字，量到 4 個字寬
    [InlineData("玫瑰鳳蝶", 62, true)]
    [InlineData("水鹿2", 30, true)] // 遊戲上不顯示結尾數字
    [InlineData("蠶", 14, true)]
    [InlineData("蠶", 30, false)]
    [InlineData("黑鳳蝶", 0, true)] // 沒量到字寬：都算對得上
    public void 字數對不對得上(string name, double width, bool fits)
        => Assert.Equal(fits, NameMatcher.FitsWidth(name, width));

    [Theory]
    [InlineData("昆蟲", "蠶")]
    [InlineData("植物", "李")]
    [InlineData("鳥類", "鶚")]
    public void 讀不出來的一個字只有一個候選(string category, string expected)
    {
        // 和 MainForm 推斷讀不出來的列一樣：沒有排序的上下界，只用字數
        List<IReadOnlyList<DiscoveryItem>> candidates = [.. TestBook.Matcher.Between(category, null, null)
            .Where(g => NameMatcher.FitsWidth(g, TestBook.GameWidth(expected)))];
        Assert.Single(candidates);
        Assert.Equal(expected, candidates[0][0].Name);
    }

    [Fact]
    public void 排序上下界()
    {
        // 照名稱排序（字元編碼）時，上下兩列之間的候選
        List<IReadOnlyList<DiscoveryItem>> between = TestBook.Matcher.Between("昆蟲", null, null);
        Assert.NotEmpty(between);
        string lower = between[0][0].Name;
        Assert.DoesNotContain(TestBook.Matcher.Between("昆蟲", lower, null), g => g[0].Name == lower);
        Assert.All(TestBook.Matcher.Between("昆蟲", lower, null),
            g => Assert.True(string.CompareOrdinal(NameMatcher.SortKey(g[0].Name), NameMatcher.SortKey(lower)) > 0));
    }

    [Theory]
    [InlineData("鳳蝶", "黑鳳蝶", true)]
    [InlineData("鳳蝶", "鳳蝶", false)] // 一樣長不算包含
    [InlineData("黑蝶", "黑鳳蝶", true)] // 不一定要連續
    [InlineData("鳳黑", "黑鳳蝶", false)] // 順序要一樣
    public void 讀到的字被名稱包含(string read, string name, bool contained)
        => Assert.Equal(contained, NameMatcher.IsContainedIn(read, name));

    [Theory]
    [InlineData("聖母加冠1", "聖母加冠")]
    [InlineData("水鹿2", "水鹿")]
    [InlineData("巴別塔（小）", "巴別塔（小）")]
    [InlineData("關島(港)", "關島")] // 遊戲上的寫法
    public void 排序用的名稱拿掉結尾數字(string name, string key)
        => Assert.Equal(key, NameMatcher.SortKey(name));

    [Fact]
    public void 分類標題()
    {
        Assert.Equal("歷史遺物", TestBook.Matcher.MatchCategory("歷史遺物"));
        Assert.Null(TestBook.Matcher.MatchCategory(""));
    }

    [Fact]
    public void 從清單內容推測分類()
    {
        // 實際的一頁（標題讀不出來時）
        string[] page = ["寺社緣起繪卷", "藥劑師的肖像", "耶穌受雞圖", "俵屋宗達的屏風畫", "浪子(畫家與莎斯姬亞)",
            "阿爾諾芬尼夫婦像", "獨眼巨人像的頭部", "姜希顏的水墨畫", "巴別塔(小)", "巴別塔"];
        Assert.Equal("美術品", TestBook.Matcher.GuessCategory(page));
    }

    [Fact]
    public void 完全一樣但字數對不上時也試錯字對照表()
    {
        // 「聖像」完全一樣，但畫面上是三個字寬：照原樣是不確定；換字後字數改變、對到字數正確的那一個
        NameMatcher matcher = new(TestBook.Book, new Dictionary<string, string> { ["聖像"] = "三聖像" });
        MatchResult m = matcher.Match("美術品", "聖像", TestBook.GameWidth("三聖像"));
        Assert.True(m.IsMatch);
        Assert.Equal("三聖像", m.Item!.Name);
    }

    [Fact]
    public void 錯字對照表長的先換_換過的不再連換()
    {
        // 「每→梅」寫在前面也一樣：「冫每」先換成「海」，不會變成「冫梅」
        NameMatcher matcher = new(TestBook.Book, new Dictionary<string, string> { ["每"] = "梅", ["冫每"] = "海" });
        Assert.Equal("海埔姜", matcher.Match("植物", "冫每埔姜", TestBook.GameWidth("海埔姜")).Item?.Name);

        // 「海→X」不會把剛換出來的「海」再換掉
        NameMatcher chained = new(TestBook.Book, new Dictionary<string, string> { ["冫每"] = "海", ["海"] = "囗" });
        Assert.Equal("海埔姜", chained.Match("植物", "冫每埔姜", TestBook.GameWidth("海埔姜")).Item?.Name);
    }

    [Theory]
    [InlineData("All", true)] // GVOexcel-AllSheet 產生的彙整頁：不是分類，不比對、不寫入
    [InlineData("all ", true)]
    [InlineData("史跡", false)]
    [InlineData("大型生物", false)]
    public void 彙整頁不當成分類(string sheet, bool summary)
        => Assert.Equal(summary, GVODiscoverySync.Excel.DiscoveryBook.IsSummarySheet(sheet));

    [Fact]
    public void 名稱對照表內建關島()
    {
        Assert.Equal("關島", MappingFile.BuiltInNameAliases["關島(港)"]);
        Assert.Equal("關島", NameMatcher.GameName("關島(港)"));
        Assert.Equal("海埔姜", NameMatcher.GameName("海埔姜")); // 沒有特別的：原本的名稱
    }

    [Fact]
    public void 錯字對照表可以換()
    {
        NameMatcher matcher = new(TestBook.Book); // 沒有錯字對照表
        MatchResult before = matcher.Match("寶藏", "黃巑", 30);
        Assert.False(before.IsMatch && before.Item!.Name == "黃鑽");
        matcher.SetConfusions(new Dictionary<string, string> { ["巑"] = "鑽" });
        Assert.Equal("黃鑽", matcher.Match("寶藏", "黃巑", 30).Item?.Name);
    }
}
