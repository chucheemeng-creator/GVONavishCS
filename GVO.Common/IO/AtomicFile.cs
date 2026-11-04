// AtomicFile.cs
// 安全存檔：先寫到暫存檔，再換掉原檔。存到一半斷電或當機，原檔也不會壞掉。
// GVONavish 的 settings.json、routes.json、seamask.bin 都用這個；其他工具存檔也可以用。

using System;
using System.IO;

namespace GVO.Common.IO;

public static class AtomicFile
{
    /// <summary>
    /// 用 write 把內容寫到「path.tmp」，成功後換掉 path。失敗時丟出例外，原檔維持原樣。
    /// 不管成功或失敗，最後都會把暫存檔清掉（例如防毒軟體鎖住原檔、換檔失敗時，不會留下 .tmp）。
    /// </summary>
    public static void Write(string path, Action<string> write)
    {
        string tmp = path + ".tmp";
        try
        {
            write(tmp);
            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch
            {
                // 刪不掉就算了，下次存檔會覆蓋同一個暫存檔
            }
        }
    }

    public static void WriteAllText(string path, string text)
    {
        Write(path, tmp => File.WriteAllText(tmp, text));
    }
}
