// Sail.cs
// 帆和風的關係：風從哪一邊來，船速剩多少。
//
// 資料來源：巴哈姆特攻略百科「帆和風的關係」（摘自尖端出版《密技大補帖》2005 年 12 月刊，數據由遊戲公司提供）
//             順風     側風     逆風
//   縱帆      62.5%    62.5%    45%
//   橫帆      87.5%    62.5%    25%
// 目前只做縱帆：順風和側風一樣快，只有逆風會慢（剩 72%）。
//
// 百科沒有寫幾度以內算逆風。這裡先假設船頭和「風吹來的方向」夾角在 45 度以內算逆風，
// 還沒有在遊戲裡量過（HeadAngle）。

using System;

namespace GVONavish.Core;

/// <summary>風從船的哪一邊來。</summary>
public enum WindSide
{
    /// <summary>順風：風從船尾吹向船頭。</summary>
    Tail,

    /// <summary>側風。</summary>
    Side,

    /// <summary>逆風：風從船頭吹向船尾。</summary>
    Head,
}

public static class Sail
{
    /// <summary>船頭和「風吹來的方向」夾角在這個角度以內算逆風；船尾同理算順風。</summary>
    public const double HeadAngle = 45.0;

    /// <summary>
    /// 風從船的哪一邊來。
    /// </summary>
    /// <param name="headingDegrees">船的航向（0 = 北，90 = 東，順時針）。</param>
    /// <param name="windToDegrees">風吹往的方向（和 <see cref="WindMap.DirectionAt"/> 相同）。</param>
    public static WindSide Classify(double headingDegrees, double windToDegrees)
    {
        // 船往風吹的方向走 = 順風（夾角 0）；正對著風吹來的方向走 = 逆風（夾角 180）
        double diff = Math.Abs(headingDegrees - windToDegrees) % 360;
        if (diff > 180) diff = 360 - diff;
        if (diff >= 180 - HeadAngle) return WindSide.Head;
        return diff <= HeadAngle ? WindSide.Tail : WindSide.Side;
    }

    /// <summary>縱帆在這種風下的船速，和不是逆風時相比的比例（逆風 0.72，其他 1）。</summary>
    public static double ForeAndAftSpeed(WindSide side) => side == WindSide.Head ? 0.45 / 0.625 : 1.0;
}
