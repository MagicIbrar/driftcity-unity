using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The client's per-city map tables (DriftCity.exe, see DriftCity_RE/MINIMAP.md): world bounds at 0x871a48
    /// (city*16: xmin, ymin, xmax, ymax in Gamebryo units = Unity x, z), minimap image table at 0x871ac8 (city*24:
    /// texture w, h and the crop rectangle x0, y0, x1, y1 that the world bounds cover), the parking lot's bounds coded in
    /// 0x5f2d80/0x5f2290/0x5f2320, and the minimap zoom table written by the minimap init 0x604300.
    /// </summary>
    public sealed class CityMapInfo
    {
        /// <summary>City index of the client (0 = parking lot, 1 moonpalace .. 5 taipei).</summary>
        public readonly int Index;
        /// <summary>Minimap image picked by 0x5f2050.</summary>
        public readonly string Image;
        public readonly float XMin, YMin, XMax, YMax;
        public readonly int TexW, TexH, X0, Y0, X1, Y1;
        /// <summary>Zoom index the client starts with (0x5f2050: 1 in a city, 3 in the parking lot).</summary>
        public readonly int DefaultZoom;

        /// <summary>Minimap zoom steps (this+0x154..0x16c, 0x604526); this+0x150 indexes them, 0.0 ends the table.</summary>
        public static readonly float[] Zoom = { 1f, 2f, 3f, 4f, 5.5f, 8f, 10f };
        /// <summary>The disc's radius in texture units is Zoom * 0.0575 (0x871c2c, 0x5f6200).</summary>
        public const float UvPerZoom = 0.0575f;
        /// <summary>The map disc: a 50-vertex fan (step 0.1256637 = 2pi/50) of radius 103.5 px about the BASE centre.</summary>
        public const float DiscRadius = 103.5f;
        public const int DiscVertices = 50;
        /// <summary>The mask texture (ID_MINIMAP_MASK's image) spans uv 0..0.9159664 across the disc (0x871c28).</summary>
        public const float MaskUvSpan = 0.9159664f;
        /// <summary>Icons beyond 90 px (0x86f87c) are pinned to the rim at x 90*sx+5, y 95 (0x871c38) and drawn at alpha 0x55.</summary>
        public const float IconRadius = 90f, IconRimX = 95f, IconRimY = 95f;
        /// <summary>Icon pixels per (world offset / world width) at zoom 1: 90 / 0.05 (0x5f6840) = 103.5 / 0.0575.</summary>
        public const float PixelsPerFraction = 1800f;

        CityMapInfo(int index, string image, float xmin, float ymin, float xmax, float ymax, int tw, int th, int x0, int y0, int x1, int y1, int zoom)
        {
            Index = index; Image = image;
            XMin = xmin; YMin = ymin; XMax = xmax; YMax = ymax;
            TexW = tw; TexH = th; X0 = x0; Y0 = y0; X1 = x1; Y1 = y1;
            DefaultZoom = zoom;
        }

        static readonly CityMapInfo[] Cities =
        {
            null,
            new CityMapInfo(1, "_moonpalace.dds", -8169, -5379, 8082, 3980, 1024, 512, 0, 0, 1024, 512, 1),
            new CityMapInfo(2, "_koinonia.dds", 0, 0, 10046, 14511, 1024, 1024, 0, 0, 1024, 1024, 1),
            new CityMapInfo(3, "_cras.dds", 0, 0, 10000, 8063, 1024, 1024, 0, 0, 1024, 1024, 1),
            new CityMapInfo(4, "_oros.dds", 644, 390, 12656, 14786, 1024, 1024, 0, 0, 1024, 1024, 1),
            new CityMapInfo(5, "_taipei.dds", 0, 0, 19900, 15480, 1024, 1024, 410, 368, 768, 827, 1),
        };
        static readonly CityMapInfo Parking1F = new CityMapInfo(0, "_parking.dds", -461, -461, 461, 461, 1024, 1024, 0, 0, 1024, 1024, 3);
        static readonly CityMapInfo ParkingB1 = new CityMapInfo(0, "_parkingB1.dds", -357, -380, 357, 380, 1024, 1024, 0, 0, 1024, 1024, 3);

        public static int CityIndex(string city)
        {
            switch ((city ?? "").ToLowerInvariant())
            {
                case "moonpalace": return 1;
                case "koinonia": return 2;
                case "cras": return 3;
                case "oros": return 4;
                case "taipei": return 5;
                default: return -1;
            }
        }

        /// <summary>The map of a city by name, or null for one the client has no map for.</summary>
        public static CityMapInfo ForCity(string city)
        {
            int i = CityIndex(city);
            return i > 0 ? Cities[i] : null;
        }

        /// <summary>The parking lot's floor map (0x761900()+100: 0 = 1F _parking.dds, 1 = B1 _parkingB1.dds).</summary>
        public static CityMapInfo ForParking(bool basement) => basement ? ParkingB1 : Parking1F;

        /// <summary>The parking lot map for the floor whose image is <paramref name="mapImage"/>.</summary>
        public static CityMapInfo ForParkingImage(string mapImage) => ForParking(mapImage != null && mapImage.ToLowerInvariant().Contains("b1"));

        public float Width => XMax - XMin;
        public float Height => YMax - YMin;

        /// <summary>
        /// Position across the world bounds: x from the west edge, y from the NORTH edge (1 - (y - ymin) / h), as
        /// 0x5f6200 and the full map's 0x5f3e90 compute it. Unity x = Gamebryo x, Unity z = Gamebryo y.
        /// </summary>
        public Vector2 Fraction(Vector3 unityPos) => new Vector2((unityPos.x - XMin) / Width, 1f - (unityPos.z - YMin) / Height);

        /// <summary>Minimap texture coordinate of a world position (D3D convention: v grows downward), 0x5f6200.</summary>
        public Vector2 TexUv(Vector3 unityPos)
        {
            var f = Fraction(unityPos);
            return new Vector2((f.x * TexW - X0) / (X1 - X0), (f.y * TexH - Y0) / (Y1 - Y0));
        }

        /// <summary>Disc radius in texture u at a zoom step; the v radius is this times Width / Height.</summary>
        public float UvRadius(int zoom) => Zoom[Mathf.Clamp(zoom, 0, Zoom.Length - 1)] * UvPerZoom;
        public float VRadiusScale => Width / Height;

        /// <summary>Minimap pixels per world unit (x east, y north) at a zoom step: 0x5f6840's icon scale.</summary>
        public Vector2 PixelsPerUnit(int zoom)
        {
            float k = PixelsPerFraction / Zoom[Mathf.Clamp(zoom, 0, Zoom.Length - 1)] / Width;
            return new Vector2(k * TexW / (X1 - X0), k * TexH / (Y1 - Y0));
        }

        // ---------------- icon tables (World/Data/Icon_<city>_Client.tdf, loaded by 0x709440) ----------------

        /// <summary>One row of a city's icon table.</summary>
        public sealed class Icon
        {
            /// <summary>Row id, e.g. "post_3rd_00043".</summary>
            public string Id;
            /// <summary>Id without the running number: "post_3rd" (places), "post_3rd_y" (delivery service points),
            /// "post_1st_WP" (world patrol), "post_1st_tQ" (training waypoints), "post_1st_HMUC" (undercity), ...</summary>
            public string Kind;
            /// <summary>Display name ("Station 1", "Cras Police Station", "Police Station_rq" ...).</summary>
            public string Name;
            /// <summary>Position in Unity coordinates (the table holds Gamebryo x, y, z).</summary>
            public Vector3 Position;
            /// <summary>Column 4 ("True"/"False"); true for the named landmarks, false for quest/event points.</summary>
            public bool Shown;
            /// <summary>Column 5 (0 in every shipped row).</summary>
            public int Value;
            /// <summary>"Station 1".."Station 4": the city's RQ houses (mission stations).</summary>
            public bool IsStation => StationNumber > 0;
            public int StationNumber
            {
                get
                {
                    var m = System.Text.RegularExpressions.Regex.Match(Name ?? "", @"^Station (\d)$");
                    return m.Success ? int.Parse(m.Groups[1].Value) : 0;
                }
            }
            public override string ToString() => $"{Id} '{Name}' {Position}";
        }

        static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Icon>> iconCache =
            new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Icon>>();

        /// <summary>File name part of a city's icon table (0x87a1c4..0x87a250); Cras's is "Craspeda".</summary>
        public static string IconTableName(string city)
        {
            switch (CityIndex(city))
            {
                case 1: return "moonpalace";
                case 2: return "koinonia";
                case 3: return "craspeda";
                case 4: return "oros";
                case 5: return "taipei";
                default: return null;
            }
        }

        /// <summary>
        /// The icon table rows of a city (empty when it has none). The client reads World/Data/Icon_&lt;city&gt;_Client.tdf;
        /// installs without that copy (Taipei) fall back to the older data\icon_*.tdf.
        /// </summary>
        public static System.Collections.Generic.List<Icon> Icons(string city)
        {
            var name = IconTableName(city);
            if (name == null) return new System.Collections.Generic.List<Icon>();
            if (iconCache.TryGetValue(name, out var cached)) return cached;
            var list = new System.Collections.Generic.List<Icon>();
            var fs = DriftCityData.Files;
            foreach (var path in new[] { $@"data\world\data\icon_{name}_client.tdf", $@"world\data\icon_{name}_client.tdf", $@"data\icon_{name}_client.tdf" })
            {
                if (fs == null || !fs.Exists(path)) continue;
                try
                {
                    foreach (var row in TdfRows(fs.Read(path)))
                    {
                        if (row.Length < 3 || string.IsNullOrEmpty(row[0])) continue;
                        var nums = System.Text.RegularExpressions.Regex.Matches(row[2], @"-?\d+(?:\.\d+)?");
                        if (nums.Count < 3) continue;
                        float F(int i) => float.Parse(nums[i].Value, System.Globalization.CultureInfo.InvariantCulture);
                        int.TryParse(row.Length > 4 ? row[4] : "0", out int value);
                        list.Add(new Icon
                        {
                            Id = row[0],
                            Kind = System.Text.RegularExpressions.Regex.Replace(row[0], @"_?\d+$", ""),
                            Name = row[1],
                            Position = new Vector3(F(0), F(2), F(1)),
                            Shown = row.Length > 3 && row[3].Trim().Equals("true", System.StringComparison.OrdinalIgnoreCase),
                            Value = value,
                        });
                    }
                }
                catch (System.Exception ex) { Debug.LogWarning($"icon table {path}: {ex.Message}"); list.Clear(); continue; }
                break;
            }
            iconCache[name] = list;
            return list;
        }

        /// <summary>The city's mission stations (RQ houses), ordered Station 1..4.</summary>
        public static System.Collections.Generic.List<Icon> Stations(string city)
        {
            var list = Icons(city).FindAll(i => i.IsStation);
            list.Sort((a, b) => a.StationNumber.CompareTo(b.StationNumber));
            return list;
        }

        /// <summary>
        /// Rows of a client .tdf table: a BMP (its size at offset 2) followed by the table header at that offset —
        /// u16 1, u16 4, u32 date, u16 0, u16 2, u32 data end, u32 columns, u32 rows — then rows*columns u32 cell
        /// offsets (relative to the header) and the cells, each a NUL-terminated UTF-16LE string.
        /// RQHouseMent_&lt;city&gt;_Client.tdf has the same layout (one column of staff dialogue markup).
        /// </summary>
        public static System.Collections.Generic.List<string[]> TdfRows(byte[] d)
        {
            var rows = new System.Collections.Generic.List<string[]>();
            if (d == null || d.Length < 32) return rows;
            int h = d[0] == (byte)'B' && d[1] == (byte)'M' ? System.BitConverter.ToInt32(d, 2) : 0;
            if (h < 0 || h + 24 > d.Length) return rows;
            int end = System.BitConverter.ToInt32(d, h + 12);
            int cols = System.BitConverter.ToInt32(d, h + 16), count = System.BitConverter.ToInt32(d, h + 20);
            if (cols <= 0 || count < 0 || h + 24 + (long)cols * count * 4 > d.Length) return rows;
            int Off(int i) => i < cols * count ? System.BitConverter.ToInt32(d, h + 24 + i * 4) : end;
            for (int r = 0; r < count; r++)
            {
                var cells = new string[cols];
                for (int c = 0; c < cols; c++)
                {
                    int a = h + Off(r * cols + c), b = h + Off(r * cols + c + 1);
                    if (a < 0 || b > d.Length || b < a) { cells[c] = ""; continue; }
                    var s = System.Text.Encoding.Unicode.GetString(d, a, (b - a) & ~1);
                    int nul = s.IndexOf('\0');
                    cells[c] = nul >= 0 ? s.Substring(0, nul) : s;
                }
                rows.Add(cells);
            }
            return rows;
        }
    }
}
