using System;
using System.Threading;
using System.Windows.Forms;
using GVONavish.UI;

namespace GVONavish;

internal static class Program
{
    /// <summary>這台電腦能不能用 WinForms 的深色模式（Windows 11，版本號 10.0.22000 以後）。</summary>
    internal static bool SupportsDarkMode => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    [STAThread]
    private static void Main()
    {
        // 一定要開 DPI 感知：Windows 顯示縮放不是 100% 時，
        // 否則截圖座標會被系統縮放，截到錯的位置。
        // 這幾行要在顯示任何視窗（包括下面的訊息框）之前執行。
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // 設定檔只讀一次，交給主視窗用（讀兩次的話，第一次發現檔案壞掉、改名保留後，
        // 第二次就讀不到原檔，也就無法告訴使用者發生了什麼事）
        AppSettings settings = AppSettings.Load();

        // 淺色／深色／跟隨 Windows（.NET 10 起正式支援）。也要在建立任何視窗之前設定
        // WinForms 的深色模式只支援 Windows 11 以後（高對比模式下 Windows 也會自動不用深色），
        // Windows 10 上一律用淺色
        SystemColorMode mode = SupportsDarkMode && Enum.TryParse(settings.ColorMode, out SystemColorMode m)
            ? m : SystemColorMode.Classic;
        Application.SetColorMode(mode);

        // 只允許開一個（和原版一樣）
        using Mutex mutex = new(false, @"Global\GVONavishCS_7554E265");
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // 前一個程式拿著 Mutex 就被強制結束了。接到這個例外時，Mutex 已經歸我們，照常執行。
            // （一般情況程式結束後 Windows 會直接刪掉 Mutex，只有剛好有別的程式還開著它時才會遇到）
            owned = true;
        }
        if (!owned)
        {
            MessageBox.Show("GVONavish 已經在執行了。\n\n如果看不到視窗，請看看工作列，或是用工作管理員結束舊的程式再開一次。",
                "GVONavish", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Application.Run(new MainForm(settings));
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
