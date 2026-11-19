// TestBook.cs
// 測試共用的發現物資料：從 TestData/Discoveries.tsv 讀（匯出自 Excel），不用打開 Excel。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GVODiscoverySync.Excel;
using GVODiscoverySync.Matching;

namespace GVODiscoverySync.Tests;

internal static class TestBook
{
    private static readonly Lazy<DiscoveryBook> LazyBook = new(Load);
    private static readonly Lazy<NameMatcher> LazyMatcher = new(() => new NameMatcher(Book, MappingFile.BuiltInConfusions));

    public static DiscoveryBook Book => LazyBook.Value;

    /// <summary>用內建錯字對照表的比對器（和程式第一次執行時一樣）。</summary>
    public static NameMatcher Matcher => LazyMatcher.Value;

    /// <summary>測試資料檔的完整路徑（複製到輸出資料夾的 TestData\）。</summary>
    public static string DataPath(string file) => Path.Combine(AppContext.BaseDirectory, "TestData", file);

    /// <summary>
    /// 讀 Tab 分隔的測試資料：略過空行和 # 開頭的說明，回傳（行號, 欄位）。
    /// </summary>
    public static List<(int Line, string[] Fields)> ReadTsv(string file)
    {
        List<(int, string[])> rows = [];
        string[] lines = File.ReadAllLines(DataPath(file));
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim() == "" || line.StartsWith('#'))
            {
                continue;
            }
            rows.Add((i + 1, line.Split('\t')));
        }
        return rows;
    }

    /// <summary>
    /// 名稱在遊戲清單上量到的字寬（遊戲像素）：中文字 16、半形字 8，
    /// 量的是第一個字的左邊到最後一個字的右邊，所以少 2。同名的一組不顯示結尾數字。
    /// </summary>
    public static double GameWidth(string name)
        => NameMatcher.SortKey(name).Sum(c => c < 0x80 ? 8 : 16) - 2;

    private static DiscoveryBook Load()
    {
        List<DiscoveryItem> items = [];
        foreach ((int line, string[] f) in ReadTsv("Discoveries.tsv"))
        {
            items.Add(new DiscoveryItem
            {
                Category = f[0],
                Name = f[1],
                Row = (uint)line,
                Merit = f.Length > 2 && int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int merit) ? merit : 0,
            });
        }
        return DiscoveryBook.FromItems(items);
    }
}
