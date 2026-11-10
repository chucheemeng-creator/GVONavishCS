// PortsTests.cs
// 港口資料和搜尋。資料的檢查（名稱不重複、座標在海邊）可以抓到打錯的座標。

using System.Collections.Generic;
using System.Linq;
using GVONavish.Core;
using Xunit;

namespace GVONavish.Tests;

public class PortsTests
{
    [Fact]
    public void 資料_名稱不重複()
    {
        List<string> duplicated = [.. PortList.All.GroupBy(p => p.Name).Where(g => g.Count() > 1).Select(g => g.Key)];
        Assert.True(duplicated.Count == 0, "重複的港口名稱：" + string.Join("、", duplicated));
    }

    [Fact]
    public void 資料_座標不重複()
    {
        List<string> duplicated = [.. PortList.All.GroupBy(p => p.Position).Where(g => g.Count() > 1)
            .Select(g => string.Join("＝", g.Select(p => p.Name)))];
        Assert.True(duplicated.Count == 0, "座標一樣的港口：" + string.Join("、", duplicated));
    }

    [Fact]
    public void 資料_每個港口都有名稱_區域_英文名()
    {
        foreach (Port p in PortList.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.Region), p.Name + " 沒有區域");
            Assert.False(string.IsNullOrWhiteSpace(p.English), p.Name + " 沒有英文名");
        }
    }

    [Fact]
    public void 資料_每個港口都在海邊()
    {
        // 港口畫在岸邊，座標可能落在陸地的格子上，但附近一定有海。
        // 離海超過這個距離，多半是座標打錯了（規劃航線會從很遠的海上開始）。
        const int maxCells = 4; // 4 格 = 16 個座標單位
        SeaMap map = TestFiles.SeaMap;
        List<string> wrong = [];
        foreach (Port p in PortList.All)
        {
            Assert.True(World.IsValid(p.Position), p.Name + " 的座標超出地圖");
            (int cx, int cy) = map.CellFromWorld(p.Position.X, p.Position.Y);
            bool nearSea = false;
            for (int dy = -maxCells; dy <= maxCells && !nearSea; dy++)
            {
                for (int dx = -maxCells; dx <= maxCells && !nearSea; dx++)
                {
                    nearSea = map.IsSeaCell(cx + dx, cy + dy);
                }
            }
            if (!nearSea) wrong.Add($"{p.Name}（{p.Position}）");
        }
        Assert.True(wrong.Count == 0, "附近沒有海的港口：" + string.Join("、", wrong));
    }

    [Theory]
    [InlineData("里斯本")]
    [InlineData("Lisbon")]
    [InlineData("lisbon")]
    [InlineData("  里斯本  ")]
    [InlineData("里斯本　Lisbon（歐洲西部）")] // 清單上顯示的文字
    public void 用名稱找港口(string text)
    {
        Assert.Equal("里斯本", PortList.Find(text)?.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("沒有這個港口")]
    [InlineData("里斯")] // Find 要完全相同，部分符合用 Search
    public void 找不到港口(string text)
    {
        Assert.Null(PortList.Find(text));
    }

    [Theory]
    [InlineData("坦堡", "伊斯坦堡")]      // 中文名的一部分
    [InlineData("istan", "伊斯坦堡")]     // 英文名的一部分
    [InlineData("CAPE TOWN", "開普敦")]   // 不分大小寫
    [InlineData("capetown", "開普敦")]    // 不用管空白
    [InlineData("hobart", "荷巴特")]
    [InlineData("fraser", "芬瑟島")]      // 第二個英文名
    [InlineData("k'gari", "芬瑟島")]
    [InlineData("錫特卡", "矽地卡")]      // 另一種中文寫法（放在英文名那一欄）
    [InlineData("sitka", "矽地卡")]
    public void 搜尋_第一個結果(string query, string expected)
    {
        List<Port> found = PortList.Search(query);
        Assert.NotEmpty(found);
        Assert.Equal(expected, found[0].Name);
    }

    [Fact]
    public void 搜尋_名稱完全相同的排最前面()
    {
        // 「倫」開頭或包含「倫」的港口不只一個，打完整的「倫敦」時倫敦要在第一個
        Assert.Equal("倫敦", PortList.Search("倫敦")[0].Name);
    }

    [Fact]
    public void 搜尋_區域名稱()
    {
        List<Port> found = PortList.Search("南非");
        Assert.NotEmpty(found);
        Assert.All(found, p => Assert.True(p.Region.Contains("南非") || p.Name.Contains("南非")));
    }

    [Fact]
    public void 搜尋_空的或找不到()
    {
        Assert.Empty(PortList.Search(""));
        Assert.Empty(PortList.Search("zzzzqqqq"));
    }

    [Theory]
    [InlineData("Lübeck", "lubeck")]
    [InlineData("Cape Town", "capetown")]
    [InlineData("K'gari", "kgari")]
    [InlineData("São Tomé", "saotome")]
    public void 比對用的寫法_去掉重音和標點(string text, string expected)
    {
        Assert.Equal(expected, PortList.Normalize(text));
    }

    [Fact]
    public void 最近的港口()
    {
        Port lisbon = PortList.Find("里斯本")!;
        WorldPoint near = new(lisbon.Position.X + 20, lisbon.Position.Y + 10);
        Assert.Same(lisbon, PortList.Nearest(near, 60));
        Assert.Null(PortList.Nearest(near, 10));                       // 超過範圍
        Assert.Null(PortList.Nearest(new WorldPoint(10000, 7500), 60)); // 太平洋南邊，附近沒有港口
    }
}
