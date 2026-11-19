// DataFileTests.cs
// 設定檔、字形、對照表的存取：資料夾不能寫入時的備援、檔案壞掉或被鎖住時不能蓋掉使用者的檔案、存檔失敗不要一直重試。

using System;
using System.IO;
using System.Linq;
using GVODiscoverySync.Ocr;
using GVODiscoverySync.Recognition;
using Xunit;

namespace GVODiscoverySync.Tests;

public sealed class DataFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GVODiscoverySyncTests-" + Guid.NewGuid().ToString("N"));

    public DataFileTests()
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

    // 不能寫入的資料夾：它的上一層是一個檔案，建不起來
    private AppFolder Unwritable(string fallbackName = "fallback")
    {
        string file = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(file, "x");
        return new AppFolder(Path.Combine(file, "app"), Path.Combine(_dir, fallbackName));
    }

    // ── AppFolder ──

    [Fact]
    public void 程式旁邊能寫入_就放在程式旁邊()
    {
        string primary = Path.Combine(_dir, "app");
        AppFolder folder = new(primary, Path.Combine(_dir, "fallback"));

        Assert.False(folder.UsingFallback);
        Assert.Equal(Path.Combine(primary, "a.json"), folder.PathOf("a.json"));
        Assert.Empty(Directory.GetFileSystemEntries(primary)); // 試寫的檔案不留
        Assert.False(Directory.Exists(Path.Combine(_dir, "fallback")));
    }

    [Fact]
    public void 程式旁邊不能寫入_改放備用的資料夾()
    {
        AppFolder folder = Unwritable();

        Assert.True(folder.UsingFallback);
        Assert.Equal(Path.Combine(_dir, "fallback", "a.json"), folder.PathOf("a.json"));
        Assert.True(Directory.Exists(Path.Combine(_dir, "fallback")));
    }

    // ── AppSettings ──

    [Fact]
    public void 設定存檔再讀回來()
    {
        string path = Path.Combine(_dir, "s.json");
        AppSettings settings = AppSettings.Load(path);
        Assert.Null(settings.LoadProblem);
        settings.Character = 3;
        settings.ExcelPath = @"C:\a.xlsx";
        settings.Save();

        AppSettings again = AppSettings.Load(path);
        Assert.Equal(3, again.Character);
        Assert.Equal(@"C:\a.xlsx", again.ExcelPath);
    }

    [Fact]
    public void 設定的數值超出範圍時拉回範圍內()
    {
        string path = Path.Combine(_dir, "s.json");
        File.WriteAllText(path, """{ "Character": 99, "IntervalMs": 1, "OcrScale": 100 }""");

        AppSettings settings = AppSettings.Load(path);

        Assert.Equal(4, settings.Character);
        Assert.Equal(100, settings.IntervalMs);
        Assert.Equal(6, settings.OcrScale);
    }

    [Theory]
    [InlineData("""{ "Layout": { "RowCount": 100000 } }""")] // 列數太大
    [InlineData("""{ "Layout": { "RowHeight": 5000 } }""")] // 列高太大
    [InlineData("""{ "Layout": { "ListWidth": 0 } }""")] // 清單寬度是 0
    [InlineData("""{ "Layout": { "TitleHeight": -5 } }""")] // 標題高度是負的
    public void 版面數值不合理時整組用預設值(string json)
    {
        string path = Path.Combine(_dir, "s.json");
        File.WriteAllText(path, json);

        AppSettings settings = AppSettings.Load(path);

        Assert.True(settings.Layout.IsValid);
        Assert.Equal(new ListLayout().RowCount, settings.Layout.RowCount);
        Assert.Equal(new ListLayout().ListWidth, settings.Layout.ListWidth);
    }

    [Fact]
    public void 版面數值合理時照設定檔()
    {
        string path = Path.Combine(_dir, "s.json");
        File.WriteAllText(path, """{ "Layout": { "RowCount": 12, "ListX": -300 } }""");

        AppSettings settings = AppSettings.Load(path);

        Assert.Equal(12, settings.Layout.RowCount);
        Assert.Equal(-300, settings.Layout.ListX);
    }

    [Fact]
    public void 設定檔內容壞了_改名保留_用預設值_之後可以存檔()
    {
        string path = Path.Combine(_dir, "s.json");
        File.WriteAllText(path, "{ 壞掉了");

        AppSettings settings = AppSettings.Load(path);

        Assert.NotNull(settings.LoadProblem);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_dir, "s.json.broken-*")); // 原檔還在，只是改了名
        settings.Save();
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void 設定檔被鎖住_不改名_這次也不存檔_原檔不動()
    {
        string path = Path.Combine(_dir, "s.json");
        File.WriteAllText(path, """{ "Character": 2 }""");

        AppSettings settings;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            settings = AppSettings.Load(path); // 讀的時候被鎖住（IOException，不是內容壞掉）
        }

        Assert.NotNull(settings.LoadProblem);
        Assert.Empty(Directory.GetFiles(_dir, "s.json.broken-*"));
        settings.Character = 4;
        settings.Save(); // 存檔被停用：不能用預設值蓋掉原檔
        Assert.Equal("""{ "Character": 2 }""", File.ReadAllText(path));
    }

    [Fact]
    public void 設定存不了_不丟例外()
    {
        string path = Path.Combine(_dir, "no-such-folder", "s.json");
        AppSettings settings = AppSettings.Load(path);
        settings.Save(); // 資料夾不存在：存不了，只是不影響使用
        Assert.False(File.Exists(path));
    }

    // ── 對照表 ──

    [Fact]
    public void 對照表_第一次建立檔案_格式有錯時繼續用原本的_改好自動換()
    {
        AppFolder folder = new(Path.Combine(_dir, "app"), Path.Combine(_dir, "fallback"));
        var (table, _, problem) = MappingFile.LoadConfusions(null, folder);
        Assert.Null(problem);
        Assert.Equal(Path.Combine(_dir, "app", "GVODiscoverySync.OcrConfusions.json"), table.FilePath);
        Assert.Equal(MappingFile.BuiltInConfusions.Count, table.Map.Count);

        File.WriteAllText(table.FilePath, """{ "甲": "乙", }""");
        File.SetLastWriteTimeUtc(table.FilePath, DateTime.UtcNow.AddMinutes(1));
        Assert.Contains("已更新", table.ReloadIfChanged());
        Assert.Equal("乙", table.Map["甲"]);
        Assert.Equal("鑽", table.Map["巑"]); // 檔案裡沒寫的內建對照照樣有

        File.WriteAllText(table.FilePath, "{ 壞掉了");
        File.SetLastWriteTimeUtc(table.FilePath, DateTime.UtcNow.AddMinutes(2));
        Assert.Contains("格式有錯", table.ReloadIfChanged());
        Assert.Equal("乙", table.Map["甲"]); // 繼續用原本的
        Assert.Null(table.ReloadIfChanged()); // 同一個錯誤不重複報
    }

    [Fact]
    public void 對照表_程式旁邊不能寫入_檔案建在備用的資料夾()
    {
        var (table, _) = MappingFile.LoadNameAliases(Unwritable());

        Assert.Equal(Path.Combine(_dir, "fallback", "GVODiscoverySync.NameAliases.json"), table.FilePath);
        Assert.True(File.Exists(table.FilePath));
        Assert.Equal("關島", table.Map["關島(港)"]);
    }

    // ── 字形表 ──

    private static GlyphStore StoreWithNewGlyph()
    {
        GlyphStore store = GlyphStore.Load(Path.Combine(Path.GetTempPath(), "does-not-exist.json"), includeBuiltIn: false);
        ushort[] rows = new ushort[20];
        rows[5] = 0xFFFF;
        store.Learn("天", [new Glyph(rows)]);
        Assert.True(store.Dirty);
        return store;
    }

    [Fact]
    public void 字形存檔失敗_30秒內不重試_關閉程式時強制再試()
    {
        string folder = Path.Combine(_dir, "later");
        string path = Path.Combine(folder, "glyphs.json");
        GlyphStore store = StoreWithNewGlyph();

        store.SaveIfDirty(path); // 資料夾不存在：存不了
        Assert.True(store.Dirty);
        Assert.NotNull(store.SaveError);

        Directory.CreateDirectory(folder);
        store.SaveIfDirty(path); // 剛失敗過：這次不試
        Assert.False(File.Exists(path));
        Assert.True(store.Dirty);

        store.SaveIfDirty(path, force: true);
        Assert.True(File.Exists(path));
        Assert.False(store.Dirty);
        Assert.Null(store.SaveError);
        Assert.Equal("天", string.Join("", GlyphStore.Load(path, includeBuiltIn: false).Read([new Glyph(Enumerable.Range(0, 20).Select(i => (ushort)(i == 5 ? 0xFFFF : 0)).ToArray())])));
    }

    [Fact]
    public void 字形檔壞了_只用內建的_不丟例外()
    {
        string path = Path.Combine(_dir, "glyphs.json");
        File.WriteAllText(path, "{ 壞掉了");

        GlyphStore store = GlyphStore.Load(path);

        Assert.True(store.Count > 1000); // 內建的字形表
    }
}
