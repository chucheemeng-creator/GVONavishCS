// SessionTests.cs
// 讀取結果的規則（Recognition/DiscoverySession.cs）：一頁一頁加進來、同一頁推斷、判斷排序、同名的發現物、手動處理。
// 用 Excel 裡的名稱排出「遊戲上的一頁」（依名稱：字元編碼由小到大；依價值：功績由高到低），模擬讀取。

using System;
using System.Collections.Generic;
using System.Linq;
using GVODiscoverySync.Excel;
using GVODiscoverySync.Matching;
using GVODiscoverySync.Ocr;
using GVODiscoverySync.Recognition;
using Xunit;

namespace GVODiscoverySync.Tests;

public class SessionTests
{
    // ── 準備資料 ──

    private static DiscoverySession NewSession() => new(TestBook.Book, TestBook.Matcher);

    private static List<DiscoveryItem> Items(string category) => TestBook.Book.Categories[category];

    // 同名的一組（名稱只差結尾數字）裡的發現物
    private static bool IsVariant(DiscoveryItem item, string category)
        => Items(category).Count(i => NameMatcher.SortKey(i.Name) == NameMatcher.SortKey(item.Name)) > 1;

    // 遊戲「依名稱排列」的順序（拿掉結尾數字後的字元編碼）；keep 以外的同名組先拿掉，免得干擾要測的規則
    private static List<DiscoveryItem> ByName(string category, string? keep = null)
        => [.. Items(category)
            .Where(i => !IsVariant(i, category) || NameMatcher.SortKey(i.Name) == keep)
            .OrderBy(i => NameMatcher.SortKey(i.Name), StringComparer.Ordinal)];

    // 遊戲「依價值排列」的順序（功績由高到低）
    private static List<DiscoveryItem> ByValue(string category, string? keep = null)
        => [.. Items(category)
            .Where(i => !IsVariant(i, category) || NameMatcher.SortKey(i.Name) == keep)
            .OrderByDescending(i => i.Merit)];

    // 從 order 第 start 個開始的一頁（count 列），每一列讀到的就是遊戲上的名稱（同名的一組不顯示結尾數字）
    private static List<ListRow> Page(List<DiscoveryItem> order, int start, int count = 10)
        => [.. order.Skip(start).Take(count).Select((item, i) => Row(i, NameMatcher.SortKey(item.Name)))];

    // 讀到 text 的一列（字寬用名稱算）
    private static ListRow Row(int index, string text)
        => new(index, text, false, [text], TestBook.GameWidth(text));

    // 讀到 text、但畫面上量到的字寬是 width 的一列（讀錯、漏字）
    private static ListRow Misread(int index, string text, double width)
        => new(index, text, false, text == "" ? Array.Empty<string>() : [text], width);

    // 把一頁的第 index 列換成 row
    private static List<ListRow> Replace(List<ListRow> page, int index, ListRow row)
    {
        List<ListRow> copy = [.. page];
        copy[index] = row;
        return copy;
    }

    private static Found MatchedOf(DiscoverySession session, string name)
        => session.Matched.Values.Single(f => f.Item?.Name == name);

    // ── 排序 ──

    [Fact]
    public void 依名稱排列的一頁()
    {
        DiscoverySession session = NewSession();
        PageResult page = session.AddPage("昆蟲", Page(ByName("昆蟲"), 20));
        Assert.Equal(SortMode.ByName, page.PageSort);
        Assert.Equal(SortMode.ByName, session.RememberedSort);
        Assert.Equal(10, page.Added);
        Assert.All(session.All, f => Assert.False(f.NeedsConfirm));
    }

    [Fact]
    public void 依價值排列的一頁()
    {
        DiscoverySession session = NewSession();
        PageResult page = session.AddPage("美術品", Page(ByValue("美術品"), 30));
        Assert.Equal(SortMode.ByValue, page.PageSort);
        Assert.Equal(SortMode.ByValue, session.RememberedSort);
    }

    [Fact]
    public void 換了排序方式()
    {
        DiscoverySession session = NewSession();
        session.AddPage("美術品", Page(ByName("美術品"), 0));
        Assert.Equal(SortMode.ByName, session.RememberedSort);
        session.AddPage("美術品", Page(ByValue("美術品"), 30));
        Assert.Equal(SortMode.ByValue, session.RememberedSort);
    }

    [Fact]
    public void 重新開始時排序重新判斷()
    {
        DiscoverySession session = NewSession();
        session.AddPage("昆蟲", Page(ByName("昆蟲"), 0));
        session.Clear();
        Assert.Equal(SortMode.Unknown, session.RememberedSort);
        Assert.Empty(session.All);
    }

    [Fact]
    public void 記得目前畫面上的發現物()
    {
        DiscoverySession session = NewSession();
        List<DiscoveryItem> order = ByName("植物");
        session.AddPage("植物", Page(order, 0));
        session.AddPage("植物", Page(order, 10));
        Assert.True(session.LastPage.SetEquals(order.Skip(10).Take(10))); // 只有最近一頁的
        session.Clear();
        Assert.Empty(session.LastPage);
    }

    // ── 重複讀取 ──

    [Fact]
    public void 同一頁讀兩次不會重複()
    {
        DiscoverySession session = NewSession();
        List<ListRow> page = Page(ByName("植物"), 10);
        session.AddPage("植物", page);
        Assert.Equal(0, session.AddPage("植物", page).Added);
        Assert.Equal(10, session.All.Count());
    }

    [Fact]
    public void 捲動時上下頁重疊()
    {
        DiscoverySession session = NewSession();
        List<DiscoveryItem> order = ByName("植物");
        session.AddPage("植物", Page(order, 10));
        Assert.Equal(3, session.AddPage("植物", Page(order, 13)).Added); // 第 13～22 個：只有 20～22 是新的
        Assert.Equal(13, session.All.Count());
    }

    // ── 讀不出來、讀錯 ──

    [Fact]
    public void 讀不出來的一個字用字數推斷()
    {
        DiscoverySession session = NewSession();
        List<DiscoveryItem> order = ByName("昆蟲");
        int at = order.FindIndex(i => i.Name == "蠶");
        int start = Math.Max(0, at - 5);
        List<ListRow> page = Replace(Page(order, start), at - start, Misread(at - start, "", TestBook.GameWidth("蠶")));
        session.AddPage("昆蟲", page);

        Found silkworm = MatchedOf(session, "蠶");
        Assert.Equal(0, silkworm.Match.Score); // 推斷出來的
        Assert.True(silkworm.NeedsConfirm); // 推斷的要使用者確認
        Assert.Empty(session.Unmatched);
    }

    [Fact]
    public void 讀不出來的列說明有幾個字()
    {
        // 依價值排列的一頁，中間一列讀不出來、推斷不出來：說明寫出字數和位置
        DiscoverySession session = NewSession();
        List<DiscoveryItem> order = ByValue("中型生物");
        List<ListRow> page = Page(order, 20);
        string above = page[4].Text, below = page[6].Text;
        session.AddPage("中型生物", Replace(page, 5, Misread(5, "", 14))); // 一個字寬
        Found f = Assert.Single(session.Unmatched.Values);
        Assert.True(f.Unreadable);
        Assert.Equal($"1 個字，在「{above}」和「{below}」之間", f.Note);
    }

    // 昆蟲依名稱排列、「蠶」那一列讀不出來的一頁（蠶用字數推斷出來）
    private static List<ListRow> SilkwormPage(bool readable = false)
    {
        List<DiscoveryItem> order = ByName("昆蟲");
        int at = order.FindIndex(i => i.Name == "蠶");
        int start = Math.Max(0, at - 5);
        List<ListRow> page = Page(order, start);
        return readable ? page : Replace(page, at - start, Misread(at - start, "", TestBook.GameWidth("蠶")));
    }

    [Fact]
    public void 推斷出來的列記得位置和字寬()
    {
        DiscoverySession session = NewSession();
        session.AddPage("昆蟲", SilkwormPage());
        Found silkworm = MatchedOf(session, "蠶");
        Assert.NotEqual("", silkworm.Above); // 指定視窗要用：上下列、字寬
        Assert.NotEqual("", silkworm.Below);
        Assert.Equal(TestBook.GameWidth("蠶"), silkworm.TextWidth);
    }

    [Fact]
    public void 推斷出來的列移除後_同分類其他推斷的列不受影響()
    {
        DiscoverySession session = NewSession();
        session.AddPage("昆蟲", SilkwormPage());
        session.Remove(MatchedOf(session, "蠶"));

        // 同一個位置再讀到：不加回來
        session.AddPage("昆蟲", SilkwormPage());
        Assert.DoesNotContain(session.All, f => f.Item?.Name == "蠶");

        // 同分類另一頁、另一列讀不出來：照樣推斷（以前移除「蠶」會記下空白文字，擋掉這個分類所有推斷出來的列）
        List<DiscoveryItem> order = ByName("昆蟲");
        int k = Enumerable.Range(1, order.Count - 2).First(j =>
            order[j].Name != "蠶"
            && TestBook.Matcher.Between("昆蟲", NameMatcher.SortKey(order[j - 1].Name), NameMatcher.SortKey(order[j + 1].Name))
                .Count(g => NameMatcher.FitsWidth(g, TestBook.GameWidth(order[j].Name))) == 1
            && Math.Abs(j - order.FindIndex(i => i.Name == "蠶")) > 10);
        int start = Math.Max(0, Math.Min(k - 5, order.Count - 10));
        session.AddPage("昆蟲", Replace(Page(order, start), k - start, Misread(k - start, "", TestBook.GameWidth(order[k].Name))));
        Assert.Equal(0, MatchedOf(session, order[k].Name).Match.Score);
    }

    [Fact]
    public void 推斷出來的列之後讀到字_換成讀到的結果()
    {
        DiscoverySession session = NewSession();
        List<Found> updated = [];
        session.Updated = (f, _) => updated.Add(f);
        session.AddPage("昆蟲", SilkwormPage());
        Found silkworm = MatchedOf(session, "蠶");
        Assert.True(silkworm.NeedsConfirm);

        session.AddPage("昆蟲", SilkwormPage(readable: true)); // 這次讀到「蠶」了
        Assert.Same(silkworm, MatchedOf(session, "蠶"));
        Assert.Equal("蠶", silkworm.ScreenText);
        Assert.Equal(1, silkworm.Match.Score);
        Assert.False(silkworm.NeedsConfirm); // 變成「新增」
        Assert.Contains(silkworm, updated);
    }

    [Fact]
    public void 不確定時最像的剛好是同名的一組_也不列出猜的名稱()
    {
        // 「水鹿」完全一樣，但畫面上是三個字寬：不確定。最像的是同名的一組（水鹿、水鹿2），也不給答案
        DiscoverySession session = NewSession();
        session.AddPage("大型生物", [Misread(0, "水鹿", TestBook.GameWidth("三個字"))]);
        Found f = Assert.Single(session.All);
        Assert.True(f.Match.IsVariant);
        Assert.True(f.Undecided);
        Assert.Null(f.Item);
    }

    [Fact]
    public void 找不到的列也說明有幾個字和位置()
    {
        DiscoverySession session = NewSession();
        List<ListRow> page = Page(ByName("植物"), 30);
        string above = page[4].Text, below = page[6].Text;
        session.AddPage("植物", Replace(page, 5, Misread(5, "囗囗囗", 46))); // 讀成亂碼、三個字寬
        Found f = Assert.Single(session.Unmatched.Values);
        Assert.Null(f.Item);
        Assert.Equal($"3 個字，在「{above}」和「{below}」之間", f.Note);
        Assert.Equal(46, f.TextWidth);
    }

    [Fact]
    public void 依價值排列時讀不出來的一個字用功績推斷()
    {
        // 中型生物一個字的有獐 640、豺 59、獾 13。依價值排列，獾的上下是鬣狗 24、北非髯羊 13：只有獾符合
        DiscoverySession session = NewSession();
        List<DiscoveryItem> order = ByValue("中型生物");
        int at = order.FindIndex(i => i.Name == "獾");
        int start = at - 6;
        session.AddPage("中型生物", Replace(Page(order, start), 6, Misread(6, "", TestBook.GameWidth("獾"))));

        Found badger = MatchedOf(session, "獾");
        Assert.Equal(0, badger.Match.Score); // 推斷出來的，要使用者確認
        Assert.True(badger.NeedsConfirm);
        Assert.Empty(session.Unmatched);
    }

    [Fact]
    public void 功績和Excel對不上時不推斷_也不列出猜的名稱()
    {
        // 實際的一頁（天文，依價值排列）：「天鵝座」漏讀成「天座」，三個字的「天○座」有十幾個一樣像。
        // 遊戲改版調整過功績、Excel 沒跟上：日落的太白星 165 排在天爐座 150 後面。
        // 這種超出預期的情況不推斷（排序看不出來），也不列出「最像的第一個」（以前會列出功績 1330 的天兔座），
        // 讓使用者自己指定
        string[] names = ["處女座的漩渦星雲", "天龍座的貓眼狀星雲", "天鷹座", "大熊座之圓形星雲", "小麥哲倫星雲",
            "水委一", "天鵝座", "金星的圓缺", "天爐座", "日落的太白星"];
        List<ListRow> page = [.. names.Select((n, i) => Row(i, n))];
        page = Replace(page, 6, Misread(6, "天座", TestBook.GameWidth("天鵝座")));

        DiscoverySession session = NewSession();
        PageResult result = session.AddPage("天文", page);
        Assert.Equal(SortMode.Unknown, result.PageSort);
        Found f = Assert.Single(session.Unmatched.Values);
        Assert.True(f.Undecided);
        Assert.Null(f.Item); // 不給答案，打勾也不會寫入
        Assert.Equal("3 個字，在「水委一」和「金星的圓缺」之間", f.Note);
        Assert.DoesNotContain(session.All, x => x.Item?.Name == "天兔座");
    }

    [Fact]
    public void 依價值排列時功績界線也用在讀錯的列()
    {
        // 「帶背的飛龍骨架」：帶背刺、帶背鰭一樣像。依價值排列時，只有功績在上下兩列之間的那一個可能
        DiscoveryItem spine = Items("化石").Single(i => i.Name == "帶背刺的飛龍骨架");
        DiscoveryItem fin = Items("化石").Single(i => i.Name == "帶背鰭的飛龍骨架");
        Assert.NotEqual(spine.Merit, fin.Merit);
        DiscoveryItem target = spine.Merit > fin.Merit ? fin : spine; // 讓功績較低的那一個當答案
        List<DiscoveryItem> order = ByValue("化石");
        int at = order.IndexOf(target);
        int start = Math.Max(0, Math.Min(at - 5, order.Count - 10));
        List<ListRow> page = Page(order, start);
        int row = at - start;
        // 另一個（功績較高的）不在這一頁上的話，才是只靠功績分辨
        DiscoveryItem other = target == fin ? spine : fin;
        Assert.DoesNotContain(page, r => r.Text == other.Name);

        DiscoverySession session = NewSession();
        session.AddPage("化石", Replace(page, row, Misread(row, "帶背的飛龍骨架", TestBook.GameWidth(target.Name))));
        Assert.True(MatchedOf(session, target.Name).Match.IsMatch);
    }

    [Fact]
    public void 同一頁已確定的不會是這一列()
    {
        // 「帶背的飛龍骨架」（漏了一個字）：帶背刺、帶背鰭都像，分不出來
        DiscoverySession alone = NewSession();
        alone.AddPage("化石", [Misread(0, "帶背的飛龍骨架", TestBook.GameWidth("帶背鰭的飛龍骨架"))]);
        Assert.DoesNotContain(alone.All, f => f.Match.IsMatch);

        // 同一頁上面一列已經是「帶背刺的飛龍骨架」：這一列就是「帶背鰭的飛龍骨架」
        DiscoverySession session = NewSession();
        session.AddPage("化石",
        [
            Row(0, "帶背刺的飛龍骨架"),
            Misread(1, "帶背的飛龍骨架", TestBook.GameWidth("帶背鰭的飛龍骨架")),
        ]);
        Found fin = MatchedOf(session, "帶背鰭的飛龍骨架");
        Assert.True(fin.Match.IsMatch);
    }

    [Fact]
    public void 讀錯的列之後讀對了就拿掉()
    {
        DiscoverySession session = NewSession();
        List<Found> removed = [];
        session.Removed = removed.Add;
        List<DiscoveryItem> order = ByName("植物");
        List<ListRow> page = Page(order, 30);
        string name = NameMatcher.SortKey(order[35].Name);

        // 第一次第 5 列讀成亂碼：找不到
        session.AddPage("植物", Replace(page, 5, Misread(5, "囗囗囗", TestBook.GameWidth(name))));
        Found wrong = Assert.Single(session.Unmatched.Values);

        // 同一個位置讀對了：那一筆找不到的過時了
        session.AddPage("植物", page);
        Assert.Empty(session.Unmatched);
        Assert.Contains(wrong, removed);
        Assert.Equal(10, session.Matched.Count);
    }

    // ── 手動處理 ──

    [Fact]
    public void 手動移除後不會再加回來()
    {
        DiscoverySession session = NewSession();
        List<ListRow> page = Page(ByName("植物"), 0);
        session.AddPage("植物", page);
        Found first = session.Matched.Values.First();
        string name = first.Item!.Name;
        session.Remove(first);

        session.AddPage("植物", page);
        Assert.DoesNotContain(session.All, f => f.Item?.Name == name);
    }

    [Fact]
    public void 手動指定()
    {
        DiscoverySession session = NewSession();
        session.AddPage("植物", [Misread(0, "囗囗囗", 46)]);
        Found f = Assert.Single(session.Unmatched.Values);
        DiscoveryItem item = Items("植物")[0];

        Assert.Null(session.Assign(f, item));
        Assert.Same(f, session.Matched[item]);
        Assert.True(f.Manual);
        Assert.False(f.NeedsConfirm);
        Assert.Empty(session.Unmatched);
    }

    [Fact]
    public void 手動指定成已經在清單上的發現物()
    {
        DiscoverySession session = NewSession();
        DiscoveryItem item = ByName("植物")[0];
        session.AddPage("植物", [Row(0, item.Name), Misread(1, "囗囗囗", 46)]);
        Found f = Assert.Single(session.Unmatched.Values);

        Found? other = session.Assign(f, item); // 重複了：不改，回傳原本那一筆
        Assert.Same(session.Matched[item], other);
        Assert.False(f.Manual);
    }

    // ── 同名的發現物（聖母加冠1、聖母加冠2，遊戲上都叫「聖母加冠」） ──

    [Fact]
    public void 同名的一組在同一頁全部看到就確定()
    {
        DiscoverySession session = NewSession();
        List<DiscoveryItem> order = ByName("美術品", keep: "聖母加冠");
        int at = order.FindIndex(i => NameMatcher.SortKey(i.Name) == "聖母加冠");
        session.AddPage("美術品", Page(order, Math.Max(0, at - 4)));

        Found one = MatchedOf(session, "聖母加冠1");
        Found two = MatchedOf(session, "聖母加冠2");
        Assert.False(one.VariantUnsure);
        Assert.False(two.VariantUnsure);
    }

    [Fact]
    public void 同名的一組只看到一個時請確認_之後看齊了就確定()
    {
        DiscoverySession session = NewSession();
        List<DiscoveryItem> order = ByName("美術品", keep: "聖母加冠");
        int at = order.FindIndex(i => NameMatcher.SortKey(i.Name) == "聖母加冠");

        // 這一頁的最後一列是第一個「聖母加冠」，第二個在下一頁
        session.AddPage("美術品", Page(order, at - 9));
        Found first = Assert.Single(session.All, f => f.Match.IsVariant);
        Assert.True(first.VariantUnsure);

        // 往下捲：兩個都看到了
        session.AddPage("美術品", Page(order, at - 4));
        List<Found> group = [.. session.All.Where(f => f.Match.IsVariant)];
        Assert.Equal(2, group.Count);
        Assert.All(group, f => Assert.False(f.VariantUnsure));
        string[] expected = ["聖母加冠1", "聖母加冠2"];
        Assert.Equal(expected, group.Select(f => f.Item!.Name).Order());
    }

    [Fact]
    public void 依價值排列時同名的用功績判斷是哪一個()
    {
        DiscoverySession session = NewSession();
        List<DiscoveryItem> order = ByValue("美術品", keep: "聖母加冠");
        int at = order.FindIndex(i => i.Name == "聖母加冠1"); // 功績 150；聖母加冠2 是 40，在很後面
        session.AddPage("美術品", Page(order, at - 5));

        Found f = Assert.Single(session.All, x => x.Match.IsVariant);
        Assert.Equal("聖母加冠1", f.Item!.Name);
        Assert.False(f.VariantUnsure);
    }

    [Fact]
    public void 依價值排列時兩隻同名都先填錯位置_之後換得過來()
    {
        // 聖母加冠1 功績 150、聖母加冠2 功績 40，依價值排列時分散在不同頁
        DiscoverySession session = NewSession();
        List<DiscoveryItem> order = ByValue("美術品", keep: "聖母加冠");
        int i1 = order.FindIndex(i => i.Name == "聖母加冠1");
        int i2 = order.FindIndex(i => i.Name == "聖母加冠2");
        session.AddPage("美術品", Page(order, 0)); // 先記住「依價值排列」

        // 甲（其實是聖母加冠2）在頁面第一列，只有下一列：兩個的功績都符合，依序填進「聖母加冠1」（填錯了）
        session.AddPage("美術品", Page(order, i2));
        // 乙（其實是聖母加冠1）在頁面最後一列，只有上一列：兩個都符合，填進剩下的「聖母加冠2」（也填錯了）
        session.AddPage("美術品", Page(order, i1 - 9));
        Assert.All(session.All.Where(f => f.Match.IsVariant), f => Assert.True(f.VariantUnsure));

        // 乙上下都看到了：功績判斷是聖母加冠1，和甲交換位置
        session.AddPage("美術品", Page(order, i1 - 5));
        Found one = MatchedOf(session, "聖母加冠1");
        Found two = MatchedOf(session, "聖母加冠2");
        Assert.False(one.VariantUnsure); // 乙：確定
        Assert.True(two.VariantUnsure); // 甲：還沒上下都看到，仍然請確認
        Assert.NotSame(one, two);
        Assert.Equal(2, session.All.Count(f => f.Match.IsVariant));
    }

    [Fact]
    public void 同名誤讀成兩列_移除假的那一列後剩下的要請確認()
    {
        // 其實只發現一個，但這一頁誤讀出兩列「聖母加冠」：看齊了，兩筆都確定
        DiscoverySession session = NewSession();
        List<DiscoveryItem> order = ByName("美術品", keep: "聖母加冠");
        int at = order.FindIndex(i => NameMatcher.SortKey(i.Name) == "聖母加冠");
        List<ListRow> page = Page(order, Math.Max(0, at - 4));
        session.AddPage("美術品", page);
        Found one = MatchedOf(session, "聖母加冠1");
        Assert.False(one.VariantUnsure);

        // 使用者移除假的那一列：剩下的分不出是 1 還是 2，改回請確認
        session.Remove(MatchedOf(session, "聖母加冠2"));
        Assert.True(one.VariantUnsure);

        // 再讀到同一頁也不會又變成確定
        session.AddPage("美術品", page);
        Assert.True(one.VariantUnsure);
        Assert.Single(session.All, f => f.Match.IsVariant);
    }

    [Fact]
    public void 同名的列改指定到組外_再移除()
    {
        DiscoverySession session = NewSession();
        List<DiscoveryItem> order = ByName("美術品", keep: "聖母加冠");
        int at = order.FindIndex(i => NameMatcher.SortKey(i.Name) == "聖母加冠");
        List<ListRow> page = Page(order, at - 4);
        session.AddPage("美術品", page);
        Found one = MatchedOf(session, "聖母加冠1");
        Found two = MatchedOf(session, "聖母加冠2");

        // 第二列其實是別的發現物：改指定到組外。組裡扣掉一個，剩下的分不出是哪一個，改回請確認
        DiscoveryItem outside = order[0]; // 不在這一頁上
        Assert.Null(session.Assign(two, outside));
        Assert.True(two.VariantSuppressed);
        Assert.Same(two, session.Matched[outside]);
        Assert.True(one.VariantUnsure);

        // 再移除同一筆：已經扣過，不會再扣（狀態不變）
        session.Remove(two);
        Assert.True(two.VariantSuppressed);
        Assert.True(one.VariantUnsure);
        Assert.False(session.Matched.ContainsKey(outside));

        // 再讀同一頁：移除的那一列不加回來，剩下的仍然請確認
        session.AddPage("美術品", page);
        Assert.Single(session.All, f => f.Match.IsVariant);
        Assert.True(one.VariantUnsure);
    }

    [Fact]
    public void 同名的列改指定到組外_又改回組內()
    {
        DiscoverySession session = NewSession();
        List<DiscoveryItem> order = ByName("美術品", keep: "聖母加冠");
        int at = order.FindIndex(i => NameMatcher.SortKey(i.Name) == "聖母加冠");
        List<ListRow> page = Page(order, at - 4);
        session.AddPage("美術品", page);
        Found one = MatchedOf(session, "聖母加冠1");
        Found two = MatchedOf(session, "聖母加冠2");

        session.Assign(two, order[0]); // 改到組外：扣一個
        Assert.True(one.VariantUnsure);

        DiscoveryItem back = TestBook.Book.Categories["美術品"].Single(i => i.Name == "聖母加冠2");
        Assert.Null(session.Assign(two, back)); // 改回組內：扣回來
        Assert.False(two.VariantSuppressed);

        // 再讀同一頁：兩個又同時看到，整組湊齊，剩下的那一筆確定
        session.AddPage("美術品", page);
        Assert.False(one.VariantUnsure);
        Assert.Equal(2, session.All.Count(f => f.Match.IsVariant));
    }

    [Fact]
    public void 同名的列移除後不會再加回來()
    {
        DiscoverySession session = NewSession();
        List<DiscoveryItem> order = ByName("美術品", keep: "聖母加冠");
        int at = order.FindIndex(i => NameMatcher.SortKey(i.Name) == "聖母加冠");
        List<ListRow> page = Page(order, Math.Max(0, at - 4));
        session.AddPage("美術品", page);
        Found one = MatchedOf(session, "聖母加冠1");
        session.Remove(one);

        session.AddPage("美術品", page);
        Assert.Single(session.All, f => f.Match.IsVariant); // 只剩另一個，移除的沒有加回來
    }
}
