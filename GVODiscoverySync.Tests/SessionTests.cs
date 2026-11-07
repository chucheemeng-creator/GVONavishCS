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
