// GameWindowCapture.cs
// 從遊戲視窗右下角截下「測量座標」那一小塊並讀出座標。
// 對應 C++ 原版 GVOGameProcess.cpp 的 FindWindow、grabImage、extractGameIcon。
// 找視窗、取得顯示縮放倍率、截圖、取得遊戲小圖示這些和座標無關的部分，在 GVO.Common 的 GameWindow。
//
// 顯示縮放（例如筆電的 150%）：遊戲不懂顯示縮放，Windows 會把整個遊戲畫面拉大。
// 這時截圖範圍跟著放大，辨識交給 ScaledCoordReader；截圖位置的設定值一律是「遊戲原本的像素」，
// 所以預設的 70、273 在任何縮放下都不用改。
//
// 遊戲多開（最多 3 開）：
//   - 還沒鎖定時，讀「最前面的」遊戲視窗；讀到有效座標，就鎖定那個視窗。
//   - 鎖定後一直讀那個視窗，直到一段時間（UnlockAfterMs）讀不到座標，才解除鎖定、回到讀最前面的。
//   - 多開的視窗常常疊在同一個位置：鎖定的視窗被另一個遊戲視窗蓋住時，螢幕上那塊顯示的是別的角色的座標，
//     不能算成鎖定的角色，所以當成讀不到。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using GVO.Common.Capture;

namespace GVONavish.Core;

public sealed class GameWindowCapture : IDisposable
{
    public const string DefaultWindowClassName = GameWindow.DefaultWindowClassName;

    private readonly GameWindow _window = new();

    /// <summary>遊戲視窗的類別名稱。原版還會比對標題，這裡只比對類別，各伺服器標題不同也找得到。</summary>
    public string WindowClassName
    {
        get => _window.WindowClassName;
        set => _window.WindowClassName = value;
    }

    /// <summary>截圖左上角距離遊戲畫面「右下角」的距離（遊戲原本的像素，不受顯示縮放影響）。原版是 (70, 273)。</summary>
    public Point OffsetFromRightBottom { get; set; } = new(70, 273);

    /// <summary>截圖大小。原版是 60 × 11，高度必須維持 11。</summary>
    public Size CaptureSize { get; set; } = new(60, SurveyCoordExtractor.DigitHeight);

    public IntPtr WindowHandle => _window.WindowHandle;

    /// <summary>上一次截圖時，遊戲畫面被 Windows 放大的倍率（1 表示沒有放大）。</summary>
    public double Scale { get; private set; } = 1;

    /// <summary>上一次截圖是放大模式（截圖比 60 × 11 大，要用 ScaledCoordReader 讀）。</summary>
    public bool IsScaled => Scale > 1.01;

    /// <summary>鎖定的視窗連續這麼久（毫秒）讀不到座標，就解除鎖定，改讀最前面的遊戲視窗。</summary>
    public long UnlockAfterMs { get; set; } = 3000;

    /// <summary>上一次找視窗時，看得到幾個遊戲視窗（多開時 &gt; 1）。</summary>
    public int GameWindowCount { get; private set; }

    /// <summary>目前鎖定在某一個遊戲視窗上（讀到過座標，而且還沒有一段時間讀不到）。</summary>
    public bool IsLocked => _lock.IsLocked;

    /// <summary>
    /// 鎖定的視窗換成另一個（多開時換了角色）。在 CaptureAndRead 回傳新視窗的座標之前觸發，
    /// 呼叫端可以趁這時重設航向、船速、航線，不要把兩個角色的位置連成一條線。
    /// </summary>
    public event EventHandler? TargetChanged;

    private readonly TargetLock _lock = new(); // 多開時讀哪一個視窗的規則
    private Rectangle _lastCaptureRect; // 上一次截圖的位置（螢幕像素），檢查是不是被其他遊戲視窗蓋住

    private Bitmap? _buffer;
    // 放大模式：遊戲原本那塊區域的左上角，在截圖裡的位置（螢幕像素，可以有小數）
    private double _originX, _originY;
    // 放大模式：截圖外圍多截的邊（對齊位置會在 ±0.5 像素內試，取樣範圍要留空間）
    private const int ScaledPadding = 3;

    /// <summary>
    /// 決定這次要讀哪一個遊戲視窗：有鎖定就用鎖定的，沒有就用最前面的（縮到最小的跳過）。找不到任何遊戲視窗時回傳 false。
    /// </summary>
    public bool FindGameWindow()
    {
        List<IntPtr> all = GameWindow.FindAllGameWindows(WindowClassName);
        GameWindowCount = all.Count;
        IntPtr target = _lock.Choose(all, GameWindow.IsMinimized);
        if (target == IntPtr.Zero)
        {
            return false;
        }
        _window.SetTarget(target);
        return true;
    }

    /// <summary>
    /// 一開始就鎖定指定的視窗（例如讀座標設定視窗要看主視窗正在讀的那一個）。IntPtr.Zero 表示不鎖定。
    /// </summary>
    public void LockTo(IntPtr hwnd) => _lock.LockTo(hwnd, Environment.TickCount64);

    /// <summary>取得遊戲視窗目前的標題（顯示用）。</summary>
    public string GetWindowTitle() => _window.GetWindowTitle();

    /// <summary>
    /// 截下座標區域。回傳的 Bitmap 由這個類別持有並重複使用，呼叫端不要 Dispose，
    /// 要保留的話請自己 Clone()。失敗時回傳 null。
    /// </summary>
    public Bitmap? Capture()
    {
        if (!_window.TryGetClientArea(out Rectangle client, out double scale))
        {
            return null;
        }

        int right = client.Right;
        int bottom = client.Bottom;
        Scale = scale;

        int srcX, srcY;
        Size size;
        if (IsScaled)
        {
            // 遊戲原本的位置換算成螢幕像素（可能有小數，例如 273 × 1.5 = 409.5），外圍多截一點
            double x0 = right - OffsetFromRightBottom.X * Scale;
            double y0 = bottom - OffsetFromRightBottom.Y * Scale;
            srcX = (int)Math.Floor(x0) - ScaledPadding;
            srcY = (int)Math.Floor(y0) - ScaledPadding;
            _originX = x0 - srcX;
            _originY = y0 - srcY;
            size = new Size((int)Math.Ceiling(CaptureSize.Width * Scale) + ScaledPadding * 2 + 1,
                            (int)Math.Ceiling(CaptureSize.Height * Scale) + ScaledPadding * 2 + 1);
        }
        else
        {
            srcX = right - OffsetFromRightBottom.X;
            srcY = bottom - OffsetFromRightBottom.Y;
            size = CaptureSize;
        }

        if (_buffer is null || _buffer.Size != size)
        {
            _buffer?.Dispose();
            _buffer = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        }

        _lastCaptureRect = new Rectangle(srcX, srcY, size.Width, size.Height);

        // 和原版一樣，從桌面畫面直接複製（遊戲視窗必須是可見的、沒被擋住）；截不到時下一次再試
        return GameWindow.CopyFromScreen(_buffer, srcX, srcY) ? _buffer : null;
    }

    /// <summary>
    /// 截一塊任意大小的區域（另外產生的 Bitmap，用完請 Dispose）。失敗時回傳 null。
    /// fromRight、fromBottom 是區域左上角距離遊戲畫面右下角的距離（和 OffsetFromRightBottom 一樣的算法）。
    /// 給「存下截圖」用：截大一點，問題回報時看得到完整的數字和周圍。
    /// </summary>
    public Bitmap? CaptureRegion(int fromRight, int fromBottom, int width, int height)
        => _window.CaptureRegion(fromRight, fromBottom, width, height);

    /// <summary>
    /// 截圖並讀座標，同時管理多開時的鎖定。成功時回傳座標，失敗回傳 null。
    /// 呼叫前先呼叫 FindGameWindow 決定要讀哪一個視窗。
    /// </summary>
    public WorldPoint? CaptureAndRead()
    {
        WorldPoint? p = ReadTarget();
        // 讀到有效座標就鎖定這個視窗；換成另一個視窗（換了角色）時通知呼叫端
        if (_lock.OnRead(_window.WindowHandle, p.HasValue, Environment.TickCount64, UnlockAfterMs))
        {
            TargetChanged?.Invoke(this, EventArgs.Empty);
        }
        return p;
    }

    private WorldPoint? ReadTarget()
    {
        Bitmap? bmp = Capture();
        if (bmp is null || IsCoveredByAnotherGameWindow())
        {
            return null;
        }
        return TryReadCaptured(bmp, out int x, out int y) ? new WorldPoint(x, y) : null;
    }

    /// <summary>
    /// 截圖的那塊是不是被「另一個」遊戲視窗蓋住（多開時視窗常疊在一起）。
    /// 只在多開時檢查，而且只認遊戲視窗：被其他程式蓋住時，截到的不是數字，本來就讀不到。
    /// 檢查左、中、右三個點。
    /// </summary>
    private bool IsCoveredByAnotherGameWindow()
    {
        if (GameWindowCount < 2)
        {
            return false;
        }
        IntPtr target = _window.WindowHandle;
        Rectangle r = _lastCaptureRect;
        int midY = r.Top + r.Height / 2;
        int[] xs = [r.Left + 1, r.Left + r.Width / 2, r.Right - 2];
        foreach (int x in xs)
        {
            IntPtr top = GameWindow.TopLevelWindowAt(new Point(x, midY));
            if (top != target && GameWindow.IsGameWindow(top, WindowClassName))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>讀 Capture() 截到的圖（依截圖時是不是放大模式，用對應的方法讀）。</summary>
    public bool TryReadCaptured(Bitmap captured, out int x, out int y)
    {
        if (!IsScaled)
        {
            return TryReadCoord(captured, out x, out y);
        }
        return ScaledCoordReader.TryRead(ToGray(captured), captured.Width, captured.Height, Scale,
            _originX, _originY, out x, out y, out _);
    }

    /// <summary>
    /// 讀 Capture() 截到的圖，同時產生黑白預覽（遊戲原本的 60 × 11，白色＝當成數字的點）。給讀座標設定視窗用。
    /// 放大模式時，預覽是縮回原本大小、而且是實際採用的對齊位置；讀和預覽只算一次。
    /// </summary>
    public bool TryReadWithPreview(Bitmap captured, out int x, out int y, out Bitmap preview)
    {
        if (!IsScaled)
        {
            preview = MakeBinarizedPreview(captured);
            return TryReadCoord(captured, out x, out y);
        }
        bool ok = ScaledCoordReader.TryRead(ToGray(captured), captured.Width, captured.Height, Scale,
            _originX, _originY, out x, out y, out float[] v);
        int w = CaptureSize.Width, h = CaptureSize.Height;
        preview = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        for (int py = 0; py < h; py++)
        {
            for (int px = 0; px < w; px++)
            {
                // 和辨識用同一個門檻，預覽上白色的點就是辨識時算成筆畫的點
                preview.SetPixel(px, py, v[py * w + px] >= ScaledCoordReader.OnMin ? Color.White : Color.Black);
            }
        }
        return ok;
    }

    // 每個像素的亮度（R、G、B 的平均，0～255）
    private static float[] ToGray(Bitmap bitmap)
    {
        byte[] bytes = GetPixelBytes(bitmap, out int stride);
        float[] gray = new float[bitmap.Width * bitmap.Height];
        for (int py = 0; py < bitmap.Height; py++)
        {
            for (int px = 0; px < bitmap.Width; px++)
            {
                int o = py * stride + px * 4;
                gray[py * bitmap.Width + px] = (bytes[o] + bytes[o + 1] + bytes[o + 2]) / 3f;
            }
        }
        return gray;
    }

    /// <summary>
    /// 取得遊戲視窗的小圖示（地圖上當作船的記號；複本，用完請 Dispose）。取不到時回傳 null。
    /// </summary>
    public Bitmap? GetGameIcon() => _window.GetGameIcon();

    /// <summary>從 Bitmap 讀座標（截圖或讀檔都可以用）。</summary>
    public static bool TryReadCoord(Bitmap bitmap, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (bitmap.Height != SurveyCoordExtractor.DigitHeight)
        {
            return false;
        }
        byte[] bytes = GetPixelBytes(bitmap, out int stride);
        return SurveyCoordExtractor.TryReadCoord(bytes, bitmap.Width, bitmap.Height, stride, 4, out x, out y);
    }

    /// <summary>產生黑白預覽圖（白字 = 白，其他 = 黑），方便確認截圖位置對不對。</summary>
    public static Bitmap MakeBinarizedPreview(Bitmap source)
    {
        Bitmap result = new(source.Width, source.Height, PixelFormat.Format32bppArgb);
        byte[] bytes = GetPixelBytes(source, out int stride);
        for (int py = 0; py < source.Height; py++)
        {
            for (int px = 0; px < source.Width; px++)
            {
                bool white = SurveyCoordExtractor.IsWhite(bytes, px, py, stride, 4);
                result.SetPixel(px, py, white ? Color.White : Color.Black);
            }
        }
        return result;
    }

    private static byte[] GetPixelBytes(Bitmap bitmap, out int stride)
    {
        Rectangle rect = new(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            stride = data.Stride;
            int length = Math.Abs(data.Stride) * data.Height;
            var bytes = new byte[length];
            Marshal.Copy(data.Scan0, bytes, 0, length);
            return bytes;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        _buffer?.Dispose();
        _buffer = null;
    }
}
