// CardIconTests.cs
// 卡片圖示的判斷：用實際的遊戲畫面（TestData/Screens，800×600、縮放 1），每一列有沒有卡片要判斷正確。
// 檔名用英文：中文檔名在 zip 解壓縮時可能變成亂碼，找不到檔案（GDI+ 會報「Parameter is not valid」）。

using System.Drawing;
using System.Linq;
using GVODiscoverySync.Ocr;
using Xunit;

namespace GVODiscoverySync.Tests;

public class CardIconTests
{
    [Theory]
    [InlineData("astronomy-all-cards.png", new int[] { })] // 天文：全部有卡片
    [InlineData("ruins-row4-no-card.png", new[] { 4 })] // 史跡：第 4 列（帕連奎）沒有
    [InlineData("art-row6-row10-no-card.png", new[] { 6, 10 })] // 美術品：第 6、10 列沒有（第 10 列是選取中的列，藍綠色底）
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
