// SimulationTests.cs
// 模擬辨識讀錯：Excel 裡每一個名稱，每一種漏字、錯字的情況都試一遍，確認「不會確定地對到錯的名稱」。
// 讀不出來、不確定都可以（使用者會確認），對錯就會把沒發現的發現物打勾寫進 Excel，這是最不能發生的。
//
// 字寬用名稱本身算（中文字 16、半形 8 遊戲像素），模擬畫面上量到的寬度。

using System.Collections.Generic;
using System.Linq;
using GVODiscoverySync.Excel;
using GVODiscoverySync.Matching;
using Xunit;
using Xunit.Abstractions;

namespace GVODiscoverySync.Tests;

public class SimulationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("漏一個字")]
    [InlineData("錯一個字")]
    [InlineData("漏兩個字")]
    public void 讀錯時不會確定地對到錯的名稱(string mode)
    {
        NameMatcher matcher = TestBook.Matcher;
        int right = 0, unsure = 0, notFound = 0;
        List<string> wrong = [];
        foreach ((string category, List<DiscoveryItem> items) in TestBook.Book.Categories)
        {
            foreach (DiscoveryItem item in items)
            {
                string key = NameMatcher.Normalize(item.Name);
                if (key.Length < (mode == "漏兩個字" ? 4 : 3))
                {
                    continue; // 太短的名稱漏了字就沒剩幾個字，本來就認不出來
                }
                double width = TestBook.GameWidth(item.Name);
                foreach (string read in Misreads(key, mode))
                {
                    MatchResult m = matcher.Match(category, read, width);
                    if (m.Item is null)
                    {
                        notFound++;
                    }
                    else if (!m.IsMatch)
                    {
                        unsure++;
                    }
                    else if (NameMatcher.SortKey(m.Item.Name) == NameMatcher.SortKey(item.Name))
                    {
                        right++; // 同名的一組對到組裡哪一個都算對（是哪一個由畫面上的位置決定）
                    }
                    else
                    {
                        wrong.Add($"{category}／{item.Name}：讀成「{read}」→ {m.Item.Name}（{m.Score:0%}）");
                    }
                }
            }
        }
        output.WriteLine($"{mode}：對 {right}、不確定 {unsure}、找不到 {notFound}、對錯 {wrong.Count}");
        Assert.True(wrong.Count == 0, $"{mode}對錯了 {wrong.Count} 個：\n" + string.Join("\n", wrong.Take(30)));
    }

    // 名稱 key（正規化後）的每一種讀錯情況（不重複）
    private static IEnumerable<string> Misreads(string key, string mode)
    {
        HashSet<string> reads = [];
        for (int i = 0; i < key.Length; i++)
        {
            switch (mode)
            {
                case "漏一個字":
                    reads.Add(key.Remove(i, 1));
                    break;
                case "錯一個字":
                    reads.Add(key[..i] + "囗" + key[(i + 1)..]); // 換成 Excel 裡沒有的字
                    break;
                case "漏兩個字":
                    for (int j = i + 1; j < key.Length; j++)
                    {
                        reads.Add(key.Remove(j, 1).Remove(i, 1));
                    }
                    break;
            }
        }
        return reads;
    }
}
