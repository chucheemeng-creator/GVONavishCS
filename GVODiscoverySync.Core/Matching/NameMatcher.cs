// NameMatcher.cs
// 把文字辨識讀到的名稱，對應到 Excel 上的發現物。
//
// 兩邊的寫法不一定完全一樣，所以用模糊比對：
//   - 間隔點有好幾種（· ‧ ・ •），辨識也可能把它讀成「-」或漏掉 → 比對時標點、空白一律拿掉
//   - 全形英數字轉半形、英文字母不分大小寫
//   - 辨識偶爾讀錯一兩個字（例如「庾」讀成「庚」），或是 Excel 本身的字跟遊戲不同 → 用編輯距離算相似度
//   - 辨識最常見的錯是「漏字」（筆畫密的字，例如「沙格鎮」讀成「沙格」）→ 讀到的比 Excel 短時，改用共同字數算
//   - 但讀到的字被好幾個名稱完整包含時（「玫瑰鳳蝶」漏讀成「鳳蝶」，「黑鳳蝶」「玫瑰鳳蝶」「天狗鳳蝶」都包含它），
//     字數最接近的分數最高，卻不一定是答案 → 標成不確定，交給同一頁推斷或使用者
//   - 固定會讀錯的字（「鑽」每次都讀成「巑」）→ 對照表（GVODiscoverySync.OcrConfusions.json）：
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

    private readonly DiscoveryBook _book;

    public NameMatcher(DiscoveryBook book, IReadOnlyDictionary<string, string>? confusions = null)
    {
        _book = book;
        SetConfusions(confusions);
        RebuildNames();
    }

    /// <summary>
    /// 重新整理比對用的名稱（名稱對照表換了的時候：比對用的是遊戲上的寫法，見 GameName）。
    /// </summary>
    public void RebuildNames()
    {
        _byCategory.Clear();
        _categories.Clear();
        foreach ((string category, List<DiscoveryItem> items) in _book.Categories)
        {
            // 依「拿掉結尾數字的名稱」分組，一組 2 個以上的就是同名
            Dictionary<string, List<DiscoveryItem>> byBase = [];
            List<string> order = [];
            foreach (DiscoveryItem item in items)
            {
                string baseKey = StripNumber(Normalize(GameName(item.Name)));
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
                    list.Add((Normalize(GameName(group[0].Name)), group)); // 不是同名：用完整名稱（保留數字）
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

    /// <summary>
    /// 這一頁讀到的每一列是不是都和 Excel 的名稱完全一樣（而且字數對得上）。title 是讀到的分類標題（"" 時從清單內容推測分類）。
    /// 是的話回傳 null；不是的話回傳原因（除錯輸出用）。
    /// 只看「完全一樣」，不看模糊比對的結果：模糊對到的，換個倍率可能讀得更完整，還是要讀。
    /// </summary>
    public string? WhyNotAllExact(string title, IReadOnlyList<(string Text, double Width)> rows)
    {
        string? category = MatchCategory(title) ?? GuessCategory([.. rows.Select(r => r.Text)]);
        if (category is null)
        {
            return "看不出分類";
        }
        foreach ((string text, double width) in rows)
        {
            MatchResult m = Match(category, text, width);
            if (m is not { IsMatch: true, Score: >= 1 })
            {
                return $"「{text}」在「{category}」裡沒有完全一樣的名稱" +
                       (m.Item is null ? "" : $"（最像的是 {m.Item.Name}，{m.Score:0%}{(m.Ambiguous ? "，不確定" : "")}）");
            }
        }
        return null;
    }

    /// <summary>
    /// 換成新的錯字對照表（讀到的 → 正確的）。對應到自己的（不想用某一組內建的）不列入。
    /// 依「讀到的」由長到短排：「冫每」要比「每」先換（見 ApplyConfusions）。
    /// </summary>
    public void SetConfusions(IReadOnlyDictionary<string, string>? confusions)
    {
        _confusions.Clear();
        _confusions.AddRange((confusions ?? new Dictionary<string, string>())
            .Where(p => !string.IsNullOrEmpty(p.Key) && p.Key != p.Value)
            .Select(p => (p.Key, p.Value ?? ""))
            .OrderByDescending(p => p.Key.Length)); // OrderBy 是穩定排序：一樣長的照檔案裡的順序
    }

    /// <summary>
    /// 套用錯字對照表：從左到右，每個位置換最長的那一組，換過的字不會再被別組換
    /// （同時有「冫每→海」「每→梅」時，「冫每」換成「海」，不會先變成「冫梅」；
    /// 「A→B」「B→C」也不會把 A 連換成 C）。
    /// </summary>
    private string ApplyConfusions(string text)
    {
        if (_confusions.Count == 0)
        {
            return text;
        }
        StringBuilder sb = new(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int at = _confusions.FindIndex(c => string.CompareOrdinal(text, i, c.Wrong, 0, c.Wrong.Length) == 0);
            if (at >= 0)
            {
                sb.Append(_confusions[at].Right);
                i += _confusions[at].Wrong.Length;
            }
            else
            {
                sb.Append(text[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 在分類 category 裡找名稱 text 對應的發現物（照原樣、套用錯字對照表各比一次，取比較好的）。
    /// width 是畫面上量到的字寬（遊戲像素，0 表示沒量到）：字數對不上的名稱不列入比對；
    /// 讀到的字完全一樣、字數卻對不上時，標成不確定。
    /// </summary>
    public MatchResult Match(string category, string text, double width = 0)
    {
        MatchResult plain = MatchRaw(category, text, width);
        // 照原樣就確定對到、而且完全一樣才不用換字再比。完全一樣但字數對不上（不確定）時還是要試：
        // 換字可能讓字數改變（「冫每」→「海」），對到字數正確的那一個
        if (_confusions.Count == 0 || (plain.IsMatch && plain.Score >= 1))
        {
            return plain;
        }
        string fixedText = ApplyConfusions(text);
        if (fixedText == text)
        {
            return plain;
        }
        MatchResult corrected = MatchRaw(category, fixedText, width);
        return IsBetter(corrected, plain) ? corrected : plain;
    }

    /// <summary>
    /// 所有相似度達到 MinScore 的候選（照原樣、套用錯字對照表都算，取比較高的），由高到低排序。
    /// 給「同一頁推斷」用：呼叫端排除不可能的候選後，自己判斷剩下的是不是夠確定。
    /// width 和 Match 一樣：字數對不上的名稱不列入候選。
    /// </summary>
    public List<(IReadOnlyList<DiscoveryItem> Items, double Score)> Candidates(string category, string text, double width = 0)
    {
        if (!_byCategory.TryGetValue(category, out List<(string Key, List<DiscoveryItem> Items)>? list))
        {
            return [];
        }
        string fixedText = ApplyConfusions(text);
        string key = Normalize(text);
        string fixedKey = Normalize(fixedText);
        List<(IReadOnlyList<DiscoveryItem> Items, double Score)> result = [];
        if (key == "")
        {
            return result;
        }
        foreach ((string k, List<DiscoveryItem> items) in list)
        {
            if (!FitsWidth(items, width)) // 完全一樣但字數對不上的也不算（同 MatchRaw）
            {
                continue;
            }
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
    /// 分類裡名稱的字元編碼介於 lower 和 upper 之間（不含）的發現物（同名的一組算一個）。null 表示那一邊沒有限制。
    /// 給「讀不出來的列」推斷用：清單照名稱排序時，這一列一定在上下兩列之間。
    /// </summary>
    public List<IReadOnlyList<DiscoveryItem>> Between(string category, string? lower, string? upper)
    {
        lower = lower is null ? null : SortKey(lower);
        upper = upper is null ? null : SortKey(upper);
        if (!_byCategory.TryGetValue(category, out List<(string Key, List<DiscoveryItem> Items)>? list))
        {
            return [];
        }
        return [.. list
            .Where(e => lower is null || string.CompareOrdinal(SortKey(e.Items[0].Name), lower) > 0)
            .Where(e => upper is null || string.CompareOrdinal(SortKey(e.Items[0].Name), upper) < 0)
            .Select(e => (IReadOnlyList<DiscoveryItem>)e.Items)];
    }

    // Excel 和遊戲寫法不同的名稱（不是同名組的結尾數字那種）：Excel 為了和別的分類區分加了註記，遊戲上沒有
    // （地理類也有一個「關島」，港口類的 Excel 就寫成「關島(港)」）。
    // 內容來自名稱對照表 GVODiscoverySync.NameAliases.json（見 MappingFile），使用者可以自己加。
    // 整個程式共用一份（排序、字數這些靜態的方法也要用），換的時候整個換掉
    private static Dictionary<string, string> _excelToGame = Aliases(MappingFile.BuiltInNameAliases);

    /// <summary>
    /// 換成新的名稱對照表（Excel 的寫法 → 遊戲上的寫法）。換了以後，已經建立的比對器要呼叫 RebuildNames。
    /// 對應到自己的（不想用某一組內建的）不列入。
    /// </summary>
    public static void SetNameAliases(IReadOnlyDictionary<string, string>? aliases)
        => _excelToGame = Aliases(aliases ?? new Dictionary<string, string>());

    private static Dictionary<string, string> Aliases(IReadOnlyDictionary<string, string> aliases)
    {
        Dictionary<string, string> map = [];
        foreach ((string excel, string game) in aliases)
        {
            string from = excel.Trim(), to = (game ?? "").Trim();
            if (from != "" && to != "" && from != to)
            {
                map[from] = to;
            }
        }
        return map;
    }

    /// <summary>
    /// Excel 上的名稱在遊戲畫面上的寫法（例如 Excel 的「關島(港)」在遊戲上是「關島」）。沒有特別的就是原本的名稱。
    /// 比對、字數、排序都用遊戲上的寫法；寫入 Excel 時還是用 Excel 的名稱。
    /// </summary>
    public static string GameName(string excelName)
        => _excelToGame.TryGetValue(excelName.Trim(), out string? game) ? game : excelName;

    /// <summary>
    /// 比較「照名稱排序」順序用的名稱：遊戲上的寫法，拿掉結尾的數字。
    /// 遊戲上同名的發現物都顯示成「聖母加冠」，Excel 才寫成「聖母加冠1」「聖母加冠2」，
    /// 用拿掉數字的名稱比較，順序才和遊戲一致。
    /// </summary>
    public static string SortKey(string name)
    {
        name = GameName(name);
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

    private MatchResult MatchRaw(string category, string text, double width)
    {
        string key = Normalize(text);
        if (key == "" || !_byCategory.TryGetValue(category, out List<(string Key, List<DiscoveryItem> Items)>? list))
        {
            return MatchResult.None(0);
        }

        List<DiscoveryItem>? best = null;
        string bestKey = "";
        double bestScore = 0, second = 0;
        int containing = 0; // 有幾個名稱完整包含讀到的字（讀到的是漏了字的）
        foreach ((string k, List<DiscoveryItem> items) in list)
        {
            if (k == key)
            {
                // 完全一樣，不用再比。但字數對不上時，多半是漏了字、剛好變成另一個名稱（「三聖像」漏讀成「聖像」），標成不確定
                return Result(items, 1, !FitsWidth(items, width));
            }
            if (!FitsWidth(items, width))
            {
                continue; // 字數對不上（例如讀到「鳳蝶」、畫面上是四個字寬，就不會是「黑鳳蝶」）
            }
            double s = Similarity(key, k);
            if (key.Length < k.Length && CommonSubsequence(key, k) == key.Length)
            {
                containing++;
            }
            if (s > bestScore)
            {
                second = bestScore;
                bestScore = s;
                best = items;
                bestKey = k;
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
        bool ambiguous = bestScore - second < MinLead
                         // 第一名是「完整包含讀到的字」，而且還有別的名稱也完整包含：漏的是哪幾個字分不出來
                         || (containing >= 2 && key.Length < bestKey.Length && CommonSubsequence(key, bestKey) == key.Length);
        return Result(best, bestScore, ambiguous);
    }

    /// <summary>
    /// 讀到的字（read）是不是被名稱（name）完整包含、只是少了字（依序都在，例如「鳳蝶」之於「玫瑰鳳蝶」）。
    /// 比較時名稱拿掉結尾數字（同名組）。
    /// </summary>
    public static bool IsContainedIn(string read, string name)
    {
        string r = Normalize(read);
        string n = StripNumber(Normalize(GameName(name)));
        return r != "" && r.Length < n.Length && CommonSubsequence(r, n) == r.Length;
    }

    /// <summary>遊戲清單的字型：中文（全形）字每個 16 遊戲像素寬，半形字 8。</summary>
    public const double FullWidthCharPixels = 16;

    // 量到的寬度和預期的寬度可以差多少（遊戲像素）：比半個中文字小，相差一個字時一定分得出來
    private const double WidthTolerance = 7;

    /// <summary>
    /// 名稱的字數和畫面上量到的字寬（width，遊戲像素）對不對得上。width 是 0（沒量到）時都算對得上。
    /// 同名的一組只要其中一個對得上就算。
    /// </summary>
    public static bool FitsWidth(IReadOnlyList<DiscoveryItem> items, double width)
        => width <= 0 || items.Any(i => FitsWidth(i.Name, width));

    /// <summary>名稱 name 的字數和畫面上量到的字寬（width，遊戲像素）對不對得上。width 是 0（沒量到）時都算對得上。</summary>
    public static bool FitsWidth(string name, double width)
    {
        if (width <= 0)
        {
            return true;
        }
        name = GameName(name);
        // 遊戲上同名的一組不顯示結尾的數字（Excel 才有），有沒有數字兩種都算
        return Fits(name) || Fits(SortKey(name));

        bool Fits(string n)
        {
            (int min, int max) = WidthUnits(n);
            // 量到的是第一個字的左邊到最後一個字的右邊，比字的間距少大約 2 遊戲像素
            double half = FullWidthCharPixels / 2;
            return width >= min * half - 2 - WidthTolerance && width <= max * half - 2 + WidthTolerance;
        }
    }

    // 這個字在遊戲上確定是一個中文字寬（英數字、括號等符號不確定）
    private static bool IsFullWidth(char c)
        => c is '·' or '・' or '‧' || !(c < 0x80 || (c >= '\uFF00' && c <= '\uFFEF') || !char.IsLetter(c));

    /// <summary>名稱的每個字在遊戲上都確定是一個中文字寬（可以一格一格切開比對字形）。</summary>
    public static bool IsFullWidthName(string name) => name.Length > 0 && name.All(IsFullWidth);

    // 名稱的寬度範圍（半形字的單位，中文字算 2）。
    // 間隔號不管 Excel 寫成 ·、・、‧ 哪一種，遊戲上都是一個中文字寬（實測「埃努瑪·埃里什」是 7 個字寬），算 2。
    // 英數字、括號等其他符號，Excel 和遊戲的全形／半形不一定一樣
    // （Excel 寫「巴別塔（小）」，遊戲上是半形的「巴別塔(小)」），算 1～2。
    // 希臘字母在遊戲上是全形（實測「天鵝座β」是 4 個字寬），和中文字一樣算 2
    private static (int Min, int Max) WidthUnits(string name)
    {
        int min = 0, max = 0;
        foreach (char c in name.Trim())
        {
            bool unsure = !IsFullWidth(c);
            min += unsure ? 1 : 2;
            max += 2;
        }
        return (min, max);
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
