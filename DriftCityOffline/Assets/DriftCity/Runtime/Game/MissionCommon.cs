using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// Shared pieces of the station missions (Delivery Service, instant missions, patrols): the client's text table with
    /// its printf formats, the post (icon table) lookup across cities, the World/Data tables, the per-driver records,
    /// minimap markers, the 3D destination beacon and the original dialogs they use. See DriftCity_RE/MISSIONS.md.
    /// </summary>
    public static class MissionText
    {
        static readonly Regex Spec = new Regex(@"%(-?)(0?)(\d*)([sdui])", RegexOptions.Compiled);

        /// <summary>globaltext.xlt entry <paramref name="key"/> (e.g. "10133") filled like the client's sprintf.</summary>
        public static string Get(string key, params object[] args) => Printf(GameTables.Text(key), args);

        /// <summary>%s %d %u %i with optional zero padding / width, as the client's format strings use them.</summary>
        public static string Printf(string fmt, params object[] args)
        {
            if (string.IsNullOrEmpty(fmt)) return "";
            int i = 0;
            return Spec.Replace(fmt, m =>
            {
                object a = i < args.Length ? args[i++] : "";
                string s;
                if (m.Groups[4].Value == "s") s = Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture);
                else
                {
                    long n = 0;
                    try { n = Convert.ToInt64(a, System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { }
                    s = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                int width = m.Groups[3].Value.Length > 0 ? int.Parse(m.Groups[3].Value) : 0;
                if (s.Length < width) s = m.Groups[1].Value == "-" ? s.PadRight(width) : s.PadLeft(width, m.Groups[2].Value == "0" ? '0' : ' ');
                return s;
            }).Replace("%%", "%");
        }

        /// <summary>mm'ss"cc as the delivery records print times (ARBEITPLAYINFO_BEST "00'00\"00").</summary>
        public static string Clock(float seconds)
        {
            if (seconds < 0) seconds = 0;
            int cs = Mathf.FloorToInt(seconds * 100f);
            return $"{cs / 6000:00}'{cs / 100 % 60:00}\"{cs % 100:00}";
        }
    }

    /// <summary>Posts ("post_1st_00052" ...) of every city's icon table (World/Data/Icon_&lt;city&gt;_Client.tdf).</summary>
    public static class MissionPosts
    {
        static readonly Dictionary<string, Dictionary<string, CityMapInfo.Icon>> byCity = new Dictionary<string, Dictionary<string, CityMapInfo.Icon>>();

        /// <summary>City of a post id from its table prefix: 1st Moon Palace, 2nd Koinonia, 3rd Cras, 4rd/4th Oros, 5th Taipei.</summary>
        public static string CityOf(string postId)
        {
            if (string.IsNullOrEmpty(postId)) return null;
            if (postId.StartsWith("post_1st")) return "moonpalace";
            if (postId.StartsWith("post_2nd")) return "koinonia";
            if (postId.StartsWith("post_3rd")) return "cras";
            if (postId.StartsWith("post_4rd") || postId.StartsWith("post_4th")) return "oros";
            if (postId.StartsWith("post_5th")) return "taipei";   // the Taipei step quests (QuestClient.tdf kind 3)
            return null;
        }

        public static CityMapInfo.Icon Find(string postId)
        {
            string city = CityOf(postId);
            if (city == null) return null;
            if (!byCity.TryGetValue(city, out var map))
            {
                map = new Dictionary<string, CityMapInfo.Icon>(StringComparer.OrdinalIgnoreCase);
                foreach (var i in CityMapInfo.Icons(city)) if (!map.ContainsKey(i.Id)) map[i.Id] = i;
                byCity[city] = map;
            }
            return map.TryGetValue(postId, out var icon) ? icon : null;
        }

        /// <summary>Horizontal distance (the table heights are rough; the car may sit a few metres off them).</summary>
        public static float Flat(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;
    }

    /// <summary>The client's World/Data/*.tdf tables (CityMapInfo.TdfRows), with the older data\ copies as fallback.</summary>
    public static class MissionTables
    {
        public static List<string[]> Read(string fileName)
        {
            var fs = DriftCityData.Files;
            if (fs == null) return new List<string[]>();
            foreach (var path in new[] { $@"data\world\data\{fileName}", $@"world\data\{fileName}", $@"data\{fileName}" })
            {
                if (!fs.Exists(path)) continue;
                try { return CityMapInfo.TdfRows(fs.Read(path)); }
                catch (Exception ex) { Debug.LogWarning($"{path}: {ex.Message}"); }
            }
            return new List<string[]>();
        }

        public static int Int(string[] row, int col, int fallback = 0) =>
            col < row.Length && int.TryParse(row[col].Trim(), out int v) ? v : fallback;

        public static float Float(string[] row, int col, float fallback = 0) =>
            col < row.Length && float.TryParse(row[col].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;

        public static string Str(string[] row, int col) => col < row.Length ? row[col] ?? "" : "";
    }

    /// <summary>
    /// A driver's delivery records (best times and medals per job) and mission counters. The profile class is shared with
    /// the server, so these live in their own file next to the save (online drivers: one file per driver name).
    /// </summary>
    [Serializable]
    public sealed class MissionRecords
    {
        [Serializable]
        public sealed class Job
        {
            public string Id;
            public List<float> Best = new List<float>();   // fastest first, at most three (ARBEIT_BEST0..2)
            public int Medal;                               // best medal: 1 gold, 2 silver, 3 bronze, 0 none
            public int Runs, Completed;
        }

        public List<Job> Jobs = new List<Job>();
        public int DeliveriesCompleted, Golds, InstantsCompleted, PatrolPosts, PatrolsCompleted;
        /// <summary>Mita 500 drinks (MitaDrink): the UTC day and bottles bought that day, the effect value and its end (ISO time).</summary>
        public string MitaDay = "", MitaUntil = "";
        public int MitaCount, MitaValue = 100;
        /// <summary>Rush Time and Virtual HUV counters (RushTime, VirtualChase).</summary>
        public int RushesJoined, RushFinalBlows, VirtualWins, VirtualPlayed;

        static MissionRecords current;
        static string currentPath;

        public static string PathFor(PlayerProfile p)
        {
            if (p != null && p.OnlineDriver)
            {
                string safe = Regex.Replace(p.Name ?? "driver", @"[^A-Za-z0-9_-]", "_");
                return System.IO.Path.Combine(PlayerProfile.SaveDirOverride ?? Application.persistentDataPath, $"online_{safe}.missions.json");
            }
            return PlayerProfile.SavePath + ".missions.json";
        }

        /// <summary>The current driver's records (loaded on first use, reloaded when the driver or slot changes).</summary>
        public static MissionRecords Current
        {
            get
            {
                string path = PathFor(PlayerProfile.Current);
                if (current != null && currentPath == path) return current;
                currentPath = path;
                current = null;
                try { if (File.Exists(path)) current = JsonUtility.FromJson<MissionRecords>(File.ReadAllText(path)); }
                catch (Exception ex) { Debug.LogWarning("mission records: " + ex.Message); }
                current ??= new MissionRecords();
                current.Jobs ??= new List<Job>();
                current.Jobs.RemoveAll(j => j == null || string.IsNullOrEmpty(j.Id));
                foreach (var j in current.Jobs) j.Best ??= new List<float>();
                return current;
            }
        }

        public Job For(string id)
        {
            var j = Jobs.Find(x => x.Id == id);
            if (j == null) { j = new Job { Id = id }; Jobs.Add(j); }
            return j;
        }

        /// <summary>Records a finished delivery; returns true for a new personal best.</summary>
        public bool AddTime(string id, float seconds, int medal)
        {
            var j = For(id);
            j.Completed++;
            bool best = j.Best.Count == 0 || seconds < j.Best[0];
            j.Best.Add(seconds);
            j.Best.Sort();
            if (j.Best.Count > 3) j.Best.RemoveRange(3, j.Best.Count - 3);
            if (medal > 0 && (j.Medal == 0 || medal < j.Medal)) j.Medal = medal;
            DeliveriesCompleted++;
            if (medal == 1) Golds++;
            return best;
        }

        public void Save()
        {
            if (string.IsNullOrEmpty(currentPath)) return;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(currentPath));
                var tmp = currentPath + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(this, true));
                if (File.Exists(currentPath)) File.Delete(currentPath);
                File.Move(tmp, currentPath);
            }
            catch (Exception ex) { Debug.LogWarning("mission records save: " + ex.Message); }
        }

        /// <summary>Forgets the cached records (tests switch save folders).</summary>
        public static void Reset() { current = null; currentPath = null; }
    }

    /// <summary>
    /// The mission systems added after the station missions, attached to a city session in one call (GameFlow.LoadCity,
    /// after PatrolMission.Attach): Mita 500 spots, Rush Time, the Virtual HUV registration, Auto-Drive and the push router.
    /// </summary>
    public static class MissionExtras
    {
        public static void Attach(CitySession session, string city)
        {
            if (!session) return;
            MissionNet.Ensure();
            MitaSpots.Attach(session, city);
            RushTime.Attach(session, city);
            QuestPlayInfo.Attach(session);
            StepQuestRunner.Attach(session, city);   // QuestClient.tdf step quests (stations, quest givers, the steps)
        }
    }

    /// <summary>
    /// Points the minimap should show for the running missions: the next delivery destination
    /// (ID_MINIMAP_ICON_ARBEIT) and the patrol's next posts (ID_MINIMAP_ICON_PATROL).
    /// </summary>
    public static class MissionMarkers
    {
        public struct Marker
        {
            public string Icon;       // game.nui 190000 icon control to copy (Minimap.MakeBlip)
            public Vector3 Position;
            public string Label;
        }

        public static List<Marker> All()
        {
            var list = new List<Marker>();
            if (DeliveryService.Active) list.AddRange(DeliveryService.Active.Markers());
            if (PatrolMission.Active) list.AddRange(PatrolMission.Active.Markers());
            if (UndercityStage.Active) list.AddRange(UndercityStage.Active.Markers());
            else if (UndercityEntrance.Instance) list.AddRange(UndercityEntrance.Instance.Markers());
            if (MitaSpots.Instance && !UndercityStage.Active) list.AddRange(MitaSpots.Instance.Markers());
            if (StepQuestRunner.Instance && !UndercityStage.Active) list.AddRange(StepQuestRunner.Instance.Markers());
            if (RushTime.Instance && !UndercityStage.Active) list.AddRange(RushTime.Instance.Markers());
            return list;
        }
    }

    /// <summary>
    /// The destination marker in the world: the client's post marker NIF (game\cylinder_*.nif, preloaded by 0x6b03e0)
    /// standing on the road, plus a tall translucent column so it can be seen from afar (as the HUV beacon).
    /// </summary>
    public sealed class MissionBeacon : MonoBehaviour
    {
        GameObject column, marker;

        public static MissionBeacon Create(string name, string nif, Color color)
        {
            var b = new GameObject(name).AddComponent<MissionBeacon>();
            b.column = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(b.column.GetComponent<Collider>());
            b.column.name = name + " Column";
            b.column.transform.SetParent(b.transform, false);
            b.column.transform.localScale = new Vector3(3f, 60f, 3f);
            b.column.transform.localPosition = new Vector3(0, 60f, 0);
            var shader = Shader.Find("Sprites/Default");
            if (shader) b.column.GetComponent<Renderer>().sharedMaterial = OwnedAssets.Track(b.gameObject, new Material(shader) { color = color });
            try
            {
                if (!string.IsNullOrEmpty(nif) && DriftCityData.Files != null && DriftCityData.Files.Exists(nif))
                {
                    b.marker = new NifSceneBuilder().Load(nif, b.transform);
                    if (b.marker)
                    {
                        b.marker.transform.localPosition = Vector3.zero;
                        foreach (var c in b.marker.GetComponentsInChildren<Collider>()) Destroy(c);
                        // the marker is drawn in metres like the city; guard against an odd scale
                        var rs = b.marker.GetComponentsInChildren<Renderer>();
                        if (rs.Length > 0)
                        {
                            var bounds = rs[0].bounds;
                            foreach (var r in rs) bounds.Encapsulate(r.bounds);
                            if (bounds.size.magnitude < 0.5f || bounds.size.magnitude > 200f) { Destroy(b.marker); b.marker = null; }
                        }
                    }
                }
            }
            catch (Exception ex) { Debug.LogWarning($"beacon {nif}: {ex.Message}"); }
            return b;
        }

        /// <summary>Stands the beacon on the ground at a post (the table height is only approximate).</summary>
        public void MoveTo(Vector3 p)
        {
            var pos = p;
            if (Physics.Raycast(p + Vector3.up * 60f, Vector3.down, out var hit, 160f, ~0, QueryTriggerInteraction.Ignore)) pos = hit.point;
            transform.position = pos;
            gameObject.SetActive(true);
        }

        public void Hide() => gameObject.SetActive(false);
    }

    /// <summary>The original dialogs the missions share: QUESTDIALOG (600000) and the "Call Mission" notice (860000).</summary>
    public static class MissionUi
    {
        // ---------------- QUESTDIALOG: the NPC's lines with Accept / Deny ----------------
        static NuiView script;
        static RawImage portrait;
        static List<Core.ScriptPage> pages;
        static int page;

        public static bool ScriptOpen => script && UiManager.Instance && UiManager.Instance.IsOpen(script);

        /// <summary>
        /// Shows a table "ment" (&lt;pimg&gt; &lt;name&gt; &lt;text&gt;/&lt;sub_text&gt; markup) in QUESTDIALOG. With
        /// <paramref name="accept"/> the last page offers ACCEPT/DENY, else DONE.
        /// </summary>
        public static NuiView Script(string ment, string fallbackTitle, bool accept, Action onAccept, Action onClose) =>
            Script(ment, fallbackTitle, accept, onAccept, onClose, "ID_QUESTDIALOG_ACCEPT", null, null);

        /// <summary>
        /// QUESTDIALOG with another accept button: RECV ("receive the request") for a step quest, REWARD for its reward
        /// (QuestClient quests, StepQuestRunner); <paramref name="prevQuest"/> / <paramref name="nextQuest"/> show the big
        /// PREVNEXT buttons that step through a station's quests.
        /// </summary>
        public static NuiView Script(string ment, string fallbackTitle, bool accept, Action onAccept, Action onClose, string acceptButton, Action prevQuest, Action nextQuest)
        {
            acceptButton ??= "ID_QUESTDIALOG_ACCEPT";
            var ui = UiManager.Instance;
            if (!ui) return null;
            pages = Core.NpcScript.Parse((ment ?? "").Replace("<sub_text", "<text"));
            if (pages.Count == 0) pages.Add(new Core.ScriptPage { Speaker = fallbackTitle });
            CloseScript();
            script = ui.Open(600000);
            if (!script) return null;
            page = 0;
            foreach (var id in new[] { "ID_QUESTDIALOG_RECV", "ID_QUESTDIALOG_REWARD", "ID_QUESTDIALOG_PREVNEXT_PREV", "ID_QUESTDIALOG_PREVNEXT_NEXT",
                                        "ID_QUESTDIALOG_WHATISUC", "ID_QUESTDIALOG_UCACCEPT", "ID_QUESTDIALOG_VCDENY", "ID_QUESTDIALOG_VC44", "ID_QUESTDIALOG_VC88" })
                script.SetVisible(id, false);
            Action render = null;
            render = () =>
            {
                if (!script) return;
                var pg = pages[page];
                script.SetText("ID_QUESTDIALOG_NAME", pg.Speaker ?? fallbackTitle);
                script.SetText("ID_QUESTDIALOG_CONTENT_1ST", string.Join("\n", pg.Lines.Select(l => l.Replace("$name", PlayerProfile.Current?.Name ?? "Driver"))));
                script.SetText("ID_QUESTDIALOG_CONTENT_2ND", "");
                script.SetText("ID_QUESTDIALOG_CONTENT_3RD", "");
                bool last = page == pages.Count - 1;
                script.SetVisible("ID_QUESTDIALOG_PREV", page > 0);
                script.SetVisible("ID_QUESTDIALOG_NEXT", !last);
                script.SetVisible("ID_QUESTDIALOG_ACCEPT", false);
                script.SetVisible(acceptButton, last && accept);
                script.SetVisible("ID_QUESTDIALOG_DENY", accept);
                script.SetVisible("ID_QUESTDIALOG_DONE", last && !accept);
                script.SetVisible("ID_QUESTDIALOG_PREVNEXT_PREV", prevQuest != null);
                script.SetVisible("ID_QUESTDIALOG_PREVNEXT_NEXT", nextQuest != null);
                ShowPortrait(pg.Portrait);
            };
            render();
            ui.On("ID_QUESTDIALOG_NEXT", _ => { page = Mathf.Min(page + 1, pages.Count - 1); render(); });
            ui.On("ID_QUESTDIALOG_PREV", _ => { page = Mathf.Max(page - 1, 0); render(); });
            ui.On(acceptButton, _ => onAccept?.Invoke());
            if (prevQuest != null) ui.On("ID_QUESTDIALOG_PREVNEXT_PREV", _ => prevQuest());
            if (nextQuest != null) ui.On("ID_QUESTDIALOG_PREVNEXT_NEXT", _ => nextQuest());
            ui.On("ID_QUESTDIALOG_DENY", _ => { CloseScript(); onClose?.Invoke(); });
            ui.On("ID_QUESTDIALOG_DONE", _ => { CloseScript(); onClose?.Invoke(); });
            return script;
        }

        /// <summary>Replaces the lines of the open QUESTDIALOG (another job picked in the list).</summary>
        public static void ScriptLines(string ment, string fallbackTitle)
        {
            if (!script) return;
            var p = Core.NpcScript.Parse((ment ?? "").Replace("<sub_text", "<text"));
            if (p.Count == 0) return;
            pages = p;
            page = pages.Count - 1;
            var pg = pages[page];
            script.SetText("ID_QUESTDIALOG_NAME", pg.Speaker ?? fallbackTitle);
            script.SetText("ID_QUESTDIALOG_CONTENT_1ST", string.Join("\n", pg.Lines.Select(l => l.Replace("$name", PlayerProfile.Current?.Name ?? "Driver"))));
            script.SetVisible("ID_QUESTDIALOG_PREV", false);
            script.SetVisible("ID_QUESTDIALOG_NEXT", false);
            script.SetVisible("ID_QUESTDIALOG_ACCEPT", true);
            ShowPortrait(pg.Portrait);
        }

        static void ShowPortrait(string name)
        {
            if (!script) return;
            if (!portrait)
            {
                var rt = NuiView.NewRect("Portrait", script.transform);
                rt.anchorMin = rt.anchorMax = new Vector2(0, 0);
                rt.pivot = new Vector2(0, 0);
                rt.anchoredPosition = new Vector2(10, 10);
                rt.sizeDelta = new Vector2(300, 420);
                rt.SetAsFirstSibling();
                portrait = rt.gameObject.AddComponent<RawImage>();
                portrait.raycastTarget = false;
            }
            var tex = string.IsNullOrEmpty(name) ? null : UiAssets.Texture(name + ".dds");
            portrait.texture = tex;
            portrait.enabled = tex;
            portrait.uvRect = new Rect(0, 1, 1, -1);
            if (tex) ((RectTransform)portrait.transform).sizeDelta = new Vector2(420f * tex.width / Mathf.Max(1, tex.height), 420);
        }

        public static void CloseScript()
        {
            if (script && UiManager.Instance) UiManager.Instance.Close(script);
            script = null; portrait = null;
        }

        // ---------------- INSTANTMISSION 860000: "Call Mission" (Esc Refuse / Tab Accept) ----------------
        public static NuiView CallMission(Action accept, Action refuse)
        {
            var ui = UiManager.Instance;
            if (!ui) return null;
            var old = ui.Find(860000);
            if (old) ui.Close(old);
            var v = ui.Open(860000);
            if (!v) return null;
            // NOTIFY1..4 are four art variants of the banner (K=0); the client shows one
            int pick = UnityEngine.Random.Range(1, 5);
            for (int i = 1; i <= 4; i++) v.SetVisible("ID_INSTANTMISSION_NOTIFY" + i, i == pick);
            AudioLibrary.PlaySfx("sfx/SID_SYSWIN_CREATE");
            ui.On("ID_INSTANTMISSION_TABBUTTON", view => { ui.Close(view); AudioLibrary.PlaySfx("sfx/SID_ARBEIT_ACCEPT"); accept?.Invoke(); });
            ui.On("ID_INSTANTMISSION_CANCEL", view => { ui.Close(view); refuse?.Invoke(); });
            return v;
        }

        /// <summary>Tab presses the banner's Accept (NATION J = 9); called from the owners' Update.</summary>
        public static bool TabPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            return kb != null && !Net.ChatBox.Typing && kb.tabKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Tab);
#endif
        }

        // ---------------- digit art ----------------

        /// <summary>The image of a control (its first element).</summary>
        public static RawImage Image(NuiView v, string id)
        {
            var c = v ? v.Control(id) : null;
            return c ? c.GetComponentInChildren<RawImage>(true) : null;
        }

        /// <summary>
        /// ARBEITPLAYINFO 820000: the run time as digit art. NUM_0..9 hold the digit images (hidden, as the client only
        /// copies them); TIME_6 TIME_5 minutes, TIME_4 TIME_3 seconds, POINT, TIME_2..TIME_0 thousandths.
        /// </summary>
        public static void TimeDigits(NuiView v, float seconds)
        {
            if (!v) return;
            int ms = Mathf.Clamp(Mathf.FloorToInt(seconds * 1000f), 0, 99 * 60000 + 59999);
            int m = ms / 60000, s = ms / 1000 % 60, f = ms % 1000;
            int[] d = { f % 10, f / 10 % 10, f / 100, s % 10, s / 10, m % 10, m / 10 };
            for (int k = 0; k < 7; k++)
            {
                var dst = Image(v, "ID_ARBEITPLAYINFO_TIME_" + k);
                var src = Image(v, "ID_ARBEITPLAYINFO_TIME_NUM_" + d[k]);
                if (!dst || !src) continue;
                dst.texture = src.texture; dst.uvRect = src.uvRect; dst.enabled = src.texture;
            }
        }

        /// <summary>A plain text label inside a control's rectangle (for boxes the client fills by drawing).</summary>
        public static Text Label(Transform parent, Vector2 pos, Vector2 size, int fontSize, Color color, TextAnchor align = TextAnchor.UpperLeft)
        {
            var rt = NuiView.NewRect("label", parent);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var t = rt.gameObject.AddComponent<Text>();
            t.font = UiAssets.Font("Calibri " + fontSize, out _, out _);
            t.fontSize = fontSize; t.color = color; t.alignment = align; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Truncate;
            return t;
        }

        /// <summary>
        /// Adds the dialog's offset G to its screen-anchored controls whose F equals the dialog's anchor E. NuiView anchors those controls by F
        /// alone; dialogs such as ARBEITPLAYINFONEW 800000 (E=12, G=-1024 0, controls F=12 at H=839..) and ARBEITPLAYINFOADV
        /// 810000 (E=10, G=-1024 0) place them at anchor + G + H, so without G they land off the screen.
        /// </summary>
        public static void ApplyDialogOffset(NuiView v)
        {
            if (!v || v.GetComponent<DialogOffsetApplied>()) return;
            v.gameObject.AddComponent<DialogOffsetApplied>();
            if (!Core.NuiControl.TryVec2(Core.NuiControl.Str(v.Dialog.Attr, "G"), out float gx, out float gy) || (gx == 0 && gy == 0)) return;
            foreach (var c in v.GetComponentsInChildren<NuiControlView>(true))
                if (c.transform.parent == v.transform && Core.NuiControl.Int(c.Data.Nation, "F", 0) == v.Dialog.Anchor && v.Dialog.Anchor != 0)
                    ((RectTransform)c.transform).anchoredPosition += new Vector2(gx, -gy);
        }

        sealed class DialogOffsetApplied : MonoBehaviour { }

        /// <summary>A Yes/No popup with the client's text.</summary>
        public static void Confirm(string text, Action yes, Action no = null) =>
            UiPopup.Show(text, b => { if (b == UiPopup.Yes) yes?.Invoke(); else no?.Invoke(); }, UiPopup.Yes, UiPopup.No);
    }

    /// <summary>
    /// The running-mission list: INSTANTMISSION 240000 for the ADV HUD (up to five rows of title, progress and a click
    /// button) or, with the basic main UI (goption MainUIType 0; the client tests `**(ui+0x16c) == 0`, 0x5b4140),
    /// INSTANTMISSION 230000: one row (CONTENT1ST title, CONTENT2ND progress, BTN) under four tabs - TAB1ST Instant, TAB2ND
    /// Patrol, TAB3RD Daily, TAB4TH Virtual HUV (handler 0x5c6270 -> 0x5c56d0 / 0x5c5810 / 0x5c5950 / 0x5c3df0). Used for
    /// instant missions and patrols; clicking a row opens its confirm dialog (give up / close).
    /// </summary>
    public static class MissionTracker
    {
        public sealed class Entry
        {
            public object Owner;
            public string Label, Title, Progress;
            public Action Click;
        }

        static readonly List<Entry> entries = new List<Entry>();
        static NuiView view;
        /// <summary>The basic HUD's list (230000) instead of the ADV one; null = follow GameOptions.MainUIType.</summary>
        public static bool? Classic;
        /// <summary>230000's selected tab: 0 Instant, 1 Patrol, 2 Daily, 3 Virtual HUV.</summary>
        public static int Tab;
        public const int ClassicId = 230000, AdvId = 240000;
        static bool UseClassic => Classic ?? GameOptions.MainUIType == 0;

        /// <summary>The 230000 tab of an entry by its owner.</summary>
        public static int TabOf(Entry e) => e.Owner is PatrolMission ? 1 : e.Owner is VirtualChase ? 3 : 0;

        public static IReadOnlyList<Entry> Entries => entries;
        public static NuiView View => view;

        public static Entry Set(object owner, string label, string title, string progress, Action click)
        {
            var e = entries.Find(x => x.Owner == owner);
            if (e == null) { e = new Entry { Owner = owner }; entries.Add(e); }
            e.Label = label; e.Title = title; e.Progress = progress; e.Click = click;
            Refresh();
            return e;
        }

        public static void Remove(object owner)
        {
            entries.RemoveAll(x => x.Owner == owner || x.Owner == null || (x.Owner is UnityEngine.Object o && !o));
            Refresh();
        }

        public static void Refresh()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            entries.RemoveAll(x => x.Owner == null || (x.Owner is UnityEngine.Object o && !o));
            if (entries.Count == 0) { if (view) ui.Close(view); view = null; return; }
            int want = UseClassic ? ClassicId : AdvId;
            if (view && view.Dialog.Id != want) { ui.Close(view); view = null; }
            if (!view || !ui.IsOpen(view)) view = ui.Open(want);
            if (!view) return;
            if (want == ClassicId)
            {
                // E=17 G=0 44 puts the row on the chat window in our layout: lift it above, as the ADV list is
                ((RectTransform)view.transform).anchoredPosition = new Vector2(0, 170);
                RefreshClassic(ui);
                return;
            }
            // the rows sit at the chat window's height in our ADV layout: lift the list so its last row ends above it
            ((RectTransform)view.transform).anchoredPosition = new Vector2(0, 190 + 55 * (Mathf.Min(entries.Count, 5) - 1));
            for (int i = 0; i < 5; i++)
            {
                string n = $"ID_INSTANTMISSION_INMISSION_ADV_{i + 1:00}_";
                bool on = i < entries.Count;
                foreach (var part in new[] { "BTN", "CONTENT1ST", "CONTENT2ND", "TITLE", "TITLETEXT" }) view.SetVisible(n + part, on);
                if (!on) continue;
                var e = entries[i];
                // TITLETEXT carries NATION F=10, which our layout anchors to the screen top: put it on its TITLE tab
                var tab = view.Control(n + "TITLE"); var label = view.Control(n + "TITLETEXT");
                if (tab && label && label.transform.parent != tab.transform.parent)
                {
                    var lrt = (RectTransform)label.transform; var trt = (RectTransform)tab.transform;
                    lrt.SetParent(trt.parent, false);
                    lrt.anchorMin = trt.anchorMin; lrt.anchorMax = trt.anchorMax; lrt.pivot = trt.pivot;
                    lrt.anchoredPosition = trt.anchoredPosition + new Vector2(1, -2);
                }
                view.SetText(n + "TITLETEXT", e.Label);
                view.SetText(n + "CONTENT1ST", e.Title);
                view.SetText(n + "CONTENT2ND", e.Progress);
                var click = e.Click;
                ui.On(n + "BTN", _ => click?.Invoke());
            }
        }

        /// <summary>230000: the selected tab's mission (0x5c4640 draws "%d/%d achieved" in CONTENT2ND).</summary>
        static void RefreshClassic(UiManager ui)
        {
            // a tab without a mission falls back to the first tab that has one
            if (!entries.Exists(x => TabOf(x) == Tab)) Tab = TabOf(entries[0]);
            var e = entries.Find(x => TabOf(x) == Tab);
            string[] tabs = { "TAB1ST", "TAB2ND", "TAB3RD", "TAB4TH" };
            for (int i = 0; i < 4; i++)
            {
                var c = view.Control("ID_INSTANTMISSION_INMISSION_" + tabs[i]);
                if (c) c.Selected = i == Tab;
                int tab = i;
                ui.On("ID_INSTANTMISSION_INMISSION_" + tabs[i], _ => { Tab = tab; Refresh(); });
            }
            view.SetText("ID_INSTANTMISSION_INMISSION_CONTENT1ST", e?.Title ?? "");
            view.SetText("ID_INSTANTMISSION_INMISSION_CONTENT2ND", e?.Progress ?? "");
            view.SetVisible("ID_INSTANTMISSION_INMISSION_CONTENT1ST", true);
            view.SetVisible("ID_INSTANTMISSION_INMISSION_CONTENT2ND", true);
            view.SetVisible("ID_INSTANTMISSION_INMISSION_BTN", e != null);
            var click = e?.Click;
            ui.On("ID_INSTANTMISSION_INMISSION_BTN", _ => click?.Invoke());
        }
    }
}
