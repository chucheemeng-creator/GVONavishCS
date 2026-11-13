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
            GlyphStore loaded = GlyphStore.Load(path, includeBuiltIn: false);
            Assert.Equal(3, loaded.Count);
            Assert.Equal(GlyphVerdict.Full, loaded.Check("獵戶座", Cells(client, 5, "獵戶座")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 內建的字形表()
    {
        // 沒有檔案時也認得內建的字；和內建一樣的不存檔，只存這台電腦另外學到的
        string path = Path.Combine(Path.GetTempPath(), $"glyphs-{Guid.NewGuid():N}.json");
        GlyphStore store = GlyphStore.Load(path);
        Assert.Equal(1807, store.Count);
        Assert.False(store.Dirty);

        using Bitmap client = Screen("astronomy-all-cards.png");
        Assert.Equal("獵戶座", store.Read(Cells(client, 5, "獵戶座"))); // 內建的字形和截圖上的一樣
        Assert.Equal(GlyphVerdict.Full, store.Check("夏季大三角", Cells(client, 7, "夏季大三角")));
    }

    [Fact]
    public void 認得的字只看到一次不一樣的字形不會被換掉()
    {
        using Bitmap client = Screen("astronomy-all-cards.png");
        IReadOnlyList<Glyph> orion = Cells(client, 5, "獵戶座"), crow = Cells(client, 6, "烏鴉座");
        GlyphStore store = new();
        store.Learn("獵戶座", orion);

        // 假設有一次讀錯位置：「座」那一格看到別的字形。只看到一次：不換
        store.Learn("座", [crow[0]]);
        Assert.Equal(GlyphVerdict.Full, store.Check("獵戶座", orion));

        // 中間又看到原本的字形：之前那一次不算數，再來一次不一樣的也還是不換
        store.Learn("座", [orion[2]]);
        store.Learn("座", [crow[0]]);
        Assert.Equal(GlyphVerdict.Full, store.Check("獵戶座", orion));

        // 連續兩次都是同一個新的字形：換
        store.Learn("座", [crow[0]]);
        Assert.Equal(GlyphVerdict.Mismatch, store.Check("獵戶座", orion));
    }

    [Fact]
    public void Excel和遊戲用的字不一樣_手動指定之後認得()
    {
        // 實際的情況：Excel 寫「阿閦如來」，遊戲上是「阿閦如来」。「來」從別的名稱學過（字形是「來」），
        // 所以這一列的字形和名稱矛盾，一直是「不確定」；手動指定之後，把這一格記成「來」的另一種字形
        using Bitmap client = Screen("relics-akshobhya.png");
        GlyphStore store = GlyphStore.Load(TestBook.DataPath("Glyphs-round1.json"), includeBuiltIn: false);
        List<ListRow> page = [Row(client, 6, "阿閦如來", "阿閦如来")];
        DiscoverySession session = new(TestBook.Book, TestBook.Matcher, store);
        session.AddPage("宗教遺物", page);
        Assert.Equal("阿？如？", store.Read(page[0].Glyphs));
        Found f = Assert.Single(session.All);
        Assert.Null(f.Item); // 不確定，請手動指定

        session.Assign(f, TestBook.Book.Categories["宗教遺物"].Single(i => i.Name == "阿閦如來"));
        Assert.Equal(1769, store.Count); // 學會「閦」；「來」多一種字形，字數不變
        Assert.Equal("阿閦如來", store.Read(page[0].Glyphs));
        Assert.Equal(GlyphVerdict.Full, store.Check("阿閦如來", page[0].Glyphs));

        // 存檔再讀回來、清除結果後重新讀：直接認得
        string path = Path.Combine(Path.GetTempPath(), $"glyphs-{Guid.NewGuid():N}.json");
        try
        {
            store.SaveIfDirty(path);
            DiscoverySession again = new(TestBook.Book, TestBook.Matcher, GlyphStore.Load(path, includeBuiltIn: false));
            again.AddPage("宗教遺物", page);
            Found g = again.Matched.Values.Single(x => x.Item?.Name == "阿閦如來");
            Assert.True(g.ByGlyph);
            Assert.False(g.NeedsConfirm);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 手動指定錯了不會把字形表弄亂()
    {
        // 把「烏鴉座」那一列指定成「獵戶座」：三個字只有「座」對得上，另外兩個都不一樣，不學
        using Bitmap client = Screen("astronomy-all-cards.png");
        GlyphStore store = new();
        store.Learn("獵戶座", Cells(client, 5, "獵戶座"));
        store.LearnPicked("獵戶座", Cells(client, 6, "烏鴉座"));
        Assert.Equal(GlyphVerdict.Mismatch, store.Check("獵戶座", Cells(client, 6, "烏鴉座")));
        Assert.Equal(3, store.Count);
    }

    [Fact]
    public void 內建的字形表認得阿閦如来()
    {
        using Bitmap client = Screen("relics-akshobhya.png");
        GlyphStore store = GlyphStore.Load(TestBook.DataPath("no-such-file.json"));
        Assert.Equal(GlyphVerdict.Full, store.Check("阿閦如來", Cells(client, 6, "阿閦如來")));
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
        Assert.Equal(GlyphVerdict.Mismatch, store.Check("牛郎星雲", crow)); // 字數不同不能逐格比，但畫面上有「座」、名稱裡沒有
        Assert.Equal(GlyphVerdict.Unknown, store.Check("南十字座", crow)); // 字數不同、名稱裡也有「座」：不能比
        Assert.Equal(GlyphVerdict.Mismatch, store.Check("Sulafat", crow)); // 有英文的名稱也一樣：畫面上有「座」
        Assert.Equal(GlyphVerdict.Mismatch, store.Check("天津四", crow)); // 第三格是認得的「座」，不會是「四」
        Assert.Equal(GlyphVerdict.Unknown, store.Check("天津四", Cells(client, 1, "天津四"))); // 一個字都不認得，也沒有矛盾
        Assert.Equal("？？座", store.Read(crow));
        Assert.Equal(GlyphVerdict.Full, store.Check("獵戶座", Cells(client, 5, "獵戶座")));
    }

    [Fact]
    public void 讀不到字形的情況()
    {
        using Bitmap client = Screen("astronomy-all-cards.png");
        GlyphStore store = GlyphStore.Load(TestBook.DataPath("no-such-file.json"));
        Assert.Null(DiscoveryListReader.ReadGlyphs(client, new ListLayout(), 0, 1, 0)); // 量不到字寬
        Assert.Null(DiscoveryListReader.ReadGlyphs(client, new ListLayout(), 0, 1.5, 158)); // 放大顯示、沒有已知的字形可以對
        Assert.Null(DiscoveryListReader.ReadGlyphs(client, new ListLayout(), 0, 1.25, 158, store.Snap)); // 125%：筆畫太糊，不讀
    }

    // 實際的 150% 截圖（筆電，1200×900）上每一列的名稱
    private static readonly string[] Birds150 =
        ["鳳凰雀", "金剛鸚鵡", "藍紫金剛鸚鵡", "海鸚鵡", "王霸鶲", "金鵰", "夜鷺", "華美極樂鳥", "雪鴞", "針尾維達鳥"];

    private static ListRow Row150(Bitmap client, GlyphStore store, int row, string name, string text)
        => new(row, text, false, text == "" ? Array.Empty<string>() : [text], TestBook.GameWidth(name),
            DiscoveryListReader.ReadGlyphs(client, new ListLayout(), row, 1.5, TestBook.GameWidth(name), store.Snap), GlyphsExact: false);

    [Fact]
    public void 放大顯示150_每個字都認得出來()
    {
        // 畫面被 Windows 放大 1.5 倍、筆畫是糊的：在每個遊戲像素的中心取樣，再換成最像的已知字形
        using Bitmap client = Screen("birds-150.png");
        GlyphStore store = GlyphStore.Load(TestBook.DataPath("no-such-file.json")); // 只用內建的字形
        for (int row = 0; row < Birds150.Length; row++)
        {
            IReadOnlyList<Glyph>? cells = DiscoveryListReader.ReadGlyphs(client, new ListLayout(), row, 1.5, TestBook.GameWidth(Birds150[row]), store.Snap);
            Assert.Equal(Birds150[row], store.Read(cells));
        }
        Assert.False(store.Dirty);
    }

    [Fact]
    public void 放大顯示150_認出讀不出來和讀錯的列()
    {
        // 文字辨識實際讀到的：金剛鸚鵡→「金剛鵡」、海鸚鵡→「海鵡」、金鵰和雪鴞讀不出來、夜鷺→「夜」
        using Bitmap client = Screen("birds-150.png");
        GlyphStore store = GlyphStore.Load(TestBook.DataPath("no-such-file.json"));
        DiscoverySession session = new(TestBook.Book, TestBook.Matcher, store);
        string[] read = ["鳳凰雀", "金剛鵡", "藍紫金剛鵡", "海鵡", "王霸鶲", "", "夜", "華美極樂鳥", "", "針尾維達鳥"];
        session.AddPage("鳥類", [.. Birds150.Select((n, i) => Row150(client, store, i, n, read[i]))]);

        Assert.Empty(session.Unmatched);
        foreach (string name in new[] { "金剛鸚鵡", "藍紫金剛鸚鵡", "海鸚鵡", "金鵰", "夜鷺", "雪鴞" })
        {
            Found f = session.Matched.Values.Single(x => x.Item?.Name == name);
            Assert.True(f.ByGlyph, name);
            Assert.False(f.NeedsConfirm, name);
        }
        Assert.Equal(1807, store.Count); // 放大顯示時不學
        Assert.False(store.Dirty);
    }

    [Fact]
    public void 放大顯示時認不出來的格子不當成矛盾()
    {
        using Bitmap client = Screen("birds-150.png");
        GlyphStore store = new();
        store.Learn("金", [GlyphStore.Load(TestBook.DataPath("no-such-file.json")).Snap(
            DiscoveryListReader.ReadGlyphs(client, new ListLayout(), 5, 1.5, 30, g => g)![0])]);
        IReadOnlyList<Glyph> cells = DiscoveryListReader.ReadGlyphs(client, new ListLayout(), 5, 1.5, 30, store.Snap)!; // 金鵰
        Assert.Equal("金？", store.Read(cells));
        Assert.Equal(GlyphVerdict.Partial, store.Check("金鵰", cells)); // 「鵰」那一格認不出來：不算矛盾，也不算全對
        Assert.Equal(GlyphVerdict.Mismatch, store.Check("雪鴞", cells));
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

    // 認得「夏季大？角」（不認得「三」）的字形表
    private static GlyphStore SummerTriangleWithoutThree(Bitmap client)
    {
        IReadOnlyList<Glyph> cells = Cells(client, 7, "夏季大三角");
        GlyphStore store = new();
        store.Learn("夏季大", [.. cells.Take(3)]);
        store.Learn("角", [cells[4]]);
        return store;
    }

    [Fact]
    public void 讀不出來_認得的格子只對得上一個名稱_推斷並在確認後學會()
    {
        // 實際的情況：「玫」文字辨識永遠讀不對，「？瑰琵鷺」「？瑰湖」一直找不到。
        // 認得的格子等於已經讀出來了：「夏季大？角」在天文類只對得上「夏季大三角」
        using Bitmap client = Screen("astronomy-all-cards.png");
        GlyphStore store = SummerTriangleWithoutThree(client);
        DiscoverySession session = new(TestBook.Book, TestBook.Matcher, store);
        List<ListRow> page = [Row(client, 7, "夏季大三角", "")];
        session.AddPage("天文", page);

        Found f = session.Matched.Values.Single(x => x.Item?.Name == "夏季大三角");
        Assert.Equal(0, f.Match.Score); // 推斷，請確認（還有不認得的字，不直接當成確定）
        Assert.True(f.NeedsConfirm);
        Assert.False(f.ByGlyph);
        Assert.Equal(4, store.Count); // 還沒學「三」

        session.Confirm(f); // 使用者勾選、寫入 Excel 之後
        Assert.Equal(5, store.Count);
        session.AddPage("天文", page); // 下次再讀到：每個字都認得，直接確定
        Assert.True(f.ByGlyph);
        Assert.False(f.NeedsConfirm);
    }

    [Fact]
    public void 漏讀_模糊比對和字形都指向同一個名稱_確定並學會不認得的字()
    {
        // 實際的情況：「南非兀鷲」讀成「南非兀」（86%），字形是「南非兀？」：兩種證據一致，順便學會「鷲」
        using Bitmap client = Screen("astronomy-all-cards.png");
        GlyphStore store = SummerTriangleWithoutThree(client);
        DiscoverySession session = new(TestBook.Book, TestBook.Matcher, store);
        session.AddPage("天文", [Row(client, 7, "夏季大三角", "夏季大角")]);

        Found f = session.Matched.Values.Single(x => x.Item?.Name == "夏季大三角");
        Assert.True(f.ByGlyph);
        Assert.Equal(1, f.Match.Score);
        Assert.Equal(5, store.Count); // 學會「三」
    }

    [Fact]
    public void 模糊比對的結果和字形矛盾時不採用()
    {
        // 畫面上是「烏鴉座」，文字辨識讀成「獵戶坐」（模糊比對會對到獵戶座）；但「獵」「戶」的字形對不上
        using Bitmap client = Screen("astronomy-all-cards.png");
        GlyphStore store = new();
        store.Learn("獵戶座", Cells(client, 5, "獵戶座"));
        DiscoverySession session = new(TestBook.Book, TestBook.Matcher, store);
        session.AddPage("天文", [Row(client, 6, "烏鴉座", "獵戶坐")]);

        Assert.DoesNotContain(session.Matched.Values, x => x.Item?.Name == "獵戶座");
    }

    [Fact]
    public void 手動指定之後學會那一列的字()
    {
        using Bitmap client = Screen("astronomy-all-cards.png");
        GlyphStore store = new();
        DiscoverySession session = new(TestBook.Book, TestBook.Matcher, store);
        session.AddPage("天文", [Row(client, 8, "月面", "")]); // 讀不出來
        Found f = Assert.Single(session.All);

        session.Assign(f, TestBook.Book.Categories["天文"].Single(i => i.Name == "月面"));
        Assert.Equal(2, store.Count); // 「月」「面」
        Assert.Equal(GlyphVerdict.Full, store.Check("月面", Cells(client, 8, "月面")));
    }

    [Fact]
    public void 實際的一頁_學過一輪之後()
    {
        // 使用者把所有分類掃過一輪學到的字形表（1768 個字），和第二輪仍然認不出來的一頁（鳥類）。
        // 文字辨識：第 3 列讀不出來、第 4 列讀成「琵閆」（找不到）、第 7 列讀成「黑天」（80%）、第 8 列漏了「鸚」
        using Bitmap client = Screen("birds-rose-spoonbill.png");
        GlyphStore store = GlyphStore.Load(TestBook.DataPath("Glyphs-round1.json"), includeBuiltIn: false);
        Assert.Equal(1768, store.Count);
        DiscoverySession session = new(TestBook.Book, TestBook.Matcher, store);
        (string Name, string Read)[] rows =
        [
            ("竹雞", "竹雞"), ("纓翅夜鷹", "纓翅夜鷹"), ("鷺鶴", ""), ("玫瑰琵鷺", "琵閆"), ("穴居貓頭鷹", "穴居貓頭鷹"),
            ("白令海鸕茲", "白令海鸕茲"), ("黑天鵝", "黑天"), ("彩虹吸蜜鸚鵡", "彩虹吸蜜鵡"), ("胡錦鳥", "胡錦鳥"), ("紅地鳩", "紅地鳩"),
        ];
        session.AddPage("鳥類", [.. rows.Select((r, i) => Row(client, i, r.Name, r.Read))]);

        Found Of(string name) => session.Matched.Values.Single(f => f.Item?.Name == name);
        Assert.True(Of("鷺鶴").ByGlyph); // 兩個字都學過：確定
        Assert.True(Of("黑天鵝").ByGlyph); // 「黑天？」＋模糊比對也是黑天鵝：確定，學會「鵝」
        Assert.True(Of("彩虹吸蜜鸚鵡").ByGlyph); // 學會「鸚」「鵡」
        Assert.Equal(0, Of("玫瑰琵鷺").Match.Score); // 「？瑰琵鷺」只對得上玫瑰琵鷺，但「玫」沒學過、文字辨識也沒讀對：推斷，請確認
        Assert.Empty(session.Unmatched);
        Assert.Equal(1771, store.Count); // 鵝、鸚、鵡

        session.Confirm(Of("玫瑰琵鷺")); // 使用者確認之後學會「玫」
        Assert.Equal(1772, store.Count);
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
