// SeaMap.cs
// 從地圖圖檔分辨「海」和「陸地」，給規劃航線用。
//
// 地圖是用固定幾種顏色畫的（以附帶的 map.png 為準）：
//   陸地      (24,44,64)
//   不能航行  (222,231,241) 北邊的冰、(18,33,50) 南邊最底下的深色帶、(33,59,89) 亞洲內陸的圖例框
//   海        各海域的藍色，以及海域分界線
// 其他顏色（地名文字、港口圖示、海流箭頭、反鋸齒邊緣）看周圍：最靠近的已知像素是海就算海，是陸地就算陸地。
//
// 海陸資料檔（seamask.bin）：
//   上面這些顏色只對附帶的 map.png 有效。為了讓使用者換任何地圖都能規劃航線，
//   附帶地圖分析好的結果存成 seamask.bin，跟程式一起發佈；規劃航線一律用這個檔案，
//   和畫面上顯示哪張地圖無關。只有這個檔案不見時，才會退回去分析目前顯示的地圖。
//
//   檔案格式：前 8 位元組 "GVOSEA1\0"，接著寬、高（各 4 位元組整數），
//   之後是 GZip 壓縮的位元資料（每格 1 位元，1 = 海，由左到右、由上到下）。

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using GVO.Common.IO;

namespace GVONavish.Core;

public class SeaMap
{
    private static readonly int[] LandColors =
    [
        Rgb(24, 44, 64),    // 陸地
        Rgb(18, 33, 50),    // 南邊不能航行的區域
        Rgb(222, 231, 241), // 北邊的冰
        Rgb(33, 59, 89),    // 圖例框
    ];

    private static readonly int[] SeaColors =
    [
        Rgb(57, 102, 154), Rgb(48, 87, 129), Rgb(40, 73, 107), Rgb(51, 92, 138),
        Rgb(38, 69, 101), Rgb(35, 63, 92), Rgb(43, 79, 117),
        Rgb(77, 124, 169),  // 海域分界線
    ];

    public int Width { get; }
    public int Height { get; }

    /// <summary>每一格是不是海。索引 = y * Width + x。</summary>
    public bool[] IsSea { get; }

    /// <summary>每一格離最近的陸地幾格（陸地本身是 0，最多記到 255）。用來讓航線和海岸保持距離。</summary>
    public byte[] CoastDistance { get; }

    /// <summary>海的比例（檢查用：顏色表不合時會很低）。</summary>
    public double SeaRatio { get; }

    private SeaMap(int width, int height, bool[] isSea)
    {
        Width = width;
        Height = height;
        IsSea = isSea;
        CoastDistance = ComputeCoastDistance();
        int seaCount = 0;
        foreach (bool b in isSea) if (b) seaCount++;
        SeaRatio = (double)seaCount / isSea.Length;
    }

    private static int Rgb(int r, int g, int b) => (r << 16) | (g << 8) | b;

    private static readonly byte[] MaskMagic = Encoding.ASCII.GetBytes("GVOSEA1\0");

    /// <summary>海陸資料檔的預設位置（程式資料夾的 seamask.bin）。</summary>
    public static string DefaultMaskPath => Path.Combine(AppContext.BaseDirectory, "seamask.bin");

    /// <summary>
    /// 海陸資料的大小：和附帶的地圖相同，4096 × 2048 格（每格 = 遊戲座標 4 × 4）。
    /// 讀檔、分析地圖時都要求剛好是這個大小：大小不對的檔案不是這個程式產生的，
    /// 而且太大的話，後面算離岸距離、規劃航線要的記憶體會暴增。
    /// </summary>
    public const int ExpectedWidth = 4096;
    public const int ExpectedHeight = 2048;

    /// <summary>
    /// 海的比例合不合理（附帶的地圖約 53%）。全海、全陸、或幾乎沒有海的資料一定是錯的，不能拿來規劃航線。
    /// </summary>
    public bool IsPlausible => Width == ExpectedWidth && Height == ExpectedHeight && SeaRatio >= 0.2 && SeaRatio <= 0.9;

    /// <summary>讀取海陸資料檔。讀不到或格式不對時回傳 null。</summary>
    public static SeaMap? LoadMask(string path)
    {
        try
        {
            using FileStream fs = File.OpenRead(path);
            using BinaryReader br = new(fs);
            byte[] magic = br.ReadBytes(MaskMagic.Length);
            if (magic.Length != MaskMagic.Length) return null;
            for (int i = 0; i < MaskMagic.Length; i++)
            {
                if (magic[i] != MaskMagic[i]) return null;
            }
            int w = br.ReadInt32();
            int h = br.ReadInt32();
            if (w != ExpectedWidth || h != ExpectedHeight) return null; // 先檢查大小，再配置記憶體

            var bits = new byte[(w * h + 7) / 8];
            using (GZipStream gz = new(fs, CompressionMode.Decompress))
            {
                int read = 0;
                while (read < bits.Length)
                {
                    int n = gz.Read(bits, read, bits.Length - read);
                    if (n <= 0) return null; // 檔案不完整
                    read += n;
                }
            }

            var isSea = new bool[w * h];
            for (int i = 0; i < isSea.Length; i++)
            {
                isSea[i] = (bits[i >> 3] & (1 << (i & 7))) != 0;
            }
            SeaMap map = new(w, h, isSea);
            return map.IsPlausible ? map : null; // 全海、全陸之類的錯誤資料當成讀不到（會改用附帶的地圖重建）
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把海陸資料存成檔案（產生 seamask.bin 用）。</summary>
    public void SaveMask(string path)
    {
        var bits = new byte[(Width * Height + 7) / 8];
        for (int i = 0; i < IsSea.Length; i++)
        {
            if (IsSea[i]) bits[i >> 3] |= (byte)(1 << (i & 7));
        }
        using FileStream fs = File.Create(path);
        using (BinaryWriter bw = new(fs, Encoding.ASCII, leaveOpen: true))
        {
            bw.Write(MaskMagic);
            bw.Write(Width);
            bw.Write(Height);
        }
        using GZipStream gz = new(fs, CompressionLevel.Optimal);
        gz.Write(bits, 0, bits.Length);
    }

    /// <summary>先寫到暫存檔再換掉原檔，寫到一半斷電也不會留下壞掉的 seamask.bin。</summary>
    public void SaveMaskAtomic(string path)
    {
        AtomicFile.Write(path, SaveMask);
    }

    /// <summary>
    /// 從像素資料建立。pixels 是 BGRA（每像素 4 位元組，Bitmap.LockBits 32bppArgb 的格式）。
    /// </summary>
    public static SeaMap FromPixels(byte[] pixels, int width, int height, int stride)
    {
        HashSet<int> land = [.. LandColors];
        HashSet<int> sea = [.. SeaColors];

        // 0 = 還不知道, 1 = 海, 2 = 陸地
        var cls = new byte[width * height];
        Queue<int> queue = [];
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int o = row + x * 4;
                int c = Rgb(pixels[o + 2], pixels[o + 1], pixels[o]);
                int i = y * width + x;
                if (sea.Contains(c)) cls[i] = 1;
                else if (land.Contains(c)) cls[i] = 2;
                if (cls[i] != 0) queue.Enqueue(i);
            }
        }

        // 不知道的像素：從已知的像素往外擴散，最先碰到的是海就算海，是陸地就算陸地
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % width, y = i / width;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0);
                int ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if (ny < 0 || ny >= height) continue;
                if (nx < 0) nx += width; else if (nx >= width) nx -= width; // 左右相連
                int j = ny * width + nx;
                if (cls[j] == 0)
                {
                    cls[j] = cls[i];
                    queue.Enqueue(j);
                }
            }
        }

        var isSea = new bool[width * height];
        for (int i = 0; i < isSea.Length; i++) isSea[i] = cls[i] == 1;
        return new SeaMap(width, height, isSea);
    }

    // 從所有陸地格子同時往外走，算出每一格海離陸地的距離（8 方向，以格數計）
    private byte[] ComputeCoastDistance()
    {
        var dist = new byte[Width * Height];
        Queue<int> queue = [];
        for (int i = 0; i < dist.Length; i++)
        {
            if (IsSea[i])
            {
                dist[i] = byte.MaxValue;
            }
            else
            {
                dist[i] = 0;
                queue.Enqueue(i);
            }
        }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % Width, y = i / Width;
            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = y + dy;
                if (ny < 0 || ny >= Height) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = WrapCellX(x + dx);
                    int j = ny * Width + nx;
                    if (dist[j] > dist[i] + 1)
                    {
                        dist[j] = (byte)(dist[i] + 1);
                        queue.Enqueue(j);
                    }
                }
            }
        }
        return dist;
    }

    public int WrapCellX(int x)
    {
        x %= Width;
        return x < 0 ? x + Width : x;
    }

    /// <summary>世界座標 → 格子座標。</summary>
    public (int X, int Y) CellFromWorld(double wx, double wy)
    {
        int cx = WrapCellX((int)Math.Floor(World.WrapX(wx) * Width / World.Width));
        int cy = Math.Clamp((int)Math.Floor(wy * Height / World.Height), 0, Height - 1);
        return (cx, cy);
    }

    /// <summary>格子中心 → 世界座標。</summary>
    public (double X, double Y) WorldFromCell(double cx, double cy)
    {
        return ((cx + 0.5) * World.Width / Width, (cy + 0.5) * World.Height / Height);
    }

    public bool IsSeaCell(int x, int y) => y >= 0 && y < Height && IsSea[y * Width + WrapCellX(x)];
}
