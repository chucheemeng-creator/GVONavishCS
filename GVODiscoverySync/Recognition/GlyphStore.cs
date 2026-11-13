// GlyphStore.cs
// 記住每個字在遊戲清單上的字形，用來比對文字辨識讀不出來、讀錯的列（不靠文字辨識，見 Ocr/Glyph.cs）。
//
// 字形從「文字辨識讀到的字和 Excel 的名稱完全一樣」的列學來：那一列的每一格就是名稱裡每個字的字形。
// 學到的字形存在程式旁邊的 GVODiscoverySync.Glyphs.json，下次執行繼續用，所以越用認得的字越多。
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
    /// <summary>不能比（字數不同、名稱裡有寬度不確定的字、認得的字一個都沒有）。</summary>
    Unknown,

    /// <summary>有認得的字對不上：這一列不是這個名稱。</summary>
    Mismatch,

    /// <summary>認得的字都對得上，但還有不認得的字。</summary>
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
    /// 從一列學字形：name 是這一列確定的名稱（文字辨識讀到的和 Excel 完全一樣）。
    /// 已經認得、但這次不一樣的字換成這次的（例如版面位置調整過，舊的字形都不能用了）。
    /// </summary>
    public void Learn(string name, IReadOnlyList<Glyph>? cells)
    {
        if (!Comparable(name, cells))
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
            if (!_glyphs.TryGetValue(display[i], out Glyph? known) || !known.Matches(cell))
            {
                _glyphs[display[i]] = cell;
                Dirty = true;
            }
        }
    }

    /// <summary>名稱 name 和這一列的字形（cells）對不對得上。</summary>
    public GlyphVerdict Check(string name, IReadOnlyList<Glyph>? cells)
    {
        if (!Comparable(name, cells))
        {
            return GlyphVerdict.Unknown;
        }
        string display = NameMatcher.SortKey(name).Trim();
        int known = 0;
        for (int i = 0; i < display.Length; i++)
        {
            if (!_glyphs.TryGetValue(display[i], out Glyph? glyph))
            {
                continue;
            }
            if (!glyph.Matches(cells![i]))
            {
                return GlyphVerdict.Mismatch;
            }
            known++;
        }
        return known == display.Length ? GlyphVerdict.Full
            : known > 0 ? GlyphVerdict.Partial
            : GlyphVerdict.Unknown;
    }

    /// <summary>從檔案讀取（檔案不存在、壞掉時從空的開始，重新學就好）。</summary>
    public static GlyphStore Load(string? path = null)
    {
        GlyphStore store = new();
        try
        {
            path ??= DefaultPath;
            if (File.Exists(path))
            {
                Dictionary<string, string>? saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), JsonOptions);
                foreach ((string key, string value) in saved ?? [])
                {
                    if (key.Length == 1 && Glyph.Parse(value) is Glyph glyph)
                    {
                        store._glyphs[key[0]] = glyph;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 讀不了：從空的開始
        }
        return store;
    }

    /// <summary>有學到新的字形才存檔。存不了（例如唯讀資料夾）就算了，這次執行照樣能用。</summary>
    public void SaveIfDirty(string? path = null)
    {
        if (!Dirty)
        {
            return;
        }
        try
        {
            Dictionary<string, string> saved = _glyphs.OrderBy(p => p.Key)
                .ToDictionary(p => p.Key.ToString(), p => p.Value.ToString());
            AtomicFile.WriteAllText(path ?? DefaultPath, JsonSerializer.Serialize(saved, JsonOptions));
            Dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 存不了：下次再試
        }
    }
}
