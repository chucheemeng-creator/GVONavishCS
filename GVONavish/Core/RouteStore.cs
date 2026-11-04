// RouteStore.cs
// 航線存檔、讀檔（routes.json，放在程式資料夾）。
// 原版只存「我的最愛」；這裡全部都存（一般航線一樣最多 30 段），下次開程式還看得到。
// 座標存成 [x, y] 的短格式，檔案比較小。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GVO.Common.IO;

namespace GVONavish.Core;

public static class RouteStore
{
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "routes.json");

    /// <summary>
    /// 航線檔的格式版本。
    /// 1：最初的格式。2：航線多了名稱（Name）和是否顯示（Hidden）；1 版讀進來時這兩個欄位用預設值（沒有名稱、顯示）。
    /// 讀到比這個大的版本（較新的程式存的），照樣顯示但不存檔。
    /// </summary>
    public const int CurrentVersion = 2;

    private class FileData
    {
        public int Version { get; set; } = CurrentVersion;
        // 沒有預設值：檔案裡少了 Routes 時會是 null，才分辨得出「格式不對」和「真的沒有航線」
        public List<ShipRoute>? Routes { get; set; }
    }

    private static JsonSerializerOptions Options()
    {
        JsonSerializerOptions o = new() { WriteIndented = false };
        o.Converters.Add(new WorldPointConverter());
        return o;
    }


    /// <summary>
    /// 讀檔。不會丟出例外。
    /// 檔案不存在：回傳空清單，problem 是 null。
    /// 檔案讀不出來：原檔改名保留後回傳空清單，problem 說明情況；
    /// 連改名都做不到時 canSave 是 false，這次不可以存檔（不然會蓋掉原檔）。
    /// </summary>
    public static List<ShipRoute> Load(out string? problem, out bool canSave)
    {
        problem = null;
        canSave = true;
        try
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }
            // 內容是 null、或沒有 Routes：不是這個程式存的格式，當成壞掉（走下面的「改名保留」），
            // 不能當成空清單，不然關閉時會用空清單蓋掉原檔
            FileData data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(FilePath), Options())
                ?? throw new JsonException("航線檔內容是 null");
            List<ShipRoute> routes = data.Routes ?? throw new JsonException("航線檔沒有 Routes");
            if (data.Version > CurrentVersion)
            {
                // 較新版本的程式存的：可能有這個版本不認得的資料，照樣顯示，但不存檔，免得把那些資料蓋掉
                canSave = false;
                problem = "航線檔 routes.json 是較新版本的 GVONavish 存的。\n\n" +
                          "航線可以照常顯示，但為了不弄丟新版本的資料，這次執行期間不會儲存航線。\n" +
                          "請更新到最新版的 GVONavish。";
            }
            // 檔案被手動改過或部分損壞時，JSON 裡的 null 不會讓讀檔失敗，卻會讓之後畫地圖出錯，
            // 所以在這裡清掉：null 的航線、null 或空的線段、清完沒有點的航線
            routes.RemoveAll(r => r is null);
            foreach (ShipRoute r in routes)
            {
                r.Closed = true; // 讀進來的都是舊航線，不再加點
                r.Lines ??= [];
                r.Name ??= ""; // 舊的存檔沒有名稱，或是 "Name": null
                r.Lines.RemoveAll(line => line is null || line.Count == 0);
            }
            routes.RemoveAll(r => r.IsEmpty);
            return routes;
        }
        catch
        {
            string? backup = BrokenFile.Preserve(FilePath);
            if (backup is not null)
            {
                problem = $"航線檔 routes.json 讀不出來，可能已經損壞。\n\n" +
                          $"原本的檔案已改名保留為：\n{Path.GetFileName(backup)}\n\n航線清單會從空的開始。";
            }
            else
            {
                canSave = false;
                problem = "航線檔 routes.json 讀不出來，可能被其他程式鎖住了。\n\n" +
                          "為了不蓋掉原本的檔案，這次執行期間不會儲存航線。請關掉程式，確認檔案沒被占用後再開一次。";
            }
            return [];
        }
    }

    /// <summary>存檔。先寫到暫存檔再換名，存到一半當機也不會把原本的檔案弄壞。</summary>
    public static bool Save(IEnumerable<ShipRoute> routes)
    {
        try
        {
            FileData data = new() { Routes = [.. routes] };
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(data, Options()));
            return true;
        }
        catch (Exception ex)
        {
            // 原檔不動（AtomicFile 先寫暫存檔），記錄下來；主視窗的狀態列會提示
            System.Diagnostics.Debug.WriteLine("routes.json 存檔失敗：" + ex);
            return false;
        }
    }

    /// <summary>WorldPoint 存成 [x, y]。</summary>
    private class WorldPointConverter : JsonConverter<WorldPoint>
    {
        public override WorldPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException();
            reader.Read();
            int x = reader.GetInt32();
            reader.Read();
            int y = reader.GetInt32();
            reader.Read();
            if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException();
            return new WorldPoint(x, y);
        }

        public override void Write(Utf8JsonWriter writer, WorldPoint value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.X);
            writer.WriteNumberValue(value.Y);
            writer.WriteEndArray();
        }
    }
}
