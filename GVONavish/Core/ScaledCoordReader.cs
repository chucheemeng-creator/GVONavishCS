// ScaledCoordReader.cs
// 遊戲畫面被 Windows 放大時（例如顯示縮放 150%），讀座標用的辨識方法。
//
// 遊戲很舊，不懂 Windows 的顯示縮放：Windows 先讓它照 100% 畫，再把整個畫面平滑地拉大。
// 結果原本 1 像素寬、純白的數字筆畫，變成 1.5 像素左右、邊緣灰灰的。
// 原本的辨識（SurveyCoordExtractor）要求「接近純白的點」和字形完全一樣，放大後就讀不到了。
//
// 這裡的做法：
//   1. 把放大後的畫面「縮回」遊戲原本的 60 × 11 像素：每個原本的像素，取它在螢幕上涵蓋的那塊面積的平均亮度。
//   2. 亮度換成 0～1（背景＝0、最亮的字＝1），不先切成黑白，保留灰階。
//   3. 一個位置一個位置和 0～9 的字形比對：字形的筆畫要夠亮（≥ OnMin）、字形以外要夠暗（≤ OffMax），
//      兩個條件都符合才算這個數字；有好幾個符合時，取差異最小的。
//      逗號、雜訊、畫面其他東西都不會同時符合這兩個條件，所以會被略過（逗號正好成為兩個數值之間的空隙）。
//   4. 放大後的像素和螢幕像素不一定對齊（例如 273 × 1.5 = 409.5），所以在 ±0.5 個螢幕像素內
//      試幾個對齊位置，取讀到最多位數、差異最小的那一個。
//
// 這些門檻是用實際在 150% 筆電上截到的畫面量出來的：正確的數字，筆畫最暗也有 0.50、背景最亮只到 0.41；
// 截錯位置時被誤認的「數字」，筆畫中位數約 0、背景約 0.84，兩邊分得很開。

using System;
using System.Collections.Generic;

namespace GVONavish.Core;

public static class ScaledCoordReader
{
    private const int W = 60;                                  // 遊戲原本的截圖寬度
    private const int H = SurveyCoordExtractor.DigitHeight;     // 11
    private const int DigitWidth = SurveyCoordExtractor.DigitWidth;

    /// <summary>字形筆畫的每個點都要至少這麼亮。預覽圖也用這個值判斷「當成數字的點」，兩邊才一致。</summary>
    public const float OnMin = 0.40f;
    private const double OffMax = 0.60;      // 字形以外的每個點都不能超過這麼亮
    private const double ColumnMin = 0.35;   // 一欄最亮的點不到這個值，當成空白欄
    private const double MinContrast = 80;   // 最亮和背景差不到這麼多（0～255），表示框裡沒有字

    // ±0.5 個螢幕像素內，每 0.25 試一次
    private static readonly double[] AlignSteps = [-0.5, -0.25, 0, 0.25, 0.5];

    // 0～9 的字形：[數字][列, 欄]，true＝筆畫
    private static readonly bool[][,] Templates = BuildTemplates();

    private static bool[][,] BuildTemplates()
    {
        var t = new bool[10][,];
        for (int d = 0; d < 10; d++)
        {
            string[] cols = SurveyCoordExtractor.GetSample(d);
            t[d] = new bool[H, DigitWidth];
            for (int c = 0; c < DigitWidth; c++)
            {
                for (int y = 0; y < H; y++)
                {
                    t[d][y, c] = cols[c][y] == '1';
                }
            }
        }
        return t;
    }

    /// <summary>
    /// 從放大後的截圖讀座標。
    /// </summary>
    /// <param name="gray">截圖每個像素的亮度（0～255，一列接一列）。</param>
    /// <param name="width">截圖寬度（螢幕像素）。</param>
    /// <param name="height">截圖高度（螢幕像素）。</param>
    /// <param name="scale">放大倍率，例如 1.5。</param>
    /// <param name="originX">遊戲原本那塊 60 × 11 區域的左上角，在截圖裡的位置（螢幕像素，可以有小數）。</param>
    /// <param name="originY">同上，Y。</param>
    /// <param name="preview">
    /// 縮回 60 × 11 後的亮度（0～1），給設定視窗顯示用：讀到時是實際採用的那個對齊位置，
    /// 讀不到時是沒有偏移的位置。
    /// </param>
    public static bool TryRead(float[] gray, int width, int height, double scale, double originX, double originY,
                               out int x, out int y, out float[] preview)
    {
        x = 0;
        y = 0;
        preview = [];
        (int Digits, double MeanError, List<string> Numbers, float[] Image)? best = null;

        foreach (double dy in AlignSteps)
        {
            foreach (double dx in AlignSteps)
            {
                float[] v = Normalize(SampleArea(gray, width, height, scale, originX + dx, originY + dy));
                if (dx == 0 && dy == 0)
                {
                    preview = v;
                }
                if (v.Length == 0)
                {
                    continue; // 框裡沒有字
                }
                (List<string> numbers, double totalError) = ReadNumbers(v);
                if (numbers.Count != 2)
                {
                    continue;
                }
                int digits = numbers[0].Length + numbers[1].Length;
                double mean = totalError / digits;
                // 讀到越多位數越好（對不準時，邊緣的數字會讀不到）；位數一樣時，取差異小的
                if (best is null || digits > best.Value.Digits || (digits == best.Value.Digits && mean < best.Value.MeanError))
                {
                    best = (digits, mean, numbers, v);
                }
            }
        }

        if (preview.Length == 0)
        {
            preview = new float[W * H]; // 沒有字：預覽全黑
        }
        if (best is null)
        {
            return false;
        }
        preview = best.Value.Image; // 預覽顯示實際讀出座標的那個對齊位置
        // 數字太長（不可能的座標）時 TryParse 會失敗，當成讀不到
        return int.TryParse(best.Value.Numbers[0], out x) && int.TryParse(best.Value.Numbers[1], out y);
    }

    // 把放大後的畫面縮回 60 × 11：每個原本的像素，取它涵蓋的螢幕像素面積加權平均
    private static float[] SampleArea(float[] gray, int width, int height, double s, double ox, double oy)
    {
        var result = new float[W * H];
        for (int v = 0; v < H; v++)
        {
            double ya = oy + v * s, yb = oy + (v + 1) * s;
            for (int u = 0; u < W; u++)
            {
                double xa = ox + u * s, xb = ox + (u + 1) * s;
                double total = 0, weight = 0;
                for (int py = (int)Math.Floor(ya); py < (int)Math.Ceiling(yb); py++)
                {
                    if (py < 0 || py >= height) continue;
                    double wy = Math.Min(yb, py + 1) - Math.Max(ya, py);
                    for (int px = (int)Math.Floor(xa); px < (int)Math.Ceiling(xb); px++)
                    {
                        if (px < 0 || px >= width) continue;
                        double wx = Math.Min(xb, px + 1) - Math.Max(xa, px);
                        total += gray[py * width + px] * wx * wy;
                        weight += wx * wy;
                    }
                }
                result[v * W + u] = weight > 0 ? (float)(total / weight) : 0f;
            }
        }
        return result;
    }

    // 亮度換成 0～1：背景（中位數）＝0，最亮的字（第 99.5 百分位，避開零星的亮點）＝1。
    // 最亮和背景差太少，表示框裡沒有字，回傳空陣列。
    private static float[] Normalize(float[] g)
    {
        float[] sorted = (float[])g.Clone();
        Array.Sort(sorted);
        float background = sorted[sorted.Length / 2];
        float peak = sorted[(int)Math.Round(0.995 * (sorted.Length - 1))];
        if (peak - background < MinContrast)
        {
            return [];
        }
        float range = peak - background;
        var v = new float[g.Length];
        for (int i = 0; i < g.Length; i++)
        {
            v[i] = Math.Clamp((g[i] - background) / range, 0f, 1f);
        }
        return v;
    }

    // 由左到右比對字形。數字之間空超過 NumberGapThreshold 欄，就是下一個數值（和原本的辨識規則相同）。
    private static (List<string> Numbers, double TotalError) ReadNumbers(float[] v)
    {
        List<string> numbers = [];
        string current = "";
        int lastDigitEnd = 0;
        double totalError = 0;
        int col = 0;
        while (col <= W - DigitWidth)
        {
            if (ColumnMax(v, col) < ColumnMin)
            {
                col++;
                continue;
            }
            int digit = -1;
            double digitError = double.MaxValue;
            for (int d = 0; d < 10; d++)
            {
                if (Matches(v, col, d, out double error) && error < digitError)
                {
                    digit = d;
                    digitError = error;
                }
            }
            if (digit < 0)
            {
                col++;
                continue;
            }
            if (col - lastDigitEnd > SurveyCoordExtractor.NumberGapThreshold && current.Length > 0)
            {
                numbers.Add(current);
                current = "";
            }
            current += (char)('0' + digit);
            totalError += digitError;
            col += DigitWidth;
            lastDigitEnd = col;
        }
        if (current.Length > 0)
        {
            numbers.Add(current);
        }
        return (numbers, totalError);
    }

    private static float ColumnMax(float[] v, int col)
    {
        float max = 0;
        for (int y = 0; y < H; y++)
        {
            max = Math.Max(max, v[y * W + col]);
        }
        return max;
    }

    // 筆畫夠亮、筆畫以外夠暗，才算符合；error 是和字形的平均差異
    private static bool Matches(float[] v, int col, int d, out double error)
    {
        bool[,] t = Templates[d];
        double sum = 0;
        error = 0;
        for (int c = 0; c < DigitWidth; c++)
        {
            for (int y = 0; y < H; y++)
            {
                float value = v[y * W + col + c];
                if (t[y, c])
                {
                    if (value < OnMin) return false;
                    sum += 1 - value;
                }
                else
                {
                    if (value > OffMax) return false;
                    sum += value;
                }
            }
        }
        error = sum / (DigitWidth * H);
        return true;
    }
}
