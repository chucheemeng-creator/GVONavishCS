// GameWindow.cs
// 找到《大航海時代 Online》的遊戲視窗、取得遊戲畫面在螢幕上的位置和顯示縮放倍率、截圖、取得遊戲的小圖示。
// GVONavish（讀測量座標）和其他工具（例如發現物同步）共用這一層；要截哪一塊、怎麼辨識由各工具自己決定。
//
// 顯示縮放（例如筆電的 150%）：遊戲不懂顯示縮放，Windows 會把整個遊戲畫面拉大。
// TryGetClientArea 回傳的位置是螢幕實際像素，Scale 是放大倍率；
// 「遊戲原本的像素」× Scale ＝ 螢幕像素。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace GVO.Common.Capture;

public sealed class GameWindow
{
    // 原版用的遊戲視窗類別名稱（"Greate" 是遊戲本身的拼字，不是打錯）。
    public const string DefaultWindowClassName = "Greate Voyages Online Game MainFrame";

    /// <summary>遊戲視窗的類別名稱。原版還會比對標題，這裡只比對類別，各伺服器標題不同也找得到。</summary>
    public string WindowClassName { get; set; } = DefaultWindowClassName;

    public IntPtr WindowHandle { get; private set; }

    /// <summary>找遊戲視窗。找不到時回傳 false。</summary>
    public bool FindGameWindow()
    {
        if (WindowHandle != IntPtr.Zero && IsWindow(WindowHandle))
        {
            return true;
        }
        WindowHandle = FindWindow(WindowClassName, null);
        return WindowHandle != IntPtr.Zero;
    }

    /// <summary>
    /// 指定要用哪一個遊戲視窗（遊戲可以多開時，由呼叫端決定讀哪一個）。
    /// 之後 TryGetClientArea、截圖都針對這個視窗；它關掉時，FindGameWindow 會改找最前面的遊戲視窗。
    /// </summary>
    public void SetTarget(IntPtr hwnd)
    {
        WindowHandle = hwnd;
    }

    /// <summary>
    /// 找出所有看得到的遊戲視窗，由最前面排到最後面（Windows 的視窗前後順序）。
    /// 遊戲可以多開，FindGameWindow 只會找到其中一個；需要分辨的話用這個。
    /// </summary>
    public static List<IntPtr> FindAllGameWindows(string className = DefaultWindowClassName)
    {
        List<IntPtr> result = [];
        // EnumWindows 依前後順序列出所有最上層視窗，第一個就是最前面的
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetClassName(hwnd) == className)
            {
                result.Add(hwnd);
            }
            return true; // 繼續列舉
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>這個視窗是不是遊戲視窗（比對類別名稱）。</summary>
    public static bool IsGameWindow(IntPtr hwnd, string className = DefaultWindowClassName)
        => hwnd != IntPtr.Zero && GetClassName(hwnd) == className;

    /// <summary>視窗是否縮到最小（縮到最小時畫面上沒有東西可截）。</summary>
    public static bool IsMinimized(IntPtr hwnd) => IsIconic(hwnd);

    /// <summary>螢幕上這個點（螢幕實際像素）最上面的是哪一個最上層視窗；沒有時回傳 IntPtr.Zero。</summary>
    public static IntPtr TopLevelWindowAt(Point screen)
    {
        IntPtr hwnd = WindowFromPoint(new POINT { X = screen.X, Y = screen.Y });
        return hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_ROOT);
    }

    private static string GetClassName(IntPtr hwnd)
    {
        StringBuilder sb = new(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    /// <summary>取得遊戲視窗目前的標題（顯示用）。</summary>
    public string GetWindowTitle()
    {
        if (WindowHandle == IntPtr.Zero)
        {
            return "";
        }
        StringBuilder sb = new(256);
        GetWindowText(WindowHandle, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// 取得遊戲畫面（不含標題列和邊框）在螢幕上的位置和大小（螢幕實際像素），以及 Windows 把遊戲放大的倍率（1 表示沒有放大）。
    /// 找不到視窗、視窗縮到最小時回傳 false。
    /// </summary>
    public bool TryGetClientArea(out Rectangle screenRect, out double scale)
    {
        screenRect = Rectangle.Empty;
        scale = 1;
        if (!FindGameWindow())
        {
            return false;
        }
        if (IsIconic(WindowHandle))
        {
            return false; // 視窗縮到最小，畫面上沒有東西可截
        }

        POINT clientOrigin = new();
        if (!ClientToScreen(WindowHandle, ref clientOrigin) || !GetClientRect(WindowHandle, out RECT rc))
        {
            WindowHandle = IntPtr.Zero; // 視窗可能剛關掉，下次重新找
            return false;
        }

        screenRect = new Rectangle(clientOrigin.X, clientOrigin.Y, rc.Right, rc.Bottom);
        scale = GetGameScale(WindowHandle, clientOrigin, rc);
        return true;
    }

    /// <summary>
    /// 從桌面畫面複製一塊到 target（大小＝target 的大小），screenX、screenY 是螢幕像素。
    /// 和原版一樣直接複製桌面，遊戲視窗必須是可見的、沒被擋住。
    /// 螢幕鎖定（Win+L）、UAC 確認視窗、顯示器休眠時，桌面無法存取，這時回傳 false，下一次再試。
    /// </summary>
    public static bool CopyFromScreen(Bitmap target, int screenX, int screenY)
    {
        try
        {
            using Graphics g = Graphics.FromImage(target);
            g.CopyFromScreen(screenX, screenY, 0, 0, target.Size, CopyPixelOperation.SourceCopy);
            return true;
        }
        catch (ExternalException)
        {
            // Win32Exception（控制代碼無效）也屬於 ExternalException
            return false;
        }
    }

    /// <summary>
    /// 截一塊任意大小的區域（另外產生的 Bitmap，用完請 Dispose）。失敗時回傳 null。
    /// fromRight、fromBottom 是區域左上角距離遊戲畫面右下角的距離，width、height 是大小，全部是螢幕像素。
    /// </summary>
    public Bitmap? CaptureRegion(int fromRight, int fromBottom, int width, int height)
    {
        if (!TryGetClientArea(out Rectangle client, out _))
        {
            return null;
        }
        return CaptureScreen(client.Right - fromRight, client.Bottom - fromBottom, width, height);
    }

    /// <summary>截下整個遊戲畫面（螢幕實際像素，另外產生的 Bitmap，用完請 Dispose）。失敗時回傳 null。</summary>
    public Bitmap? CaptureClient()
    {
        if (!TryGetClientArea(out Rectangle client, out _) || client.Width <= 0 || client.Height <= 0)
        {
            return null;
        }
        return CaptureScreen(client.X, client.Y, client.Width, client.Height);
    }

    private static Bitmap? CaptureScreen(int screenX, int screenY, int width, int height)
    {
        Bitmap bmp = new(width, height, PixelFormat.Format32bppArgb);
        if (CopyFromScreen(bmp, screenX, screenY))
        {
            return bmp;
        }
        bmp.Dispose();
        return null;
    }

    /// <summary>
    /// 遊戲畫面被 Windows 放大的倍率：遊戲視窗所在螢幕的 DPI ÷ 遊戲自己用的 DPI。
    /// 遊戲不懂顯示縮放，所以它的 DPI 一直是 96；螢幕設 150% 時 DPI 是 144，倍率就是 1.5。
    /// 取不到（例如很舊的 Windows）時當成 1。
    /// </summary>
    /// <remarks>
    /// 做法：把遊戲畫面的左上角、右下角（螢幕上的實際像素）用遊戲視窗自己的換算方式轉成「遊戲以為的座標」，
    /// 兩者大小的比例就是 Windows 把遊戲放大的倍率。
    /// PhysicalToLogicalPointForPerMonitorDPI 不管呼叫端（我們是 PerMonitorV2）怎麼處理 DPI，
    /// 都依照 hWnd 那個視窗的設定換算，所以遊戲在哪個螢幕、我們在哪個螢幕都沒關係。
    /// （GetDpiForMonitor 文件寫明不該在 per-monitor DPI aware 的執行緒上使用，所以只當備案。）
    /// 呼叫端的程式一定要開 PerMonitorV2 DPI 感知，否則截圖座標會被系統縮放，截到錯的位置。
    /// </remarks>
    private static double GetGameScale(IntPtr hwnd, POINT clientOrigin, RECT client)
    {
        int w = client.Right, h = client.Bottom;
        if (w > 1 && h > 1)
        {
            try
            {
                POINT a = clientOrigin;
                POINT b = new() { X = clientOrigin.X + w - 1, Y = clientOrigin.Y + h - 1 }; // 最後一個像素，仍在視窗內
                if (PhysicalToLogicalPointForPerMonitorDPI(hwnd, ref a) && PhysicalToLogicalPointForPerMonitorDPI(hwnd, ref b))
                {
                    int lw = b.X - a.X, lh = b.Y - a.Y;
                    if (lw > 0 && lh > 0)
                    {
                        double sx = (double)(w - 1) / lw, sy = (double)(h - 1) / lh;
                        // 橫向、縱向的倍率應該一樣；差太多表示換算結果不可靠，改用備案
                        if (Math.Abs(sx - sy) < 0.02)
                        {
                            return Math.Round((sx + sy) / 2, 2); // 例如 1535/1023 = 1.5005 → 1.5
                        }
                    }
                }
            }
            catch (Exception)
            {
                // EntryPointNotFoundException（Windows 8.1 以前沒有這個 API）：用備案
            }
        }
        return GetGameScaleFromDpi(hwnd);
    }

    // 備案：遊戲所在螢幕的 DPI ÷ 遊戲視窗的 DPI
    private static double GetGameScaleFromDpi(IntPtr hwnd)
    {
        try
        {
            uint windowDpi = GetDpiForWindow(hwnd);
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (windowDpi == 0 || GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint monitorDpi, out _) != 0)
            {
                return 1;
            }
            return (double)monitorDpi / windowDpi;
        }
        catch (Exception)
        {
            return 1; // EntryPointNotFoundException、DllNotFoundException
        }
    }

    /// <summary>
    /// 取得遊戲視窗的小圖示（複本，用完請 Dispose）。取不到時回傳 null。
    /// </summary>
    public Bitmap? GetGameIcon()
    {
        if (!FindGameWindow())
        {
            return null;
        }
        IntPtr hIcon = GetClassLongPtrSafe(WindowHandle, GCLP_HICONSM);
        if (hIcon == IntPtr.Zero)
        {
            hIcon = GetClassLongPtrSafe(WindowHandle, GCLP_HICON);
        }
        if (hIcon == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            // Icon.FromHandle 不會接管這個圖示（不會把遊戲的圖示刪掉），ToBitmap 會產生獨立的複本
            using Icon icon = Icon.FromHandle(hIcon);
            return icon.ToBitmap();
        }
        catch
        {
            return null;
        }
    }

    #region Win32

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PhysicalToLogicalPointForPerMonitorDPI(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    // 64 位元程式用 GetClassLongPtr，32 位元程式只有 GetClassLong
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
    private static extern uint GetClassLong32(IntPtr hWnd, int nIndex);

    private static IntPtr GetClassLongPtrSafe(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetClassLongPtr64(hWnd, nIndex)
            : new IntPtr(unchecked((int)GetClassLong32(hWnd, nIndex)));
    }

    #endregion
}
