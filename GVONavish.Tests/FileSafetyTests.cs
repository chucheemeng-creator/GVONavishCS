// FileSafetyTests.cs
// GVO.Common 的安全存檔（AtomicFile）和壞檔保留（BrokenFile）。

using System;
using System.IO;
using System.Linq;
using GVO.Common.IO;
using Xunit;

namespace GVONavish.Tests;

public class FileSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gvonavish-files-" + Guid.NewGuid().ToString("N"));

    public FileSafetyTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    [Fact]
    public void 安全存檔_檔案還不存在_建立新檔()
    {
        AtomicFile.WriteAllText(PathOf("a.txt"), "hello");
        Assert.Equal("hello", File.ReadAllText(PathOf("a.txt")));
        Assert.False(File.Exists(PathOf("a.txt.tmp")));
    }

    [Fact]
    public void 安全存檔_已經有檔案_換成新內容()
    {
        File.WriteAllText(PathOf("a.txt"), "old");
        AtomicFile.WriteAllText(PathOf("a.txt"), "new");
        Assert.Equal("new", File.ReadAllText(PathOf("a.txt")));
        Assert.False(File.Exists(PathOf("a.txt.tmp")));
    }

    [Fact]
    public void 安全存檔_寫到一半失敗_原檔不變_暫存檔清掉()
    {
        File.WriteAllText(PathOf("a.txt"), "old");
        Assert.Throws<InvalidOperationException>(() => AtomicFile.Write(PathOf("a.txt"), tmp =>
        {
            File.WriteAllText(tmp, "half");
            throw new InvalidOperationException("寫到一半");
        }));
        Assert.Equal("old", File.ReadAllText(PathOf("a.txt")));
        Assert.False(File.Exists(PathOf("a.txt.tmp")));
    }

    [Fact]
    public void 壞檔保留_原檔改名_回傳新檔名()
    {
        File.WriteAllText(PathOf("s.json"), "broken");
        string? backup = BrokenFile.Preserve(PathOf("s.json"));
        Assert.NotNull(backup);
        Assert.False(File.Exists(PathOf("s.json")));
        Assert.Equal("broken", File.ReadAllText(backup!));
        Assert.Contains(".broken-", backup);
    }

    [Fact]
    public void 壞檔保留_連續兩次不會互相蓋掉()
    {
        File.WriteAllText(PathOf("s.json"), "first");
        string? a = BrokenFile.Preserve(PathOf("s.json"));
        File.WriteAllText(PathOf("s.json"), "second");
        string? b = BrokenFile.Preserve(PathOf("s.json"));

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(a, b);
        Assert.Equal("first", File.ReadAllText(a!));
        Assert.Equal("second", File.ReadAllText(b!));
        Assert.Equal(2, Directory.GetFiles(_dir, "s.json.broken-*").Count());
    }

    [Fact]
    public void 壞檔保留_檔案不存在_回傳null()
    {
        Assert.Null(BrokenFile.Preserve(PathOf("missing.json")));
    }
}
