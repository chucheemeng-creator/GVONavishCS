// AppFolder.cs
// 設定檔、學過的字形、對照表放哪裡：預設放在程式旁邊（和以前一樣，也方便直接編輯對照表）；
// 程式裝在不能寫入的地方（例如 Program Files）時，改放在 %LocalAppData%\GVODiscoverySync，
// 不然設定、學到的字形存不進去，每次開啟都從頭來。

using System;
using System.IO;

namespace GVODiscoverySync;

public sealed class AppFolder(string primary, string fallback)
{
    /// <summary>程式用的資料夾：程式旁邊，不能寫入時是 %LocalAppData%\GVODiscoverySync。</summary>
    public static AppFolder Default { get; } = new(AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GVODiscoverySync"));

    // 第一次用到時試著寫一個檔案確認（之後不再試）
    private bool? _primaryWritable;

    /// <summary>程式旁邊不能寫入，改用備用的資料夾。</summary>
    public bool UsingFallback => !PrimaryWritable;

    /// <summary>目前使用的資料夾。</summary>
    public string FolderPath => PrimaryWritable ? primary : fallback;

    /// <summary>資料檔 fileName 的完整路徑（備用資料夾會在這時候建立）。</summary>
    public string PathOf(string fileName) => Path.Combine(FolderPath, fileName);

    private bool PrimaryWritable => _primaryWritable ??= CanWrite(primary) || !PrepareFallback();

    private bool PrepareFallback()
    {
        try
        {
            Directory.CreateDirectory(fallback);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // 備用的也建不起來：還是用程式旁邊的，存檔失敗時由各處處理
        }
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, ".write-test-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
