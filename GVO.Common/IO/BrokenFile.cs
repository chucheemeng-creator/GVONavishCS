// BrokenFile.cs
// 設定檔、航線檔讀不出來時的處理。
//
// 讀不出來就當成空的繼續用，程式關掉時會存檔，原本的檔案就被空的蓋掉了。
// 檔案可能只是格式有點問題、或暫時被其他程式（例如同步軟體）鎖住，裡面的資料還救得回來，
// 所以先把原檔改名保留，再從空的開始；連改名都做不到時，這次就不要存檔，以免蓋掉原檔。

using System;
using System.IO;

namespace GVO.Common.IO;

public static class BrokenFile
{
    /// <summary>
    /// 把讀不出來的檔案改名為「原檔名.broken-日期時間」。
    /// 成功時回傳新檔名；失敗（例如檔案被鎖住）回傳 null，這時呼叫端不可以覆蓋原檔。
    /// </summary>
    public static string? Preserve(string path)
    {
        try
        {
            string backup = $"{path}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, backup);
            return backup;
        }
        catch
        {
            return null;
        }
    }
}
