// TestFiles.cs
// 測試要用到的檔案：海陸資料 seamask.bin（規劃航線、檢查港口座標用）、季節風向 winds.bin。

using System;
using System.IO;
using GVONavish.Core;

namespace GVONavish.Tests;

public static class TestFiles
{
    private static readonly Lazy<SeaMap> _seaMap = new(LoadSeaMap);

    /// <summary>海陸資料。所有測試共用同一份（讀一次約 0.1 秒）。</summary>
    public static SeaMap SeaMap => _seaMap.Value;

    private static readonly Lazy<WindMap> _windMap = new(() =>
    {
        string path = Find("winds.bin");
        return WindMap.Load(path) ?? throw new InvalidOperationException("winds.bin 讀不出來：" + path);
    });

    /// <summary>附帶的季節風向資料。</summary>
    public static WindMap WindMap => _windMap.Value;

    private static SeaMap LoadSeaMap()
    {
        string path = Find("seamask.bin");
        return SeaMap.LoadMask(path) ?? throw new InvalidOperationException("seamask.bin 讀不出來：" + path);
    }

    /// <summary>
    /// 先找測試的輸出資料夾（引用 GVONavish 專案時會複製過來）；
    /// 沒有的話往上層找原始碼的 GVONavish 資料夾。
    /// </summary>
    private static string Find(string fileName)
    {
        string local = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(local)) return local;

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "GVONavish", fileName);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"找不到 {fileName}（應該在 GVONavish 資料夾裡）");
    }
}
