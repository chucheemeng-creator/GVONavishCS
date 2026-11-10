// TestFiles.cs
// 測試要用到的檔案：海陸資料 seamask.bin（規劃航線、檢查港口座標用）。

using System;
using System.IO;
using GVONavish.Core;

namespace GVONavish.Tests;

public static class TestFiles
{
    private static readonly Lazy<SeaMap> _seaMap = new(LoadSeaMap);

    /// <summary>海陸資料。所有測試共用同一份（讀一次約 0.1 秒）。</summary>
    public static SeaMap SeaMap => _seaMap.Value;

    private static SeaMap LoadSeaMap()
    {
        string path = FindSeaMask();
        return SeaMap.LoadMask(path) ?? throw new InvalidOperationException("seamask.bin 讀不出來：" + path);
    }

    /// <summary>
    /// 先找測試的輸出資料夾（引用 GVONavish 專案時會複製過來）；
    /// 沒有的話往上層找原始碼的 GVONavish/seamask.bin。
    /// </summary>
    private static string FindSeaMask()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "seamask.bin");
        if (File.Exists(local)) return local;

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "GVONavish", "seamask.bin");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("找不到 seamask.bin（應該在 GVONavish 資料夾裡）");
    }
}
