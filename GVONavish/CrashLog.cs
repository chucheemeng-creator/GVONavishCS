// CrashLog.cs
// 程式出錯時留下紀錄（crash.log，放在程式資料夾），使用者回報問題時可以附上這個檔案。
// 寫紀錄本身絕對不能再出錯：任何失敗都吞掉。檔案超過上限時換成 crash.log.old（只留一份舊的）。

using System;
using System.IO;
using System.Text;
using System.Threading;

namespace GVONavish;

public static class CrashLog
{
    /// <summary>紀錄檔的位置。預設是程式資料夾的 crash.log（測試時可以改到別處）。</summary>
    public static string FilePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "crash.log");

    /// <summary>單一檔案的大小上限（位元組）。</summary>
    public const long MaxBytes = 512 * 1024;

    private static readonly Lock Gate = new();

    /// <summary>加一筆紀錄。ex 是 null 時只寫說明。這個方法不會丟出例外。</summary>
    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                FileInfo file = new(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }
                string version = typeof(CrashLog).Assembly.GetName().Version?.ToString(3) ?? "?";
                string text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{version} {message}{Environment.NewLine}" +
                              (ex is null ? "" : ex + Environment.NewLine) + Environment.NewLine;
                File.AppendAllText(FilePath, text, Encoding.UTF8);
            }
        }
        catch
        {
            // 紀錄寫不進去（唯讀資料夾、磁碟滿了…）就算了
        }
    }
}
