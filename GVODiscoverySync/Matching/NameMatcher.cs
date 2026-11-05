// NameMatcher.cs
// 把文字辨識讀到的名稱，對應到 Excel 上的發現物。
//
// 兩邊的寫法不一定完全一樣，所以用模糊比對：
//   - 間隔點有好幾種（· ‧ ・ •），辨識也可能把它讀成「-」或漏掉 → 比對時標點、空白一律拿掉
//   - 全形英數字轉半形、英文字母不分大小寫
//   - 辨識偶爾讀錯一兩個字（例如「庾」讀成「庚」），或是 Excel 本身的字跟遊戲不同 → 用編輯距離算相似度
//   - 辨識最常見的錯是「漏字」（筆畫密的字，例如「沙格鎮」讀成「沙格」）→ 讀到的比 Excel 短時，改用共同字數算
//   - 固定會讀錯的字（「鑽」每次都讀成「巑」）→ 對照表（設定檔的 OcrConfusions）：
//     照原樣比一次、換字後再比一次，取比較像的，所以原本就有這個字的名稱不受影響
// 只在同一個分類裡找（不同分類有同名的發現物，例如「渾天儀」「犀牛」）。
//
// 同名的發現物：遊戲裡兩個都叫「聖母加冠」，Excel 寫成「聖母加冠1」「聖母加冠2」（也有「火蜥蜴」「火蜥蜴2」）。
// 名稱只差結尾數字的當成一組（Variants），比對時用拿掉數字的名稱；是組裡的哪一個由呼叫端依出現次數決定。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GVODiscoverySync.Excel;

namespace GVODiscoverySync.Matching;

/// <summary>
/// 比對結果。Variants 是同名的一組（依 Excel 名稱結尾的數字排序，2 個以上），不是同名時是空的；
/// 同名時 Item 是組裡的第一個。
/// </summary>
public sealed record MatchResult(DiscoveryItem? Item, double Score, bool Ambiguous, IReadOnlyList<DiscoveryItem> Variants)
{
    public bool IsMatch => Item is not null && !Ambiguous;

    public bool IsVariant => Variants.Count > 1;

    public static MatchResult None(double score) => new(null, score, false, []);
}

public sealed class NameMatcher
{
    /// <summary>相似度至少要這麼高才算對到。</summary>
    public const double MinScore = 0.6;

    /// <summary>第一名和第二名的相似度至少要差這麼多，否則當成「不確定」。</summary>
    public const double MinLead = 0.1;

    // 分類 → (正規化後的名稱, 發現物)；同名的一組共用一個名稱（拿掉結尾數字）
    private readonly Dictionary<string, List<(string Key, List<DiscoveryItem> Items)>> _byCategory = [];
    private readonly List<(string Key, string Category)> _categories = [];

    // 固定會讀錯的字：讀到的 → 正確的
    private readonly List<(string Wrong, string Right)> _confusions = [];

    public NameMatcher(DiscoveryBook book, IReadOnlyDictionary<string, string>? confusions = null)
    {
        foreach ((string wrong, string right) in confusions ?? new Dictionary<string, string>())
        {
            if (wrong != "" && wrong != right)
            {
                _confusions.Add((wrong, right ?? ""));
            }
        }

        foreach ((string category, List<DiscoveryItem> items) in book.Categories)
        {
            // 依「拿掉結尾數字的名稱」分組，一組 2 個以上的就是同名
            Dictionary<string, List<DiscoveryItem>> byBase = [];
            List<string> order = [];
            foreach (DiscoveryItem item in items)
            {
                string baseKey = StripNumber(Normalize(item.Name));
                if (!byBase.TryGetValue(baseKey, out List<DiscoveryItem>? group))
                {
                    group = [];
                    byBase[baseKey] = group;
                    order.Add(baseKey);
                }
                group.Add(item);
            }

            List<(string, List<DiscoveryItem>)> list = [];
            foreach (string baseKey in order)
            {
                List<DiscoveryItem> group = byBase[baseKey];
                if (group.Count == 1)
                {
                    list.Add((Normalize(group[0].Name), group)); // 不是同名：用完整名稱（保留數字）
                }
                else
                {
                    // 「火蜥蜴」（沒有數字）排第一，接著 2、3……
                    List<DiscoveryItem> sorted = [.. group.OrderBy(i => NumberSuffix(Normalize(i.Name)))];
                    list.Add((baseKey, sorted));
                }
            }
            _byCategory[category] = list;
            _categories.Add((Normalize(category), category));
        }
    }

    /// <summary>把畫面上讀到的分類標題對應到工作表名稱。對不到時回傳 null。</summary>
    public string? MatchCategory(string text)
    {
        string key = Normalize(text);
        if (key == "")
        {
            return null;
        }
        string? best = null;
        double bestScore = 0, second = 0;
        foreach ((string k, string category) in _categories)
        {
            double s = Similarity(key, k);
            if (s > bestScore)
            {
                second = bestScore;
                bestScore = s;
                best = category;
            }
            else if (s > second)
            {
                second = s;
            }
        }
        // 分類只有 19 個，名稱也都不像：對一半的字、而且明顯比第二名像就算（例如「史蹟」→「史跡」）
        return bestScore >= 0.5 && bestScore - second >= 0.25 ? best : null;
    }

    /// <summary>
    /// 從清單上讀到的名稱推測分類（分類標題讀不到時用）：
    /// 每個分類算有幾個名稱對得上（相似度 80% 以上），最多的那個分類要明顯領先，而且至少對到一半。
    /// 各分類之間幾乎沒有同名的發現物，一頁 10 個名稱就很確定是哪個分類。
    /// </summary>
    public string? GuessCategory(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0)
        {
            return null;
        }
        string? best = null;
        int bestCount = 0, second = 0;
        foreach (string category in _byCategory.Keys)
        {
            int count = 0;
            foreach (string text in texts)
            {
                MatchResult m = Match(category, text);
                if (m.IsMatch && m.Score >= 0.8)
                {
                    count++;
                }
            }
            if (count > bestCount)
            {
                second = bestCount;
                bestCount = count;
                best = category;
            }
            else if (count > second)
            {
                second = count;
            }
        }
        return bestCount >= 1 && bestCount > second && bestCount * 2 >= texts.Count ? best : null;
    }

    /// <summary>在分類 category 裡找名稱 text 對應的發現物（照原樣、套用錯字對照表各比一次，取比較好的）。</summary>
    public MatchResult Match(string category, string text)
    {
        MatchResult plain = MatchRaw(category, text);
        if (_confusions.Count == 0 || plain.Score >= 1)
        {
            return plain;
        }
        string fixedText = text;
        foreach ((string wrong, string right) in _confusions)
        {
            fixedText = fixedText.Replace(wrong, right);
        }
        if (fixedText == text)
        {
            return plain;
        }
        MatchResult corrected = MatchRaw(category, fixedText);
        return IsBetter(corrected, plain) ? corrected : plain;
    }

    /// <summary>
    /// 所有相似度達到 MinScore 的候選（照原樣、套用錯字對照表都算，取比較高的），由高到低排序。
    /// 給「同一頁推斷」用：呼叫端排除不可能的候選後，自己判斷剩下的是不是夠確定。
    /// </summary>
    public List<(IReadOnlyList<DiscoveryItem> Items, double Score)> Candidates(string category, string text)
    {
        if (!_byCategory.TryGetValue(category, out List<(string Key, List<DiscoveryItem> Items)>? list))
        {
            return [];
        }
        string fixedText = text;
        foreach ((string wrong, string right) in _confusions)
        {
            fixedText = fixedText.Replace(wrong, right);
        }
        string key = Normalize(text);
        string fixedKey = Normalize(fixedText);
        List<(IReadOnlyList<DiscoveryItem> Items, double Score)> result = [];
        if (key == "")
        {
            return result;
        }
        foreach ((string k, List<DiscoveryItem> items) in list)
        {
            double s = Math.Max(k == key ? 1 : Similarity(key, k), fixedKey == key ? 0 : (k == fixedKey ? 1 : Similarity(fixedKey, k)));
            if (s >= MinScore)
            {
                result.Add((items, s));
            }
        }
        result.Sort((a, b) => b.Score.CompareTo(a.Score));
        return result;
    }

    /// <summary>
    /// 分類裡名稱的字元編碼介於 lower 和 upper 之間（不含）的發現物（同名的一組算一個）。
    /// 給「讀不出來的列」推斷用：清單照名稱排序時，這一列一定在上下兩列之間。
    /// </summary>
    public List<IReadOnlyList<DiscoveryItem>> Between(string category, string lower, string upper)
    {
        lower = SortKey(lower);
        upper = SortKey(upper);
        if (!_byCategory.TryGetValue(category, out List<(string Key, List<DiscoveryItem> Items)>? list))
        {
            return [];
        }
        return [.. list
            .Where(e => string.CompareOrdinal(SortKey(e.Items[0].Name), lower) > 0 && string.CompareOrdinal(SortKey(e.Items[0].Name), upper) < 0)
            .Select(e => (IReadOnlyList<DiscoveryItem>)e.Items)];
    }

    /// <summary>
    /// 比較「照名稱排序」順序用的名稱：拿掉結尾的數字。
    /// 遊戲上同名的發現物都顯示成「聖母加冠」，Excel 才寫成「聖母加冠1」「聖母加冠2」，
    /// 用拿掉數字的名稱比較，順序才和遊戲一致。
    /// </summary>
    public static string SortKey(string name)
    {
        int end = name.Length;
        while (end > 0 && char.IsAsciiDigit(name[end - 1]))
        {
            end--;
        }
        return end == 0 ? name : name[..end];
    }

    // a 比 b 好：先看是不是確定對到，再看相似度
    private static bool IsBetter(MatchResult a, MatchResult b)
        => a.IsMatch != b.IsMatch ? a.IsMatch : a.Score > b.Score;

    private MatchResult MatchRaw(string category, string text)
    {
        string key = Normalize(text);
        if (key == "" || !_byCategory.TryGetValue(category, out List<(string Key, List<DiscoveryItem> Items)>? list))
        {
            return MatchResult.None(0);
        }

        List<DiscoveryItem>? best = null;
        double bestScore = 0, second = 0;
        foreach ((string k, List<DiscoveryItem> items) in list)
        {
            if (k == key)
            {
                return Result(items, 1, false); // 完全一樣，不用再比
            }
            double s = Similarity(key, k);
            if (s > bestScore)
            {
                second = bestScore;
                bestScore = s;
                best = items;
            }
            else if (s > second)
            {
                second = s;
            }
        }
        if (best is null || bestScore < MinScore)
        {
            return MatchResult.None(bestScore);
        }
        return Result(best, bestScore, bestScore - second < MinLead);
    }

    private static MatchResult Result(List<DiscoveryItem> items, double score, bool ambiguous)
        => new(items[0], score, ambiguous, items.Count > 1 ? items : []);

    // 拿掉結尾的數字：「聖母加冠2」→「聖母加冠」（全部是數字時不拿）
    private static string StripNumber(string key)
    {
        int end = key.Length;
        while (end > 0 && char.IsAsciiDigit(key[end - 1]))
        {
            end--;
        }
        return end == 0 ? key : key[..end];
    }

    // 結尾的數字（沒有數字時是 1，例如「火蜥蜴」排在「火蜥蜴2」前面）
    private static int NumberSuffix(string key)
    {
        string stripped = StripNumber(key);
        return stripped.Length < key.Length && int.TryParse(key[stripped.Length..], out int n) ? n : 1;
    }

    /// <summary>比對用的寫法：拿掉空白和標點，全形英數字轉半形，英文轉小寫。</summary>
    public static string Normalize(string text)
    {
        StringBuilder sb = new(text.Length);
        foreach (char ch in text)
        {
            char c = ch;
            if (c >= '！' && c <= '～')
            {
                c = (char)(c - '！' + '!'); // 全形 → 半形
            }
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 相似度 0～1。read 是辨識讀到的，name 是 Excel 上的。
    /// 一般用 1 − 編輯距離 ÷ 較長的長度；讀到的比較短（漏字）時，也算
    /// 2 × 依序相同的字數 ÷ 兩邊字數和，取比較高的（「古跡」對「古蜀遺跡」：0.5 → 0.67）。
    /// 讀到的不比較短時不用漏字的算法，否則「小連翹」錯一個字會被當成「連翹」。
    /// </summary>
    public static double Similarity(string read, string name)
    {
        int max = Math.Max(read.Length, name.Length);
        if (max == 0)
        {
            return 1;
        }
        double score = 1 - (double)EditDistance(read, name) / max;
        if (read.Length < name.Length)
        {
            score = Math.Max(score, 2.0 * CommonSubsequence(read, name) / (read.Length + name.Length));
        }
        return score;
    }

    // 兩個字串依序相同的字最多有幾個（最長共同子序列）
    private static int CommonSubsequence(string a, string b)
    {
        int[] prev = new int[b.Length + 1];
        int[] cur = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static int EditDistance(string a, string b)
    {
        int[] prev = new int[b.Length + 1];
        int[] cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
