// CardIconTests.cs
// 卡片圖示的判斷：用實際的遊戲畫面（TestData/Screens，800×600、縮放 1），每一列有沒有卡片要判斷正確。

using System.Drawing;
using System.Linq;
using GVODiscoverySync.Ocr;
using Xunit;

namespace GVODiscoverySync.Tests;

public class CardIconTests
{
    [Theory]
    [InlineData("天文-全部有卡片.png", new int[] { })]
    [InlineData("史跡-第4列沒有卡片.png", new[] { 4 })]
    [InlineData("美術品-第6和10列沒有卡片.png", new[] { 6, 10 })] // 第 10 列是選取中的列（藍綠色底）
    public void 每一列有沒有卡片(string file, int[] withoutCard)
    {
        using Bitmap client = new(TestBook.DataPath(System.IO.Path.Combine("Screens", file)));
        ListLayout layout = new();
        for (int row = 0; row < layout.RowCount; row++)
        {
            bool expected = !withoutCard.Contains(row + 1);
            Assert.True(expected == DiscoveryListReader.HasCardIcon(client, layout, row, 1),
                $"{file} 第 {row + 1} 列應該{(expected ? "有" : "沒有")}卡片");
        }
    }
}
