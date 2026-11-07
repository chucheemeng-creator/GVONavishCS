// DiscoverySession.cs
// 讀取的結果和推斷規則（不依賴畫面）：一頁一頁加進來、同一頁推斷、判斷排序、同名的發現物、手動指定和移除。
// 畫面（MainForm）只負責顯示：新加一筆、某一筆要更新、某一筆移除時，用 Added、Updated、Removed 通知它。
// 拆出來是為了讓測試專案能直接測這些規則。

using System;
using System.Collections.Generic;
using System.Linq;
using GVODiscoverySync.Excel;
using GVODiscoverySync.Matching;
using GVODiscoverySync.Ocr;

namespace GVODiscoverySync.Recognition;

/// <summary>清單的排序方式（遊戲裡按 ⇅ 切換）。</summary>
public enum SortMode { Unknown, ByValue, ByName }

/// <summary>加入一頁的結果：新出現幾筆、這一頁的排序、這個分類目前對到幾個／Excel 裡共幾個。</summary>
public sealed record PageResult(int Added, SortMode PageSort, int Listed, int Total);

public sealed class DiscoverySession(DiscoveryBook book, NameMatcher matcher)
{
    /// <summary>新加了一筆（畫面要加一列）。</summary>
    public Action<Found>? Added { get; set; }

    /// <summary>某一筆的內容變了（畫面要更新那一列；第二個參數 true 表示勾選要回到預設）。</summary>
    public Action<Found, bool>? Updated { get; set; }

    /// <summary>某一筆移除了（畫面要拿掉那一列）。</summary>
    public Action<Found>? Removed { get; set; }

    public DiscoveryBook Book => book;

    public NameMatcher Matcher => matcher;

    /// <summary>對到 Excel 的（每個發現物最多一筆）。</summary>
    public IReadOnlyDictionary<DiscoveryItem, Found> Matched => _matched;

    /// <summary>對不到、不確定、讀不出來的。</summary>
    public IReadOnlyDictionary<string, Found> Unmatched => _unmatched;

    /// <summary>全部的結果。</summary>
    public IEnumerable<Found> All => _matched.Values.Concat(_unmatched.Values);

    /// <summary>記住的排序方式（證據充分的頁面判斷出來的）。</summary>
    public SortMode RememberedSort => _sortMode;

    // 對到 Excel 的（每個發現物只會出現一次）、對不到或不確定的（依分類＋畫面上的文字）
    private readonly Dictionary<DiscoveryItem, Found> _matched = [];
    private readonly Dictionary<string, Found> _unmatched = [];
    // 同名的一組（以組裡第一個為代表）→ 在同一頁最多看到幾個
    private readonly Dictionary<DiscoveryItem, int> _variantSeen = [];
    // 手動指定或移除過的列（分類＋畫面上的文字）：繼續捲動時又讀到同樣的文字，不要再加回來
    private readonly HashSet<string> _resolvedKeys = [];
    // 同名的一組（以組裡第一個為代表）→ 使用者移除、或改指定到組外的筆數（補的時候要算進去）
    private readonly Dictionary<DiscoveryItem, int> _variantSuppressed = [];
    // 同名的一組 → 看過的每一個（用上下鄰居的名稱分辨：鄰居不同就是不同的一個）
    private readonly Dictionary<DiscoveryItem, List<Occurrence>> _variantOccurrences = [];
    // 使用者移除過的同名列的身分：之後再讀到同一個，不要加回來
    private readonly HashSet<Occurrence> _removedOccurrences = [];
    // 同名的一組 → 使用者移除的「沒有身分」的筆數（沒有鄰居的列，補的時候要算進去）
    private readonly Dictionary<DiscoveryItem, int> _anonymousRemoved = [];
    // 同名組的一列（AddVariants 用）
    private sealed record VariantRow(string Text, bool HasCard, Occurrence? Occurrence);

    /// <summary>全部清掉（重新開始讀）。排序方式也重新判斷（使用者可能已經按 ⇅ 換了排序）。</summary>
    public void Clear()
    {
        _matched.Clear();
        _unmatched.Clear();
        _variantSeen.Clear();
        _resolvedKeys.Clear();
        _variantSuppressed.Clear();
        _variantOccurrences.Clear();
        _removedOccurrences.Clear();
        _anonymousRemoved.Clear();
        _sortMode = SortMode.Unknown;
        _pageSort = SortMode.Unknown;
    }

    /// <summary>
    /// 分析一頁（不加進結果）：每一列挑最像的讀法、判斷排序、同一頁推斷。
    /// remember＝false 時不改變記住的排序方式（除錯輸出用）。
    /// </summary>
    public List<(ListRow Row, string Text, MatchResult Match)> Analyze(string category, IReadOnlyList<ListRow> rows, bool remember = false)
    {
        List<(ListRow Row, string Text, MatchResult Match)> picks = [];
        foreach (ListRow row in rows)
        {
            (string text, MatchResult match) = PickBestReading(category, row);
            picks.Add((row, text, match));
        }
        // 這一頁是怎麼排序的（依名稱、依價值、看不出來）；用來推斷不確定的列和同名的發現物
        SortMode mode = DetectSort(picks, remember);
        ResolveWithinPage(category, picks, mode == SortMode.ByName);
        return picks;
    }

    /// <summary>
    /// 讀到的一頁：每一列挑最像的讀法、判斷排序、同一頁推斷，然後加進結果（新的一筆用 Added 通知畫面）。
    /// </summary>
    public PageResult AddPage(string category, IReadOnlyList<ListRow> pageRows)
    {
        List<(ListRow Row, string Text, MatchResult Match)> picks = Analyze(category, pageRows, remember: true);
        // 同名的一組另外處理（要看同一頁出現幾次）
        int added = 0;
        Dictionary<DiscoveryItem, (MatchResult Match, List<VariantRow> Rows)> variantRows = [];
        // 依價值排列的頁面：同名的列用上下鄰居的功績直接判斷是組裡的哪一個（同名的各個功績都不同）
        _pageSort = DetectSort(picks, remember: true); // 推斷完再判斷一次，已確定的列比較多
        bool byValue = _pageSort == SortMode.ByValue;
        for (int i = 0; i < picks.Count; i++)
        {
            (ListRow row, string text, MatchResult match) = picks[i];
            Occurrence? occurrence = null;
            if (match.IsMatch && match.IsVariant)
            {
                // 記住這一個同名列的上下鄰居，分辨是不是之前看過的那一個
                // 只用已確定的鄰居（Excel 的寫法）：同一個鄰居讀錯成兩種寫法時，才不會被當成兩個不同的
                occurrence = RegisterOccurrence(match.Variants, ConfidentName(picks, i - 1), ConfidentName(picks, i + 1));
                if (occurrence is not null && _removedOccurrences.Contains(occurrence))
                {
                    continue; // 使用者移除過的那一個
                }
            }
            if (match.IsMatch && match.IsVariant && byValue && PickVariantByMerit(picks, i, match.Variants) is DiscoveryItem exact)
            {
                if (AddVariantExact(category, text, row.HasCard, match, exact, occurrence))
                {
                    added++;
                }
                continue;
            }
            if (row.Readings.Count == 0 && !match.IsMatch)
            {
                // 讀不出來也推斷不出來：記下它在哪兩個發現物之間，讓使用者自己去遊戲裡看
                string above = i > 0 ? NameOf(picks[i - 1]) : "";
                string below = i + 1 < picks.Count ? NameOf(picks[i + 1]) : "";
                if (AddUnreadable(category, above, below, ConfidentName(picks, i - 1), ConfidentName(picks, i + 1)))
                {
                    added++;
                }
                continue;
            }
            if (match.IsMatch && match.IsVariant)
            {
                DiscoveryItem first = match.Variants[0];
                if (!variantRows.TryGetValue(first, out (MatchResult Match, List<VariantRow> Rows) group))
                {
                    group = (match, new List<VariantRow>());
                    variantRows[first] = group;
                }
                group.Rows.Add(new VariantRow(text, row.HasCard, occurrence));
            }
            else if (AddResult(category, text, row.HasCard, match, ConfidentName(picks, i - 1), ConfidentName(picks, i + 1)))
            {
                added++;
            }
        }
        foreach ((MatchResult match, List<VariantRow> rows) in variantRows.Values)
        {
            added += AddVariants(category, match, rows);
        }
        RemoveStale(category, picks);
        int listed = _matched.Values.Count(f => f.Category == category);
        int total = book.Categories.TryGetValue(category, out List<DiscoveryItem>? items) ? items.Count : 0;
        return new PageResult(added, _pageSort, listed, total);
    }

    // 第 index 列已確定的話，它的 Excel 名稱；沒有這一列、還不確定、或是同名的一組時是 ""
    // （同名組的每一列都先對到組裡第一個，好幾列的鄰居會叫同一個名字，不能拿來當位置的依據）
    private static string ConfidentName(List<(ListRow Row, string Text, MatchResult Match)> picks, int index)
        => index >= 0 && index < picks.Count && picks[index].Match.IsMatch && !picks[index].Match.IsVariant
            ? picks[index].Match.Item!.Name : "";

    /// <summary>
    /// f 和「上一列是 above、下一列是 below」是不是清單上同一個位置。
    /// 兩邊都有資料時兩邊都要相同；只有一邊有資料（頁面邊緣、鄰居還不確定）時看那一邊；都沒有時不算。
    /// </summary>
    private static bool SamePosition(Found f, string above, string below)
    {
        bool aboveKnown = above != "" && f.Above != "";
        bool belowKnown = below != "" && f.Below != "";
        if (aboveKnown && belowKnown)
        {
            return f.Above == above && f.Below == below;
        }
        if (aboveKnown)
        {
            return f.Above == above;
        }
        return belowKnown && f.Below == below;
    }

    /// <summary>
    /// 清掉過時的「找不到」「不確定」「讀不出來」：這一頁確定讀到的列，和它們在同一個位置（見 SamePosition），
    /// 表示那一筆就是這一列之前讀錯（或讀不出來）的結果。手動處理過的不動。
    /// </summary>
    private void RemoveStale(string category, List<(ListRow Row, string Text, MatchResult Match)> picks)
    {
        if (_unmatched.Count == 0)
        {
            return;
        }
        for (int i = 0; i < picks.Count; i++)
        {
            if (!picks[i].Match.IsMatch)
            {
                continue;
            }
            string above = ConfidentName(picks, i - 1);
            string below = ConfidentName(picks, i + 1);
            if (above == "" && below == "")
            {
                continue;
            }
            List<Found> stale = [.. _unmatched.Values.Where(f => !f.Manual && f.Category == category && SamePosition(f, above, below))];
            foreach (Found f in stale)
            {
                DetachFound(f);
                Removed?.Invoke(f);
            }
        }
    }

    // 一列的名稱（顯示用）：對得到 Excel 就用 Excel 的寫法；同名的一組用遊戲上的寫法（拿掉結尾數字），否則用讀到的
    private static string NameOf((ListRow Row, string Text, MatchResult Match) pick)
        => pick.Match.Item is DiscoveryItem item
            ? (pick.Match.IsVariant ? NameMatcher.SortKey(item.Name) : item.Name)
            : pick.Text;

    /// <summary>
    /// 加入一筆「有字但讀不出來」的列。above、below 是顯示用的上下列名稱，confidentAbove、confidentBelow 是已確定的。
    /// 同一個位置已經有了（捲到頁面邊緣時只剩一邊的鄰居，也算同一個位置）就回傳 false。
    /// </summary>
    private bool AddUnreadable(string category, string above, string below, string confidentAbove, string confidentBelow)
    {
        // 索引鍵用已確定的鄰居（同名組的列不算，免得夾在同名列旁邊的兩筆撞在一起）；兩邊都沒有時才用顯示用的名稱
        string key = confidentAbove != "" || confidentBelow != ""
            ? category + "\n?" + confidentAbove + "|" + confidentBelow
            : category + "\n?~" + above + "|" + below;
        if (_unmatched.ContainsKey(key) || _resolvedKeys.Contains(key)
            || _unmatched.Values.Any(f => f.Category == category && SamePosition(f, confidentAbove, confidentBelow)))
        {
            return false;
        }
        string note = (above, below) switch
        {
            ("", "") => "位置不明",
            ("", _) => $"在「{below}」上面",
            (_, "") => $"在「{above}」下面",
            _ => $"在「{above}」和「{below}」之間",
        };
        Found found = new()
        {
            Category = category, ScreenText = "（讀不出來）", Match = MatchResult.None(0),
            Unreadable = true, Note = note, UnmatchedKey = key, Above = confidentAbove, Below = confidentBelow,
            PageByValue = _pageSort == SortMode.ByValue,
        };
        _unmatched[key] = found;
        Added?.Invoke(found);
        return true;
    }

    // 同一列用不同倍率讀到好幾種結果時，挑對得上而且最像的那個
    private (string Text, MatchResult Match) PickBestReading(string category, ListRow row)
    {
        string bestText = row.Text;
        MatchResult? best = null;
        foreach (string text in row.Readings)
        {
            MatchResult m = matcher.Match(category, text, row.TextWidth);
            if (best is null || (m.IsMatch && !best.IsMatch) || (m.IsMatch == best.IsMatch && m.Score > best.Score))
            {
                best = m;
                bestText = text;
            }
        }
        return (bestText, best ?? MatchResult.None(0));
    }

    /// <summary>
    /// 用同一頁的其他列，推斷「不確定」「找不到」的列（例如漏讀了「鰭」，「帶背的飛龍骨架」跟「帶背刺」「帶背鰭」一樣像）：
    ///   1. 同一頁的每一列都是不同的發現物：別列已經確定的，不會是這一列（同名的一組除外）
    ///   2. 清單照名稱的字元編碼排序時（預設的排序；按 ⇅ 換過排序就不是），這一列一定排在上下已確定的兩列之間
    /// 排除之後只剩一個、或第一名明顯領先，就當成確定。新確定的列可以再幫忙推斷別列，所以最多做 3 輪。
    /// 注意：只排除「這一頁」已確定的，不排除前幾頁對到的（_matched）。捲動時上下頁大部分重疊，
    /// 這一列很可能就是前幾頁已經對到的那一個、只是這次讀錯了；排除它會被推成另一個沒發現的發現物並預設打勾。
    /// </summary>
    private void ResolveWithinPage(string category, List<(ListRow Row, string Text, MatchResult Match)> picks, bool sortedByName)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            HashSet<DiscoveryItem> claimed = [.. picks
                .Where(p => p.Match.IsMatch && !p.Match.IsVariant)
                .Select(p => p.Match.Item!)];
            List<(int Index, string Name)> anchors = [.. picks
                .Where(p => p.Match.IsMatch)
                .Select(p => (p.Row.Index, Name: NameMatcher.SortKey(p.Match.Item!.Name)))]; // 比較順序用拿掉結尾數字的名稱
            // 這一頁照名稱排序（DetectSort 判斷的），而且新確定的列也沒有違反，才用上下列的順序排除候選
            bool sorted = sortedByName
                          && anchors.Zip(anchors.Skip(1)).All(z => string.CompareOrdinal(z.First.Name, z.Second.Name) <= 0);

            bool changed = false;
            for (int i = 0; i < picks.Count; i++)
            {
                (ListRow row, string text, MatchResult match) = picks[i];
                if (match.IsMatch)
                {
                    continue;
                }
                string? lower = sorted ? anchors.Where(a => a.Index < row.Index).Select(a => a.Name).LastOrDefault() : null;
                string? upper = sorted ? anchors.Where(a => a.Index > row.Index).Select(a => a.Name).FirstOrDefault() : null;

                // 整列讀不出來（多半是一、兩個字的短名稱）：用字數、排序的上下界、這一頁已確定的排除候選，只剩一個就是它。
                // 例如昆蟲類一個字的只有「蠶」；量不到字寬時，要照名稱排序而且上下都有確定的列才推斷
                if (row.Readings.Count == 0)
                {
                    if (row.TextWidth > 0 || (lower is not null && upper is not null))
                    {
                        List<IReadOnlyList<DiscoveryItem>> between = [.. matcher.Between(category, lower, upper)
                            .Where(g => !(g.Count == 1 && claimed.Contains(g[0])))
                            .Where(g => NameMatcher.FitsWidth(g, row.TextWidth))];
                        if (between.Count == 1)
                        {
                            IReadOnlyList<DiscoveryItem> only = between[0];
                            // 相似度 0 表示是推斷出來的（畫面上顯示「推斷」）
                            picks[i] = (row, "", new MatchResult(only[0], 0, false, only.Count > 1 ? only : []));
                            changed = true;
                        }
                    }
                    continue;
                }

                // 每種倍率讀到的結果都列出候選，同一個候選取最高分
                // 以組裡第一個發現物當索引鍵（不靠清單物件本身的參考相等）
                Dictionary<DiscoveryItem, (IReadOnlyList<DiscoveryItem> Items, double Score, string Text)> merged = [];
                foreach (string reading in row.Readings)
                {
                    foreach ((IReadOnlyList<DiscoveryItem> items, double score) in matcher.Candidates(category, reading, row.TextWidth))
                    {
                        if (!merged.TryGetValue(items[0], out (IReadOnlyList<DiscoveryItem> Items, double Score, string Text) old)
                            || score > old.Score)
                        {
                            merged[items[0]] = (items, score, reading);
                        }
                    }
                }
                List<(IReadOnlyList<DiscoveryItem> Items, double Score, string Text)> candidates = [.. merged.Values
                    .Where(c => !(c.Items.Count == 1 && claimed.Contains(c.Items[0])))
                    .Where(c => lower is null || string.CompareOrdinal(NameMatcher.SortKey(c.Items[0].Name), lower) >= 0)
                    .Where(c => upper is null || string.CompareOrdinal(NameMatcher.SortKey(c.Items[0].Name), upper) <= 0)
                    .OrderByDescending(c => c.Score)];
                if (candidates.Count == 0 || (candidates.Count > 1 && candidates[0].Score - candidates[1].Score < NameMatcher.MinLead))
                {
                    continue;
                }
                // 讀到的字被剩下的好幾個名稱完整包含（「鳳蝶」之於「黑鳳蝶」「玫瑰鳳蝶」）：漏的是哪幾個字分不出來，不推斷
                string read = candidates[0].Text;
                if (candidates.Count > 1 && NameMatcher.IsContainedIn(read, candidates[0].Items[0].Name)
                    && candidates.Skip(1).Any(c => NameMatcher.IsContainedIn(read, c.Items[0].Name)))
                {
                    continue;
                }
                (IReadOnlyList<DiscoveryItem> best, double bestScore, string bestText) = candidates[0];
                picks[i] = (row, bestText, new MatchResult(best[0], bestScore, false, best.Count > 1 ? best : []));
                changed = true;
            }
            if (!changed)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 找出同名組裡這一列的身分（上下鄰居是 above、below）：和之前看過的某一個是同一列就回傳它（順便補上新看到的那一邊），
    /// 都不是就記成新的一個。above、below 都是已確定的名稱（ConfidentName），兩邊都沒有時無法辨識，回傳 null。
    /// 比對規則和 SamePosition 一樣：兩邊都知道時兩邊都要相同，只共用一個鄰居的兩列不會被當成同一列。
    /// </summary>
    private Occurrence? RegisterOccurrence(IReadOnlyList<DiscoveryItem> variants, string above, string below)
    {
        if (above == "" && below == "")
        {
            return null;
        }
        if (!_variantOccurrences.TryGetValue(variants[0], out List<Occurrence>? list))
        {
            list = [];
            _variantOccurrences[variants[0]] = list;
        }
        Occurrence? hit = list.FirstOrDefault(o => o.Matches(above, below));
        if (hit is null)
        {
            hit = new Occurrence { Above = above, Below = below };
            list.Add(hit);
            return hit;
        }
        // 補上之前不知道的那一邊
        if (hit.Above == "")
        {
            hit.Above = above;
        }
        if (hit.Below == "")
        {
            hit.Below = below;
        }
        return hit;
    }

    // 已確定（不是同名的一組）的列：給判斷排序、功績範圍用。
    // 要求功績 > 0：功績是 0 表示 Excel 那一欄是空的或讀不出數字，不能拿來比大小
    // （目前的 Excel 3,931 個發現物功績都大於 0；新版 Excel 若出現合法的 0 功績要回來看這裡）
    private static bool IsAnchor((ListRow Row, string Text, MatchResult Match) p)
        => p.Match.IsMatch && !p.Match.IsVariant && p.Match.Item is { Merit: > 0 };

    // 記住的排序方式：遊戲的排序是一個設定（按 ⇅ 才會換），有一頁證據充分就記住，之後證據不足的頁面沿用
    private SortMode _sortMode = SortMode.Unknown;
    // 最近一頁判斷出的排序（狀態列顯示；讀不出來的列記下來，手動指定時才知道能不能用功績範圍）
    private SortMode _pageSort = SortMode.Unknown;

    /// <summary>
    /// 判斷這一頁的排序（依價值：功績由高到低；依名稱：字元編碼由小到大）。
    ///   - 已確定的列至少 4 個、而且只符合其中一種：證據充分，記住這個排序方式
    ///   - 至少 2 個、只符合其中一種：這一頁就是這種排序
    ///   - 證據不足（例如發現物少、這一頁只有一兩個已確定的列，兩種都符合）：沒有矛盾就沿用記住的排序方式
    /// 兩種都符合時不下判斷，免得誤判（列數少時常常兩種都符合）。
    /// 用剛開始玩的玩家模擬過（只有 1%～3% 的發現物）：判斷不出來的時候有，但沒有判斷錯的。
    /// </summary>
    private SortMode DetectSort(List<(ListRow Row, string Text, MatchResult Match)> picks, bool remember)
    {
        // 兩種排序用同一組已確定的列（不含同名組：同名組的列都先對到組裡第一個，功績、名稱都不代表那一列）
        List<(ListRow Row, string Text, MatchResult Match)> anchors = [.. picks.Where(IsAnchor)];
        List<int> merits = [.. anchors.Select(p => p.Match.Item!.Merit)];
        List<string> names = [.. anchors.Select(p => NameMatcher.SortKey(p.Match.Item!.Name))];
        bool valueOrdered = merits.Zip(merits.Skip(1)).All(z => z.First >= z.Second);
        bool nameOrdered = names.Zip(names.Skip(1)).All(z => string.CompareOrdinal(z.First, z.Second) <= 0);

        bool value4 = valueOrdered && merits.Count >= 4, name4 = nameOrdered && names.Count >= 4;
        if (remember && value4 != name4)
        {
            _sortMode = value4 ? SortMode.ByValue : SortMode.ByName;
        }

        bool value2 = valueOrdered && merits.Count >= 2, name2 = nameOrdered && names.Count >= 2;
        if (value2 != name2)
        {
            SortMode page = value2 ? SortMode.ByValue : SortMode.ByName;
            // 這一頁只符合另一種排序：可能在遊戲裡按 ⇅ 換了排序，記住的不可靠了，清掉
            if (remember && _sortMode != SortMode.Unknown && _sortMode != page)
            {
                _sortMode = SortMode.Unknown;
            }
            return page;
        }
        // 證據不足：這一頁沒有違反記住的排序就沿用
        return _sortMode switch
        {
            SortMode.ByValue when valueOrdered => SortMode.ByValue,
            SortMode.ByName when nameOrdered => SortMode.ByName,
            _ => SortMode.Unknown,
        };
    }

    /// <summary>
    /// 依價值排列時，同名的列是組裡的哪一個：功績要介於上一個、下一個已確定的列之間。
    /// 只有一個符合時回傳它；判斷不出來（上下都沒有已確定的列、或符合的不只一個）回傳 null。
    /// </summary>
    private static DiscoveryItem? PickVariantByMerit(List<(ListRow Row, string Text, MatchResult Match)> picks, int index,
        IReadOnlyList<DiscoveryItem> variants)
    {
        int? above = null, below = null;
        for (int j = index - 1; j >= 0 && above is null; j--)
        {
            if (IsAnchor(picks[j]))
            {
                above = picks[j].Match.Item!.Merit;
            }
        }
        for (int j = index + 1; j < picks.Count && below is null; j++)
        {
            if (IsAnchor(picks[j]))
            {
                below = picks[j].Match.Item!.Merit;
            }
        }
        if (above is null && below is null)
        {
            return null;
        }
        // 由高到低：上一列的功績 ≥ 這一列 ≥ 下一列（只知道一邊時，另一邊不限）
        int high = above ?? int.MaxValue;
        int low = below ?? int.MinValue;
        List<DiscoveryItem> fit = [.. variants.Where(v => v.Merit <= high && v.Merit >= low)];
        return fit.Count == 1 ? fit[0] : null;
    }

    /// <summary>加入一筆已經確定是同名組裡哪一個的列（依功績判斷的）。已經有了就回傳 false。</summary>
    private bool AddVariantExact(string category, string text, bool hasCard, MatchResult match, DiscoveryItem item,
        Occurrence? occurrence)
    {
        // 這一列（同一個身分）已經在清單上、而且確定了（手動指定、或看齊了）：尊重它，什麼都不動。
        // 要放在最前面：否則下面會先把佔住這個發現物的那一筆挪走，然後才發現不用加
        if (occurrence is not null
            && _matched.Values.FirstOrDefault(f => f.Occurrence == occurrence && (f.Manual || !f.VariantUnsure)) is Found confirmed)
        {
            // 這次才看到卡片的話補上卡片標記（手動指定的不動；不影響勾選）
            if (hasCard && !confirmed.HasCard && !confirmed.Manual)
            {
                confirmed.HasCard = true;
                Updated?.Invoke(confirmed, false);
            }
            return false;
        }
        if (_matched.TryGetValue(item, out Found? existing))
        {
            // 這個發現物已經在清單上。同名組的列一開始是依序填空位，那一筆不一定就是這一列，要先看身分：
            if (occurrence is not null && existing.Occurrence == occurrence)
            {
                // 就是這一列：確定下來
                if (!existing.Manual && (existing.VariantUnsure || (hasCard && !existing.HasCard)))
                {
                    existing.VariantUnsure = false;
                    existing.HasCard |= hasCard;
                    Updated?.Invoke(existing, true);
                }
                return false;
            }
            if (existing.Manual || !existing.VariantUnsure)
            {
                return false; // 手動指定的、已經確定的：不動
            }
            // 那一筆是別的列（或還沒有身分），只是先佔了這個位置：把它移到組裡還空著的位置（仍然「請確認」），
            // 這個發現物留給這一列。組裡沒有空位時那一筆不動（這一列就不另外加）
            DiscoveryItem? free = match.Variants.FirstOrDefault(v => !_matched.ContainsKey(v));
            if (free is null)
            {
                return false;
            }
            DetachFound(existing);
            existing.Assigned = free;
            _matched[free] = existing;
            Updated?.Invoke(existing, true);
            // 往下走：為這一列新增一筆確定的
        }
        // 同一個身分之前用「同名，請確認」先記下的那一筆：就是這一列，改成功績判斷出來的那一個。
        // 只改同一個身分的；沒有身分的那一筆可能是另一列，不能拿來改（改錯會把另一隻變成已確定並預設寫入）
        Found? unsure = occurrence is null ? null
            : _matched.Values.FirstOrDefault(f => f.VariantUnsure && !f.Manual && f.Occurrence == occurrence);
        if (unsure is not null)
        {
            DetachFound(unsure);
            unsure.Assigned = item;
            unsure.VariantUnsure = false;
            unsure.HasCard |= hasCard;
            unsure.Occurrence ??= occurrence;
            _matched[item] = unsure;
            Updated?.Invoke(unsure, true);
            return false;
        }

        Found found = new()
        {
            Category = category, ScreenText = text, Match = match, HasCard = hasCard, Assigned = item, Occurrence = occurrence,
        };
        _matched[item] = found;
        Added?.Invoke(found);
        return true;
    }

    /// <summary>
    /// 同名的一組（例如「聖母加冠1」「聖母加冠2」，遊戲上都叫「聖母加冠」）——照名稱排列的頁面用：
    /// 照名稱排列時同名的排在一起，同一頁看到幾個，就是組裡前幾個都發現了。全部看到才確定；只看到一部分時分不出是哪一個，標成「同名，請確認」。
    /// 回傳新加入幾筆。
    /// </summary>
    private int AddVariants(string category, MatchResult match, List<VariantRow> rows)
    {
        IReadOnlyList<DiscoveryItem> variants = match.Variants;
        DiscoveryItem first = variants[0];
        // 同一頁同時看到幾個（照名稱排列時同名的排在一起）：同時看到全部才算確定。
        // 刻意不用「清單上這一組已經湊滿」當作確定：清單上的筆數有一部分是靠上下鄰居辨識的身分加的，
        // 鄰居認錯時同一隻會被算成兩隻；湊滿就確定的話，多算一隻會讓整組（包括沒發現的那一個）都預設打勾。
        // 實際上很少一直卡在「請確認」：依名稱排列時同名的連在一起，正常捲動一定有一頁同時看到全部；
        // 依價值排列時同名的分散在各頁，但每一隻會由 AddVariantExact 用上下鄰居的功績各自確定。
        _variantSeen.TryGetValue(first, out int before);
        int together = Math.Max(before, Math.Min(rows.Count, variants.Count));
        _variantSeen[first] = together;
        bool complete = together >= variants.Count;
        _variantSuppressed.TryGetValue(first, out int suppressed);
        _anonymousRemoved.TryGetValue(first, out int anonymousRemoved);

        // 每一個實際看到的列對應清單上的一筆，不會因為鄰居數出幾個就一次補滿：
        //   - 有身分（上下鄰居）的列：同一個身分已經有一筆就不再加，新的身分就加一筆
        //   - 沒有身分的列：沒有身分的筆數不超過「同一頁同時看到幾個」
        int added = 0;
        foreach (VariantRow row in rows)
        {
            List<Found> members = [.. variants.Where(_matched.ContainsKey).Select(v => _matched[v])];
            int present = members.Count + suppressed;

            if (row.Occurrence is not null)
            {
                // 同一個身分已經在清單上（可能被手動改成組外的發現物，所以整個清單找）
                Found? linked = _matched.Values.FirstOrDefault(f => f.Occurrence == row.Occurrence);
                if (linked is not null)
                {
                    if (row.HasCard && !linked.HasCard && !linked.Manual)
                    {
                        linked.HasCard = true;
                        Updated?.Invoke(linked, false);
                    }
                    continue;
                }
                // 新的身分：加一筆新的「同名，請確認」。不要寫進之前沒有身分的那一筆——那可能是另一列，
                // 寫進去會讓這一列靜靜漏掉。同一列先沒鄰居、後來有鄰居，最多暫時變成兩筆「請確認」（少算一隻比較嚴重）
                if (present >= variants.Count)
                {
                    continue;
                }
            }
            else if (members.Count(f => f.Occurrence is null) + anonymousRemoved >= together)
            {
                continue; // 沒有鄰居的列：沒有身分的筆數（含移除的）已經和這一頁同時看到的一樣多了
            }

            DiscoveryItem? item = variants.FirstOrDefault(v => !_matched.ContainsKey(v));
            if (item is null)
            {
                continue;
            }
            Found found = new()
            {
                Category = category, ScreenText = row.Text, Match = match, HasCard = row.HasCard,
                Assigned = item, VariantUnsure = !complete, Occurrence = row.Occurrence,
            };
            _matched[item] = found;
            Added?.Invoke(found);
            added++;
        }

        // 這次才同時看齊全部的話，之前標成「同名，請確認」的改成確定（手動指定的本來就確定）
        if (complete)
        {
            foreach (DiscoveryItem item in variants)
            {
                if (_matched.TryGetValue(item, out Found? f) && f.VariantUnsure && !f.Manual)
                {
                    f.VariantUnsure = false;
                    Updated?.Invoke(f, true);
                }
            }
        }
        return added;
    }

    /// <summary>加入一筆讀到的結果。已經有了就回傳 false。</summary>
    private bool AddResult(string category, string screenText, bool hasCard, MatchResult match,
        string confidentAbove = "", string confidentBelow = "")
    {
        if (_resolvedKeys.Contains(TextKey(category, screenText)))
        {
            return false; // 這個文字手動處理過了
        }
        Found found;
        if (match.IsMatch)
        {
            if (_matched.TryGetValue(match.Item!, out Found? existing))
            {
                if (hasCard && !existing.HasCard)
                {
                    existing.HasCard = true;
                    Updated?.Invoke(existing, false);
                }
                return false;
            }
            found = new Found { Category = category, ScreenText = screenText, Match = match, HasCard = hasCard };
            _matched[match.Item!] = found;
        }
        else
        {
            string key = TextKey(category, screenText);
            // 同一個位置已經有一筆對不到的（同一列讀錯成不同的字）：不要重複
            if (_unmatched.ContainsKey(key)
                || _unmatched.Values.Any(f => f.Category == category && SamePosition(f, confidentAbove, confidentBelow)))
            {
                return false;
            }
            found = new Found
            {
                Category = category, ScreenText = screenText, Match = match, HasCard = hasCard, UnmatchedKey = key,
                Above = confidentAbove, Below = confidentBelow,
            };
            _unmatched[key] = found;
        }

        Added?.Invoke(found);
        return true;
    }

    private static string TextKey(string category, string screenText) => category + "\n" + NameMatcher.Normalize(screenText);

    /// <summary>
    /// 手動指定這一筆是 item。item 已經是清單上的另一筆時不改，回傳那一筆（由呼叫端問使用者要不要移除這一筆）。
    /// </summary>
    public Found? Assign(Found f, DiscoveryItem item)
    {
        if (_matched.TryGetValue(item, out Found? other) && other != f)
        {
            return other;
        }
        RememberResolved(f);
        // 原本屬於同名組的列：改指定到組外就從組的筆數扣掉，改回組內就扣回來（每一筆最多扣一次）
        SetVariantSuppressed(f, f.Match.IsVariant && !f.Match.Variants.Contains(item));
        DetachFound(f);
        f.Assigned = item;
        f.Category = item.Category;
        f.Manual = true;
        f.VariantUnsure = false;
        _matched[item] = f;
        Updated?.Invoke(f, true);
        return null;
    }

    /// <summary>
    /// 設定這一筆（原本屬於同名組）有沒有從組的筆數扣掉。狀態有變才改計數：
    /// 沒扣 → 扣：組的筆數 +1（沒有身分的話「沒有身分被移除」也 +1）；扣 → 沒扣：扣回來。
    /// 同一筆改指定到組外、再移除，只會扣一次；改回組內會恢復。
    /// </summary>
    private void SetVariantSuppressed(Found f, bool suppressed)
    {
        if (!f.Match.IsVariant || f.VariantSuppressed == suppressed)
        {
            return;
        }
        DiscoveryItem first = f.Match.Variants[0];
        int delta = suppressed ? 1 : -1;
        if (suppressed)
        {
            f.AnonymousCounted = f.Occurrence is null;
        }
        _variantSuppressed[first] = Math.Max(0, _variantSuppressed.GetValueOrDefault(first) + delta);
        if (f.AnonymousCounted)
        {
            _anonymousRemoved[first] = Math.Max(0, _anonymousRemoved.GetValueOrDefault(first) + delta);
        }
        if (!suppressed)
        {
            f.AnonymousCounted = false;
        }
        f.VariantSuppressed = suppressed;
    }

    /// <summary>移除一筆（使用者按「移除這一筆」）：之後再讀到同樣的文字、同一個同名列，不要加回來。</summary>
    public void Remove(Found f)
    {
        if (f.Match.IsVariant)
        {
            SetVariantSuppressed(f, true); // 移除同名的一列：下次讀到不要補回來（之前改指定到組外時已經扣過，不會重複扣）
            if (f.Occurrence is not null)
            {
                _removedOccurrences.Add(f.Occurrence);
            }
        }
        RememberResolved(f);
        DetachFound(f);
        Removed?.Invoke(f);
    }

    // 記住這一列手動處理過（同名的一組例外：組裡每一列的文字都一樣，記住的話其他列也讀不到了）
    private void RememberResolved(Found f)
    {
        if (f.Unreadable && f.UnmatchedKey is not null)
        {
            _resolvedKeys.Add(f.UnmatchedKey);
        }
        else if (!f.Match.IsVariant)
        {
            _resolvedKeys.Add(TextKey(f.Category, f.ScreenText));
        }
    }

    // 從「對到」「對不到」兩邊的紀錄拿掉（清單上的那一列不動）
    private void DetachFound(Found f)
    {
        if (f.UnmatchedKey is not null)
        {
            _unmatched.Remove(f.UnmatchedKey);
            f.UnmatchedKey = null;
        }
        foreach (DiscoveryItem key in _matched.Where(kv => kv.Value == f).Select(kv => kv.Key).ToList())
        {
            _matched.Remove(key);
        }
    }
}
