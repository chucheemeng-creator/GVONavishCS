// StorageTests.cs
// routes.json、settings.json 的讀檔、存檔、壞檔處理。
// 這兩個檔案固定放在程式資料夾（測試的輸出資料夾），所以用同一個 Collection 讓這幾個測試依序執行，
// 而且每個測試前後都清掉。

using System;
using System.IO;
using System.Linq;
using GVONavish;
using GVONavish.Core;
using Xunit;

namespace GVONavish.Tests;

[CollectionDefinition("程式資料夾的檔案", DisableParallelization = true)]
public class BaseDirectoryCollection;

[Collection("程式資料夾的檔案")]
public class StorageTests : IDisposable
{
    private static string RoutesPath => RouteStore.FilePath;
    private static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public StorageTests()
    {
        Clean();
    }

    public void Dispose()
    {
        Clean();
    }

    private static void Clean()
    {
        foreach (string f in Directory.GetFiles(AppContext.BaseDirectory, "routes.json*")
                     .Concat(Directory.GetFiles(AppContext.BaseDirectory, "settings.json*")))
        {
            File.Delete(f);
        }
    }

    private static ShipRoute Route(string name, params (int X, int Y)[] points)
    {
        ShipRoute r = new() { Name = name };
        foreach ((int x, int y) in points) r.AddPoint(new WorldPoint(x, y));
        return r;
    }

    private static string[] BackupFiles(string prefix) =>
        Directory.GetFiles(AppContext.BaseDirectory, prefix + ".broken-*");

    // ── routes.json ──

    [Fact]
    public void 航線_沒有檔案_空清單_沒有問題()
    {
        var routes = RouteStore.Load(out string? problem, out bool canSave);
        Assert.Empty(routes);
        Assert.Null(problem);
        Assert.True(canSave);
    }

    [Fact]
    public void 航線_存檔再讀回來()
    {
        ShipRoute a = Route("里斯本 → 倫敦", (100, 200), (110, 210), (16380, 300));
        a.Favorite = true;
        a.Hidden = true;
        Assert.True(RouteStore.Save([a, Route("", (1, 1), (2, 2))]));

        var routes = RouteStore.Load(out string? problem, out bool canSave);
        Assert.Null(problem);
        Assert.True(canSave);
        Assert.Equal(2, routes.Count);
        Assert.Equal("里斯本 → 倫敦", routes[0].Name);
        Assert.True(routes[0].Favorite);
        Assert.True(routes[0].Hidden);
        Assert.Equal([new WorldPoint(100, 200), new WorldPoint(110, 210), new WorldPoint(16380, 300)], routes[0].Lines[0]);
        Assert.True(routes.All(r => r.Closed)); // 讀進來的都是舊航線
        Assert.False(File.Exists(RoutesPath + ".tmp"));
    }

    [Fact]
    public void 航線_存成短格式_座標是_x_y_陣列()
    {
        RouteStore.Save([Route("", (5, 6), (7, 8))]);
        Assert.Contains("[5,6]", File.ReadAllText(RoutesPath));
    }

    [Theory]
    [InlineData("這不是 JSON")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Version\":2}")]
    [InlineData("{\"Version\":2,\"Routes\":[{\"Lines\":[[[\"a\",\"b\"]]]}]}")]
    public void 航線_壞檔_原檔改名保留_從空的開始(string content)
    {
        File.WriteAllText(RoutesPath, content);

        var routes = RouteStore.Load(out string? problem, out bool canSave);

        Assert.Empty(routes);
        Assert.NotNull(problem);
        Assert.True(canSave);
        Assert.False(File.Exists(RoutesPath));
        string[] backups = BackupFiles("routes.json");
        Assert.Single(backups);
        Assert.Equal(content, File.ReadAllText(backups[0]));
    }

    [Fact]
    public void 航線_較新版本的檔案_照樣顯示_但不存檔()
    {
        RouteStore.Save([Route("新", (1, 1), (5, 5))]);
        string json = File.ReadAllText(RoutesPath).Replace("\"Version\":2", "\"Version\":99");
        File.WriteAllText(RoutesPath, json);

        var routes = RouteStore.Load(out string? problem, out bool canSave);

        Assert.Single(routes);
        Assert.NotNull(problem);
        Assert.False(canSave);
        Assert.True(File.Exists(RoutesPath)); // 沒有被改名
    }

    [Fact]
    public void 航線_檔案裡的null和空線段_讀進來時清掉()
    {
        File.WriteAllText(RoutesPath,
            "{\"Version\":2,\"Routes\":[null," +
            "{\"Name\":null,\"Lines\":null}," +
            "{\"Lines\":[[],null,[[1,2],[3,4]]]}," +
            "{\"Lines\":[[]]}]}");

        var routes = RouteStore.Load(out string? problem, out _);

        Assert.Null(problem);
        ShipRoute only = Assert.Single(routes);
        Assert.Equal("", only.Name);
        Assert.Single(only.Lines);
        Assert.Equal([new WorldPoint(1, 2), new WorldPoint(3, 4)], only.Lines[0]);
    }

    // ── settings.json ──

    [Fact]
    public void 設定_沒有檔案_用預設值()
    {
        AppSettings s = AppSettings.Load();
        Assert.Null(s.LoadProblem);
        Assert.False(s.SaveDisabled);
        Assert.Equal(70, s.OffsetX);
        Assert.Equal(273, s.OffsetY);
        Assert.Equal(500, s.IntervalMs);
    }

    [Fact]
    public void 設定_存檔再讀回來()
    {
        AppSettings s = AppSettings.Load();
        s.OffsetX = 80;
        s.Scale = 2.0;
        s.Season = Season.Winter;
        s.RecentPorts = ["里斯本", "倫敦"];
        s.Save();
        Assert.False(s.LastSaveFailed);

        AppSettings loaded = AppSettings.Load();
        Assert.Null(loaded.LoadProblem);
        Assert.Equal(80, loaded.OffsetX);
        Assert.Equal(2.0, loaded.Scale);
        Assert.Equal(Season.Winter, loaded.Season);
        Assert.Equal(["里斯本", "倫敦"], loaded.RecentPorts);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void 設定_不合理的值和null_讀進來時補正()
    {
        File.WriteAllText(SettingsPath,
            "{\"OffsetX\":99999,\"OffsetY\":-5,\"IntervalMs\":1,\"Scale\":-1,\"Season\":7,\"SpeedCorner\":9," +
            "\"ColorMode\":\"Bogus\",\"MapFile\":null,\"PlanStart\":null,\"LastX\":-1," +
            "\"RecentPorts\":[\"a\",null,\"b\",\"c\",\"d\",\"e\",\"f\",\"g\",\"h\",\"i\",\"j\",\"k\"],\"RouteManagerColumns\":null}");

        AppSettings s = AppSettings.Load();

        Assert.Null(s.LoadProblem);
        Assert.Equal(5000, s.OffsetX);
        Assert.Equal(0, s.OffsetY);
        Assert.Equal(100, s.IntervalMs);
        Assert.Equal(1.0, s.Scale);
        Assert.Equal(Season.Summer, s.Season);
        Assert.Equal(GVONavish.UI.ScreenCorner.TopRight, s.SpeedCorner);
        Assert.Equal("Classic", s.ColorMode);
        Assert.Equal("map.png", s.MapFile);
        Assert.Equal("", s.PlanStart);
        Assert.Equal(15785, s.LastX);
        Assert.Equal(10, s.RecentPorts.Count);
        Assert.DoesNotContain(null!, s.RecentPorts);
        Assert.NotNull(s.RouteManagerColumns);
    }

    [Theory]
    [InlineData("這不是 JSON")]
    [InlineData("null")]
    public void 設定_壞檔_原檔改名保留_用預設值_還可以存檔(string content)
    {
        File.WriteAllText(SettingsPath, content);

        AppSettings s = AppSettings.Load();

        Assert.NotNull(s.LoadProblem);
        Assert.False(s.SaveDisabled);
        Assert.False(File.Exists(SettingsPath));
        string[] backups = BackupFiles("settings.json");
        Assert.Single(backups);
        Assert.Equal(content, File.ReadAllText(backups[0]));
    }

    [Fact]
    public void 設定_壞檔又改不了名_這次不存檔()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // 只有 Windows 的檔案鎖會讓改名失敗
        }
        File.WriteAllText(SettingsPath, "壞掉了");
        using FileStream locked = new(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);

        AppSettings s = AppSettings.Load();
        s.Save();

        Assert.NotNull(s.LoadProblem);
        Assert.True(s.SaveDisabled);
        locked.Dispose();
        Assert.Equal("壞掉了", File.ReadAllText(SettingsPath)); // 沒有被預設值蓋掉
    }
}

[Collection("程式資料夾的檔案")]
public class CrashLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gvonavish-crash-" + Guid.NewGuid().ToString("N"));
    private readonly string _original = CrashLog.FilePath;

    public CrashLogTests()
    {
        Directory.CreateDirectory(_dir);
        CrashLog.FilePath = Path.Combine(_dir, "crash.log");
    }

    public void Dispose()
    {
        CrashLog.FilePath = _original;
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void 寫紀錄_有說明和例外內容_一筆接著一筆()
    {
        CrashLog.Write("第一筆", new InvalidOperationException("壞掉了"));
        CrashLog.Write("第二筆");

        string text = File.ReadAllText(CrashLog.FilePath);
        Assert.Contains("第一筆", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("壞掉了", text);
        Assert.True(text.IndexOf("第一筆", StringComparison.Ordinal) < text.IndexOf("第二筆", StringComparison.Ordinal));
    }

    [Fact]
    public void 寫紀錄_資料夾不存在_不丟例外()
    {
        CrashLog.FilePath = Path.Combine(_dir, "不存在的資料夾", "crash.log");
        CrashLog.Write("寫不進去");
    }

    [Fact]
    public void 寫紀錄_超過上限_舊的換成old()
    {
        File.WriteAllBytes(CrashLog.FilePath, new byte[CrashLog.MaxBytes + 1]);
        CrashLog.Write("新的一筆");

        Assert.True(File.Exists(CrashLog.FilePath + ".old"));
        Assert.Contains("新的一筆", File.ReadAllText(CrashLog.FilePath));
        Assert.True(new FileInfo(CrashLog.FilePath).Length < CrashLog.MaxBytes);
    }
}
