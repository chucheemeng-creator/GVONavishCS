// SurveyCoordExtractorTests.cs
// 讀測量座標：用 GetSample 的字形畫出假的座標圖，檢查辨識結果、每列尾端的對齊位元組、雜訊。

using System;
using System.Collections.Generic;
using GVONavish.Core;
using Xunit;

namespace GVONavish.Tests;

public class SurveyCoordExtractorTests
{
    /// <summary>
    /// 畫座標圖（高 11）。text 裡的數字用字形畫，空白是 gap 欄的空隙。
    /// 回傳像素資料和寬度；padding 是每列尾端多出來的位元組（模擬 stride 對齊）。
    /// </summary>
    private static (byte[] Pixels, int Width, int Stride) Render(string text, int bytesPerPixel, int gap = 8, int padding = 0)
    {
        List<int> columns = [];
        foreach (char ch in text)
        {
            if (ch == ' ')
            {
                for (int i = 0; i < gap; i++) columns.Add(0);
                continue;
            }
            string[] sample = SurveyCoordExtractor.GetSample(ch - '0');
            for (int c = 0; c < SurveyCoordExtractor.DigitWidth; c++)
            {
                int mask = 0;
                for (int y = 0; y < SurveyCoordExtractor.DigitHeight; y++)
                {
                    mask = (mask << 1) | (sample[c][y] == '1' ? 1 : 0);
                }
                columns.Add(mask);
            }
        }

        int width = columns.Count;
        int stride = width * bytesPerPixel + padding;
        byte[] pixels = new byte[stride * SurveyCoordExtractor.DigitHeight];
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < SurveyCoordExtractor.DigitHeight; y++)
            {
                bool white = (columns[x] & (1 << (SurveyCoordExtractor.DigitHeight - 1 - y))) != 0;
                if (!white) continue;
                for (int b = 0; b < bytesPerPixel; b++) pixels[y * stride + x * bytesPerPixel + b] = 255;
            }
        }
        return (pixels, width, stride);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void 讀出兩個數字_24位元和32位元(int bytesPerPixel)
    {
        (byte[] pixels, int w, int stride) = Render("15785 3204", bytesPerPixel);
        Assert.True(SurveyCoordExtractor.TryReadCoord(pixels, w, 11, stride, bytesPerPixel, out int x, out int y));
        Assert.Equal(15785, x);
        Assert.Equal(3204, y);
    }

    [Fact]
    public void 十個數字每一個都認得出來()
    {
        for (int d = 0; d <= 9; d++)
        {
            (byte[] pixels, int w, int stride) = Render($"{d} {d}", 4);
            Assert.True(SurveyCoordExtractor.TryReadCoord(pixels, w, 11, stride, 4, out int x, out int y), $"數字 {d}");
            Assert.Equal(d, x);
            Assert.Equal(d, y);
        }
    }

    [Fact]
    public void 每列尾端有對齊位元組_不會錯位()
    {
        (byte[] pixels, int w, int stride) = Render("123 456", 3, padding: 3);
        Assert.True(SurveyCoordExtractor.TryReadCoord(pixels, w, 11, stride, 3, out int x, out int y));
        Assert.Equal(123, x);
        Assert.Equal(456, y);
    }

    [Fact]
    public void 高度不是11_讀不到()
    {
        (byte[] pixels, int w, int stride) = Render("1 2", 4);
        Assert.False(SurveyCoordExtractor.TryReadCoord(pixels, w, 10, stride, 4, out _, out _));
    }

    [Fact]
    public void 全黑_讀不到()
    {
        byte[] pixels = new byte[60 * 4 * 11];
        Assert.False(SurveyCoordExtractor.TryReadCoord(pixels, 60, 11, 60 * 4, 4, out _, out _));
    }

    [Fact]
    public void 全白_讀不到()
    {
        byte[] pixels = new byte[60 * 4 * 11];
        Array.Fill(pixels, (byte)255);
        Assert.False(SurveyCoordExtractor.TryReadCoord(pixels, 60, 11, 60 * 4, 4, out _, out _));
    }

    [Fact]
    public void 只有一個數值_讀不到()
    {
        (byte[] pixels, int w, int stride) = Render("1234", 4);
        Assert.False(SurveyCoordExtractor.TryReadCoord(pixels, w, 11, stride, 4, out _, out _));
        Assert.Single(SurveyCoordExtractor.ExtractNumbers(pixels, w, 11, stride, 4));
    }

    [Fact]
    public void 三個數值_讀不到()
    {
        (byte[] pixels, int w, int stride) = Render("1 2 3", 4);
        Assert.False(SurveyCoordExtractor.TryReadCoord(pixels, w, 11, stride, 4, out _, out _));
    }

    [Fact]
    public void 數字太長超過int範圍_略過那一個數值()
    {
        (byte[] pixels, int w, int stride) = Render("99999999999 5", 4);
        List<int> values = SurveyCoordExtractor.ExtractNumbers(pixels, w, 11, stride, 4);
        Assert.Equal([5], values);
    }

    [Fact]
    public void 比對失敗的位置不會讓後面的數字讀不到()
    {
        // 前面有一欄雜訊（不是任何字形），後面的座標仍然要讀得到
        (byte[] pixels, int w, int stride) = Render("1 2", 4);
        byte[] noisy = new byte[(w + 2) * 4 * 11];
        for (int y = 0; y < 11; y++)
        {
            Array.Copy(pixels, y * stride, noisy, y * (w + 2) * 4 + 2 * 4, w * 4);
            if (y is 3 or 4) noisy[y * (w + 2) * 4] = 255; // 第 0 欄：兩個白點
            if (y is 3 or 4) noisy[y * (w + 2) * 4 + 1] = 255;
            if (y is 3 or 4) noisy[y * (w + 2) * 4 + 2] = 255;
        }
        Assert.True(SurveyCoordExtractor.TryReadCoord(noisy, w + 2, 11, (w + 2) * 4, 4, out int x, out int yy));
        Assert.Equal(1, x);
        Assert.Equal(2, yy);
    }

    [Fact]
    public void ToColumnMasks_最上面的點是最高位元()
    {
        byte[] pixels = new byte[4 * 11];
        pixels[0] = pixels[1] = pixels[2] = 255; // 第 0 列、第 0 欄
        int[] columns = SurveyCoordExtractor.ToColumnMasks(pixels, 1, 11, 4, 4);
        Assert.Equal(1 << 10, columns[0]);
    }
}
