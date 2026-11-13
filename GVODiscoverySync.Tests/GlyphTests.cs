// GlyphTests.cs
// 比對字形：用實際的遊戲畫面（TestData/Screens，800×600、縮放 1）。
// 遊戲的字是點陣字，同一個字每次出現的點完全一樣，所以從讀對的列學到字形後，讀不出來、讀錯的列可以直接比對。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using GVODiscoverySync.Matching;
using GVODiscoverySync.Ocr;
using GVODiscoverySync.Recognition;
using Xunit;

namespace GVODiscoverySync.Tests;

public class GlyphTests
{
    // 三張截圖上每一列的名稱（遊戲上的寫法；null 是有括號的名稱，寬度不固定，不比對字形）
    private static readonly string[] Astronomy =
        ["獵戶座中的傘狀之星雲", "天津四", "北極星", "天鵝座β", "織女星", "獵戶座", "烏鴉座", "夏季大三角", "月面", "牛郎星"];

    private static readonly string?[] Art =
        ["寺社緣起繪卷", "藥劑師的肖像", "耶穌受難圖", "俵屋宗達的屏風畫", null, "阿爾諾芬尼夫婦像", "獨眼巨人像的頭部", "姜希顔的水墨畫", null, "巴別塔"];

    private static Bitmap Screen(string file) => new(TestBook.DataPath(Path.Combine("Screens", file)));

    // 截圖上第 row 列的字形（字寬用名稱算，和畫面上量到的一樣）
    private static IReadOnlyList<Glyph> Cells(Bitmap client, int row, string name)
        => DiscoveryListReader.ReadGlyphs(client, new ListLayout(), row, 1, TestBook.GameWidth(name))!;

    // 一列：讀到 text（"" 是讀不出來），字形從截圖取
    private static ListRow Row(Bitmap client, int row, string name, string? text = null)
    {
        text ??= name;
        return new ListRow(row, text, false, text == "" ? Array.Empty<string>() : [text], TestBook.GameWidth(name), Cells(client, row, name));
    }

    [Fact]
    public void 同一個字的字形完全一樣_不同的字差很多()
    {
        using Bitmap client = Screen("astronomy-all-cards.png");
        List<(char Char, Glyph Glyph)> all = [];
        for (int row = 0; row < Astronomy.Length; row++)
        {
            IReadOnlyList<Glyph> cells = Cells(client, row, Astronomy[row]);
            all.AddRange(Astronomy[row].Select((c, i) => (c, cells[i])));
        }
        Assert.All(all, g => Assert.False(g.Glyph.IsEmpty));
        foreach ((char a, Glyph ga) in all)
        {
            foreach ((char b, Glyph gb) in all)
            {
                if (a == b)
                {
                    Assert.True(ga.Distance(gb) == 0, $"「{a}」兩次出現的字形不一樣"); // 「座」「星」各出現四次，含選取中的那一列
                }
                else
                {
                    Assert.True(ga.Distance(gb) >= 10, $"「{a}」和「{b}」的字形太像（差 {ga.Distance(gb)} 個點）");
                }
            }
        }
    }

    [Fact]
    public void 不同的畫面上同一個字也一樣()
    {
        // 「的」在天文和美術品兩張截圖都有
        using Bitmap astronomy = Screen("astronomy-all-cards.png");
        using Bitmap art = Screen("art-row6-row10-no-card.png");
        Glyph a = Cells(astronomy, 0, Astronomy[0])[Astronomy[0].IndexOf('的')];
        Glyph b = Cells(art, 1, Art[1]!)[Art[1]!.IndexOf('的')];
        Assert.Equal(0, a.Distance(b));
    }

    [Fact]
    public void 字形可以存檔再讀回來()
    {
        using Bitmap client = Screen("astronomy-all-cards.png");
        GlyphStore store = new();
        store.Learn("獵戶座", Cells(client, 5, "獵戶座"));
        Assert.True(store.Dirty);
        string path = Path.Combine(Path.GetTempPath(), $"glyphs-{Guid.NewGuid():N}.json");
        try
        {
            store.SaveIfDirty(path);
            Assert.False(store.Dirty);
            GlyphStore loaded = GlyphStore.Load(path);
            Assert.Equal(3, loaded.Count);
            Assert.Equal(GlyphVerdict.Full, loaded.Check("獵戶座", Cells(client, 5, "獵戶座")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 字形比對的結果()
    {
        using Bitmap client = Screen("astronomy-all-cards.png");
        GlyphStore store = new();
        store.Learn("獵戶座", Cells(client, 5, "獵戶座"));
        store.Learn("北極星", Cells(client, 2, "北極星"));

        IReadOnlyList<Glyph> crow = Cells(client, 6, "烏鴉座"); // 畫面上是「烏鴉座」
        Assert.Equal(GlyphVerdict.Partial, store.Check("烏鴉座", crow)); // 只認得「座」，對得上
        Assert.Equal(GlyphVerdict.Mismatch, store.Check("獵戶座", crow)); // 「獵」「戶」對不上
        Assert.Equal(GlyphVerdict.Mismatch, store.Check("織女星", crow)); // 「星」對不上
        Assert.Equal(GlyphVerdict.Unknown, store.Check("牛郎星雲", crow)); // 字數不同：不能比
        Assert.Equal(GlyphVerdict.Unknown, store.Check("天津四", crow)); // 一個字都不認得
        Assert.Equal(GlyphVerdict.Full, store.Check("獵戶座", Cells(client, 5, "獵戶座")));
    }

    [Fact]
    public void 放大顯示時不比對字形()
    {
        using Bitmap client = Screen("astronomy-all-cards.png");
        Assert.Null(DiscoveryListReader.ReadGlyphs(client, new ListLayout(), 0, 1.5, 158));
        Assert.Null(DiscoveryListReader.ReadGlyphs(client, new ListLayout(), 0, 1, 0)); // 量不到字寬
    }

    [Fact]
    public void 讀不出來的列_每個字都學過就認得出來()
    {
        using Bitmap client = Screen("astronomy-all-cards.png");
        DiscoverySession session = new(TestBook.Book, TestBook.Matcher, new GlyphStore());

        // 第一次：「月面」讀不出來，「月」「面」都沒學過，認不出來
        List<ListRow> page = [.. Astronomy.Select((n, i) => Row(client, i, n, n == "月面" ? "" : null))];
        session.AddPage("天文", page);
        Assert.DoesNotContain(session.Matched.Values, f => f.ByGlyph);

        // 從別的列學到「月」「面」之後（這裡直接教），同一頁再讀一次：認出來了，而且是確定的（不是推斷）
        session.Glyphs!.Learn("月面", Cells(client, 8, "月面"));
        session.AddPage("天文", page);
        Found moon = session.Matched.Values.Single(f => f.Item?.Name == "月面");
        Assert.True(moon.ByGlyph);
        Assert.Equal(1, moon.Match.Score);
        Assert.False(moon.NeedsConfirm);
    }

    [Fact]
    public void 漏讀的列_每個字都學過就認得出來()
    {
        // 「獵戶座」漏讀成「戶座」。「獵」「戶」之前學過，「座」從同一頁讀對的「烏鴉座」學到：字形全部對得上，確定是獵戶座
        using Bitmap client = Screen("astronomy-all-cards.png");
        GlyphStore store = new();
        store.Learn("獵戶", [.. Cells(client, 5, "獵戶座").Take(2)]);
        DiscoverySession session = new(TestBook.Book, TestBook.Matcher, store);

        string[] names = ["天津四", "北極星", "織女星", "獵戶座", "烏鴉座", "牛郎星"];
        int[] rows = [1, 2, 4, 5, 6, 9];
        List<ListRow> page = [.. names.Select((n, i) => Row(client, rows[i], n))];
        page[3] = Row(client, 5, "獵戶座", "戶座");
        session.AddPage("天文", page);

        Found orion = session.Matched.Values.Single(f => f.Item?.Name == "獵戶座");
        Assert.True(orion.ByGlyph);
        Assert.Equal("獵戶座", orion.ScreenText);
        Assert.Equal(1, orion.Match.Score);
        Assert.Empty(session.Unmatched);
    }

    [Fact]
    public void 只認得一部分的字_用來排除不可能的名稱()
    {
        // 「烏鴉座」讀不出來，只認得「座」：不能確定是哪一個，但字形對不上的（○○星）不會被推斷出來
        using Bitmap client = Screen("astronomy-all-cards.png");
        GlyphStore store = new();
        store.Learn("獵戶座", Cells(client, 5, "獵戶座"));
        store.Learn("北極星", Cells(client, 2, "北極星"));
        DiscoverySession session = new(TestBook.Book, TestBook.Matcher, store);
        session.AddPage("天文", [Row(client, 6, "烏鴉座", "")]);

        Assert.DoesNotContain(session.All, f => f.Item is not null && f.Item.Name.EndsWith('星'));
        Assert.DoesNotContain(session.All, f => f.ByGlyph);
    }

    [Fact]
    public void 讀對的列會學到字形()
    {
        using Bitmap client = Screen("art-row6-row10-no-card.png");
        DiscoverySession session = new(TestBook.Book, TestBook.Matcher, new GlyphStore());
        session.AddPage("美術品", [Row(client, 9, "巴別塔"), Row(client, 0, "寺社緣起繪卷")]);
        Assert.Equal(9, session.Glyphs!.Count);
        Assert.True(session.Glyphs.Dirty);
    }
}
