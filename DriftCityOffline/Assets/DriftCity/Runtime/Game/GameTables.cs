using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.Core;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>A player car row from init\vehiclelist.xlt.</summary>
    public sealed class VehicleInfo
    {
        public int Index;
        public int UniqueId;
        public string Maker, CarName, UiName, FileName;
        public int Accel, Speed, Crash, Boost, ReqLevel, Grade;
        public float LengthMm, WidthMm, HeightMm;
        public bool Sellable;
        public int TireId;
        /// <summary>vehiclelist "Tire Scale Front" is the tire diameter in mm.</summary>
        public float TireDiameterMm;
        /// <summary>The four factory colours offered at creation and in the dealer: paint ids (effects\VehicleColor.nxt)
        /// from vehiclelist's Default_Color columns (Body0 of each Body0/Window pair).</summary>
        public int[] DefaultColors = { 8, 1, 16, 23 };
        /// <summary>vehiclelist CamN / CamH: extra driving-camera distance and height (m) for this model.</summary>
        public float CamN, CamH;
        public float TireRadius => TireDiameterMm > 200 ? Mathf.Clamp(TireDiameterMm / 2000f, 0.24f, 0.5f) : CarFactory.DefaultTireRadius;

        /// <summary>"pc_0000c" from "pc_0000c.nif".</summary>
        public string ModelBase => System.IO.Path.GetFileNameWithoutExtension(FileName ?? "").ToLowerInvariant();
        /// <summary>vehiclelist OldFileName without extension ("ac01_01_a"): the key of the car's UI images (name plates, car key).</summary>
        public string OldModel;
        /// <summary>"c" models take a front-bumper body (_bNN) plus a hood part (_iNN); "s" models ship a
        /// whole body per aero kit (_NN).</summary>
        public bool ComboBody => ModelBase.EndsWith("c");
        public string BodyNif(int variant = 1) => ComboBody ? $"vehicle\\player\\{ModelBase}_b{variant:00}.nif" : $"vehicle\\player\\{ModelBase}_{variant:00}.nif";
    }

    /// <summary>Game content tables loaded from the installed client (Init.agt / Localize.agt).</summary>
    public static class GameTables
    {
        public static List<VehicleInfo> PlayerCars { get; private set; }
        /// <summary>Traffic car NIFs (vehiclelist "Traffic" rows).</summary>
        public static List<string> TrafficModels { get; private set; } = new List<string>();
        /// <summary>HUV rows (fleeing vehicles in chases) and HUV bosses.</summary>
        public static List<VehicleInfo> Huvs { get; private set; } = new List<VehicleInfo>();
        public static List<VehicleInfo> BossHuvs { get; private set; } = new List<VehicleInfo>();
        static Dictionary<string, string> globalText;
        static Dictionary<int, string> tireFiles;

        /// <summary>
        /// A city's traffic as the client spawns it (init\trafficflowcontrol.xlt): the models it uses, each with its
        /// spawn frequency (1, 3 or 6), and how many traffic cars drive at once. Cities are numbered by chapter:
        /// moonpalace 1st, koinonia 2nd, cras 3rd, oros 4th.
        /// </summary>
        public static (List<(string nif, int weight)> models, int count) TrafficFor(string city)
        {
            var result = new List<(string, int)>();
            int index = Array.IndexOf(new[] { "moonpalace", "koinonia", "cras", "oros" }, city);
            var fs = DriftCityData.Files;
            if (index < 0 || !fs.Exists(@"init\trafficflowcontrol.xlt")) return (result, 0);
            var data = fs.Read(@"init\trafficflowcontrol.xlt");
            string text = data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE ? System.Text.Encoding.Unicode.GetString(data, 2, data.Length - 2) : Cp949.Decode(data);
            int section = -1, count = 0;
            foreach (var line in text.Split('\n'))
            {
                var c = line.TrimEnd('\r').Split('\t');
                // each city's block starts with "Value  <n>차도시  <CntTrafficCarAll> ..."
                if (c.Length > 2 && c[0] == "Value" && c[1].Contains("도시")) { section++; if (section == index) int.TryParse(c[2], out count); continue; }
                if (section != index || c.Length < 8 || !int.TryParse(c[0], out _) || !int.TryParse(c[3], out int weight)) continue;
                string nif = $@"vehicle\traffic\{c[7].Trim()}";
                if (fs.Exists(nif)) result.Add((nif, Mathf.Max(1, weight)));
            }
            return (result, count);
        }

        public static void Load()
        {
            if (PlayerCars != null) return;
            var fs = DriftCityData.Files;
            PlayerCars = new List<VehicleInfo>();
            // vehiclelist has a group-header row; real column names are on row 3
            var vl = XltTable.Parse(fs.Read(@"init\vehiclelist.xlt"), headerRow: 3);
            foreach (var r in vl.Rows)
            {
                string desc = vl.Get(r, "Desc");
                string fileName = vl.Get(r, "File Name");
                string trafficPath = $@"vehicle\traffic\{fileName}";
                if (desc == "Traffic" && trafficPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) && fs.Exists(trafficPath) && !TrafficModels.Contains(trafficPath))
                    TrafficModels.Add(trafficPath);
                if ((desc == "HUV" || desc == "HUV BOSS") && fs.Exists($@"vehicle\huv\{fileName}"))
                {
                    var h = new VehicleInfo { Index = vl.GetInt(r, "Index"), UniqueId = vl.GetInt(r, "Unique Id"), CarName = vl.Get(r, "Car Name"),
                                              UiName = vl.Get(r, "UI Name"), FileName = fileName, Speed = vl.GetInt(r, "Speed"), Crash = vl.GetInt(r, "Crash"),
                                              ReqLevel = vl.GetInt(r, "Req Level") };
                    (desc == "HUV" ? Huvs : BossHuvs).Add(h);
                }
                if (desc != "PlayerCar" || vl.Get(r, "Support") != "1") continue;
                var file = vl.Get(r, "File Name");
                if (string.IsNullOrEmpty(file)) continue;
                var v = new VehicleInfo
                {
                    Index = vl.GetInt(r, "Index"),
                    UniqueId = vl.GetInt(r, "Unique Id"),
                    Maker = vl.Get(r, "Maker"),
                    CarName = vl.Get(r, "Car Name"),
                    UiName = vl.Get(r, "UI Name"),
                    FileName = file,
                    Accel = vl.GetInt(r, "Accel"), Speed = vl.GetInt(r, "Speed"),
                    Crash = vl.GetInt(r, "Crash"), Boost = vl.GetInt(r, "Boost"),
                    ReqLevel = vl.GetInt(r, "Req Level"), Grade = vl.GetInt(r, "Lvl"),
                    LengthMm = vl.GetFloat(r, "length"), WidthMm = vl.GetFloat(r, "width"), HeightMm = vl.GetFloat(r, "height"),
                    Sellable = vl.Get(r, "Sellable") != "0",
                    TireId = vl.GetInt(r, "Tire Id_01"),
                    TireDiameterMm = vl.GetFloat(r, "Tire Scale Front"),
                    CamN = vl.GetFloat(r, "CamN"), CamH = vl.GetFloat(r, "CamH"),
                    OldModel = System.IO.Path.GetFileNameWithoutExtension(vl.Get(r, "OldFileName") ?? ""),
                };
                int body0 = System.Array.FindIndex(vl.Columns, c => c.Trim() == "Body0");
                if (body0 >= 0)
                    for (int k = 0; k < 4; k++)
                        if (body0 + 2 * k < r.Length && int.TryParse(r[body0 + 2 * k].Trim(), out int colorId) && colorId > 0) v.DefaultColors[k] = colorId;
                if (fs.Exists(v.BodyNif())) PlayerCars.Add(v);
            }
            Debug.Log($"Drift City: {PlayerCars.Count} player cars, {TrafficModels.Count} traffic models, {Huvs.Count} HUVs, {BossHuvs.Count} boss HUVs");
        }

        /// <summary>Cars offered at character creation: the level-1 starters.</summary>
        public static List<VehicleInfo> StarterCars => PlayerCars.Where(v => v.ReqLevel <= 1).Take(8).ToList();

        public static VehicleInfo Car(int index) => PlayerCars.FirstOrDefault(v => v.Index == index) ?? PlayerCars.FirstOrDefault();

        /// <summary>US-English text from localize\globaltext.xlt by key (falls back to the key).</summary>
        public static string Text(string key)
        {
            if (globalText == null)
            {
                globalText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var t = XltTable.Parse(DriftCityData.Files.Read(@"localize\globaltext.xlt"));
                foreach (var r in t.Rows)
                {
                    var k = t.Get(r, "Key");
                    var v = t.Get(r, "Value_US");
                    if (string.IsNullOrEmpty(v)) v = t.Get(r, "Value_KR");
                    if (!string.IsNullOrEmpty(k)) globalText[k] = v;
                }
            }
            return globalText.TryGetValue(key, out var s) ? s : key;
        }

        /// <summary>The car's stock wheels (vehiclelist "Tire Id_01" -> tirelist file), else the first player tire.</summary>
        public static string TireNif(VehicleInfo v)
        {
            if (tireFiles == null)
            {
                tireFiles = new Dictionary<int, string>();
                var t = XltTable.Parse(DriftCityData.Files.Read(@"init\tirelist.xlt"));
                foreach (var r in t.Rows)
                {
                    int id = t.GetInt(r, "Tire ID", -1);
                    string file = t.Get(r, "File Name");
                    if (id >= 0 && !string.IsNullOrEmpty(file) && t.Get(r, "CarPrototype") == "PlayerCar")
                        tireFiles[id] = @"vehicle\player\wheel\" + file.ToLowerInvariant();
                }
            }
            if (v != null && tireFiles.TryGetValue(v.TireId, out var path) && DriftCityData.Files.Exists(path)) return path;
            return @"vehicle\player\wheel\tire_ply0001.nif";
        }

        static Dictionary<string, TrafficWheelSpec> trafficWheels;
        static Dictionary<int, TireInfo> tireInfos;

        /// <summary>
        /// The wheels of a traffic / HUV model by its body NIF path (vehiclelist TireCnt, RearCnt, Tire Id_01..Tr10;
        /// the first row naming the file wins). Null for unknown models.
        /// </summary>
        public static TrafficWheelSpec TrafficWheels(string bodyNif)
        {
            if (trafficWheels == null)
            {
                trafficWheels = new Dictionary<string, TrafficWheelSpec>(StringComparer.OrdinalIgnoreCase);
                var fs = DriftCityData.Files;
                if (fs != null && fs.Exists(@"init\vehiclelist.xlt"))
                {
                    var vl = XltTable.Parse(fs.Read(@"init\vehiclelist.xlt"), headerRow: 3);
                    var idColumns = new[] { "Tire Id_01", "Tr02", "Tr03", "Tr04", "Tr05", "Tr06", "Tr07", "Tr08", "Tr09", "Tr10" };
                    foreach (var r in vl.Rows)
                    {
                        string desc = vl.Get(r, "Desc"), file = vl.Get(r, "File Name").Trim();
                        string folder = desc == "Traffic" ? "traffic" : desc == "HUV" || desc == "HUV BOSS" ? "huv" : null;
                        if (folder == null || file.Length == 0) continue;
                        string key = $@"vehicle\{folder}\{file}";
                        if (trafficWheels.ContainsKey(key)) continue;
                        int count = Mathf.Clamp(vl.GetInt(r, "TireCnt"), 0, idColumns.Length);
                        trafficWheels[key] = new TrafficWheelSpec
                        {
                            TireCount = count,
                            RearCount = Mathf.Clamp(vl.GetInt(r, "RearCnt"), 0, count),
                            TireIds = idColumns.Select(c => vl.GetInt(r, c, -1)).ToArray(),
                        };
                    }
                }
            }
            return bodyNif != null && trafficWheels.TryGetValue(Core.NayaPack.NormalizePath(bodyNif), out var s) ? s : null;
        }

        /// <summary>
        /// A tirelist row (any car type). The client's loader (0x4b48e0) keeps Diameter(mm) * 0.0005 as the radius and
        /// Width(mm) * 0.001 as the width; the model folder follows the id range like 0x46c460 (player / huv / traffic).
        /// </summary>
        public static TireInfo Tire(int id)
        {
            if (tireInfos == null)
            {
                tireInfos = new Dictionary<int, TireInfo>();
                var fs = DriftCityData.Files;
                if (fs != null && fs.Exists(@"init\tirelist.xlt"))
                {
                    var t = XltTable.Parse(fs.Read(@"init\tirelist.xlt"));
                    foreach (var r in t.Rows)
                    {
                        int tid = t.GetInt(r, "Tire ID", -1);
                        string file = t.Get(r, "File Name").Trim();
                        if (tid < 0 || file.Length == 0 || tireInfos.ContainsKey(tid)) continue;
                        string folder = tid < 1001 ? "player" : tid < 2001 ? "huv" : tid < 3001 ? "traffic" : "player";
                        tireInfos[tid] = new TireInfo
                        {
                            Id = tid,
                            Nif = $@"vehicle\{folder}\wheel\{file.ToLowerInvariant()}",
                            Radius = t.GetFloat(r, "Diameter(mm)") * 0.0005f,
                            Width = t.GetFloat(r, "Width(mm)") * 0.001f,
                        };
                    }
                }
            }
            return tireInfos.TryGetValue(id, out var info) ? info : null;
        }

        static List<AiDriverInfo> aiDrivers;

        /// <summary>The Battle Zone AI drivers (init\aicarparam.xlt; empty without the table).</summary>
        public static List<AiDriverInfo> AiDrivers
        {
            get
            {
                if (aiDrivers != null) return aiDrivers;
                aiDrivers = new List<AiDriverInfo>();
                var fs = DriftCityData.Files;
                try
                {
                    if (fs != null && fs.Exists(@"init\aicarparam.xlt"))
                    {
                        var t = XltTable.Parse(fs.Read(@"init\aicarparam.xlt"));
                        foreach (var r in t.Rows)
                        {
                            string name = t.Get(r, "AI Car Name").Trim();
                            int level = t.GetInt(r, "AI Lv");
                            if (name.Length == 0 || level <= 0) continue;
                            var ids = t.Get(r, "Car Model Unique").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(s => int.TryParse(s, out int id) ? id : -1).Where(id => id > 0).ToArray();
                            aiDrivers.Add(new AiDriverInfo { Index = t.GetInt(r, "Idx"), Name = name, Level = level, CarUniqueIds = ids });
                        }
                    }
                }
                catch (Exception ex) { Debug.LogWarning("aicarparam.xlt: " + ex.Message); }
                return aiDrivers;
            }
        }

        /// <summary>
        /// The player cars an AI driver may drive: its Car Model Unique ids that are player cars here (ids of 10000+ are
        /// event / traffic models the remake's AI cannot drive).
        /// </summary>
        public static List<VehicleInfo> AiCars(AiDriverInfo ai) =>
            ai == null || PlayerCars == null ? new List<VehicleInfo>() : PlayerCars.Where(c => ai.CarUniqueIds.Contains(c.UniqueId)).ToList();
    }

    /// <summary>
    /// A Battle Zone AI driver of init\aicarparam.xlt (loader 0x49dfe0 -> generic row reader 0x7e1a40, 0xc4-byte
    /// records kept at +0x60 of the AI manager): name ("[AI] Elio"), AI level (1..99, the player level scale: the
    /// level 1-4 rows drive the starter cars), the Unique Ids of the cars it may drive, and driving numbers (km/h speeds,
    /// cornering, boost) the remake's AI does not use (its drivers take a skill).
    /// </summary>
    public sealed class AiDriverInfo
    {
        public int Index, Level;
        public string Name;
        public int[] CarUniqueIds = new int[0];
    }

    /// <summary>vehiclelist wheel columns of a traffic / HUV row: wheels 0..TireCount-1, the last RearCount on the trailer.</summary>
    public sealed class TrafficWheelSpec
    {
        public int TireCount, RearCount;
        public int[] TireIds;
        public int TireId(int wheel) => TireIds != null && wheel >= 0 && wheel < TireIds.Length ? TireIds[wheel] : -1;
    }

    /// <summary>A tirelist row: model path, radius and width in metres.</summary>
    public sealed class TireInfo
    {
        public int Id;
        public string Nif;
        public float Radius, Width;
    }
}
