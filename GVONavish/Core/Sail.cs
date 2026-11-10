// Sail.cs
// 帆和風的關係：風從哪個角度來，船速剩多少。
//
// 資料來源：巴哈姆特攻略百科「帆和風的關係」（摘自尖端出版《密技大補帖》2005 年 12 月刊，數據由遊戲公司提供）
//             順風     側風     逆風
//   縱帆      62.5%    62.5%    45%
//   橫帆      87.5%    62.5%    25%
// 目前只做縱帆：順風和側風一樣快，只有偏向逆風才會慢，正逆風時剩 72%（45 ÷ 62.5）。
//
// 遊戲裡船速是隨角度慢慢變的，不是轉過某個角度就跳一級。百科只給了順風、側風、逆風三個數字，
// 這裡在側風和正逆風之間用直線連起來。
//
// 實測（2026-11，馬德拉海域、冬季、帆船；最快 13.33 節當 100%）：
//   和風向的夾角   14°    60°    78°    102°   120°   168°
//   實測            99%    97%    100%   98%    93%    78%
//   這裡算的        100%   100%   100%   96%    91%    76%
// 誤差在 3% 以內；照實測往外推，正逆風大約剩 74%（百科 72%）。

using System;

namespace GVONavish.Core;

public static class Sail
{
    /// <summary>縱帆正逆風時的船速，和順風、側風時相比的比例。</summary>
    public const double ForeAndAftHeadSpeed = 0.45 / 0.625;

    /// <summary>船速掉到這個比例以下的航段，在地圖上標成逆風。</summary>
    public const double HeadwindSpeed = 0.9;

    /// <summary>
    /// 船的航向和風吹往的方向差幾度（0 ~ 180）：0 = 正順風，90 = 側風，180 = 正逆風。
    /// </summary>
    /// <param name="headingDegrees">船的航向（0 = 北，90 = 東，順時針）。</param>
    /// <param name="windToDegrees">風吹往的方向（和 <see cref="WindMap.DirectionAt"/> 相同）。</param>
    public static double AngleOffWind(double headingDegrees, double windToDegrees)
    {
        double diff = Math.Abs(headingDegrees - windToDegrees) % 360;
        return diff > 180 ? 360 - diff : diff;
    }

    /// <summary>
    /// 縱帆在這個角度的船速，和順風時相比的比例：順風到側風（0 ~ 90 度）是 1，
    /// 過了側風以後慢慢變慢，正逆風（180 度）是 0.72。
    /// </summary>
    /// <param name="angleOffWind"><see cref="AngleOffWind"/> 算出來的角度。</param>
    public static double ForeAndAftSpeed(double angleOffWind)
    {
        if (angleOffWind <= 90) return 1.0;
        double t = Math.Min(angleOffWind, 180) / 90 - 1; // 側風 0 → 正逆風 1
        return 1.0 - (1.0 - ForeAndAftHeadSpeed) * t;
    }

    /// <summary>這個角度算不算逆風（船速少了一成以上；縱帆大約是離正逆風 58 度以內）。</summary>
    public static bool IsHeadwind(double angleOffWind) => ForeAndAftSpeed(angleOffWind) < HeadwindSpeed;
}
