// SurveyCoordExtractor.cs
// 從遊戲畫面截下來的「測量座標」小圖裡，辨識出 X、Y 兩個數字。
// 移植自 GVONavish 的 GVOSurveyCoordExtractor.cpp（C++）。
//
// 原理：
//   1. 截圖區域固定高 11 像素。把每個像素轉成黑白（R+G+B >= 720 視為白字）。
//   2. 每個數字寬 5 像素、高 11 像素。由左往右逐欄掃描，
//      看「從這一欄開始的 5 欄」是否和 0~9 的點陣樣本完全一致。
//   3. 數字之間的空白如果超過 4 像素，就當作換到下一個數字（X 和 Y 的分隔）。
//
// 和 C++ 原版的差異（都是改善，不影響正常情況的結果）：
//   - 原版遇到一個比對失敗的位置就整個停止掃描；這裡會繼續往右找，比較耐雜訊。
//   - 原版判斷「整欄全白」用的是 0x3FF（只有 10 位元，是個小 bug），這裡用正確的 11 位元。
//   - 原版沒處理每列尾端的對齊位元組（stride），這裡有處理，任何寬度都不會錯位。

using System;
using System.Collections.Generic;

namespace GVONavish.Core;

public static class SurveyCoordExtractor
{
    /// <summary>數字點陣的高度（像素）。截圖高度必須等於這個值。</summary>
    public const int DigitHeight = 11;

    /// <summary>每個數字的寬度（像素）。</summary>
    public const int DigitWidth = 5;

    /// <summary>數字之間空白超過這麼多像素，就視為下一個數值。</summary>
    public const int NumberGapThreshold = 4;

    /// <summary>R+G+B 總和達到這個值才算「白字」。</summary>
    public const int WhiteThreshold = 240 * 3;

    // 0~9 的點陣樣本，和 C++ 原版完全相同。
    // 每個數字 5 欄，每欄 11 個字元，由上到下；'1' = 白點。
    private static readonly string[][] SampleColumns =
    [
        ["00111111100", "01000000010", "01000000010", "00111111100", "00000000000"], // 0
        ["00100000000", "01111111110", "00000000000", "00000000000", "00000000000"], // 1
        ["00110000110", "01000011010", "01000100010", "00111000010", "00000000000"], // 2
        ["00110001100", "01000100010", "01000100010", "00111011100", "00000000000"], // 3
        ["00000011000", "00001101000", "00110001000", "01111111110", "00000001000"], // 4
        ["01111101100", "01001000010", "01001000010", "01000111100", "00000000000"], // 5
        ["00111111100", "01000100010", "01000100010", "00110011100", "00000000000"], // 6
        ["01000000000", "01000001110", "01001110000", "01110000000", "00000000000"], // 7
        ["00111011100", "01000100010", "01000100010", "00111011100", "00000000000"], // 8
        ["00111001100", "01000100010", "01000100010", "00111111100", "00000000000"], // 9
    ];

    // 把樣本轉成整數（每欄一個 11 位元的整數，最上面的點是最高位元），比對比較快。
    private static readonly int[][] SampleMasks = BuildSampleMasks();

    private const int FullColumn = (1 << DigitHeight) - 1; // 0x7FF：整欄全白

    private static int[][] BuildSampleMasks()
    {
        var masks = new int[SampleColumns.Length][];
        for (int d = 0; d < SampleColumns.Length; d++)
        {
            masks[d] = new int[DigitWidth];
            for (int c = 0; c < DigitWidth; c++)
            {
                masks[d][c] = Convert.ToInt32(SampleColumns[d][c], 2);
            }
        }
        return masks;
    }

    /// <summary>
    /// 從像素資料讀出座標。成功時回傳 true，並輸出 x、y。
    /// </summary>
    /// <param name="pixels">像素資料（BGR 或 BGRA，一列接一列）。</param>
    /// <param name="width">寬度（像素）。</param>
    /// <param name="height">高度（像素），必須是 11。</param>
    /// <param name="stride">每一列佔幾個位元組（含尾端對齊）。</param>
    /// <param name="bytesPerPixel">每個像素幾個位元組：3（24 位元）或 4（32 位元）。</param>
    public static bool TryReadCoord(ReadOnlySpan<byte> pixels, int width, int height, int stride,
                                    int bytesPerPixel, out int x, out int y)
    {
        x = 0;
        y = 0;
        List<int> values = ExtractNumbers(pixels, width, height, stride, bytesPerPixel);
        if (values.Count != 2)
        {
            return false;
        }
        x = values[0];
        y = values[1];
        return true;
    }

    /// <summary>
    /// 讀出圖中所有的數值（由左到右）。正常情況下應該剛好是 2 個：X 和 Y。
    /// </summary>
    public static List<int> ExtractNumbers(ReadOnlySpan<byte> pixels, int width, int height, int stride,
                                           int bytesPerPixel)
    {
        List<int> values = [];
        if (height != DigitHeight || width < DigitWidth)
        {
            return values; // 原版也只支援高度 11
        }

        int[] columns = ToColumnMasks(pixels, width, height, stride, bytesPerPixel);

        string number = "";
        int lastDigitEnd = 0; // 上一個數字結束後的下一欄
        int col = 0;

        while (col <= width - DigitWidth)
        {
            // 跳過全黑、全白的欄
            if (columns[col] == 0 || columns[col] == FullColumn)
            {
                col++;
                continue;
            }

            int digit = MatchDigitAt(columns, col);
            if (digit < 0)
            {
                col++;
                continue;
            }

            // 和上一個數字之間空太多 → 前一個數值結束
            if (col - lastDigitEnd > NumberGapThreshold && number.Length > 0)
            {
                AddNumber(values, number);
                number = "";
            }

            number += (char)('0' + digit);
            col += DigitWidth;
            lastDigitEnd = col;
        }

        if (number.Length > 0)
        {
            AddNumber(values, number);
        }
        return values;
    }

    // 數字太長（畫面被擋住時，雜訊可能湊出一長串「1」）會超過 int 的範圍，這種一定是讀錯了，直接略過
    private static void AddNumber(List<int> values, string number)
    {
        if (int.TryParse(number, out int value))
        {
            values.Add(value);
        }
    }

    /// <summary>
    /// 把圖轉成「每欄一個整數」：第 y 列是白點，對應的位元就是 1（最上面的列是最高位元）。
    /// </summary>
    public static int[] ToColumnMasks(ReadOnlySpan<byte> pixels, int width, int height, int stride,
                                      int bytesPerPixel)
    {
        var columns = new int[width];
        for (int px = 0; px < width; px++)
        {
            int mask = 0;
            for (int py = 0; py < height; py++)
            {
                mask = (mask << 1) | (IsWhite(pixels, px, py, stride, bytesPerPixel) ? 1 : 0);
            }
            columns[px] = mask;
        }
        return columns;
    }

    /// <summary>這個像素算不算白字。給畫面預覽用的黑白圖也會用到。</summary>
    public static bool IsWhite(ReadOnlySpan<byte> pixels, int px, int py, int stride, int bytesPerPixel)
    {
        int offset = py * stride + px * bytesPerPixel;
        int total = pixels[offset] + pixels[offset + 1] + pixels[offset + 2];
        return total >= WhiteThreshold;
    }

    private static int MatchDigitAt(int[] columns, int start)
    {
        for (int d = 0; d < SampleMasks.Length; d++)
        {
            int[] sample = SampleMasks[d];
            bool same = true;
            for (int c = 0; c < DigitWidth; c++)
            {
                if (columns[start + c] != sample[c])
                {
                    same = false;
                    break;
                }
            }
            if (same)
            {
                return d;
            }
        }
        return -1;
    }

    /// <summary>取得某個數字的點陣樣本（測試用：可以拿來畫出假的座標圖）。</summary>
    public static string[] GetSample(int digit) => SampleColumns[digit];
}
