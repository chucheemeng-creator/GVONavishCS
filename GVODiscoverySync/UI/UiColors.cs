// UiColors.cs
// 介面上自訂的文字顏色，依目前的顯示模式選擇（和 GVONavish 的 UiColors 一樣的規則）：
//   - 高對比模式：一律用 Windows 的系統文字顏色
//   - 深色模式：用比較亮的顏色
//   - 一般（淺色）：原本的顏色

using System.Drawing;
using System.Windows.Forms;

namespace GVODiscoverySync.UI;

internal static class UiColors
{
    /// <summary>一般視窗上的文字。</summary>
    public static Color Pick(Color light, Color dark) => Pick(light, dark, SystemColors.ControlText);

    /// <summary>清單（ListView）裡的文字。</summary>
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
