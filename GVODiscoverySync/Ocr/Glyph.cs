// Glyph.cs
// 清單上一個字的字形：一格（16 遊戲像素寬 × 一列高）裡哪些點是字的筆畫。
// 遊戲的字是點陣字、沒有平滑處理：同一個字每次出現的點完全一樣（實測），所以可以直接比對，不靠文字辨識。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace GVODiscoverySync.Ocr;

public sealed class Glyph
{
    /// <summary>一個字的寬度（遊戲像素）。</summary>
    public const int Width = 16;

    // 每一橫排一個數字，由左到右是高位元到低位元
    private readonly ushort[] _rows;

    public Glyph(ushort[] rows)
    {
        _rows = rows;
    }

    /// <summary>一格裡完全沒有筆畫。</summary>
    public bool IsEmpty => Array.TrueForAll(_rows, r => r == 0);

    /// <summary>和另一個字形有幾個點不一樣（高度不同時當成完全不同）。</summary>
    public int Distance(Glyph other)
    {
        if (other._rows.Length != _rows.Length)
        {
            return int.MaxValue;
        }
        int distance = 0;
        for (int i = 0; i < _rows.Length; i++)
        {
            distance += BitOperations.PopCount((uint)(_rows[i] ^ other._rows[i]));
        }
        return distance;
    }

    /// <summary>
    /// 是不是同一個字。實測同一個字的點完全一樣、不同的字至少差二十幾個點；
    /// 容許差 1 個點，免得背景剛好有一個很亮的點就對不上。
    /// </summary>
    public bool Matches(Glyph other) => Distance(other) <= 1;

    /// <summary>存檔用的文字（每一橫排 4 個十六進位數字）。</summary>
    public override string ToString() => string.Concat(Array.ConvertAll(_rows, r => r.ToString("x4", CultureInfo.InvariantCulture)));

    /// <summary>從存檔用的文字讀回來；格式不對時回傳 null。</summary>
    public static Glyph? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length % 4 != 0)
        {
            return null;
        }
        ushort[] rows = new ushort[text.Length / 4];
        for (int i = 0; i < rows.Length; i++)
        {
            if (!ushort.TryParse(text.AsSpan(i * 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rows[i]))
            {
                return null;
            }
        }
        return new Glyph(rows);
    }
}
