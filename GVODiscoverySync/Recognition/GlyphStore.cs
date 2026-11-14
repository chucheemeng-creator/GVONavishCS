// GlyphStore.cs
// 記住每個字在遊戲清單上的字形，用來比對文字辨識讀不出來、讀錯的列（不靠文字辨識，見 Ocr/Glyph.cs）。
//
// 字形從「文字辨識讀到的字和 Excel 的名稱完全一樣」的列學來：那一列的每一格就是名稱裡每個字的字形。
// 程式內建一份字形表（BuiltInGlyphs.json，包在 exe 裡），所以一開始就認得大部分的字。
// 這台電腦另外學到的字形存在程式旁邊的 GVODiscoverySync.Glyphs.json，下次執行繼續用，所以越用認得的字越多。
// 這個檔案是程式自己維護的，不用手動改；刪掉也沒關係，會重新學。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using GVO.Common.IO;
using GVODiscoverySync.Matching;
using GVODiscoverySync.Ocr;

namespace GVODiscoverySync.Recognition;

/// <summary>名稱和一列的字形比對的結果。</summary>
public enum GlyphVerdict
{
    /// <summary>不能比（字數不同、名稱裡有寬度不確定的字），或沒有矛盾但名稱裡的字一個都不認得。</summary>
    Unknown,

    /// <summary>有認得的字對不上：這一列不是這個名稱。</summary>
    Mismatch,

    /// <summary>認得的字都對得上、不認得的字那幾格也不是別的認得的字，但還有不認得的字。</summary>
    Partial,

    /// <summary>每個字都認得、都對得上：這一列就是這個名稱。</summary>
    Full,
}

public sealed class GlyphStore
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "GVODiscoverySync.Glyphs.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = true,
    };

    private readonly Dictionary<char, Glyph> _glyphs = [];

    // 反過來查：這個字形是哪個字（「·」「‧」「・」在遊戲上是同一個字形，所以可能不只一個）
    private readonly Dictionary<string, HashSet<char>> _chars = [];

    // 同一個字的另一種字形：Excel 和遊戲用的字不一樣時（Excel 寫「阿閦如來」，遊戲上是「阿閦如来」），
    // 使用者手動指定之後記下來（見 LearnPicked）。比對時主要的字形、另一種字形都算對得上
    private readonly Dictionary<char, List<Glyph>> _variants = [];

    // 這一格是不是這個字（主要的字形，或另一種字形）
    private bool IsChar(char c, Glyph cell)
        => (_glyphs.TryGetValue(c, out Glyph? glyph) && glyph.Matches(cell))
           || (_variants.TryGetValue(c, out List<Glyph>? others) && others.Exists(o => o.Matches(cell)));

    // 已經認得的字，這次看到的字形不一樣：先記著，下次看到的也是這個新的字形才換。
    // 只看到一次就換的話，背景剛好有幾個很亮的點（半透明視窗後面的東西）就會把正確的字形換成壞的
    private readonly Dictionary<char, Glyph> _pending = [];

    /// <summary>認得幾個字。</summary>
    public int Count => _glyphs.Count;

    /// <summary>學到新的字形、還沒存檔。</summary>
    public bool Dirty { get; private set; }

    /// <summary>
    /// 名稱 name（Excel 上的名稱）能不能和 cells 逐字比對：遊戲上的寫法（見 NameMatcher.SortKey）字數和格數一樣，
    /// 而且每個字都是一個中文字寬（英數字、括號的寬度不確定，不比）。
    /// </summary>
    public static bool Comparable(string name, IReadOnlyList<Glyph>? cells)
    {
        if (cells is null || cells.Count == 0)
        {
            return false;
        }
        string display = NameMatcher.SortKey(name).Trim();
        return display.Length == cells.Count && NameMatcher.IsFullWidthName(display);
    }

    /// <summary>
    /// 從一列學字形：name 是這一列的名稱，cells 是這一列每一格的字形。
    /// onlyNew＝false（文字辨識讀到的和 Excel 完全一樣的列）：已經認得、但字形不一樣的字，
    /// 連續兩次看到同一個新的字形才換（只看到一次的可能是背景的亮點）；舊的字形留作另一種字形。
    /// onlyNew＝true（使用者確認的、或模糊比對和字形都指向同一個名稱的列）：只學還不認得的字，
    /// 而且這一列不能和已經認得的字形有任何矛盾，免得學錯。
    /// </summary>
    public void Learn(string name, IReadOnlyList<Glyph>? cells, bool onlyNew = false)
    {
        if (!Comparable(name, cells) || (onlyNew && Check(name, cells) == GlyphVerdict.Mismatch))
        {
            return;
        }
        string display = NameMatcher.SortKey(name).Trim();
        for (int i = 0; i < display.Length; i++)
        {
            Glyph cell = cells![i];
            if (cell.IsEmpty)
            {
                continue;
            }
            char c = display[i];
            if (_glyphs.ContainsKey(c))
            {
                if (IsChar(c, cell))
                {
                    _pending.Remove(c); // 和認得的一樣：之前那一次不一樣的不算數
                    continue;
                }
                if (onlyNew)
                {
                    continue;
                }
                if (!_pending.TryGetValue(c, out Glyph? pending) || !pending.Matches(cell))
                {
                    _pending[c] = cell; // 第一次看到這個新的字形：先記著
                    continue;
                }
                _pending.Remove(c);
            }
            Set(c, cell);
            Dirty = true;
        }
    }

    // 換掉一個字的字形時，舊的不丟，留作那個字的「另一種字形」：
    // 遊戲上同一個字可能有兩種寫法（「彗」在有的名稱裡是另一種字形），只留一種的話兩種會輪流把對方換掉
    private void Set(char c, Glyph glyph)
    {
        _glyphs.TryGetValue(c, out Glyph? old);
        if (_variants.TryGetValue(c, out List<Glyph>? others))
        {
            others.RemoveAll(o => o.Matches(glyph)); // 新的主要字形原本是另一種字形：不用留兩份
        }
        _glyphs[c] = glyph;
        AddReverse(c, glyph);
        if (old is not null && !old.IsEmpty)
        {
            AddVariant(c, old); // 和新的一樣時 AddVariant 不會加
        }
    }

    private void AddReverse(char c, Glyph glyph)
    {
        string key = glyph.ToString();
        if (!_chars.TryGetValue(key, out HashSet<char>? chars))
        {
            chars = [];
            _chars[key] = chars;
        }
        chars.Add(c);
    }

    private void AddVariant(char c, Glyph glyph)
    {
        if (IsChar(c, glyph))
        {
            return;
        }
        if (!_variants.TryGetValue(c, out List<Glyph>? others))
        {
            others = [];
            _variants[c] = others;
        }
        others.Add(glyph);
        AddReverse(c, glyph);
    }

    /// <summary>
    /// 使用者手動指定了這一列是 name（親自選的，是最強的證據）：
    /// 學還不認得的字；認得、但字形不一樣的字，把這一格記成那個字的「另一種字形」——
    /// Excel 和遊戲用的字不一樣時（Excel 寫「阿閦如來」、遊戲上是「阿閦如来」），之後才認得出來。
    /// 為了避免指定錯了把字形表弄亂：有對不上的字時，至少要有一個字對得上，而且對不上的字最多一個（名稱很長時最多四分之一）。
    /// 完全沒有對不上的字時（例如整列都不認得的「蠶」）直接學。
    /// </summary>
    public void LearnPicked(string name, IReadOnlyList<Glyph>? cells)
    {
        if (!Comparable(name, cells))
        {
            return;
        }
        string display = NameMatcher.SortKey(name).Trim();
        int matched = 0;
        List<int> different = [], unknown = [];
        for (int i = 0; i < display.Length; i++)
        {
            Glyph cell = cells![i];
            if (cell.IsEmpty)
            {
                continue; // 空的、或放大顯示時認不出來的格子：不學
            }
            if (!_glyphs.ContainsKey(display[i]))
            {
                unknown.Add(i);
            }
            else if (IsChar(display[i], cell))
            {
                matched++;
            }
            else
            {
                different.Add(i);
            }
        }
        // 不認得的字、但那一格是另一個認得的字：也算對不上（照樣學，但要受下面的限制）
        int conflicts = different.Count + unknown.Count(i => _chars.ContainsKey(cells![i].ToString()));
        if (conflicts > 0 && (matched == 0 || conflicts > Math.Max(1, display.Length / 4)))
        {
            return;
        }
        foreach (int i in unknown)
        {
            Set(display[i], cells![i]);
            Dirty = true;
        }
        foreach (int i in different)
        {
            AddVariant(display[i], cells![i]);
            Dirty = true;
        }
    }

    /// <summary>
    /// 名稱 name 和這一列的字形（cells）對不對得上。每一格：
    /// 名稱的這個字認得 → 字形要一樣；不認得 → 這一格不能是別的認得的字（不同的字字形一定不同）。
    /// 所以認得的格子等於已經「讀」出來了，名稱在那些位置的字一定要一樣。
    /// </summary>
    public GlyphVerdict Check(string name, IReadOnlyList<Glyph>? cells)
    {
        string display = NameMatcher.SortKey(name).Trim();
        if (!Comparable(name, cells))
        {
            // 不能一格一格比（字數不同，或名稱裡有英數字、括號，格子對不齊）：
            // 但這一列有哪一格是認得的字、名稱裡卻沒有這個字，就一定不是它
            if (cells is not null && cells.Any(c => _chars.TryGetValue(c.ToString(), out HashSet<char>? chars) && !chars.Any(display.Contains)))
            {
                return GlyphVerdict.Mismatch;
            }
            return GlyphVerdict.Unknown;
        }
        int known = 0;
        for (int i = 0; i < display.Length; i++)
        {
            if (ReferenceEquals(cells![i], Glyph.Unreadable))
            {
                continue; // 這一格認不出來（放大顯示時）：不算對得上，也不算矛盾
            }
            if (_glyphs.ContainsKey(display[i]))
            {
                if (!IsChar(display[i], cells![i]))
                {
                    return GlyphVerdict.Mismatch;
                }
                known++;
            }
            else if (_chars.ContainsKey(cells![i].ToString()))
            {
                return GlyphVerdict.Mismatch; // 這一格是另一個認得的字
            }
        }
        return known == display.Length ? GlyphVerdict.Full
            : known > 0 ? GlyphVerdict.Partial
            : GlyphVerdict.Unknown;
    }

    /// <summary>
    /// 放大顯示時用：raw 是從糊掉的畫面取樣到的字形，換成最像的已知字形。
    /// 要夠像（差 28 個點以內），而且明顯比第二像的字更像（至少多 5 個點）才算；否則回傳 Glyph.Unreadable。
    /// 用實際的 150% 截圖量過（172 個字）：正確的字最多差 23 個點，和第二像的至少差 7 個點。
    /// 不認得的字有可能被當成長得像的另一個字（例如沒學過「白」時當成「占」），所以放大顯示時的字形只用來認出整列。
    /// </summary>
    public Glyph Snap(Glyph raw)
    {
        Glyph? best = null;
        int bestDistance = int.MaxValue;
        foreach (Glyph glyph in _glyphs.Values.Concat(_variants.Values.SelectMany(v => v)))
        {
            int distance = glyph.Distance(raw);
            if (distance < bestDistance)
            {
                best = glyph;
                bestDistance = distance;
            }
        }
        if (best is null || bestDistance > 28)
        {
            return Glyph.Unreadable;
        }
        // 第二像的要是「別的字」：同一個字的另一種字形（可能只差幾個點）、同一個字形的「·」「‧」不算，
        // 否則有兩種相近字形的字會因為「不夠明顯」而認不出來
        HashSet<char> same = _chars.TryGetValue(best.ToString(), out HashSet<char>? chars) ? chars : [];
        int second = int.MaxValue;
        foreach ((char c, Glyph glyph) in _glyphs)
        {
            if (!same.Contains(c))
            {
                second = Math.Min(second, glyph.Distance(raw));
            }
        }
        foreach ((char c, List<Glyph> others) in _variants)
        {
            if (!same.Contains(c))
            {
                foreach (Glyph glyph in others)
                {
                    second = Math.Min(second, glyph.Distance(raw));
                }
            }
        }
        return second - (long)bestDistance >= 5 ? best : Glyph.Unreadable;
    }

    /// <summary>這一列每一格認出來的字（不認得的是「？」），除錯輸出用。</summary>
    public string Read(IReadOnlyList<Glyph>? cells)
        => cells is null ? "" : string.Concat(cells.Select(c => _chars.TryGetValue(c.ToString(), out HashSet<char>? chars) ? chars.Min() : '？'));

    /// <summary>
    /// 這一列的每個字都認得、而且每一格只可能是一個字時，讀出來的文字；否則是 null。
    /// 「字形優先」用：整頁每一列都讀得出來時不用做文字辨識。
    /// </summary>
    public string? ReadExact(IReadOnlyList<Glyph> cells)
    {
        if (cells.Count == 0)
        {
            return null;
        }
        char[] text = new char[cells.Count];
        for (int i = 0; i < cells.Count; i++)
        {
            if (cells[i].IsEmpty || !_chars.TryGetValue(cells[i].ToString(), out HashSet<char>? chars))
            {
                return null;
            }
            // 同一個字形對到不只一個字（例如手動指定時把這個字形記成另一個字的另一種字形）：分不出是哪一個，不讀。
            // 只有間隔號例外（「·」「‧」「・」在遊戲上是同一個字形，比對名稱時也不看標點）
            if (chars.Count > 1 && chars.Any(char.IsLetterOrDigit))
            {
                return null;
            }
            text[i] = chars.Min();
        }
        return new string(text);
    }

    // 內建的字形（包在 exe 裡的 BuiltInGlyphs.json）：存檔時和內建一樣的不用存
    private readonly Dictionary<char, string> _builtIn = [];

    /// <summary>
    /// 讀取字形表：先放內建的（includeBuiltIn；作者把所有分類掃過幾輪學到的，所以一開始就認得大部分的字），
    /// 再放檔案裡的（這台電腦自己學到的；和內建不同時檔案裡的當主要的字形，內建的留作另一種字形）。
    /// 檔案不存在、壞掉時只用內建的，重新學就好。
    /// </summary>
    public static GlyphStore Load(string? path = null, bool includeBuiltIn = true)
    {
        GlyphStore store = new();
        if (includeBuiltIn)
        {
            try
            {
                using Stream? stream = typeof(GlyphStore).Assembly.GetManifestResourceStream("BuiltInGlyphs.json");
                if (stream is not null)
                {
                    using StreamReader reader = new(stream);
                    store.Add(reader.ReadToEnd(), builtIn: true);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // 內建的讀不了：從空的開始
            }
        }
        try
        {
            path ??= DefaultPath;
            if (File.Exists(path))
            {
                store.Add(File.ReadAllText(path), builtIn: false);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 讀不了：只用內建的
        }
        return store;
    }

    // 檔案的格式：每個字一行，"字": "字形"；有另一種字形時用「|」接在後面（"來": "主要的字形|另一種字形"）
    private void Add(string json, bool builtIn)
    {
        Dictionary<string, string>? saved = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
        foreach ((string key, string value) in saved ?? [])
        {
            string[] parts = (value ?? "").Split('|');
            if (key.Length != 1 || Glyph.Parse(parts[0]) is not Glyph glyph)
            {
                continue;
            }
            Set(key[0], glyph);
            foreach (string part in parts.Skip(1))
            {
                if (Glyph.Parse(part) is Glyph other)
                {
                    AddVariant(key[0], other);
                }
            }
            if (builtIn)
            {
                _builtIn[key[0]] = Saved(key[0]);
            }
        }
    }

    // 這個字存檔的內容（主要的字形，加上另一種字形）
    private string Saved(char c)
        => string.Join('|', new[] { _glyphs[c] }.Concat(_variants.TryGetValue(c, out List<Glyph>? others) ? others : []));

    /// <summary>
    /// 有學到新的字形才存檔（只存內建沒有、或和內建不同的字）。存不了（例如唯讀資料夾）就算了，這次執行照樣能用。
    /// </summary>
    public void SaveIfDirty(string? path = null)
    {
        if (!Dirty)
        {
            return;
        }
        try
        {
            Dictionary<string, string> saved = _glyphs.OrderBy(p => p.Key)
                .Where(p => !_builtIn.TryGetValue(p.Key, out string? builtIn) || builtIn != Saved(p.Key))
                .ToDictionary(p => p.Key.ToString(), p => Saved(p.Key));
            AtomicFile.WriteAllText(path ?? DefaultPath, JsonSerializer.Serialize(saved, JsonOptions));
            Dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 存不了：下次再試
        }
    }
}
