// SeaMapTests.cs
// 海陸資料：從像素分析、存成 seamask.bin 再讀回來、檔案壞掉時讀不到、離岸距離。

using System;
using System.IO;
using System.IO.Compression;
using GVONavish.Core;
using Xunit;

namespace GVONavish.Tests;

public class SeaMapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gvonavish-sea-" + Guid.NewGuid().ToString("N"));

    public SeaMapTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    private static readonly (byte R, byte G, byte B) Sea = (57, 102, 154);
    private static readonly (byte R, byte G, byte B) Land = (24, 44, 64);

    // 全尺寸（4096 × 2048）的地圖：x < landFrom 是海，其餘是陸地
    private static SeaMap FullSizeMap(int landFrom)
    {
        const int w = SeaMap.ExpectedWidth, h = SeaMap.ExpectedHeight;
        byte[] pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                (byte r, byte g, byte b) = x < landFrom ? Sea : Land;
                int o = (y * w + x) * 4;
                pixels[o] = b;
                pixels[o + 1] = g;
                pixels[o + 2] = r;
                pixels[o + 3] = 255;
            }
        }
        return SeaMap.FromPixels(pixels, w, h, w * 4);
    }

    [Fact]
    public void 分析像素_海和陸地分得出來()
    {
        SeaMap map = FullSizeMap(landFrom: 3000);
        Assert.True(map.IsSeaCell(0, 0));
        Assert.True(map.IsSeaCell(2999, 100));
        Assert.False(map.IsSeaCell(3000, 100));
        Assert.False(map.IsSeaCell(4095, 2047));
        Assert.Equal(3000.0 / 4096, map.SeaRatio, 6);
        Assert.True(map.IsPlausible);
    }

    [Fact]
    public void 不認得的顏色_看最靠近的已知像素()
    {
        // 8 × 4，全部是海，只有中間一格是地名文字的顏色
        const int w = 8, h = 4;
        byte[] pixels = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            pixels[i * 4] = Sea.B;
            pixels[i * 4 + 1] = Sea.G;
            pixels[i * 4 + 2] = Sea.R;
        }
        int o = (1 * w + 3) * 4;
        pixels[o] = 1;
        pixels[o + 1] = 2;
        pixels[o + 2] = 3;

        SeaMap map = SeaMap.FromPixels(pixels, w, h, w * 4);
        Assert.True(map.IsSeaCell(3, 1));
    }

    [Fact]
    public void 離岸距離_陸地是0_旁邊是1_左右相連()
    {
        SeaMap map = FullSizeMap(landFrom: 4000);
        int y = 500;
        Assert.Equal(0, map.CoastDistance[y * map.Width + 4000]);
        Assert.Equal(1, map.CoastDistance[y * map.Width + 3999]);
        Assert.Equal(3, map.CoastDistance[y * map.Width + 3997]);
        // 地圖最左邊（x = 0）的左邊是最右邊的陸地（x = 4095）：離岸 1
        Assert.Equal(1, map.CoastDistance[y * map.Width + 0]);
    }

    [Fact]
    public void 存檔再讀回來_內容一樣()
    {
        SeaMap map = FullSizeMap(landFrom: 3000);
        map.SaveMask(PathOf("mask.bin"));

        SeaMap? loaded = SeaMap.LoadMask(PathOf("mask.bin"));
        Assert.NotNull(loaded);
        Assert.Equal(map.Width, loaded!.Width);
        Assert.Equal(map.Height, loaded.Height);
        Assert.Equal(map.IsSea, loaded.IsSea);
        Assert.Equal(map.CoastDistance, loaded.CoastDistance);
    }

    [Fact]
    public void 安全存檔_讀得回來_而且沒有留下暫存檔()
    {
        SeaMap map = FullSizeMap(landFrom: 3000);
        map.SaveMaskAtomic(PathOf("mask.bin"));
        map.SaveMaskAtomic(PathOf("mask.bin")); // 已經有檔案時換掉它
        Assert.NotNull(SeaMap.LoadMask(PathOf("mask.bin")));
        Assert.False(File.Exists(PathOf("mask.bin.tmp")));
    }

    [Fact]
    public void 檔案不存在_讀不到()
    {
        Assert.Null(SeaMap.LoadMask(PathOf("missing.bin")));
    }

    [Fact]
    public void 開頭標記不對_讀不到()
    {
        File.WriteAllBytes(PathOf("bad.bin"), new byte[64]);
        Assert.Null(SeaMap.LoadMask(PathOf("bad.bin")));
    }

    [Fact]
    public void 檔案只有一半_讀不到()
    {
        SeaMap map = FullSizeMap(landFrom: 3000);
        map.SaveMask(PathOf("mask.bin"));
        byte[] all = File.ReadAllBytes(PathOf("mask.bin"));
        File.WriteAllBytes(PathOf("half.bin"), all[..(all.Length / 2)]);
        Assert.Null(SeaMap.LoadMask(PathOf("half.bin")));
    }

    [Fact]
    public void 大小不對_讀不到_不會先配置記憶體()
    {
        using (FileStream fs = File.Create(PathOf("big.bin")))
        using (BinaryWriter bw = new(fs))
        {
            bw.Write("GVOSEA1\0"u8);
            bw.Write(100_000);
            bw.Write(100_000);
        }
        Assert.Null(SeaMap.LoadMask(PathOf("big.bin")));
    }

    [Fact]
    public void 全海_當成錯誤資料讀不到()
    {
        // 大小正確，但每一格都是海：IsPlausible 會擋下來
        using (FileStream fs = File.Create(PathOf("allsea.bin")))
        {
            using (BinaryWriter bw = new(fs, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                bw.Write("GVOSEA1\0"u8);
                bw.Write(SeaMap.ExpectedWidth);
                bw.Write(SeaMap.ExpectedHeight);
            }
            using GZipStream gz = new(fs, CompressionLevel.Fastest);
            byte[] bits = new byte[SeaMap.ExpectedWidth * SeaMap.ExpectedHeight / 8];
            Array.Fill(bits, (byte)0xFF);
            gz.Write(bits);
        }
        Assert.Null(SeaMap.LoadMask(PathOf("allsea.bin")));
    }
}
