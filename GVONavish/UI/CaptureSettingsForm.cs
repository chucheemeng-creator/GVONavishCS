// CaptureSettingsForm.cs
// 「讀座標設定」對話框：調整截圖位置和讀取間隔，即時顯示截到的畫面和辨識結果。
// 就是之前 GVOCoordReader 測試工具的畫面，改成對話框。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using GVONavish.Core;

namespace GVONavish.UI;

public class CaptureSettingsForm : Form
{
    private readonly GameWindowCapture _capture = new();
    private readonly System.Windows.Forms.Timer _timer = new();

    private readonly Label _lblWindow = new();
    private readonly Label _lblCoord = new();
    private readonly PixelPictureBox _picRaw = new();
    private readonly PixelPictureBox _picBinary = new();
    private readonly NumericUpDown _numOffsetX = new();
    private readonly NumericUpDown _numOffsetY = new();
    private readonly NumericUpDown _numInterval = new();


    public int OffsetX => (int)_numOffsetX.Value;
    public int OffsetY => (int)_numOffsetY.Value;
    public int IntervalMs => (int)_numInterval.Value;

    public CaptureSettingsForm(int offsetX, int offsetY, int intervalMs)
    {
        BuildUi(offsetX, offsetY, intervalMs);
        ApplyOffset();
        _timer.Interval = 300;
        _timer.Tick += (s, e) =>
        {
            try
            {
                CaptureOnce();
            }
            catch (Exception ex)
            {
                SetText(_lblWindow, "截圖時發生錯誤，稍後重試：" + ex.Message);
            }
        };
        _timer.Start();
    }

    private void BuildUi(int offsetX, int offsetY, int intervalMs)
    {
        Text = "讀座標設定";
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft JhengHei UI", 10f);
        // 只會變大、不會縮小：狀態文字每 0.3 秒更新，長短不同時視窗不要跟著忽大忽小
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowOnly;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;

        TableLayoutPanel layout = new()
        {
            ColumnCount = 1,
            Padding = new Padding(10),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        Controls.Add(layout);

        _lblWindow.AutoSize = true;
        _lblWindow.Text = "遊戲視窗：搜尋中…";
        layout.Controls.Add(_lblWindow);

        _lblCoord.AutoSize = true;
        _lblCoord.Font = new Font("Consolas", 20f, FontStyle.Bold);
        _lblCoord.Text = "----, ----";
        _lblCoord.Margin = new Padding(3, 6, 3, 6);
        layout.Controls.Add(_lblCoord);

        layout.Controls.Add(new Label { AutoSize = true, Text = "截到的畫面（放大 6 倍）：" });
        SetupPictureBox(_picRaw);
        layout.Controls.Add(_picRaw);

        layout.Controls.Add(new Label { AutoSize = true, Text = "黑白判讀（白色＝當成數字的點）：", Margin = new Padding(3, 6, 3, 0) });
        SetupPictureBox(_picBinary);
        layout.Controls.Add(_picBinary);

        Label hint = new()
        {
            AutoSize = true,
            MaximumSize = new Size(380, 0),
            ForeColor = SystemColors.GrayText,
            Text = "調整下面的數字，讓座標完整出現在框裡、黑白圖裡數字清楚。數字越大，框越往左、往上。",
            Margin = new Padding(3, 8, 3, 0),
        };
        layout.Controls.Add(hint);

        FlowLayoutPanel panel = new() { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        panel.Controls.Add(new Label { AutoSize = true, Text = "距右下角 X：", Margin = new Padding(3, 6, 0, 0) });
        SetupNumeric(_numOffsetX, 0, 5000, offsetX);
        panel.Controls.Add(_numOffsetX);
        panel.Controls.Add(new Label { AutoSize = true, Text = "Y：", Margin = new Padding(10, 6, 0, 0) });
        SetupNumeric(_numOffsetY, 0, 5000, offsetY);
        panel.Controls.Add(_numOffsetY);
        layout.Controls.Add(panel);

        FlowLayoutPanel panel2 = new() { AutoSize = true, WrapContents = false };
        panel2.Controls.Add(new Label { AutoSize = true, Text = "讀取間隔（毫秒）：", Margin = new Padding(3, 6, 0, 0) });
        SetupNumeric(_numInterval, 100, 5000, intervalMs);
        _numInterval.Increment = 100;
        panel2.Controls.Add(_numInterval);
        layout.Controls.Add(panel2);

        _numOffsetX.ValueChanged += (s, e) => ApplyOffset();
        _numOffsetY.ValueChanged += (s, e) => ApplyOffset();

        FlowLayoutPanel buttons = new()
        {
            AutoSize = true,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 10, 0, 0),
        };
        Button btnSave = new() { Text = "存下截圖…", AutoSize = true };
        Button btnOk = new() { Text = "確定", DialogResult = DialogResult.OK, AutoSize = true };
        Button btnCancel = new() { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        Button btnDefault = new() { Text = "預設值", AutoSize = true };
        btnSave.Click += (s, e) => SaveLastCapture();
        btnDefault.Click += (s, e) =>
        {
            _numOffsetX.Value = 70;
            _numOffsetY.Value = 273;
        };
        buttons.Controls.Add(btnSave);
        buttons.Controls.Add(btnDefault);
        buttons.Controls.Add(btnOk);
        buttons.Controls.Add(btnCancel);
        layout.Controls.Add(buttons);

        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private static void SetupPictureBox(PixelPictureBox pic)
    {
        pic.Size = new Size(60 * 6, 11 * 6);
        pic.SizeMode = PictureBoxSizeMode.StretchImage;
        pic.BackColor = Color.DimGray;
        pic.BorderStyle = BorderStyle.FixedSingle;
    }

    private static void SetupNumeric(NumericUpDown num, int min, int max, int value)
    {
        num.Minimum = min;
        num.Maximum = max;
        num.Value = Math.Clamp(value, min, max);
        num.Width = 70;
    }

    private void ApplyOffset()
    {
        _capture.OffsetFromRightBottom = new Point((int)_numOffsetX.Value, (int)_numOffsetY.Value);
    }

    private void CaptureOnce()
    {
        if (!_capture.FindGameWindow())
        {
            SetText(_lblWindow, "遊戲視窗：找不到（請先開啟遊戲）");
            return;
        }
        string status = "遊戲視窗：" + _capture.GetWindowTitle();

        Bitmap? bmp = _capture.Capture();
        if (bmp is null)
        {
            SetText(_lblWindow, status + "（無法截圖，視窗可能縮小了）");
            return;
        }

        if (_capture.IsScaled)
        {
            // 遊戲被 Windows 放大了：程式會自動換算，截圖位置的數字照遊戲原本的像素填（預設 70、273）
            status += $"\n顯示縮放 {_capture.Scale:0%}：已自動換算，位置用預設值即可";
        }
        SetText(_lblWindow, status);

        ReplaceImage(_picRaw, (Bitmap)bmp.Clone());
        bool ok = _capture.TryReadWithPreview(bmp, out int x, out int y, out Bitmap preview);
        ReplaceImage(_picBinary, preview);

        if (ok)
        {
            SetText(_lblCoord, $"{x}, {y}");
            _lblCoord.ForeColor = SystemColors.ControlText;
        }
        else
        {
            SetText(_lblCoord, "讀不到");
            _lblCoord.ForeColor = SystemColors.GrayText; // 系統的灰色字：深色、高對比模式下也看得清楚
        }
    }

    // 文字一次設定好，而且內容沒變就不重設（重設會讓版面重新計算大小，畫面會閃）
    private static void SetText(Label label, string text)
    {
        if (label.Text != text)
        {
            label.Text = text;
        }
    }

    private static void ReplaceImage(PictureBox pic, Image image)
    {
        Image? old = pic.Image;
        pic.Image = image;
        old?.Dispose();
    }

    // 「存下截圖」存的範圍：比讀座標的框大，往左多 30、往上多 10 像素，存 160 × 40（遊戲原本的像素，放大時跟著放大）。
    // 遊戲被 Windows 放大（例如顯示縮放 150%）時，數字會比框大，這樣才截得到完整的數字，方便回報問題。
    private const int SaveMarginLeft = 30;
    private const int SaveMarginTop = 10;
    private const int SaveWidth = 160;
    private const int SaveHeight = 40;

    private void SaveLastCapture()
    {
        // 先截圖，再開存檔視窗（不然會截到存檔視窗本身）
        // 位置是遊戲原本的像素；遊戲被放大時，換算成螢幕像素（範圍也跟著放大）
        double s = _capture.Scale;
        using Bitmap? bmp = _capture.CaptureRegion((int)Math.Round((OffsetX + SaveMarginLeft) * s), (int)Math.Round((OffsetY + SaveMarginTop) * s),
            (int)Math.Round(SaveWidth * s), (int)Math.Round(SaveHeight * s));
        if (bmp is null)
        {
            MessageBox.Show(this, "截不到遊戲畫面（找不到遊戲視窗，或視窗縮到最小）。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using SaveFileDialog dlg = new()
        {
            Filter = "PNG 圖檔|*.png",
            FileName = $"coord_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            bmp.Save(dlg.FileName, ImageFormat.Png);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        _capture.Dispose();
        base.OnFormClosed(e);
    }
}

/// <summary>放大顯示時不做模糊處理，每個像素都看得清楚。</summary>
public class PixelPictureBox : PictureBox
{
    protected override void OnPaint(PaintEventArgs pe)
    {
        pe.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        pe.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        base.OnPaint(pe);
    }
}
