// Ports.cs
// 港口清單（206 個），給規劃航線輸入港口名稱用。
//
// 資料來源：巴哈姆特 大航海時代 Online 攻略百科「港口」及各港口頁面的「座標」欄
//   https://wiki2.gamer.com.tw/wiki.php?n=7501:%E6%B8%AF%E5%8F%A3
// 整理方式：
//   - 每個座標都用地圖檢查過，確定在海邊（離海 12 個座標單位以內）。
//   - 網站上有幾個港口的座標是從別的港口複製過來的（例如甘地亞＝薩洛尼卡），這些改用地圖量出的位置。
//   - 內陸城市（巴黎、法蘭克福、羅馬、牛津、西安…）和商會開拓地（北美東岸的藍點）沒有收錄。
//   - 清單和攻略百科「港口」頁、安瑟兒的中文地圖（2023-07-03 版，blog.dolanselnote.com）逐一比對過：
//     兩邊的港口、補給港都在清單裡。薩格雷斯的座標是從中文地圖的標示點量的。
//   - 英文名對照附帶地圖（map.png）上的英文地名，搜尋時中英文都可以用；有好幾種寫法的用 | 分開。
//
// 要新增或修正港口：在程式資料夾放一個 ports.json（格式見 PortList.LoadCustom 的說明），不用改程式。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace GVONavish.Core;

public class Port
{
    public string Name { get; }
    public string Region { get; }
    public WorldPoint Position { get; }

    /// <summary>英文名（可能有好幾種寫法，第一個是地圖上用的）。</summary>
    public IReadOnlyList<string> EnglishNames { get; }

    /// <summary>主要的英文名（地圖上的寫法）。</summary>
    public string English => EnglishNames.Count > 0 ? EnglishNames[0] : "";

    public Port(string name, string region, int x, int y, string english = "")
    {
        Name = name;
        Region = region;
        Position = new WorldPoint(x, y);
        EnglishNames = [.. english.Split('|').Select(s => s.Trim()).Where(s => s != "")];
    }

    /// <summary>「中文名 英文名」；沒有英文名（例如 ports.json 自訂的港口）時只有中文名，不會多出空格。</summary>
    public string NameWithEnglish(string separator = " ") => English != "" ? Name + separator + English : Name;

    /// <summary>清單顯示的文字，例如「里斯本　Lisbon（歐洲西部）」。</summary>
    public override string ToString() => $"{NameWithEnglish("　")}（{Region}）";
}

public static class PortList
{
    private static IReadOnlyList<Port>? _all;

    /// <summary>所有港口（內建 + 程式資料夾的 ports.json）。第一次用到時才建立。</summary>
    public static IReadOnlyList<Port> All => _all ??= Build();

    /// <summary>
    /// 用名稱找港口。接受中文名、英文名，或清單顯示的「里斯本　Lisbon（歐洲西部）」。找不到回傳 null。
    /// </summary>
    public static Port? Find(string text)
    {
        string t = (text ?? "").Trim();
        int paren = t.IndexOfAny(['（', '(']);
        if (paren > 0) t = t[..paren].Trim();
        if (t == "") return null;

        // 「里斯本　Lisbon」：取全形空白前面的中文名
        int space = t.IndexOf('　');
        string first = space > 0 ? t[..space].Trim() : t;

        Port? byName = All.FirstOrDefault(p => p.Name == first || p.Name == t);
        if (byName is not null) return byName;

        string key = Normalize(t);
        return All.FirstOrDefault(p => p.EnglishNames.Any(e => Normalize(e) == key));
    }

    /// <summary>
    /// 搜尋港口：中文名、英文名、區域名稱的任何部分都可以比對，不分大小寫，不用管空白、連字號和重音符號。
    /// 結果依符合程度排序：名稱完全相同 → 開頭相同 → 名稱中間有 → 區域名稱有。
    /// </summary>
    public static List<Port> Search(string query)
    {
        string q = (query ?? "").Trim();
        if (q == "") return [];
        string nq = Normalize(q);

        List<(Port Port, int Score)> scored = [];
        foreach (Port p in All)
        {
            int score = Score(p, q, nq);
            if (score > 0) scored.Add((p, score));
        }
        return [.. scored.OrderByDescending(s => s.Score).ThenBy(s => s.Port.Name.Length).Select(s => s.Port)];
    }

    private static int Score(Port p, string q, string nq)
    {
        if (p.Name == q) return 100;
        int best = 0;
        if (p.Name.StartsWith(q, StringComparison.Ordinal)) best = 80;
        else if (p.Name.Contains(q)) best = 60;

        if (nq != "")
        {
            foreach (string e in p.EnglishNames)
            {
                string ne = Normalize(e);
                if (ne == nq) best = Math.Max(best, 95);
                else if (ne.StartsWith(nq, StringComparison.Ordinal)) best = Math.Max(best, 75);
                else if (ne.Contains(nq)) best = Math.Max(best, 50);
            }
        }
        if (best == 0 && p.Region.Contains(q)) best = 20;
        return best;
    }

    /// <summary>比對用：轉小寫、去掉空白和標點、去掉重音符號（Lübeck → lubeck）。</summary>
    public static string Normalize(string s)
    {
        string d = (s ?? "").Normalize(NormalizationForm.FormD);
        StringBuilder sb = new(d.Length);
        foreach (char c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>離某個座標最近的港口（距離在 maxDistance 以內），沒有就回傳 null。</summary>
    public static Port? Nearest(WorldPoint p, double maxDistance)
    {
        Port? best = null;
        double bestD = maxDistance;
        foreach (Port port in All)
        {
            double d = World.Distance(p, port.Position);
            if (d <= bestD)
            {
                bestD = d;
                best = port;
            }
        }
        return best;
    }

    private static List<Port> Build()
    {
        List<Port> list = [.. BuiltIn];
        foreach (Port custom in LoadCustom())
        {
            list.RemoveAll(p => p.Name == custom.Name); // 同名的以自訂的為準
            list.Add(custom);
        }
        return list;
    }

    /// <summary>
    /// 讀取程式資料夾的 ports.json（沒有就算了）。格式：
    /// [ { "Name": "新阿姆斯特丹", "Region": "開拓地", "X": 1234, "Y": 1234, "English": "New Amsterdam" }, ... ]
    /// Region、English 可以省略。
    /// </summary>
    private static List<Port> LoadCustom()
    {
        List<Port> result = [];
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "ports.json");
            if (!File.Exists(path)) return result;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            int index = 0;
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                index++;
                // 一筆一筆處理：某一筆格式錯誤（例如座標寫成文字）只略過那一筆，後面的照樣讀
                try
                {
                    string name = e.GetProperty("Name").GetString() ?? "";
                    string region = e.TryGetProperty("Region", out JsonElement r) ? r.GetString() ?? "自訂" : "自訂";
                    string english = e.TryGetProperty("English", out JsonElement en) ? en.GetString() ?? "" : "";
                    int x = e.GetProperty("X").GetInt32();
                    int y = e.GetProperty("Y").GetInt32();
                    if (name != "" && World.IsValid(new WorldPoint(x, y)))
                    {
                        result.Add(new Port(name, region, x, y, english));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ports.json 第 {index} 筆格式錯誤，略過：{ex.Message}");
                }
            }
        }
        catch
        {
            // 檔案格式錯誤就忽略，只用內建的
        }
        return result;
    }

    private static readonly Port[] BuiltIn =
    [
        // 歐洲北部
        new("倫敦", "歐洲北部", 16324, 2418, "London"),
        new("多佛", "歐洲北部", 16324, 2490, "Dover"),
        new("普利茅斯", "歐洲北部", 16085, 2495, "Plymouth"),
        new("愛丁堡", "歐洲北部", 16206, 2125, "Edinburgh"),
        new("都柏林", "歐洲北部", 16051, 2281, "Dublin"),
        new("樸茨茅斯", "歐洲北部", 16256, 2488, "Portsmouth"),
        new("曼徹斯特", "歐洲北部", 16164, 2270, "Manchester"),
        new("卑爾根", "歐洲北部", 236, 1952, "Bergen"),
        new("奧斯陸", "歐洲北部", 484, 1947, "Oslo"),
        new("哥本哈根", "歐洲北部", 522, 2210, "Copenhagen"), // 從地圖量測（網站沒有或資料有誤）
        new("哥特堡", "歐洲北部", 530, 2076, "Gothenburg|Goteborg"),
        new("盧貝克", "歐洲北部", 485, 2332, "Lubeck|Lübeck"),
        new("斯德哥爾摩", "歐洲北部", 847, 1981, "Stockholm"),
        new("維斯比", "歐洲北部", 840, 2104, "Visby"),
        new("但澤", "歐洲北部", 863, 2312, "Danzig|Gdansk"),
        new("里加", "歐洲北部", 1071, 2145, "Riga"),
        new("阿姆斯特丹", "歐洲北部", 97, 2497, "Amsterdam"),
        new("海爾德", "歐洲北部", 66, 2466, "Helder"),
        new("格羅寧根", "歐洲北部", 141, 2452, "Groningen"),
        new("安特衛普", "歐洲北部", 48, 2520, "Antwerp"),
        new("鹿特丹", "歐洲北部", 52, 2492, "Rotterdam"),
        new("加來", "歐洲北部", 16304, 2566, "Calais"),
        new("南特", "歐洲北部", 16097, 2734, "Nantes"),
        new("波爾多", "歐洲北部", 16108, 2836, "Bordeaux"),
        new("勒阿弗爾", "歐洲北部", 16259, 2576, "Le Havre"),
        new("漢堡", "歐洲北部", 356, 2365, "Hamburg"),
        new("不來梅", "歐洲北部", 226, 2393, "Bremen"),
        new("聖彼得堡", "歐洲北部", 1216, 1956, "St. Petersburg|Saint Petersburg"),
        new("科科拉", "歐洲北部", 979, 1706, "Kokkola"),

        // 歐洲西部
        new("里斯本", "歐洲西部", 15784, 3205, "Lisbon"),
        new("希洪", "歐洲西部", 15847, 2920, "Gijon"),
        new("奧波多", "歐洲西部", 15764, 3116, "Porto|Oporto"),
        new("法魯", "歐洲西部", 15802, 3267, "Faro"),
        new("薩格雷斯", "歐洲西部", 15786, 3242, "Sagres"), // 從中文地圖的標示點量測
        new("塞維爾", "歐洲西部", 15904, 3270, "Seville"),
        new("馬拉加", "歐洲西部", 16059, 3276, "Malaga"),
        new("卡薩布蘭加", "歐洲西部", 15773, 3444, "Casablanca"),
        new("帕爾馬", "歐洲西部", 16286, 3204, "Palma"),
        new("巴賽隆納", "歐洲西部", 16358, 3111, "Barcelona"),
        new("蒙彼利埃", "歐洲西部", 42, 3007, "Montpellier"),
        new("休達", "歐洲西部", 15971, 3362, "Ceuta"),
        new("瓦倫西亞", "歐洲西部", 16178, 3160, "Valencia"),
        new("維亞納堡", "歐洲西部", 15770, 3052, "Viana do Castelo"),
        new("畢爾包", "歐洲西部", 16050, 2944, "Bilbao"),
        new("馬德拉", "歐洲西部", 15487, 3441, "Madeira"),
        new("拉斯帕爾馬斯", "歐洲西部", 15511, 3676, "Las Palmas"),
        new("馬賽", "歐洲西部", 126, 2970, "Marseille"),
        new("熱那亞", "歐洲西部", 299, 2918, "Genoa"),
        new("卡利亞里", "歐洲西部", 329, 3171, "Cagliari"),
        new("比薩", "歐洲西部", 360, 2950, "Pisa"),
        new("卡爾維", "歐洲西部", 292, 2972, "Calvi"),
        new("薩沙里", "歐洲西部", 269, 3092, "Sassari"),
        new("阿爾及爾", "歐洲西部", 36, 3276, "Algiers"),
        new("突尼斯", "歐洲西部", 419, 3271, "Tunis"),
        new("的黎波里", "歐洲西部", 490, 3426, "Tripoli"),
        new("阿爾金", "歐洲西部", 15592, 3977, "Arguin"),
        new("亞速爾", "歐洲西部", 14732, 3148, "Azores|The Azores"),

        // 東歐
        new("威尼斯", "東歐", 542, 2806, "Venice"),
        new("第里雅斯特", "東歐", 650, 2823, "Trieste"),
        new("札達爾", "東歐", 716, 2915, "Zadar"),
        new("拉古薩", "東歐", 800, 2978, "Ragusa"),
        new("安科納", "東歐", 583, 2919, "Ancona"),
        new("錫拉庫薩", "東歐", 618, 3242, "Syracuse"),
        new("拿坡里", "東歐", 616, 3119, "Naples"),
        new("科托爾", "東歐", 852, 3030, "Kotor"),
        new("雅典", "東歐", 1088, 3258, "Athens"),
        new("薩洛尼卡", "東歐", 1038, 3115, "Salonika|Thessaloniki"),
        new("甘地亞", "東歐", 1144, 3364, "Candia"), // 從地圖量測（網站沒有或資料有誤）
        new("法馬古斯塔", "東歐", 1539, 3383, "Famagusta"),
        new("亞歷山大", "東歐", 1344, 3534, "Alexandria"),
        new("開羅", "東歐", 1416, 3598, "Cairo"),
        new("班加西", "東歐", 891, 3480, "Benghazi"),
        new("貝魯特", "東歐", 1597, 3397, "Beirut"),
        new("雅法", "東歐", 1553, 3503, "Jaffa"),
        new("伊斯坦堡", "東歐", 1295, 3049, "Istanbul"),
        new("卡法", "東歐", 1651, 2844, "Kaffa"),
        new("特拉布宗", "東歐", 1706, 3041, "Trebizond"),
        new("奧德薩", "東歐", 1388, 2819, "Odessa"),
        new("塞瓦斯托波爾", "東歐", 1501, 2867, "Sevastopol"),

        // 非洲西部
        new("維德角", "非洲西部", 15466, 4202, "Cabo Verde|Cape Verde"),
        new("塞拉里昂", "非洲西部", 15744, 4442, "Sierra Leone"),
        new("阿比讓", "非洲西部", 16193, 4570, "Abidjan"),
        new("聖喬治", "非洲西部", 72, 4523, "Saint-Georges|Sao Jorge"),
        new("貝南", "非洲西部", 194, 4551, "Benin"),
        new("聖多美", "非洲西部", 242, 4759, "Sao Tome"),
        new("杜阿拉", "非洲西部", 416, 4641, "Douala"),
        new("加彭", "非洲西部", 366, 4806, "Gabon"),
        new("羅安達", "非洲西部", 544, 5020, "Luanda"),

        // 南非
        new("莫三比克", "南非", 1865, 5328, "Mozambique"),
        new("塔馬塔夫", "南非", 2255, 5427, "Tamatave"),
        new("納塔爾", "南非", 1481, 5915, "Natal"),
        new("伊尼揚巴內", "南非", 1609, 5604, "Inhambane"),
        new("索法拉", "南非", 1571, 5504, "Sofala"),
        new("本格拉", "南非", 587, 5206, "Benguela"),
        new("卡里比布", "南非", 613, 5583, "Karibib"),
        new("開普敦", "南非", 967, 6004, "Cape Town"),

        // 東非‧印度洋
        new("桑給巴爾", "東非‧印度洋", 1812, 4977, "Zanzibar"),
        new("基爾瓦", "東非‧印度洋", 1775, 5087, "Kilwa"),
        new("蒙巴薩", "東非‧印度洋", 1844, 4836, "Mombasa"),
        new("麻林地", "東非‧印度洋", 1911, 4788, "Malindi"),
        new("摩加迪休", "東非‧印度洋", 2186, 4575, "Mogadishu"),
        new("亞丁", "東非‧印度洋", 2068, 4272, "Aden"),
        new("杜法爾", "東非‧印度洋", 2444, 4132, "Dhofar"),
        new("澤拉", "東非‧印度洋", 1933, 4307, "Zeila|Saylac"), // 從地圖量測（網站沒有或資料有誤）
        new("馬薩瓦", "東非‧印度洋", 1806, 4169, "Massawa"),
        new("吉達", "東非‧印度洋", 1785, 3960, "Jeddah|Jiddah"),
        new("延布", "東非‧印度洋", 1774, 3896, "Yanbu"),
        new("蘇伊士", "東非‧印度洋", 1584, 3682, "Suez"),
        new("索科特拉", "東非‧印度洋", 2474, 4272, "Socotra"),
        new("馬斯喀特", "東非‧印度洋", 2699, 3857, "Muscat"),
        new("巴士拉", "東非‧印度洋", 2192, 3735, "Basra"),
        new("麥納麥", "東非‧印度洋", 2300, 3825, "Manama"),
        new("荷姆茲", "東非‧印度洋", 2538, 3728, "Hormuz"),
        new("第烏", "東非‧印度洋", 3207, 3962, "Diu"),
        new("果阿", "東非‧印度洋", 3308, 4105, "Goa"),
        new("門格洛爾", "東非‧印度洋", 3410, 4270, "Mangalore"),
        new("卡利卡特", "東非‧印度洋", 3437, 4317, "Calicut"),
        new("科欽", "東非‧印度洋", 3460, 4383, "Cochin"),
        new("錫蘭", "東非‧印度洋", 3649, 4470, "Ceylon"),
        new("本地治里", "東非‧印度洋", 3662, 4293, "Pondicherry"),
        new("加爾各答", "東非‧印度洋", 3980, 3943, "Calcutta"),
        new("維沙卡帕特南", "東非‧印度洋", 3801, 4072, "Visakhapatnam"),
        new("默蘇利珀德姆", "東非‧印度洋", 3686, 4165, "Machilipatnam|Masulipatnam"),

        // 加勒比海‧中美
        new("聖多明哥", "加勒比海‧中美", 13484, 4038, "Santo Domingo"),
        new("聖胡安", "加勒比海‧中美", 13603, 4037, "San Juan"),
        new("牙買加", "加勒比海‧中美", 13171, 4033, "Jamaica"),
        new("聖地牙哥", "加勒比海‧中美", 13193, 3974, "Santiago"),
        new("哈瓦那", "加勒比海‧中美", 12973, 3848, "Havana"),
        new("大開曼", "加勒比海‧中美", 12988, 3979, "Grand Cayman"),
        new("威廉斯塔德", "加勒比海‧中美", 13240, 4309, "Willemstad"),
        new("拿索", "加勒比海‧中美", 13104, 3735, "Nassau"),
        new("皇家港", "加勒比海‧中美", 13182, 4071, "Port Royal|Port Royale"),
        new("特魯希略", "加勒比海‧中美", 12707, 4028, "Trujillo"),
        new("伯南布哥", "加勒比海‧中美", 14815, 5070, "Pernambuco"),
        new("卡宴", "加勒比海‧中美", 13914, 4643, "Cayenne"),
        new("加拉卡斯", "加勒比海‧中美", 13343, 4411, "Caracas"),
        new("波多貝羅", "加勒比海‧中美", 12851, 4167, "Portobelo|Porto Bello"),
        new("梅里達", "加勒比海‧中美", 12398, 3920, "Merida"),
        new("馬拉開波", "加勒比海‧中美", 13143, 4391, "Maracaibo"),
        new("維拉克魯斯", "加勒比海‧中美", 12108, 4046, "Vera Cruz|Veracruz"),
        new("蘭巴耶克", "加勒比海‧中美", 12687, 5045, "Lambayeque"),
        new("通貝斯", "加勒比海‧中美", 12595, 4797, "Tumbes"),
        new("巴拿馬", "加勒比海‧中美", 12716, 4260, "Panama"),
        new("瓜地馬拉", "加勒比海‧中美", 12176, 4257, "Guatemala"),
        new("阿卡普爾科", "加勒比海‧中美", 11843, 4269, "Acapulco"),
        new("希瓦瓦", "加勒比海‧中美", 10133, 5007, "Hiva Oa"),
        new("夏威夷", "加勒比海‧中美", 9095, 3963, "Hawaii"),

        // 北美
        new("波士頓", "北美", 13242, 3057, "Boston"),
        new("舊金山", "北美", 10566, 3100, "San Francisco"),
        new("塔科馬", "北美", 10522, 2603, "Tacoma"),

        // 南美
        new("里約熱內盧", "南美", 14458, 5711, "Rio de Janeiro"),
        new("巴伊亞", "南美", 14647, 5336, "Bahia"),
        new("布宜諾斯艾利斯", "南美", 13723, 6109, "Buenos Aires"),
        new("聖安東尼奧", "南美", 13487, 6302, "San Antonio"),
        new("瓦爾帕萊索", "南美", 13015, 6052, "Valparaiso"),
        new("科皮亞波", "南美", 13143, 5793, "Copiapo"),
        new("利馬", "南美", 12835, 5259, "Lima"),

        // 東南亞‧大洋洲
        new("勃固", "東南亞‧大洋洲", 4400, 4136, "Pegu"),
        new("嘉定", "東南亞‧大洋洲", 4904, 4401, "Gia Dinh"),
        new("洛布里", "東南亞‧大洋洲", 4649, 4241, "Lop Buri"),
        new("帝利", "東南亞‧大洋洲", 5799, 5076, "Dili"),
        new("安汶", "東南亞‧大洋洲", 5812, 4913, "Ambon"),
        new("嵐嶼", "東南亞‧大洋洲", 5963, 4943, "Run"),
        new("馬尼拉", "東南亞‧大洋洲", 5575, 4270, "Manila"),
        new("和魯", "東南亞‧大洋洲", 5520, 4525, "Jolo"),
        new("達沃", "東南亞‧大洋洲", 5769, 4492, "Davao|Davao City"),
        new("特爾納特", "東南亞‧大洋洲", 5776, 4712, "Ternate"),
        new("亞齊", "東南亞‧大洋洲", 4232, 4566, "Aceh|Banda Aceh"),
        new("占碑", "東南亞‧大洋洲", 4577, 4752, "Jambi"),
        new("巨港", "東南亞‧大洋洲", 4640, 4819, "Palembang"),
        new("麻六甲", "東南亞‧大洋洲", 4681, 4697, "Malacca"),
        new("北大年", "東南亞‧大洋洲", 4649, 4510, "Pattani"),
        new("古晉", "東南亞‧大洋洲", 5054, 4667, "Kuching"),
        new("雅加達", "東南亞‧大洋洲", 4815, 4980, "Jakarta"),
        new("泗水", "東南亞‧大洋洲", 5207, 5032, "Surabaya"),
        new("馬辰", "東南亞‧大洋洲", 5230, 4891, "Banjarmasin"),
        new("望加錫", "東南亞‧大洋洲", 5468, 4921, "Makassar"),
        new("汶萊", "東南亞‧大洋洲", 5230, 4543, "Brunei"),
        new("關島", "東南亞‧大洋洲", 6584, 4219, "Guam"),
        new("薩馬賴", "東南亞‧大洋洲", 6868, 5163, "Samarai"),
        new("卡卡杜", "東南亞‧大洋洲", 6332, 5700, "Kakadu"),
        new("芬瑟島", "東南亞‧大洋洲", 7303, 6251, "K'gari|Fraser Island"),
        new("平哈拉", "東南亞‧大洋洲", 5564, 6477, "Pinjarra"),
        new("荷巴特", "東南亞‧大洋洲", 7047, 6960, "Hobart"),
        new("旺格努伊", "東南亞‧大洋洲", 8312, 6580, "Wanganui"),

        // 東亞
        new("淡水", "東亞", 5708, 3780, "Tamsui"),
        new("安平", "東亞", 5680, 3920, "Anping"),
        new("堺市", "東亞", 6336, 3400, "Sakai"),
        new("江戶", "東亞", 6556, 3336, "Edo"),
        new("長崎", "東亞", 6063, 3460, "Nagasaki"),
        new("漢陽", "東亞", 5771, 3225, "Hanyang"),
        new("浦項", "東亞", 5920, 3275, "Pohang"),
        new("釜山", "東亞", 5910, 3352, "Busan"),
        new("杭州", "東亞", 5553, 3548, "Hangzhou"),
        new("泉州", "東亞", 5404, 3801, "Quanzhou"),
        new("雲台山", "東亞", 5474, 3354, "Yuntaishan"), // 從地圖量測（網站沒有或資料有誤）
        new("重慶", "東亞", 4768, 3529, "Chongqing"),
        new("澳門", "東亞", 5176, 3897, "Macau"),

        // 補給港（名稱和座標照巴哈姆特攻略百科「港口」頁；錫特卡在百科寫成「矽地卡」，兩個名字都找得到）
        new("納爾維克", "補給港", 699, 1318, "Narvik"),
        new("芒加塞亞", "補給港", 3315, 1062, "Mangazeya"),
        new("提克西", "補給港", 5952, 960, "Tiksi"),
        new("克孜勒扎爾", "補給港", 7325, 2431, "Petropavlovsk"),
        new("巴羅", "補給港", 9307, 1063, "Barrow"),
        new("錫特卡", "補給港", 10351, 2258, "Sitka|矽地卡"),
        new("邱吉爾", "補給港", 12337, 1966, "Churchill"),
        new("烏斯懷亞", "補給港", 13263, 6853, "Ushuaia"),
    ];
}
