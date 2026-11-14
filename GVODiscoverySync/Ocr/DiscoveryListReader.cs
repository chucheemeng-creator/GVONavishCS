// DiscoveryListReader.cs
// 用 Windows 內建的文字辨識（Windows.Media.Ocr）讀遊戲「發現物一覽」畫面上的分類標題和清單。
//
// 不讀取、不修改遊戲記憶體，也不對遊戲送出按鍵或滑鼠操作；只看畫面（和 GVONavish 讀座標一樣）。
//
// 辨識前的處理：清單是白字（有些是青色字）、深藍底（選到的那一列是青綠底），視窗是半透明的。
// 放大到遊戲原本像素的 OcrScale 倍（預設 3），取 R、G、B 最亮的值，每一列各自找出底色（中位數），
// 把底色以下壓成 0、字拉到 255，再反轉成白底黑字，四周加白邊。
// 每一列各自找底色，筆畫密的字（鎮、茲、蘭）才不會被壓斷（v0.1.1 用固定門檻時常漏讀這些字）。
// v0.1.2 放大到 4 倍時分類標題完全讀不到，所以預設改回 3 倍；標題讀不到時再換倍率試。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace GVODiscoverySync.Ocr;

/// <summary>
/// 清單上的一列。Index 是畫面上的第幾列（0 起算）。
/// Readings 是用不同放大倍率讀到的結果（同一列換個倍率常常就讀對了，由呼叫端挑最像的）；Text 是第一個。
/// 這一列有字、但怎麼讀都讀不出來時（常見於一、兩個字的短名稱，例如「蠶」），Readings 是空的、Text 是 ""。
/// TextWidth 是這一列的字從畫面上量到的寬度（遊戲原本的像素，0 表示沒量到）。
/// 清單的字型每個中文字一樣寬，所以就算辨識漏了字，也看得出名稱原本有幾個字。
/// Glyphs 是這一列每一格（一個中文字寬）的字形，用來比對字形（見 Glyph）；讀不到時是 null。
/// GlyphsExact：字形是不是逐點精確的。沒有放大顯示（縮放 1）時是；放大顯示（150% 等）時畫面是糊的，
/// 每一格是「最像的已知字形」，不是百分之百可靠，只用來認出整列，不用來排除別的名稱、也不學。
/// </summary>
public sealed record ListRow(int Index, string Text, bool HasCard, IReadOnlyList<string> Readings, double TextWidth,
    IReadOnlyList<Glyph>? Glyphs = null, bool GlyphsExact = true);

/// <summary>一次辨識的結果。</summary>
public sealed class ListSnapshot
{
    public string TitleText { get; init; } = "";
    public List<ListRow> Rows { get; init; } = [];

    /// <summary>標題是用幾倍放大讀到的（給除錯用）。</summary>
    public double TitleScale { get; init; }

    /// <summary>這一頁每一列都是用字形讀出來的，沒有做文字辨識（標題也沒讀，分類由清單內容判斷）。</summary>
    public bool ByGlyphs { get; init; }
}

/// <summary>辨識前處理好的圖（BGRA，白底黑字，四周有白邊）。</summary>
public sealed class PreparedImage
{
    public required byte[] Pixels { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>四周白邊的寬度（像素）。</summary>
    public required int Padding { get; init; }

    /// <summary>遊戲原本的 1 像素，在這張圖裡是幾像素。</summary>
    public required double PixelsPerGamePixel { get; init; }

    public SoftwareBitmap ToSoftwareBitmap()
    {
        SoftwareBitmap result = new(BitmapPixelFormat.Bgra8, Width, Height, BitmapAlphaMode.Premultiplied);
        result.CopyFromBuffer(CryptographicBuffer.CreateFromByteArray(Pixels));
        return result;
    }

    /// <summary>轉成 Bitmap（存檔除錯用，用完請 Dispose）。</summary>
    public Bitmap ToBitmap()
    {
        Bitmap bmp = new(Width, Height, PixelFormat.Format32bppArgb);
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < Height; y++)
            {
                Marshal.Copy(Pixels, y * Width * 4, data.Scan0 + y * data.Stride, Width * 4);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }
}

public sealed class DiscoveryListReader
{
    // 辨識前四周加的白邊（遊戲原本的像素）
    private const int PaddingGamePixels = 6;

    // 亮度（R、G、B 最大值）比底色亮 BackgroundMargin 以上才開始算字，到 High 是全黑。
    // 實測：一般列的底色約 40～70、選到那一列的底色約 150、分類標題的底色約 130，字約 220～250
    private const int BackgroundMargin = 20;
    private const int High = 235;

    private readonly OcrEngine _engine;

    public ListLayout Layout { get; set; }

    /// <summary>辨識前放大成遊戲原本像素的幾倍（設定檔的 OcrScale）。</summary>
    public double OcrScale { get; set; } = 3;

    /// <summary>辨識用的語言（顯示用）。</summary>
    public string LanguageName => _engine.RecognizerLanguage.DisplayName;

    private DiscoveryListReader(OcrEngine engine, ListLayout layout)
    {
        _engine = engine;
        Layout = layout;
    }

    /// <summary>
    /// 建立讀取器。這台電腦沒有安裝繁體中文的文字辨識時回傳 null
    /// （Windows 設定 → 時間與語言 → 語言與地區 → 新增「中文（繁體，台灣）」，選項裡要有「光學字元辨識」）。
    /// </summary>
    public static DiscoveryListReader? TryCreate(ListLayout layout)
    {
        foreach (Language language in OcrEngine.AvailableRecognizerLanguages)
        {
            string tag = language.LanguageTag;
            if (tag.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)
                || tag.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase)
                || tag.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase))
            {
                OcrEngine? engine = OcrEngine.TryCreateFromLanguage(language);
                if (engine is not null)
                {
                    return new DiscoveryListReader(engine, layout);
                }
            }
        }
        return null;
    }

    /// <summary>這台電腦已安裝的文字辨識語言（找不到繁體中文時，顯示給使用者看）。</summary>
    public static string InstalledLanguages()
        => string.Join("、", OcrEngine.AvailableRecognizerLanguages.Select(l => l.DisplayName));

    // 亮度到這裡以上才算「字」（給判斷畫面有沒有變用）
    private const int TextThreshold = 200;

    /// <summary>
    /// 分類標題和清單上「字」的位置（每個像素 1＝字、0＝不是字），用來判斷捲動是不是停下來了、要不要重新辨識。
    /// 「發現物一覽」的視窗是半透明的，後面的城鎮畫面一直在動，所以不能直接比像素，只比字。
    /// client 是整個遊戲畫面的截圖（螢幕像素），scale 是 Windows 把遊戲放大的倍率。
    /// </summary>
    public byte[] TextMask(Bitmap client, double scale)
    {
        // 每 400ms 跑一次（150% 縮放時約 12 萬個點）：先算好總數，配置一次陣列，不用 List 一直擴充
        Rectangle[] rects = [TitleRect(client.Size, scale), ListRect(client.Size, scale)];
        int length = 0;
        foreach (Rectangle r in rects)
        {
            length += Math.Max(0, r.Width) * Math.Max(0, r.Height);
        }
        byte[] mask = new byte[length];
        int pos = 0;
        foreach (Rectangle r in rects)
        {
            if (r.Width <= 0 || r.Height <= 0)
            {
                continue;
            }
            byte[] bytes = GetPixelBytes(client, r, out int stride);
            for (int y = 0; y < r.Height; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < r.Width; x++)
                {
                    int o = rowOffset + x * 4;
                    int m = Math.Max(bytes[o], Math.Max(bytes[o + 1], bytes[o + 2]));
                    mask[pos++] = m >= TextThreshold ? (byte)1 : (byte)0;
                }
            }
        }
        return mask;
    }

    /// <summary>
    /// 兩次的 TextMask 算不算同一個畫面：不同的點不到 0.2% 就算一樣
    /// （字的邊緣會因為後面的背景變化，偶爾有幾個點不一樣）。
    /// </summary>
    public static bool SameText(byte[]? a, byte[]? b)
    {
        if (a is null || b is null || a.Length != b.Length)
        {
            return false;
        }
        int diff = 0;
        int limit = a.Length / 500 + 10;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i] && ++diff > limit)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 辨識分類標題和清單。acceptTitle 判斷讀到的標題能不能用（例如是不是 Excel 裡的分類）；
    /// 不能用時換別的放大倍率再讀一次標題。
    /// allConfident 判斷第一個倍率讀到的清單是不是每一列都確定對到了（參數是標題、每一列讀到的字和量到的字寬）；
    /// 是的話就不用再換倍率讀，省下兩次文字辨識。null 時一律用全部的倍率讀（除錯輸出用，看得到每種倍率的結果）。
    /// 有設定 GlyphText 時先用字形讀：每一列都讀得出來、allConfident 也確定時，完全不做文字辨識（標題是 ""）。
    /// </summary>
    public async Task<ListSnapshot> ReadAsync(Bitmap client, double scale, Func<string, bool>? acceptTitle = null,
        Func<string, IReadOnlyList<(string Text, double Width)>, bool>? allConfident = null)
    {
        Rectangle titleRect = TitleRect(client.Size, scale);
        Rectangle listRect = ListRect(client.Size, scale);
        if (titleRect.Width <= 0 || listRect.Width <= 0 || listRect.Height <= 0)
        {
            return new ListSnapshot(); // 遊戲畫面太小，對不到位置
        }

        // 字形優先：每一列的每個字都認得、而且都和 Excel 的名稱完全一樣時，這一頁不做文字辨識（快很多，也不會讀錯字）。
        // 有任何一列沒把握（有不認得的字、有英數字或括號、對不到名稱）就照原本的方式整頁做文字辨識。
        // 只在沒有放大顯示時用：放大顯示時的字形是「最像的」，不是精確的，要有文字辨識互相對照
        if (allConfident is not null && GlyphText is not null && Math.Abs(scale - 1) <= 0.001
            && ReadByGlyphs(client, Layout, scale, GlyphText, SnapGlyph) is List<ListRow> byGlyphs && byGlyphs.Count > 0
            && allConfident("", [.. byGlyphs.Select(r => (r.Text, r.TextWidth))]))
        {
            return new ListSnapshot { Rows = byGlyphs, ByGlyphs = true };
        }

        string title = "";
        double titleScale = 0;
        // 標題只有兩、三個字，Windows 的文字辨識常常讀不出來（換倍率也一樣），
        // 讀不到時由呼叫端從清單內容推測分類，所以這裡只多試一個倍率，不要拖慢每一次讀取
        double[] titleScales = [OcrScale, 2];
        foreach (double s in titleScales.Distinct())
        {
            PreparedImage titleImage = Prepare(client, titleRect, scale, s, 1);
            using SoftwareBitmap bitmap = titleImage.ToSoftwareBitmap();
            OcrResult result = await _engine.RecognizeAsync(bitmap);
            title = string.Concat(result.Lines.Select(JoinWords));
            titleScale = s;
            if (title != "" && (acceptTitle is null || acceptTitle(title)))
            {
                break;
            }
        }

        // 清單用幾種放大倍率各讀一次（OcrScale、少 0.5、多 0.5），每一列收集所有讀到的結果
        double[] listScales = [OcrScale, OcrScale - 0.5, OcrScale + 0.5];
        Dictionary<int, List<string>> readings = [];
        Dictionary<int, double> widths = []; // 每一列量到的字寬（量過的記下來，不重複量）
        double Width(int row)
        {
            if (!widths.TryGetValue(row, out double w))
            {
                w = MeasureTextWidth(client, RowRect(listRect, row, scale), scale);
                widths[row] = w;
            }
            return w;
        }
        bool first = true;
        foreach (double s in listScales.Where(x => x >= 1.5).Distinct())
        {
            Dictionary<int, string> pass = await ReadListAsync(client, listRect, scale, s);
            foreach ((int row, string text) in pass)
            {
                if (!readings.TryGetValue(row, out List<string>? list))
                {
                    list = [];
                    readings[row] = list;
                }
                if (!list.Contains(text))
                {
                    list.Add(text);
                }
            }
            // 第一個倍率就每一列都確定對到了：不用再換倍率讀
            if (first && allConfident is not null && readings.Count > 0 && NoMissingRows(client, listRect, scale, readings)
                && allConfident(title, [.. readings.OrderBy(p => p.Key).Select(p => (p.Value[0], Width(p.Key)))]))
            {
                break;
            }
            first = false;
        }

        // 有字但整列都沒讀到的（短名稱常常這樣）：單獨把這一列裁出來，換幾種倍率再讀
        for (int row = 0; row < Layout.RowCount; row++)
        {
            if (readings.ContainsKey(row))
            {
                continue;
            }
            Rectangle rowRect = RowRect(listRect, row, scale);
            if (!HasText(client, rowRect, scale))
            {
                continue; // 空白列（分類的最後一頁不滿 10 列）
            }
            List<string> texts = [];
            double[] rowScales = [OcrScale, 4, 2, 5];
            foreach (double s in rowScales.Distinct())
            {
                PreparedImage rowImage = Prepare(client, rowRect, scale, s, 1);
                using SoftwareBitmap bitmap = rowImage.ToSoftwareBitmap();
                OcrResult result = await _engine.RecognizeAsync(bitmap);
                string text = string.Concat(result.Lines.Select(JoinWords));
                if (text != "" && !texts.Contains(text))
                {
                    texts.Add(text);
                }
            }
            readings[row] = texts; // 還是讀不出來時是空的，由呼叫端推斷或提示
        }

        Rectangle iconRect = IconRect(client.Size, scale);
        List<ListRow> rows = [];
        foreach ((int row, List<string> texts) in readings.OrderBy(p => p.Key))
        {
            rows.Add(new ListRow(row, texts.Count > 0 ? texts[0] : "", HasCardIcon(client, iconRect, row, Layout.RowHeight, scale), texts, Width(row),
                ReadGlyphs(client, Layout, row, scale, Width(row), SnapGlyph), Math.Abs(scale - 1) <= 0.001));
        }
        return new ListSnapshot { TitleText = title, Rows = rows, TitleScale = titleScale };
    }

    /// <summary>
    /// 每一列都用字形讀出文字（glyphText：每個字都認得才有結果）；有任何一列讀不出來時回傳 null。空白列不算。
    /// </summary>
    public static List<ListRow>? ReadByGlyphs(Bitmap client, ListLayout layout, double scale,
        Func<IReadOnlyList<Glyph>, string?> glyphText, Func<Glyph, Glyph>? snap = null)
    {
        Rectangle listRect = ToScreen(layout.ListX, layout.ListY, layout.ListWidth, layout.ListHeight, client.Size, scale);
        List<ListRow> rows = [];
        for (int row = 0; row < layout.RowCount; row++)
        {
            int top = listRect.Y + (int)Math.Round(row * layout.RowHeight * scale);
            Rectangle rowRect = Rectangle.Intersect(
                new Rectangle(listRect.X, top, listRect.Width, (int)Math.Round(layout.RowHeight * scale)), listRect);
            if (!HasText(client, rowRect, scale))
            {
                continue; // 空白列
            }
            double width = MeasureTextWidth(client, rowRect, scale);
            IReadOnlyList<Glyph>? cells = ReadGlyphs(client, layout, row, scale, width, snap);
            if (cells is null || glyphText(cells) is not string text)
            {
                return null;
            }
            rows.Add(new ListRow(row, text, HasCardIcon(client, layout, row, scale), [text], width, cells, Math.Abs(scale - 1) <= 0.001));
        }
        return rows;
    }

    // 用 ocrScale 倍放大讀一次清單，回傳 第幾列 → 文字
    private async Task<Dictionary<int, string>> ReadListAsync(Bitmap client, Rectangle listRect, double scale, double ocrScale)
    {
        // 每一行字依中心的高度分到第幾列（同一列有好幾段時，依左右順序接起來）
        Dictionary<int, List<(double X, string Text)>> byRow = [];
        PreparedImage listImage = Prepare(client, listRect, scale, ocrScale, Layout.RowCount);
        double rowHeight = Layout.RowHeight * listImage.PixelsPerGamePixel;
        using (SoftwareBitmap bitmap = listImage.ToSoftwareBitmap())
        {
            OcrResult result = await _engine.RecognizeAsync(bitmap);
            foreach (OcrLine line in result.Lines)
            {
                if (line.Words.Count == 0)
                {
                    continue;
                }
                double top = line.Words.Min(w => w.BoundingRect.Y);
                double bottom = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
                int row = (int)Math.Floor(((top + bottom) / 2 - listImage.Padding) / rowHeight);
                if (row < 0 || row >= Layout.RowCount)
                {
                    continue;
                }
                if (!byRow.TryGetValue(row, out List<(double X, string Text)>? parts))
                {
                    parts = [];
                    byRow[row] = parts;
                }
                parts.Add((line.Words[0].BoundingRect.X, JoinWords(line)));
            }
        }

        Dictionary<int, string> rows = [];
        foreach ((int row, List<(double X, string Text)> parts) in byRow)
        {
            string text = string.Concat(parts.OrderBy(p => p.X).Select(p => p.Text));
            if (text != "")
            {
                rows[row] = text;
            }
        }
        return rows;
    }

    /// <summary>辨識前處理好的標題和清單（存檔除錯用）。</summary>
    public (PreparedImage Title, PreparedImage List) PrepareForDebug(Bitmap client, double scale)
        => (Prepare(client, TitleRect(client.Size, scale), scale, OcrScale, 1),
            Prepare(client, ListRect(client.Size, scale), scale, OcrScale, Layout.RowCount));

    // 清單的第 row 列（螢幕像素）
    private Rectangle RowRect(Rectangle listRect, int row, double scale)
    {
        int top = listRect.Y + (int)Math.Round(row * Layout.RowHeight * scale);
        int height = (int)Math.Round(Layout.RowHeight * scale);
        return Rectangle.Intersect(new Rectangle(listRect.X, top, listRect.Width, height), listRect);
    }

    // 這一塊有沒有字：夠亮（字的亮度）的點夠多。一個字大約有 50 個點（遊戲原本的像素）是筆畫
    private static bool HasText(Bitmap client, Rectangle r, double scale)
    {
        if (r.Width <= 0 || r.Height <= 0)
        {
            return false;
        }
        byte[] bytes = GetPixelBytes(client, r, out int stride);
        int count = 0;
        for (int y = 0; y < r.Height; y++)
        {
            for (int x = 0; x < r.Width; x++)
            {
                int o = y * stride + x * 4;
                if (Math.Max(bytes[o], Math.Max(bytes[o + 1], bytes[o + 2])) >= TextThreshold)
                {
                    count++;
                }
            }
        }
        return count >= 25 * scale * scale;
    }

    // 有字的列是不是都讀到了（有字卻沒讀到的列，要換倍率或單獨裁出來再讀）
    private bool NoMissingRows(Bitmap client, Rectangle listRect, double scale, Dictionary<int, List<string>> readings)
    {
        for (int row = 0; row < Layout.RowCount; row++)
        {
            if (!readings.ContainsKey(row) && HasText(client, RowRect(listRect, row, scale), scale))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 量這一列的字有多寬（遊戲原本的像素，從第一個字的左邊到最後一個字的右邊；量不到時是 0）。
    /// 用和辨識前處理一樣的方法分出字（比這一列的底色亮一定程度），從最左邊的字往右接，
    /// 中間空了超過 12 遊戲像素（大約 3/4 個中文字）就當成結束，以免把右邊背景的亮點也算進去。
    /// </summary>
    private static double MeasureTextWidth(Bitmap client, Rectangle r, double scale)
    {
        r = Rectangle.Intersect(r, new Rectangle(Point.Empty, client.Size));
        if (r.Width <= 0 || r.Height <= 0)
        {
            return 0;
        }
        byte[] bytes = GetPixelBytes(client, r, out int stride);
        int[] histogram = new int[256];
        for (int y = 0; y < r.Height; y++)
        {
            for (int x = 0; x < r.Width; x++)
            {
                int o = y * stride + x * 4;
                histogram[Math.Max(bytes[o], Math.Max(bytes[o + 1], bytes[o + 2]))]++;
            }
        }
        int half = r.Width * r.Height / 2;
        int background = 0;
        for (int sum = 0; background < 255; background++)
        {
            sum += histogram[background];
            if (sum > half)
            {
                break;
            }
        }
        int low = background + BackgroundMargin;
        int threshold = (low + Math.Max(low + 40, High)) / 2; // 前處理後比灰色深的才算字

        bool[] ink = new bool[r.Width];
        for (int x = 0; x < r.Width; x++)
        {
            for (int y = 0; y < r.Height; y++)
            {
                int o = y * stride + x * 4;
                if (Math.Max(bytes[o], Math.Max(bytes[o + 1], bytes[o + 2])) >= threshold)
                {
                    ink[x] = true;
                    break;
                }
            }
        }
        int left = Array.IndexOf(ink, true);
        if (left < 0)
        {
            return 0;
        }
        int maxGap = (int)Math.Round(12 * scale);
        int right = left;
        for (int x = left + 1; x < r.Width && x - right <= maxGap; x++)
        {
            if (ink[x])
            {
                right = x;
            }
        }
        return (right - left + 1) / scale;
    }

    // 中文辨識結果每個字之間有空白，接起來
    private static string JoinWords(OcrLine line)
    {
        StringBuilder sb = new();
        foreach (OcrWord word in line.Words)
        {
            sb.Append(word.Text);
        }
        return sb.ToString().Replace(" ", "");
    }

    // 清單上的字從清單左邊算起第幾個遊戲像素開始（第一個字那一格的左邊；實測）
    private const int GlyphOriginX = 3;

    // 字的筆畫的亮度：字是純色（最亮的色版是 255）、沒有平滑處理；選取的那一列底色大約 160，比這個暗
    private const int GlyphThreshold = 240;

    // 放大顯示時筆畫的亮度：畫面是糊的，筆畫的邊緣變灰。用實際的 150% 截圖試過，200～215 都能全部認對
    private const int ScaledGlyphThreshold = 205;

    /// <summary>
    /// 放大顯示時，把取樣到的字形換成最像的已知字形（GlyphStore.Snap）；沒設定的話，放大顯示時不讀字形。
    /// </summary>
    public Func<Glyph, Glyph>? SnapGlyph { get; set; }

    /// <summary>
    /// 把一列的字形讀成文字（GlyphStore.ReadExact）：每個字都認得才有結果，否則是 null。
    /// 有設定的話，每一列都讀得出來、而且都確定對到名稱的頁面不做文字辨識（見 ReadAsync）。
    /// </summary>
    public Func<IReadOnlyList<Glyph>, string?>? GlyphText { get; set; }

    /// <summary>
    /// 清單第 row 列每一格（一個中文字寬）的字形。textWidth 是這一列量到的字寬（決定有幾格）。量不到字寬時回傳 null。
    /// 遊戲畫面被 Windows 放大（150% 以上）時：在每個遊戲像素的中心取樣，再用 snap 換成最像的已知字形
    /// （沒有 snap、或放大倍率太小（125%：一個點寬的筆畫會糊到取樣不到）時回傳 null）。
    /// </summary>
    public static IReadOnlyList<Glyph>? ReadGlyphs(Bitmap client, ListLayout layout, int row, double scale, double textWidth,
        Func<Glyph, Glyph>? snap = null)
    {
        if (textWidth <= 0)
        {
            return null;
        }
        if (Math.Abs(scale - 1) > 0.001)
        {
            return snap is null || scale < 1.45 ? null : ReadScaledGlyphs(client, layout, row, scale, textWidth, snap);
        }
        int count = (int)Math.Round((textWidth + 2) / Glyph.Width);
        Rectangle list = ToScreen(layout.ListX, layout.ListY, layout.ListWidth, layout.ListHeight, client.Size, scale);
        Rectangle r = new(list.X + GlyphOriginX, list.Y + row * layout.RowHeight, count * Glyph.Width, layout.RowHeight);
        if (count < 1 || !new Rectangle(Point.Empty, client.Size).Contains(r) || r.Right > list.Right)
        {
            return null;
        }
        byte[] bytes = GetPixelBytes(client, r, out int stride);
        List<Glyph> glyphs = [];
        for (int i = 0; i < count; i++)
        {
            ushort[] rows = new ushort[r.Height];
            for (int y = 0; y < r.Height; y++)
            {
                int bits = 0;
                for (int x = 0; x < Glyph.Width; x++)
                {
                    int o = y * stride + (i * Glyph.Width + x) * 4;
                    bool ink = Math.Max(bytes[o], Math.Max(bytes[o + 1], bytes[o + 2])) >= GlyphThreshold;
                    bits = (bits << 1) | (ink ? 1 : 0);
                }
                rows[y] = (ushort)bits;
            }
            glyphs.Add(new Glyph(rows));
        }
        return glyphs;
    }

    private static readonly (int Dx, int Dy)[] ScaledShifts =
        [(0, 0), (-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, -1), (-1, 1), (1, 1)];

    // 上一次每一格都認得的位置
    private static (int Dx, int Dy) _lastShift = (0, 0);

    // 放大顯示時讀字形：遊戲的每個像素被放大成 scale 個螢幕像素，在它的中心取樣（中心那一點最不受旁邊的像素影響）。
    // 遊戲畫面對到螢幕像素的位置可能差一個像素（視窗位置、邊框寬度不同），所以差一個像素的幾種位置都試，
    // 用認得出最多格的那一種
    private static IReadOnlyList<Glyph>? ReadScaledGlyphs(Bitmap client, ListLayout layout, int row, double scale, double textWidth,
        Func<Glyph, Glyph> snap)
    {
        int count = (int)Math.Round((textWidth + 2) / Glyph.Width);
        if (count < 1 || GlyphOriginX + count * Glyph.Width > layout.ListWidth)
        {
            return null;
        }
        // 這一列第一格的左上角（遊戲像素，從遊戲畫面的左上角算起）
        double gameX = client.Width / scale / 2 + layout.ListX + GlyphOriginX;
        double gameY = client.Height / scale / 2 + layout.ListY + row * layout.RowHeight;
        Rectangle all = new(Point.Empty, client.Size);
        Rectangle r = Rectangle.Intersect(all, Rectangle.FromLTRB(
            (int)Math.Floor(gameX * scale) - 2, (int)Math.Floor(gameY * scale) - 2,
            (int)Math.Ceiling((gameX + count * Glyph.Width) * scale) + 2, (int)Math.Ceiling((gameY + layout.RowHeight) * scale) + 2));
        if (r.Width <= 0 || r.Height <= 0)
        {
            return null;
        }
        byte[] bytes = GetPixelBytes(client, r, out int stride);

        List<Glyph>? best = null;
        int bestKnown = -1;
        // 上一次每一格都認得的位置先試（同一個視窗的位置不會變，通常第一次就中）
        foreach ((int dx, int dy) in ScaledShifts.OrderBy(s => s == _lastShift ? 0 : 1))
        {
            List<Glyph> glyphs = [];
            for (int i = 0; i < count; i++)
            {
                ushort[] rows = new ushort[layout.RowHeight];
                for (int y = 0; y < rows.Length; y++)
                {
                    int sy = Math.Clamp((int)Math.Floor((gameY + y + 0.5) * scale) + dy - r.Y, 0, r.Height - 1);
                    int bits = 0;
                    for (int x = 0; x < Glyph.Width; x++)
                    {
                        int sx = Math.Clamp((int)Math.Floor((gameX + i * Glyph.Width + x + 0.5) * scale) + dx - r.X, 0, r.Width - 1);
                        int o = sy * stride + sx * 4;
                        bool ink = Math.Max(bytes[o], Math.Max(bytes[o + 1], bytes[o + 2])) >= ScaledGlyphThreshold;
                        bits = (bits << 1) | (ink ? 1 : 0);
                    }
                    rows[y] = (ushort)bits;
                }
                glyphs.Add(snap(new Glyph(rows)));
            }
            int known = glyphs.Count(g => !ReferenceEquals(g, Glyph.Unreadable));
            if (known > bestKnown)
            {
                best = glyphs;
                bestKnown = known;
            }
            if (known == count)
            {
                _lastShift = (dx, dy);
                break; // 每一格都認得：就是這個位置
            }
        }
        return best;
    }

    /// <summary>
    /// 清單第 row 列的前面有沒有卡片圖示（client 是整個遊戲畫面的截圖）。給測試用，讀取時用下面的版本。
    /// </summary>
    public static bool HasCardIcon(Bitmap client, ListLayout layout, int row, double scale)
        => HasCardIcon(client, ToScreen(layout.IconX, layout.ListY, layout.IconWidth, layout.ListHeight, client.Size, scale),
            row, layout.RowHeight, scale);

    // 卡片圖示（紅褐色、米色的小卷軸）：清單的底色是深藍色（選取的那一列是藍綠色），
    // 紅色明顯比藍色多的點（暖色）有一定數量就當成有圖示。
    // 以前只認「亮橘色」，實際的圖示顏色是 (155,54,20)、(218,194,182) 這類，一個點都對不上，卡片欄一直是空的
    private static bool HasCardIcon(Bitmap client, Rectangle iconRect, int row, int rowHeight, double scale)
    {
        int rowTop = iconRect.Y + (int)Math.Round(row * rowHeight * scale);
        Rectangle r = Rectangle.Intersect(new Rectangle(iconRect.X, rowTop, iconRect.Width, (int)Math.Round(rowHeight * scale)),
            new Rectangle(Point.Empty, client.Size));
        if (r.Width <= 0 || r.Height <= 0)
        {
            return false;
        }
        byte[] bytes = GetPixelBytes(client, r, out int stride);
        int count = 0;
        for (int y = 0; y < r.Height; y++)
        {
            for (int x = 0; x < r.Width; x++)
            {
                int o = y * stride + x * 4;
                int b = bytes[o], red = bytes[o + 2];
                if (red >= 100 && red >= b + 60)
                {
                    count++;
                }
            }
        }
        // 實測：有圖示的一格（8×20 遊戲像素）有 43 個點，沒有圖示的是 0；放大時點數跟著面積變多
        return count >= 10 * scale * scale;
    }

    public Rectangle TitleRect(Size client, double scale)
        => ToScreen(Layout.TitleX, Layout.TitleY, Layout.TitleWidth, Layout.TitleHeight, client, scale);

    public Rectangle ListRect(Size client, double scale)
        => ToScreen(Layout.ListX, Layout.ListY, Layout.ListWidth, Layout.ListHeight, client, scale);

    public Rectangle IconRect(Size client, double scale)
        => ToScreen(Layout.IconX, Layout.ListY, Layout.IconWidth, Layout.ListHeight, client, scale);

    // 相對於遊戲畫面中央的位置（遊戲原本的像素）→ 截圖上的位置（螢幕像素）
    private static Rectangle ToScreen(int relX, int relY, int width, int height, Size client, double scale)
    {
        double centerX = client.Width / scale / 2;
        double centerY = client.Height / scale / 2;
        Rectangle r = new((int)Math.Round((centerX + relX) * scale), (int)Math.Round((centerY + relY) * scale),
            (int)Math.Round(width * scale), (int)Math.Round(height * scale));
        return Rectangle.Intersect(r, new Rectangle(Point.Empty, client));
    }

    // 截圖的一塊 → 放大成遊戲原本像素的 ocrScale 倍、轉成白底黑字、四周加白邊。
    // bands 是分成幾條各自找底色（清單是每一列一條，標題是 1 條）
    private static PreparedImage Prepare(Bitmap client, Rectangle r, double scale, double ocrScale, int bands)
    {
        double factor = ocrScale / scale;
        int pad = (int)Math.Round(PaddingGamePixels * ocrScale);
        int w = Math.Max(1, (int)Math.Round(r.Width * factor));
        int h = Math.Max(1, (int)Math.Round(r.Height * factor));
        // 文字辨識有圖片大小上限（通常是 2600 像素）
        double limit = OcrEngine.MaxImageDimension - pad * 2;
        if (w > limit || h > limit)
        {
            double shrink = Math.Min(limit / w, limit / h);
            w = (int)(w * shrink);
            h = (int)(h * shrink);
        }
        double pixelsPerGamePixel = (double)h / r.Height * scale;

        using Bitmap scaled = new(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(client, new Rectangle(0, 0, w, h), r, GraphicsUnit.Pixel);
        }

        byte[] src = GetPixelBytes(scaled, new Rectangle(0, 0, w, h), out int stride);
        int outW = w + pad * 2, outH = h + pad * 2;
        byte[] dst = new byte[outW * outH * 4];
        Array.Fill(dst, (byte)255); // 白邊
        bands = Math.Max(1, bands);
        for (int band = 0; band < bands; band++)
        {
            int y0 = band * h / bands;
            int y1 = (band + 1) * h / bands;
            if (y1 <= y0)
            {
                continue;
            }

            // 這一條的底色：亮度的中位數（字只占一小部分，中位數就是底色）
            int[] histogram = new int[256];
            for (int y = y0; y < y1; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = y * stride + x * 4;
                    histogram[Math.Max(src[o], Math.Max(src[o + 1], src[o + 2]))]++;
                }
            }
            int half = (y1 - y0) * w / 2;
            int background = 0;
            for (int sum = 0; background < 255; background++)
            {
                sum += histogram[background];
                if (sum > half)
                {
                    break;
                }
            }
            int low = background + BackgroundMargin;
            int high = Math.Max(low + 40, High);

            for (int y = y0; y < y1; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = y * stride + x * 4;
                    int m = Math.Max(src[o], Math.Max(src[o + 1], src[o + 2]));
                    int v = Math.Clamp((m - low) * 255 / (high - low), 0, 255);
                    byte gray = (byte)(255 - v);
                    int d = ((y + pad) * outW + x + pad) * 4;
                    dst[d] = gray;
                    dst[d + 1] = gray;
                    dst[d + 2] = gray;
                    dst[d + 3] = 255;
                }
            }
        }

        return new PreparedImage { Pixels = dst, Width = outW, Height = outH, Padding = pad, PixelsPerGamePixel = pixelsPerGamePixel };
    }

    private static byte[] GetPixelBytes(Bitmap bitmap, Rectangle rect, out int stride)
    {
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            stride = data.Stride;
            int length = Math.Abs(data.Stride) * data.Height;
            byte[] bytes = new byte[length];
            Marshal.Copy(data.Scan0, bytes, 0, length);
            return bytes;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
