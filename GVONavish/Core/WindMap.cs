// WindMap.cs
// 每個海域的季節風向（規劃航線時標出逆風的航段用）。
//
// 資料檔 winds.bin：從安瑟兒的中文地圖（2023-07-03 版）整理出來的。
//   地圖上每個海域有一紅一藍兩個箭頭，紅色是夏季的風向、藍色是冬季的風向，
//   箭頭指的是「風吹過去的方向」（例如加勒比海是往西：東北信風）。
//
// 檔案格式：
//   8 位元組    "GVOWIND1"
//   int × 3     格子的寬、高、海域數
//   每個海域    ushort × 4：箭頭的位置 X、Y（世界座標）、夏季風向、冬季風向（度）
//   其餘        GZip 壓縮的格子資料：每格 1 位元組，寫的是這一格屬於第幾個海域
//
// 風向的角度和航向一樣：0 = 往北，90 = 往東，順時針。

using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace GVONavish.Core;

/// <summary>季節。遊戲裡半年換一次（現實時間 9 小時）。</summary>
public enum Season
{
    Summer,
    Winter,
}

public sealed class WindMap
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("GVOWIND1");

    /// <summary>風向資料檔的預設位置（程式資料夾的 winds.bin）。</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "winds.bin");

    // 檔案裡的大小要在這個範圍內：超出的不是這個程式用的檔案
    private const int MaxGridSide = 4096;
    private const int MaxAreas = 255;

    private readonly int _width, _height;
    private readonly byte[] _areaOfCell;      // 每一格屬於哪個海域
    private readonly ushort[] _summer, _winter; // 每個海域的風向（度）

    /// <summary>海域的數目。</summary>
    public int AreaCount => _summer.Length;

    private WindMap(int width, int height, byte[] areaOfCell, ushort[] summer, ushort[] winter)
    {
        _width = width;
        _height = height;
        _areaOfCell = areaOfCell;
        _summer = summer;
        _winter = winter;
    }

    /// <summary>
    /// 這個位置的風吹往哪個方向（度：0 = 往北，90 = 往東，順時針）。
    /// X 超出地圖會折回來（地圖左右相連），Y 超出範圍算最靠近的那一格。
    /// </summary>
    public double DirectionAt(double worldX, double worldY, Season season)
    {
        int area = AreaAt(worldX, worldY);
        return season == Season.Summer ? _summer[area] : _winter[area];
    }

    /// <summary>這個位置屬於第幾個海域（0 開始）。同一個海域的風向相同。</summary>
    public int AreaAt(double worldX, double worldY)
    {
        int cx = (int)(World.WrapX(worldX) * _width / World.Width);
        int cy = (int)(worldY * _height / World.Height);
        cx = Math.Clamp(cx, 0, _width - 1);
        cy = Math.Clamp(cy, 0, _height - 1);
        return _areaOfCell[cy * _width + cx];
    }

    /// <summary>讀取風向資料檔。檔案不存在、格式不對或內容不合理時回傳 null。</summary>
    public static WindMap? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using FileStream fs = File.OpenRead(path);
            using BinaryReader reader = new(fs);

            byte[] magic = reader.ReadBytes(Magic.Length);
            if (!magic.AsSpan().SequenceEqual(Magic)) return null;

            int width = reader.ReadInt32(), height = reader.ReadInt32(), areas = reader.ReadInt32();
            if (width is < 1 or > MaxGridSide || height is < 1 or > MaxGridSide || areas is < 1 or > MaxAreas) return null;

            ushort[] summer = new ushort[areas], winter = new ushort[areas];
            for (int i = 0; i < areas; i++)
            {
                reader.ReadUInt16(); // 箭頭的位置 X、Y：目前沒有用到
                reader.ReadUInt16();
                summer[i] = reader.ReadUInt16();
                winter[i] = reader.ReadUInt16();
                if (summer[i] >= 360 || winter[i] >= 360) return null;
            }

            byte[] cells = new byte[width * height];
            using GZipStream gz = new(fs, CompressionMode.Decompress);
            gz.ReadExactly(cells);
            if (gz.ReadByte() != -1) return null; // 資料比宣告的大小還多
            foreach (byte c in cells)
            {
                if (c >= areas) return null;
            }
            return new WindMap(width, height, cells, summer, winter);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null; // 檔案讀到一半就結束、壓縮資料壞掉
        }
    }
}
