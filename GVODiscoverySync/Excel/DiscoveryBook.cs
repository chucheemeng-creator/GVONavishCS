// DiscoveryBook.cs
// 讀寫安瑟兒的發現物統計 Excel（.xlsx）。
//
// 表的格式：每個分類一個工作表，第 1 列是標題，C 欄是發現物名稱；
// 標題是「þ」「2」「3」「4」「5」的五欄分別是人物 1～5，打勾＝在儲存格填「þ」（Wingdings 字型顯示成勾）。
// 首頁用 COUNTA／SUMIF 算這五欄，所以只要填「þ」就好，不用動公式。
//
// 用 Open XML SDK 直接改儲存格的值：只動到有打勾的那幾個工作表，
// 格式化條件、表格、註解、圖片等其他內容原封不動（用一般的 Excel 套件整個讀進來再存，可能會弄丟不認得的功能）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace GVODiscoverySync.Excel;

public sealed class DiscoveryBook
{
    public const int CharacterCount = 5;

    /// <summary>打勾的字（Wingdings 字型的勾）。</summary>
    public const string Mark = "þ";

    // 人物 1～5 那五欄的標題
    private static readonly string[] MarkHeaders = ["þ", "2", "3", "4", "5"];

    // 發現物名稱固定在 C 欄（各工作表的標題不同：史跡、宗教建築、港口˙村落……）
    private const string NameColumn = "C";

    // 首頁第 2 列的人物名稱（預設是「人物1」～「人物5」，可以改成角色名字）
    private static readonly string[] CharacterNameCells = ["F2", "I2", "L2", "O2", "R2"];

    public string FilePath { get; }

    /// <summary>分類名稱（工作表名稱）→ 這個分類的發現物，順序和 Excel 一樣。</summary>
    public Dictionary<string, List<DiscoveryItem>> Categories { get; } = [];

    // 分類名稱 → 人物 1～5 那五欄的欄名（例如 J、K、L、M、N）
    private readonly Dictionary<string, string[]> _markColumns = [];

    private DiscoveryBook(string path)
    {
        FilePath = path;
    }

    /// <summary>人物 1～5 的名稱（從首頁第 2 列讀，讀不到就是「人物1」～「人物5」）。</summary>
    public string[] CharacterNames { get; } = ["人物1", "人物2", "人物3", "人物4", "人物5"];

    public int ItemCount => Categories.Values.Sum(l => l.Count);

    /// <summary>
    /// 讀 Excel。Excel 開著這個檔案也可以讀（寫入時才需要關掉）。
    /// 讀不出來時丟出例外（訊息可以直接給使用者看）。
    /// </summary>
    public static DiscoveryBook Load(string path)
    {
        DiscoveryBook book = new(path);
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using SpreadsheetDocument doc = SpreadsheetDocument.Open(fs, false);
        WorkbookPart wbPart = doc.WorkbookPart ?? throw new InvalidDataException("這不是有效的 Excel 檔案。");
        List<string> strings = ReadSharedStrings(wbPart);

        foreach (Sheet sheet in wbPart.Workbook.Sheets?.Elements<Sheet>() ?? [])
        {
            string category = sheet.Name?.Value ?? "";
            if (category == "" || sheet.Id?.Value is not string relId)
            {
                continue;
            }
            if (wbPart.GetPartById(relId) is not WorksheetPart wsPart)
            {
                continue;
            }
            SheetData? data = wsPart.Worksheet?.GetFirstChild<SheetData>();
            if (data is null)
            {
                continue;
            }
            if (category.StartsWith("首頁", StringComparison.Ordinal))
            {
                book.ReadCharacterNames(data, strings);
                continue;
            }
            book.ReadSheet(category, data, strings);
        }

        if (book.Categories.Count == 0)
        {
            throw new InvalidDataException("找不到發現物的工作表（標題列要有「þ」「2」「3」「4」「5」五欄）。\n請確認選的是安瑟兒的發現物統計表。");
        }
        return book;
    }

    private void ReadCharacterNames(SheetData data, List<string> strings)
    {
        Row? row = data.Elements<Row>().FirstOrDefault(r => r.RowIndex?.Value == 2);
        if (row is null)
        {
            return;
        }
        for (int i = 0; i < CharacterCount; i++)
        {
            Cell? cell = row.Elements<Cell>().FirstOrDefault(c => c.CellReference?.Value == CharacterNameCells[i]);
            string name = cell is null ? "" : GetText(cell, strings).Trim();
            if (name != "")
            {
                CharacterNames[i] = name;
            }
        }
    }

    private void ReadSheet(string category, SheetData data, List<string> strings)
    {
        Row? header = data.Elements<Row>().FirstOrDefault(r => r.RowIndex?.Value == 1);
        if (header is null)
        {
            return;
        }

        // 標題文字 → 欄名
        Dictionary<string, string> columnByHeader = [];
        foreach (Cell cell in header.Elements<Cell>())
        {
            string text = GetText(cell, strings).Trim();
            if (text != "" && cell.CellReference?.Value is string reference)
            {
                columnByHeader.TryAdd(text, ColumnOf(reference));
            }
        }

        string[] markColumns = new string[CharacterCount];
        for (int i = 0; i < CharacterCount; i++)
        {
            if (!columnByHeader.TryGetValue(MarkHeaders[i], out string? column))
            {
                return; // 不是發現物的工作表（首頁、陸地調查一覽、NPC回報……）
            }
            markColumns[i] = column;
        }
        columnByHeader.TryGetValue("ID", out string? idColumn);
        columnByHeader.TryGetValue("功績", out string? meritColumn);
        columnByHeader.TryGetValue("★", out string? starsColumn);

        List<DiscoveryItem> items = [];
        foreach (Row row in data.Elements<Row>())
        {
            uint rowIndex = row.RowIndex?.Value ?? 0;
            if (rowIndex <= 1)
            {
                continue;
            }
            Dictionary<string, Cell> cells = [];
            foreach (Cell cell in row.Elements<Cell>())
            {
                if (cell.CellReference?.Value is string reference)
                {
                    cells.TryAdd(ColumnOf(reference), cell);
                }
            }

            // 最後的「合計」列（名稱欄是公式）不是發現物
            if (cells.TryGetValue("A", out Cell? first) && GetText(first, strings).Trim() == "合計")
            {
                break;
            }
            if (!cells.TryGetValue(NameColumn, out Cell? nameCell) || nameCell.CellFormula is not null)
            {
                continue;
            }
            string name = GetText(nameCell, strings).Trim();
            if (name == "")
            {
                continue;
            }

            int id = 0;
            if (idColumn is not null && cells.TryGetValue(idColumn, out Cell? idCell))
            {
                int.TryParse(GetText(idCell, strings), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
            }

            int merit = 0;
            if (meritColumn is not null && cells.TryGetValue(meritColumn, out Cell? meritCell)
                && double.TryParse(GetText(meritCell, strings), NumberStyles.Float, CultureInfo.InvariantCulture, out double m))
            {
                merit = (int)Math.Round(m);
            }

            string stars = starsColumn is not null && cells.TryGetValue(starsColumn, out Cell? starsCell)
                ? GetText(starsCell, strings).Trim() : "";

            DiscoveryItem item = new() { Category = category, Name = name, Row = rowIndex, Id = id, Merit = merit, Stars = stars };
            for (int i = 0; i < CharacterCount; i++)
            {
                item.Marked[i] = cells.TryGetValue(markColumns[i], out Cell? markCell) && GetText(markCell, strings).Trim() == Mark;
            }
            items.Add(item);
        }

        if (items.Count > 0)
        {
            Categories[category] = items;
            _markColumns[category] = markColumns;
        }
    }

    /// <summary>
    /// 幫人物 character（0～4）在這些發現物打勾，存回 Excel。已經打勾的會跳過。
    /// 寫入前先在同一個資料夾備份原檔（檔名.bak-日期時間.xlsx），回傳備份檔的路徑。
    /// Excel 開著這個檔案時會丟出 IOException。
    /// </summary>
    public string WriteMarks(int character, IReadOnlyCollection<DiscoveryItem> items)
    {
        if (character < 0 || character >= CharacterCount)
        {
            throw new ArgumentOutOfRangeException(nameof(character));
        }

        EnsureNotOpenElsewhere();

        string dir = Path.GetDirectoryName(FilePath) ?? "";
        string backup = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(FilePath)}.bak-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(FilePath)}");
        File.Copy(FilePath, backup, false);

        using (SpreadsheetDocument doc = SpreadsheetDocument.Open(FilePath, true))
        {
            WorkbookPart wbPart = doc.WorkbookPart ?? throw new InvalidDataException("這不是有效的 Excel 檔案。");
            foreach (IGrouping<string, DiscoveryItem> group in items.GroupBy(i => i.Category))
            {
                if (!_markColumns.TryGetValue(group.Key, out string[]? markColumns))
                {
                    continue;
                }
                SheetData? data = FindSheetData(wbPart, group.Key);
                if (data is null)
                {
                    continue;
                }
                Dictionary<uint, Row> rows = [];
                foreach (Row row in data.Elements<Row>())
                {
                    if (row.RowIndex?.Value is uint index)
                    {
                        rows.TryAdd(index, row);
                    }
                }
                foreach (DiscoveryItem item in group)
                {
                    if (item.Marked[character] || !rows.TryGetValue(item.Row, out Row? row))
                    {
                        continue;
                    }
                    Cell cell = GetOrCreateCell(row, markColumns[character] + item.Row.ToString(CultureInfo.InvariantCulture));
                    // 用內嵌字串，不用改共用字串表；Excel 存檔時會自己整理。儲存格原本的格式（Wingdings 字型）保留
                    cell.CellFormula = null;
                    cell.CellValue = null;
                    cell.DataType = CellValues.InlineString;
                    cell.InlineString = new InlineString(new Text(Mark));
                    item.Marked[character] = true;
                }
            }

            // 打開檔案時重新計算，首頁的發現數、功績才會更新
            Workbook workbook = wbPart.Workbook;
            workbook.CalculationProperties ??= new CalculationProperties();
            workbook.CalculationProperties.FullCalculationOnLoad = true;
        }
        return backup;
    }

    // 寫入前確認沒有其他程式（Excel）開著這個檔案，給清楚的錯誤訊息
    private void EnsureNotOpenElsewhere()
    {
        try
        {
            using FileStream fs = new(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new IOException("Excel 檔案正在被其他程式使用（可能是 Excel 開著它）。\n請先關閉 Excel 再寫入。", ex);
        }
    }

    private static SheetData? FindSheetData(WorkbookPart wbPart, string sheetName)
    {
        Sheet? sheet = wbPart.Workbook.Sheets?.Elements<Sheet>().FirstOrDefault(s => s.Name?.Value == sheetName);
        if (sheet?.Id?.Value is not string relId || wbPart.GetPartById(relId) is not WorksheetPart wsPart)
        {
            return null;
        }
        return wsPart.Worksheet?.GetFirstChild<SheetData>();
    }

    // 找儲存格，沒有的話照欄的順序插入一個新的（Excel 要求同一列的儲存格依欄排序）
    private static Cell GetOrCreateCell(Row row, string reference)
    {
        int target = ColumnNumber(ColumnOf(reference));
        Cell? after = null;
        foreach (Cell cell in row.Elements<Cell>())
        {
            if (cell.CellReference?.Value is not string r)
            {
                continue;
            }
            int n = ColumnNumber(ColumnOf(r));
            if (n == target)
            {
                return cell;
            }
            if (n > target)
            {
                after = cell;
                break;
            }
        }
        Cell created = new() { CellReference = reference };
        if (after is null)
        {
            row.AppendChild(created);
        }
        else
        {
            row.InsertBefore(created, after);
        }
        return created;
    }

    private static List<string> ReadSharedStrings(WorkbookPart wbPart)
    {
        SharedStringTable? table = wbPart.SharedStringTablePart?.SharedStringTable;
        if (table is null)
        {
            return [];
        }
        // 只取本文（t 和 r 裡的 t），不要注音（rPh）
        return [.. table.Elements<SharedStringItem>().Select(item =>
            item.Text?.Text ?? string.Concat(item.Elements<Run>().Select(r => r.Text?.Text ?? "")))];
    }

    private static string GetText(Cell cell, List<string> strings)
    {
        if (cell.DataType?.Value == CellValues.SharedString)
        {
            return int.TryParse(cell.CellValue?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                   && index >= 0 && index < strings.Count
                ? strings[index] : "";
        }
        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.Text?.Text
                   ?? string.Concat(cell.InlineString?.Elements<Run>().Select(r => r.Text?.Text ?? "") ?? []);
        }
        return cell.CellValue?.Text ?? "";
    }

    // "J12" → "J"
    private static string ColumnOf(string reference)
    {
        int i = 0;
        while (i < reference.Length && char.IsLetter(reference[i]))
        {
            i++;
        }
        return reference[..i].ToUpperInvariant();
    }

    // "A" → 1、"Z" → 26、"AA" → 27
    private static int ColumnNumber(string column)
    {
        int n = 0;
        foreach (char c in column)
        {
            n = n * 26 + (c - 'A' + 1);
        }
        return n;
    }
}
