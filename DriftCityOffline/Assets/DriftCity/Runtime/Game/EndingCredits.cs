using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DriftCity.Core;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The story ending (DriftCity_RE/UNLABELLED.md section 2). The last step of the ROO and OMD main quest lines in
    /// QuestClient.tdf ends with the markup tag &lt;ending ROO&gt; / &lt;ending OMD&gt;; the client's markup parser
    /// (0x70d320 / 0x70d370) starts the ending object (0x4e1030), which runs eight phases on one clock (0x4e0160):
    /// 0 Game/credits/fade.nif (2.167 s; the driver-information request goes out at 0.99 s), 1 the team's closing lines
    /// 10670-10674 (ROO) / 10675-10679 (OMD) at 20 / 24 / 28 s, 2 the "&lt;Driver Information&gt;" card 10680-10705
    /// (packet 0x5dd), 3 Game\credits\Credits_Main.dat, 4 the Game\credits\Credits.dat roll, 6 roo_and.nif / omd_and.nif
    /// (6.667 s), 7 back to the game (UI on, the city's music). Timed lines fade in and out over the "fade" time of
    /// Credits_Main.dat. The ending music is bgm/DriftCity_ending.mp3 (sound id 0xea79); the camera orbits the car 8 m out
    /// and 3 m up at 0.375 rad/s (0x4ddbd0).
    ///
    /// Remake: drawn with IMGUI in the client's 1024 x 768 layout scaled to the screen. Offline the card is filled from
    /// the PlayerProfile (fields the remake does not track show 0). (inferred) the roll moves 60 px a second (the client
    /// moves it one pixel a frame), the fade.nif / team NIFs are shown as fades and their .dds pictures.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed class EndingCredits : MonoBehaviour
    {
        public const float FadeNifSeconds = 2.167f, TeamPictureSeconds = 6.667f, DefaultFade = 0.5f;
        public const float ScrollPixelsPerSecond = 60f;
        public const float LayoutHeight = 768f, LayoutWidth = 1024f;

        public sealed class Line
        {
            public float Start, Duration, Y;
            public int Font, Column;
            public string Text = "", Image;
        }

        public static EndingCredits Current { get; private set; }
        /// <summary>Test hook: speeds the clock up.</summary>
        public static float TimeScale = 1f;

        public bool Roo;
        public int Phase;
        public float PhaseTime;
        public float Fade = DefaultFade;
        public Transform Target;
        public Action Done;
        public List<Line> TeamLines = new List<Line>(), CardLines = new List<Line>(), MainLines = new List<Line>(), RollLines = new List<Line>();
        readonly Dictionary<string, Texture2D> images = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        float orbitAngle;
        GUIStyle[] styles;
        Texture2D black;

        // ---------- data ----------

        /// <summary>
        /// Credits_Main.dat (0x4df290): line 1 a ruler, line 2 "fade &lt;seconds&gt;" (0.5 when missing), then rows
        /// "start duration y font text" (tab separated; y in the 768-high layout, the client adds screen height - 768).
        /// </summary>
        public static List<Line> ParseMain(string text, out float fade)
        {
            fade = DefaultFade;
            var list = new List<Line>();
            var lines = (text ?? "").Replace("\r", "").Split('\n');
            if (lines.Length > 1)
            {
                var f = lines[1].Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 2 && float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) fade = v;
            }
            for (int i = 2; i < lines.Length; i++)
            {
                var t = lines[i].Split('\t');
                if (t.Length < 5 || !float.TryParse(t[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var start)) continue;
                float.TryParse(t[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dur);
                int.TryParse(t[2], out var y); int.TryParse(t[3], out var font);
                list.Add(new Line { Start = start, Duration = dur, Y = y, Font = font, Text = string.Join("\t", t.Skip(4)).Trim() });
            }
            return list;
        }

        /// <summary>
        /// Credits.dat (0x4df7e0): rows "text &lt;tab&gt; font &lt;tab&gt; picture" ("0" = none); "**********" adds 45 px;
        /// [SCROLL] = centred, [SCROLL0] = left column, [SCROLL1] = right column starting back at [SCROLL0]'s offset.
        /// Every row starts one screen below the top (y = 768 + offset), a row with a picture 128 px lower (the picture,
        /// 320 px wide, is drawn 195 px above its text, Game\credits\dds\&lt;picture&gt;); each row adds 45 px.
        /// </summary>
        public static List<Line> ParseRoll(string text)
        {
            var list = new List<Line>();
            int offset = 0, column = 0, scroll0Start = 0;
            var lines = (text ?? "").Replace("\r", "").Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                string raw = lines[i];
                if (raw.Trim().Length == 0) continue;
                var t = raw.Split('\t');
                string key = t[0].Trim();
                if (key == "[SCROLL]") { column = 0; continue; }
                if (key == "[SCROLL0]") { scroll0Start = offset; column = 1; continue; }
                if (key == "[SCROLL1]") { column = 2; offset = scroll0Start; continue; }
                if (key == "**********") { offset += 45; continue; }
                var line = new Line { Text = key, Column = column };
                if (t.Length > 1) int.TryParse(t[1].Trim(), out line.Font);
                string pic = t.Length > 2 ? t[2].Trim() : "0";
                if (pic.Length > 0 && pic != "0") { line.Image = pic; offset += 128; }
                line.Y = LayoutHeight + offset;
                list.Add(line);
                offset += 45;
            }
            return list;
        }

        /// <summary>0x4e0270: the closing lines at 20 / 24 / 28 s, 4 s each, at y 590 / 655 of the layout.</summary>
        public static List<Line> TeamLinesFor(bool roo)
        {
            int b = roo ? 10670 : 10675;
            return new List<Line>
            {
                new Line { Start = 20, Duration = 4, Y = 0x24e, Font = 2, Text = MissionText.Get((b + 0).ToString()) },
                new Line { Start = 20, Duration = 4, Y = 0x28f, Font = 2, Text = MissionText.Get((b + 1).ToString()) },
                new Line { Start = 24, Duration = 4, Y = 0x24e, Font = 2, Text = MissionText.Get((b + 2).ToString()) },
                new Line { Start = 24, Duration = 4, Y = 0x28f, Font = 2, Text = MissionText.Get((b + 3).ToString()) },
                new Line { Start = 28, Duration = 4, Y = 0x24e, Font = 2, Text = MissionText.Get((b + 4).ToString()) },
            };
        }

        /// <summary>
        /// 0x4e0430: "&lt;Driver Information&gt;" (10680) and twenty-one label / value pairs (10681-10701), one pair every 3 s from 7 s
        /// (label at y 590, value at y 655). Online the server's packet 0x5dd fills it; offline the profile does.
        /// </summary>
        public static List<Line> DriverCard(PlayerProfile p)
        {
            p = p ?? new PlayerProfile();
            string car = "";
            try { if (p.Cars.Count > 0) car = GameTables.Car(p.Cars[Mathf.Clamp(p.CurrentCar, 0, p.Cars.Count - 1)].VehicleIndex)?.UiName ?? ""; } catch { }
            int played = Mathf.FloorToInt(Time.realtimeSinceStartup / 60f);
            string hm(int minutes) => MissionText.Get("10703", minutes / 60, minutes % 60);
            string n(long v) => MissionText.Get("10704", v);
            var values = new[]
            {
                p.Name, p.Created ?? "", p.Name, "", "",
                hm(played), hm(played), MissionText.Get("10730", p.DistanceKm).Replace("%.0f", Mathf.RoundToInt(p.DistanceKm).ToString()),
                car, n(p.Cars.Count), n(p.Cars.Count), n(p.Level), n(p.Exp), p.Mito.ToString(CultureInfo.InvariantCulture) + " Mito",
                $"{p.PvpWins} / {Mathf.Max(0, p.PvpRaces - p.PvpWins)}", "0 / 0", n(p.HuvCaught), n(0), n(0), n(0), hm(0),
            };
            var list = new List<Line> { new Line { Start = 4, Duration = 3 * values.Length + 3, Y = 0x200, Font = 1, Text = MissionText.Get("10680") } };
            for (int k = 0; k < values.Length; k++)
            {
                float start = 7 + 3 * k;
                list.Add(new Line { Start = start, Duration = 3, Y = 0x24e, Font = 2, Text = MissionText.Get((10681 + k).ToString()) });
                list.Add(new Line { Start = start, Duration = 3, Y = 0x28f, Font = 2, Text = values[k] });
            }
            return list;
        }

        /// <summary>Alpha of a timed line: fades in and out over <paramref name="fade"/> seconds (0x4ddcb0).</summary>
        public static float LineAlpha(Line l, float t, float fade)
        {
            if (t < l.Start || t > l.Start + l.Duration) return 0;
            float into = t - l.Start, left = l.Start + l.Duration - t;
            if (fade <= 0) return 1;
            return Mathf.Clamp01(Mathf.Min(into, left) / fade);
        }

        /// <summary>Length of a timed phase: the last line's end.</summary>
        public static float PhaseLength(List<Line> lines) => lines.Count == 0 ? 0 : lines.Max(l => l.Start + l.Duration);

        // ---------- running ----------

        /// <summary>Starts the ending for team ROO (true) or OMD (false); <paramref name="done"/> runs when it is over.</summary>
        public static EndingCredits Play(bool roo, Action done = null, Transform target = null)
        {
            if (Current) Destroy(Current.gameObject);
            var e = new GameObject("Ending " + (roo ? "ROO" : "OMD")).AddComponent<EndingCredits>();
            e.Roo = roo;
            e.Done = done;
            e.Target = target ? target : (CitySession.Current && CitySession.Current.Car ? CitySession.Current.Car.transform : null);
            var fs = DriftCityData.Files;
            float fade = DefaultFade;
            if (fs != null)
            {
                if (fs.Exists(@"game\credits\credits_main.dat")) e.MainLines = ParseMain(System.Text.Encoding.Default.GetString(fs.Read(@"game\credits\credits_main.dat")), out fade);
                if (fs.Exists(@"game\credits\credits.dat")) e.RollLines = ParseRoll(System.Text.Encoding.Default.GetString(fs.Read(@"game\credits\credits.dat")));
            }
            e.Fade = fade;
            e.TeamLines = TeamLinesFor(roo);
            e.CardLines = DriverCard(PlayerProfile.Current);
            Current = e;
            if (GameFlow.Instance) AudioLibrary.PlayMusic(GameFlow.Instance, "bgm/DriftCity_ending.mp3");
            Debug.Log($"Drift City: ending {(roo ? "ROO" : "OMD")} ({e.RollLines.Count} credit rows)");
            return e;
        }

        void Update()
        {
            float dt = Time.deltaTime * TimeScale;
            PhaseTime += dt;
            switch (Phase)
            {
                case 0: if (PhaseTime >= FadeNifSeconds) Next(); break;
                case 1: if (PhaseTime > PhaseLength(TeamLines)) Next(); break;
                case 2: if (PhaseTime > PhaseLength(CardLines)) Next(); break;
                case 3: if (PhaseTime > PhaseLength(MainLines)) Next(); break;
                case 4:
                    foreach (var l in RollLines) l.Y -= ScrollPixelsPerSecond * dt;
                    if (RollLines.Count == 0 || RollLines[RollLines.Count - 1].Y < -40) Next();
                    break;
                case 5: Next(); break;
                case 6: if (PhaseTime >= TeamPictureSeconds) Next(); break;
                default: Finish(); break;
            }
            orbitAngle += 0.375f * dt;
        }

        void Next() { Phase++; PhaseTime = 0; }

        void Finish()
        {
            if (GameFlow.Instance) AudioLibrary.PlayMusic(GameFlow.Instance, CityMusic.For(GameFlow.Instance.City));
            var d = Done; Done = null;
            if (Current == this) Current = null;
            Destroy(gameObject);
            d?.Invoke();
        }

        void LateUpdate()
        {
            // 0x4ddbd0: the camera circles the car 8 m out, 3 m up, looking at it
            var cam = Camera.main;
            if (!cam || !Target) return;
            var p = Target.position;
            var eye = p + new Vector3(Mathf.Cos(orbitAngle) * 8f, 3f, Mathf.Sin(orbitAngle) * 8f);
            cam.transform.position = eye;
            cam.transform.LookAt(p + Vector3.up * 0.8f);
        }

        Texture2D Image(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (images.TryGetValue(name, out var t)) return t;
            try { t = TextureLibrary.Get(name, @"game\credits\dds"); } catch { t = null; }
            images[name] = t;
            return t;
        }

        void OnGUI()
        {
            if (styles == null)
            {
                Font f = null;
                try { f = Font.CreateDynamicFontFromOSFont(new[] { "Tahoma", "Arial" }, 16); } catch { }
                styles = new GUIStyle[4];
                int[] sizes = { 16, 24, 20, 18 };
                for (int i = 0; i < 4; i++)
                    styles[i] = new GUIStyle { font = f, fontSize = sizes[i], alignment = TextAnchor.MiddleCenter, richText = false };
                black = new Texture2D(1, 1); black.SetPixel(0, 0, Color.black); black.Apply();
            }
            float scale = Screen.height / LayoutHeight;
            float width = Screen.width / scale;
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            switch (Phase)
            {
                case 0:
                {
                    // fade.nif: dark in, dark out (inferred shape)
                    float a = 1f - Mathf.Abs(PhaseTime / FadeNifSeconds * 2f - 1f);
                    GUI.color = new Color(0, 0, 0, a);
                    GUI.DrawTexture(new Rect(0, 0, width, LayoutHeight), black);
                    break;
                }
                case 1: DrawTimed(TeamLines, width); break;
                case 2: DrawTimed(CardLines, width); break;
                case 3: DrawTimed(MainLines, width); break;
                case 4:
                    foreach (var l in RollLines)
                    {
                        if (l.Y < -260 || l.Y > LayoutHeight + 40) continue;
                        float x = l.Column == 1 ? 0 : l.Column == 2 ? width / 2 : 0, w = l.Column == 0 ? width : width / 2;
                        var img = Image(l.Image);
                        if (img) { GUI.color = Color.white; GUI.DrawTexture(new Rect(x + w / 2 - 160, l.Y - 195, 320, 180), img, ScaleMode.ScaleToFit); }
                        Text(l.Text, new Rect(x, l.Y - 20, w, 40), l.Font, l.Font == 0 ? Color.white : new Color(1f, 0.85f, 0.3f), 1);
                    }
                    break;
                case 6:
                {
                    var pic = Image(Roo ? "roo_and.dds" : "omd_and.dds");
                    float a = Mathf.Clamp01(Mathf.Min(PhaseTime, TeamPictureSeconds - PhaseTime) / 1f);
                    GUI.color = new Color(0, 0, 0, a);
                    GUI.DrawTexture(new Rect(0, 0, width, LayoutHeight), black);
                    if (pic) { GUI.color = new Color(1, 1, 1, a); GUI.DrawTexture(new Rect(0, 0, width, LayoutHeight), pic, ScaleMode.ScaleToFit); }
                    break;
                }
            }
            GUI.color = Color.white;
            GUI.matrix = old;
        }

        void DrawTimed(List<Line> lines, float width)
        {
            foreach (var l in lines)
            {
                float a = LineAlpha(l, PhaseTime, Fade);
                if (a <= 0) continue;
                Text(l.Text, new Rect(0, l.Y - 20, width, 40), l.Font, new Color(1, 1, 1, a), a);
            }
        }

        void Text(string s, Rect r, int font, Color c, float alpha)
        {
            if (string.IsNullOrEmpty(s)) return;
            var st = styles[Mathf.Clamp(font, 0, styles.Length - 1)];
            // the client draws each line four times one pixel off in the outline colour, then the text (0x4ddcb0)
            st.normal.textColor = new Color(0, 0, 0, alpha);
            foreach (var o in new[] { new Vector2(-1, 0), new Vector2(1, 0), new Vector2(0, -1), new Vector2(0, 1) })
                GUI.Label(new Rect(r.x + o.x, r.y + o.y, r.width, r.height), s, st);
            st.normal.textColor = c;
            GUI.Label(r, s, st);
        }

        void OnDestroy() { if (Current == this) Current = null; }
    }
}
