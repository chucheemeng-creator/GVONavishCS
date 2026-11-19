// DiscoveryBookTests.cs
// 讀寫 Excel：用 Open XML SDK 在測試裡即時產生一個小的 xlsx（格式和安瑟兒的發現物統計表一樣：
// 每個分類一個工作表，第 1 列是標題，C 欄是名稱，þ、2～5 是五個人物的打勾欄），不需要真的 Excel 檔。
// 重點是「寫入不能弄壞使用者的檔案」：列號對不上要跳過、失敗要不動原檔、每次寫入前都有備份。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GVODiscoverySync.Excel;
using Xunit;

namespace GVODiscoverySync.Tests;

public sealed class DiscoveryBookTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GVODiscoverySyncTests-" + Guid.NewGuid().ToString("N"));

    public DiscoveryBookTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // 清不掉就留在暫存資料夾
        }
    }

    // 史跡：三個發現物（第 3 列的人物 1 已經打勾，格式是 5）；「All」是公式彙整頁，不能讀、不能寫
    private static readonly string[][] Ruins =
    [
        ["1", "10", "帕連奎"],
        ["2", "20", "吳哥窟"],
        ["3", "30", "馬丘比丘"],
    ];

    private string NewBook(string[][]? ruins = null, string file = "book.xlsx")
    {
        string path = Path.Combine(_dir, file);
        using SpreadsheetDocument doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        WorkbookPart wb = doc.AddWorkbookPart();
        wb.Workbook = new Workbook(new Sheets());
        Sheets sheets = wb.Workbook.Sheets!;

        // 共用字串表（Excel 存檔時就是這樣）
        List<string> strings = [];
        string Shared(string text)
        {
            int i = strings.IndexOf(text);
            if (i < 0)
            {
                strings.Add(text);
                i = strings.Count - 1;
            }
            return i.ToString();
        }
        Cell Text(string reference, string text, uint? style = null)
            => new() { CellReference = reference, DataType = CellValues.SharedString, CellValue = new CellValue(Shared(text)), StyleIndex = style };

        void AddSheet(string name, uint id, List<Row> rows)
        {
            WorksheetPart part = wb.AddNewPart<WorksheetPart>();
            part.Worksheet = new Worksheet(new SheetData(rows));
            sheets.Append(new Sheet { Name = name, SheetId = id, Id = wb.GetIdOfPart(part) });
        }

        // 首頁：F2 是人物 1 的名字
        AddSheet("首頁", 1, [new Row(Text("A1", "首頁")) { RowIndex = 1 }, new Row(Text("F2", "艾莉")) { RowIndex = 2 }]);

        List<Row> rows =
        [
            new Row(Text("A1", "ID"), Text("B1", "功績"), Text("C1", "名稱"), Text("D1", "þ"), Text("E1", "2"),
                Text("F1", "3"), Text("G1", "4"), Text("H1", "5")) { RowIndex = 1 },
        ];
        foreach (string[] r in ruins ?? Ruins)
        {
            uint index = uint.Parse(r[0]) + 1;
            Row row = new(Text("A" + index, r[0]), new Cell { CellReference = "B" + index, CellValue = new CellValue(r[1]) },
                Text("C" + index, r[2])) { RowIndex = index };
            if (index == 4)
            {
                row.Append(Text("D4", "þ", 5)); // 第 3 個發現物：人物 1 已打勾，格式 5
            }
            rows.Add(row);
        }
        rows.Add(new Row(Text("A5", "合計"), new Cell { CellReference = "C5", CellFormula = new CellFormula("COUNTA(C2:C4)") }) { RowIndex = 5 });
        AddSheet("史跡", 2, rows);

        // 彙整頁：標題一樣，名稱是公式
        AddSheet("All", 3,
        [
            new Row(Text("C1", "名稱"), Text("D1", "þ"), Text("E1", "2"), Text("F1", "3"), Text("G1", "4"), Text("H1", "5")) { RowIndex = 1 },
            new Row(new Cell { CellReference = "C2", CellFormula = new CellFormula("史跡!C2"), CellValue = new CellValue("帕連奎") }) { RowIndex = 2 },
        ]);

        wb.AddNewPart<SharedStringTablePart>().SharedStringTable =
            new SharedStringTable(strings.Select(s => new SharedStringItem(new DocumentFormat.OpenXml.Spreadsheet.Text(s))));
        wb.Workbook.Save();
        return path;
    }

    private static Cell? CellAt(string path, string sheet, string reference)
    {
        using SpreadsheetDocument doc = SpreadsheetDocument.Open(path, false);
        WorkbookPart wb = doc.WorkbookPart!;
        Sheet s = wb.Workbook.Sheets!.Elements<Sheet>().First(x => x.Name == sheet);
        WorksheetPart part = (WorksheetPart)wb.GetPartById(s.Id!.Value!);
        Cell? cell = part.Worksheet.Descendants<Cell>().FirstOrDefault(c => c.CellReference?.Value == reference);
        return cell?.CloneNode(true) as Cell; // 檔案關閉後還能用
    }

    private static string InlineText(Cell? cell) => cell?.InlineString?.Text?.Text ?? "";

    [Fact]
    public void 讀檔_分類_名稱_功績_打勾_人物名稱()
    {
        DiscoveryBook book = DiscoveryBook.Load(NewBook());
        Assert.Equal(["史跡"], book.Categories.Keys); // 首頁、All 不是分類
        List<DiscoveryItem> items = book.Categories["史跡"];
        Assert.Equal(["帕連奎", "吳哥窟", "馬丘比丘"], items.Select(i => i.Name)); // 「合計」列不算
        Assert.Equal([10, 20, 30], items.Select(i => i.Merit));
        Assert.Equal([2u, 3u, 4u], items.Select(i => i.Row));
        Assert.Equal([false, false, true], items.Select(i => i.Marked[0]));
        Assert.Equal("艾莉", book.CharacterNames[0]);
        Assert.Equal("人物2", book.CharacterNames[1]);
    }

    [Fact]
    public void 沒有發現物的工作表_丟出看得懂的例外()
    {
        string path = Path.Combine(_dir, "empty.xlsx");
        using (SpreadsheetDocument doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            WorkbookPart wb = doc.AddWorkbookPart();
            WorksheetPart part = wb.AddNewPart<WorksheetPart>();
            part.Worksheet = new Worksheet(new SheetData());
            wb.Workbook = new Workbook(new Sheets(new Sheet { Name = "其他", SheetId = 1, Id = wb.GetIdOfPart(part) }));
        }
        Assert.Throws<InvalidDataException>(() => DiscoveryBook.Load(path));
    }

    [Fact]
    public void 寫入_打勾存進檔案_已經打勾的跳過_其他人物不動()
    {
        string path = NewBook();
        DiscoveryBook book = DiscoveryBook.Load(path);
        List<DiscoveryItem> items = book.Categories["史跡"];

        WriteResult result = book.WriteMarks(0, items); // 第 3 個人物 1 已經打勾

        Assert.Equal(["帕連奎", "吳哥窟"], result.Written.Select(i => i.Name));
        Assert.Empty(result.Skipped);
        Assert.True(File.Exists(result.BackupPath));
        Assert.Equal([true, true, true], items.Select(i => i.Marked[0])); // 記憶體也更新了

        DiscoveryBook reloaded = DiscoveryBook.Load(path);
        Assert.Equal([true, true, true], reloaded.Categories["史跡"].Select(i => i.Marked[0]));
        Assert.All(reloaded.Categories["史跡"], i => Assert.False(i.Marked[1]));

        Assert.Equal("þ", InlineText(CellAt(path, "史跡", "D2")));
        // 沒動到的：合計列的公式、名稱、All 頁
        Assert.Equal("COUNTA(C2:C4)", CellAt(path, "史跡", "C5")!.CellFormula!.Text);
        Assert.Equal("史跡!C2", CellAt(path, "All", "C2")!.CellFormula!.Text);
        Assert.Null(CellAt(path, "All", "D2"));
        // 開檔時重新計算首頁的統計
        using SpreadsheetDocument doc = SpreadsheetDocument.Open(path, false);
        Assert.True(doc.WorkbookPart!.Workbook.CalculationProperties!.FullCalculationOnLoad!.Value);
    }

    [Fact]
    public void 寫入_新建的儲存格借同一欄附近的格式()
    {
        string path = NewBook();
        DiscoveryBook book = DiscoveryBook.Load(path);
        book.WriteMarks(0, [book.Categories["史跡"][1]]); // D3 原本沒有儲存格；D4 的格式是 5

        Assert.Equal(5u, CellAt(path, "史跡", "D3")!.StyleIndex!.Value);
    }

    [Fact]
    public void 寫入_讀檔之後Excel被改過_列號對不上名稱的跳過_不寫錯列()
    {
        string path = NewBook();
        DiscoveryBook book = DiscoveryBook.Load(path);
        // 使用者在 Excel 把前兩列對調（排序）：列號 2 現在是吳哥窟
        NewBook([["1", "20", "吳哥窟"], ["2", "10", "帕連奎"], ["3", "30", "馬丘比丘"]]);

        DiscoveryItem pal = book.Categories["史跡"][0]; // 讀檔時在第 2 列的帕連奎，現在第 2 列是吳哥窟
        WriteResult result = book.WriteMarks(0, [pal]);

        Assert.Empty(result.Written);
        Assert.Equal([pal], result.Skipped);
        Assert.False(pal.Marked[0]);
        Assert.Null(CellAt(path, "史跡", "D2")); // 吳哥窟那一列沒有被打勾
    }

    [Fact]
    public void 寫入失敗_原檔一個位元組都沒動_不留暫存檔()
    {
        string path = NewBook();
        byte[] before = File.ReadAllBytes(path);
        DiscoveryBook book = DiscoveryBook.Load(path);

        // 模擬 Excel 開著檔案
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ExcelWriteException ex = Assert.Throws<ExcelWriteException>(() => book.WriteMarks(0, book.Categories["史跡"]));
            Assert.Equal("", ex.BackupPath); // 還沒開始備份
        }

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.All(book.Categories["史跡"].Where(i => i.Name != "馬丘比丘"), i => Assert.False(i.Marked[0])); // 記憶體也沒改
    }

    [Fact]
    public void 寫入失敗_檔案內容壞掉時_原檔不動_備份留著()
    {
        string path = NewBook();
        DiscoveryBook book = DiscoveryBook.Load(path);
        File.WriteAllText(path, "這不是 xlsx"); // 讀檔之後檔案被換成壞掉的
        byte[] before = File.ReadAllBytes(path);

        ExcelWriteException ex = Assert.Throws<ExcelWriteException>(() => book.WriteMarks(0, book.Categories["史跡"]));

        Assert.True(File.Exists(ex.BackupPath));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void 同一秒連續寫入兩次_備份不會蓋掉前一份()
    {
        string path = NewBook();
        DiscoveryBook book = DiscoveryBook.Load(path);
        WriteResult first = book.WriteMarks(0, [book.Categories["史跡"][0]]);
        WriteResult second = book.WriteMarks(1, [book.Categories["史跡"][0]]);

        Assert.NotEqual(first.BackupPath, second.BackupPath);
        Assert.True(File.Exists(first.BackupPath));
        Assert.True(File.Exists(second.BackupPath));
        // 第一份是第一次寫入「之前」的原檔：人物 1 還沒打勾
        Assert.False(DiscoveryBook.Load(first.BackupPath).Categories["史跡"][0].Marked[0]);
        Assert.True(DiscoveryBook.Load(second.BackupPath).Categories["史跡"][0].Marked[0]);
    }

    [Fact]
    public void 人物編號超出範圍()
    {
        DiscoveryBook book = DiscoveryBook.Load(NewBook());
        Assert.Throws<ArgumentOutOfRangeException>(() => book.WriteMarks(5, book.Categories["史跡"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => book.WriteMarks(-1, book.Categories["史跡"]));
    }

    [Fact]
    public void 重新讀取打勾狀態_Excel被改過時記憶體跟著變()
    {
        string path = NewBook();
        DiscoveryBook book = DiscoveryBook.Load(path);
        Assert.False(book.Categories["史跡"][0].Marked[0]);

        // 另一個程式幫帕連奎的人物 1 打了勾
        DiscoveryBook other = DiscoveryBook.Load(path);
        other.WriteMarks(0, [other.Categories["史跡"][0]]);

        book.ReloadMarks();
        Assert.True(book.Categories["史跡"][0].Marked[0]);
    }

    [Fact]
    public void 重新讀取打勾狀態_檔案壞了或不見了_維持原狀不丟例外()
    {
        string path = NewBook();
        DiscoveryBook book = DiscoveryBook.Load(path);
        book.Categories["史跡"][0].Marked[0] = true; // 記憶體裡的狀態

        File.WriteAllText(path, "這不是 xlsx");
        book.ReloadMarks();
        Assert.True(book.Categories["史跡"][0].Marked[0]);

        File.Delete(path);
        book.ReloadMarks();
        Assert.True(book.Categories["史跡"][0].Marked[0]);
    }
}
