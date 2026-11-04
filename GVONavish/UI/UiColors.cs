// UiColors.cs
// 介面上自訂的文字顏色（強調色），依目前的顯示模式選擇：
//   - 高對比模式：一律用 Windows 的系統文字顏色，不自訂顏色（使用者選的高對比配色才看得清楚）
//   - 深色模式：用比較亮的顏色，深色背景上才看得清楚
//   - 一般（淺色）：原本的顏色
// 注意：高對比模式下 Application.IsDarkModeEnabled 一定是 false，所以要先判斷高對比。

using System.Drawing;
using System.Windows.Forms;

namespace GVONavish.UI;

internal static class UiColors
{
    /// <summary>一般視窗、狀態列上的文字。</summary>
    public static Color Pick(Color light, Color dark) => Pick(light, dark, SystemColors.ControlText);

    /// <summary>清單（ListView、ListBox）裡的文字：高對比時用清單的系統文字顏色。</summary>
    public static Color PickForList(Color light, Color dark) => Pick(light, dark, SystemColors.WindowText);

    private static Color Pick(Color light, Color dark, Color highContrast)
    {
        if (SystemInformation.HighContrast)
        {
            return highContrast;
        }
        return Application.IsDarkModeEnabled ? dark : light;
    }
}
