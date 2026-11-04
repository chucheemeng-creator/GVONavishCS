using System;
using System.Windows.Forms;
using GVODiscoverySync.UI;

namespace GVODiscoverySync;

internal static class Program
{
    /// <summary>這台電腦能不能用 WinForms 的深色模式（Windows 11，版本號 10.0.22000 以後）。</summary>
    internal static bool SupportsDarkMode => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    [STAThread]
    private static void Main()
    {
        // 一定要開 DPI 感知：Windows 顯示縮放不是 100% 時，否則截圖座標會被系統縮放，截到錯的位置。
        // 這幾行要在顯示任何視窗之前執行。
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // 跟隨 Windows 的淺色／深色設定（深色只支援 Windows 11）
        Application.SetColorMode(SupportsDarkMode ? SystemColorMode.System : SystemColorMode.Classic);

        AppSettings settings = AppSettings.Load();
        Application.Run(new MainForm(settings));
    }
}
