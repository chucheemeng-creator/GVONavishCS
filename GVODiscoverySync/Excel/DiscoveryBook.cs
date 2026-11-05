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
        // 大部分工作表是「任務／地圖」，傳說是「任務名稱」
        if (!columnByHeader.TryGetValue("任務／地圖", out string? questColumn))
        {
            columnByHeader.TryGetValue("任務名稱", out questColumn);
        }

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

            string quest = questColumn is not null && cells.TryGetValue(questColumn, out Cell? questCell)
                ? GetText(questCell, strings).Trim() : "";

            DiscoveryItem item = new()
            {
                Category = category, Name = name, Row = rowIndex, Id = id, Merit = merit, Stars = stars, Quest = quest,
            };
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
    /// 寫入前先備份原檔到 Excel 所在資料夾的 backup 子資料夾（檔名.bak-日期時間.xlsx）。
    ///
    /// 安全措施：
    ///   - 每一列寫入前核對 C 欄的名稱還是不是這個發現物（讀檔之後在 Excel 插列、排序、換版本，列號就不對了）；
    ///     不一致的跳過，列在回傳結果的 Skipped 裡
    ///   - 不自動存檔：全部改完才存一次，中途出錯就不存，原檔不動
    ///   - 確定存好之後才把記憶體裡的 Marked 改成 true
    /// 失敗時丟出 ExcelWriteException（帶著備份檔路徑，備份還沒做好時是 ""）。
    /// </summary>
    public WriteResult WriteMarks(int character, IReadOnlyCollection<DiscoveryItem> items)
    {
        if (character < 0 || character >= CharacterCount)
        {
            throw new ArgumentOutOfRangeException(nameof(character));
        }

        string backup = "";
        bool saving = false; // 開始存檔之後才出錯的話，原檔可能已經寫了一部分
        try
        {
            EnsureNotOpenElsewhere();

            string dir = Path.Combine(Path.GetDirectoryName(FilePath) ?? "", "backup");
            Directory.CreateDirectory(dir);
            // 同一秒內連續寫入兩次時檔名會一樣：不要蓋掉（第一份才是寫入前的原檔），加上 -2、-3……
            string stem = $"{Path.GetFileNameWithoutExtension(FilePath)}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
            string ext = Path.GetExtension(FilePath);
            string path = Path.Combine(dir, stem + ext);
            for (int n = 2; File.Exists(path); n++)
            {
                path = Path.Combine(dir, $"{stem}-{n}{ext}");
            }
            File.Copy(FilePath, path, false);
            backup = path;

            List<DiscoveryItem> written = [];
            List<DiscoveryItem> skipped = [];
            using (SpreadsheetDocument doc = SpreadsheetDocument.Open(FilePath, true, new OpenSettings { AutoSave = false }))
            {
                WorkbookPart wbPart = doc.WorkbookPart ?? throw new InvalidDataException("這不是有效的 Excel 檔案。");
                List<string> strings = ReadSharedStrings(wbPart);
                foreach (IGrouping<string, DiscoveryItem> group in items.GroupBy(i => i.Category))
                {
                    SheetData? data = _markColumns.ContainsKey(group.Key) ? FindSheetData(wbPart, group.Key) : null;
                    if (data is null)
                    {
                        skipped.AddRange(group.Where(i => !i.Marked[character]));
                        continue;
                    }
                    string column = _markColumns[group.Key][character];
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
                        if (item.Marked[character])
                        {
                            continue;
                        }
                        // 核對這一列還是不是這個發現物
                        if (!rows.TryGetValue(item.Row, out Row? row) || NameInRow(row, strings) != item.Name)
                        {
                            skipped.Add(item);
                            continue;
                        }
                        string reference = column + item.Row.ToString(CultureInfo.InvariantCulture);
                        Cell cell = GetOrCreateCell(row, reference, out bool created);
                        if (created)
                        {
                            // 新建的儲存格沒有格式：借同一欄其他列的格式（Wingdings 字型），才會顯示成勾
                            cell.StyleIndex = FindColumnStyle(rows, column, item.Row);
                        }
                        // 用內嵌字串，不用改共用字串表；Excel 存檔時會自己整理。儲存格原本的格式保留
                        cell.CellFormula = null;
                        cell.CellValue = null;
                        cell.DataType = CellValues.InlineString;
                        cell.InlineString = new InlineString(new Text(Mark));
                        written.Add(item);
                    }
                }

                // 打開檔案時重新計算，首頁的發現數、功績才會更新
                Workbook workbook = wbPart.Workbook;
                workbook.CalculationProperties ??= new CalculationProperties();
                workbook.CalculationProperties.FullCalculationOnLoad = true;

                saving = true;
                doc.Save(); // 全部改完才存
            }

            // 確定存好了，才更新記憶體
            foreach (DiscoveryItem item in written)
            {
                item.Marked[character] = true;
            }
            return new WriteResult(backup, written, skipped);
        }
        catch (Exception ex) when (ex is not ExcelWriteException)
        {
            throw new ExcelWriteException(ex.Message, backup, saving, ex);
        }
    }

    /// <summary>
    /// 從磁碟重新讀一次打勾的狀態，更新記憶體裡的發現物（物件不變，畫面上的清單繼續有效）。
    /// 寫入失敗、或使用者可能用備份蓋回原檔時用，讓記憶體和檔案一致。讀不出來時不動。
    /// </summary>
    public void ReloadMarks()
    {
        DiscoveryBook fresh;
        try
        {
            fresh = Load(FilePath);
        }
        catch
        {
            return;
        }
        foreach ((string category, List<DiscoveryItem> items) in Categories)
        {
            if (!fresh.Categories.TryGetValue(category, out List<DiscoveryItem>? freshItems))
            {
                continue;
            }
            Dictionary<string, DiscoveryItem> byName = [];
            foreach (DiscoveryItem f in freshItems)
            {
                byName.TryAdd(f.Name, f);
            }
            foreach (DiscoveryItem item in items)
            {
                if (byName.TryGetValue(item.Name, out DiscoveryItem? f))
                {
                    Array.Copy(f.Marked, item.Marked, CharacterCount);
                }
            }
        }
    }

    // 這一列 C 欄（名稱）的文字
    private static string NameInRow(Row row, List<string> strings)
    {
        Cell? cell = row.Elements<Cell>().FirstOrDefault(c => c.CellReference?.Value is string r && ColumnOf(r) == NameColumn);
        return cell is null ? "" : GetText(cell, strings).Trim();
    }

    // 同一欄最近的一列有格式的儲存格，借它的格式（找不到時是 null，等於預設格式）
    private static UInt32Value? FindColumnStyle(Dictionary<uint, Row> rows, string column, uint rowIndex)
    {
        for (uint d = 1; d < 50; d++)
        {
            uint[] nearby = [rowIndex - d, rowIndex + d]; // 減過頭會變成很大的數，TryGetValue 找不到，自然跳過
            foreach (uint r in nearby)
            {
                if (r <= 1 || !rows.TryGetValue(r, out Row? row))
                {
                    continue; // 第 1 列是標題，不借
                }
                Cell? cell = row.Elements<Cell>().FirstOrDefault(c => c.CellReference?.Value is string rf && ColumnOf(rf) == column);
                if (cell?.StyleIndex is not null)
                {
                    return cell.StyleIndex.Value;
                }
            }
        }
        return null;
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
    private static Cell GetOrCreateCell(Row row, string reference, out bool created)
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
                created = false;
                return cell;
            }
            if (n > target)
            {
                after = cell;
                break;
            }
        }
        Cell newCell = new() { CellReference = reference };
        if (after is null)
        {
            row.AppendChild(newCell);
        }
        else
        {
            row.InsertBefore(newCell, after);
        }
        created = true;
        return newCell;
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

/// <summary>寫入結果：備份檔、實際打勾的、因為列號對不上名稱而跳過的。</summary>
public sealed record WriteResult(string BackupPath, IReadOnlyList<DiscoveryItem> Written, IReadOnlyList<DiscoveryItem> Skipped);

/// <summary>
/// 寫入 Excel 失敗。BackupPath 是已經做好的備份（還沒做好時是 ""）。
/// FileMayBeModified：存檔途中才出錯，原檔可能已經寫了一部分（可能損壞），要用備份還原；
/// false 時是存檔之前就出錯，原檔沒有被改動。
/// </summary>
public sealed class ExcelWriteException(string message, string backupPath, bool fileMayBeModified, Exception inner)
    : IOException(message, inner)
{
    public string BackupPath { get; } = backupPath;

    public bool FileMayBeModified { get; } = fileMayBeModified;
}
