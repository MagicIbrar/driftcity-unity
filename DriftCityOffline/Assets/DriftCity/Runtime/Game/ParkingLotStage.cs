using System;
using System.Collections;
using System.Collections.Generic;
using DriftCity.Core;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DriftCity.Game
{
    /// <summary>
    /// The client's parking lot stage (stage 9, "Driver Dome"; DriftCity_RE/PARKINGLOT.md): the hub a driver arrives in
    /// after login and from a city's RQ house. World/parkinglot/parkinglot.nif (+ _fx.nif) is drawn, floor_p.hit and
    /// wall_p.hit collide, and every frame the ground under the car is picked from above in the hidden zone meshes
    /// (0x761a30): the _fx.nif strips name the buildings (garage, autoshop, parts, auction, battle, teamcenter), the
    /// _a.nif nodes area_exit / area_f1 / area_b1. Driving into a zone opens it (0x763840): an exit asks for a gate
    /// (SELGATE) and loads that city, garage / autoshop / parts open their menus, closed buildings say "Restricted
    /// Area" and put the car back where it last stood outside every zone (0x7629b0).
    /// </summary>
    public sealed class ParkingLotStage : MonoBehaviour
    {
        /// <summary>Zone codes of 0x761a30.</summary>
        public enum Area { None = 0, Exit = 1, Parts = 2, Garage = 3, Auction = 4, TeamCenter = 5, Battle = 6, AutoShop = 7, F1 = 8, B1 = 9 }

        /// <summary>The city gates the SELGATE rows offer ("Gate %2d - &lt;city&gt;", texts 10861-10864).</summary>
        public static readonly (string City, string Name)[] Gates =
        {
            ("moonpalace", "Moon Palace"), ("koinonia", "Koinonia"), ("cras", "Cras"), ("oros", "Oros"),
        };

        public const string Music = "bgm/DriftCity_parkinglot.mp3";
        /// <summary>Half the width in metres that the 1024 px floor maps (_parking.dds, VIEWMAP_P_1) cover around the dome centre
        /// (measured from the building icons on the image, about 1.165 px per metre).</summary>
        public const float MapHalfSize = 440f;
        const int GateRows = 3;
        /// <summary>globalcontent "SelectGate_PlayerCapacity": players for a full congestion bar.</summary>
        const int GateCapacity = 700;

        // arrival points of 0x762f70 (Gamebryo x, y) on the six exit axes; the car faces the dome centre
        static readonly Vector2[] Arrivals =
        {
            new Vector2(250, 0), new Vector2(120, 210), new Vector2(-120, 210),
            new Vector2(-250, 0), new Vector2(-120, -210), new Vector2(120, -210),
        };

        // floors 0 (F1) and 1 (B1); this install only ships F1, B1 is used when its files exist
        static readonly (string Nif, string FloorHit, string WallHit, string Map, int MapDialog)[] FloorFiles =
        {
            (@"parkinglot\parkinglot", @"parkinglot\floor_p.hit", @"parkinglot\wall_p.hit", "_parking.dds", 570000),
            (@"parkinglot\b1_parkinglot", @"parkinglot\b1_floor_p.hit", @"parkinglot\b1_wall_p.hit", "_parkingB1.dds", 580000),
        };

        public static ParkingLotStage Current { get; private set; }
        public static bool GateOpen => Current && Current.gateMain;
        /// <summary>Frame the gate list was closed with Esc (that Esc must not also open the quit menu).</summary>
        public static int GateClosedFrame = -1;
        /// <summary>globalcontent "ParkingLotBoost" = 1: the booster is filled once, on the first arrival of a run.</summary>
        public static bool BoostGiven { get; private set; }

        public DriftCar Car;
        public bool Active { get; private set; }
        public Area CurrentArea { get; private set; }
        public int Floor { get; private set; }
        public Bounds Bounds { get; private set; }
        public string MapImage => floors.Count > 0 ? FloorFiles[floors[Floor].Index].Map : FloorFiles[0].Map;
        public int MapDialog => floors.Count > 0 ? FloorFiles[floors[Floor].Index].MapDialog : FloorFiles[0].MapDialog;

        sealed class FloorData
        {
            public int Index;
            public GameObject Root, Colliders;
            public ZoneSet Areas, Buildings;
        }

        readonly List<FloorData> floors = new List<FloorData>();
        GameFlow flow;
        Vector3 freePos;
        Quaternion freeRot = Quaternion.identity;
        bool holding, leaving;
        NuiView gateBg, gateMain, gatePrev, gateTabs;
        int gatePage;

        // ---------------- world ----------------
        /// <summary>Loads both floors' geometry, zones and collision under <paramref name="world"/>.</summary>
        public IEnumerator Build(Transform world, Action<float> progress)
        {
            var fs = DriftCityData.Files;
            var root = new GameObject("Parking Lot").transform;
            root.SetParent(world, false);
            for (int i = 0; i < FloorFiles.Length; i++)
            {
                var files = FloorFiles[i];
                if (!fs.Exists(files.Nif + ".nif")) continue;
                var f = new FloorData { Index = i, Root = new GameObject(i == 0 ? "F1" : "B1") };
                f.Root.transform.SetParent(root, false);
                progress?.Invoke(0.1f + 0.4f * i);
                yield return null;
                var visual = new NifSceneBuilder { LogErrors = false }.Load(files.Nif + ".nif", f.Root.transform);
                // the dome roof would shadow the whole floor with Unity's shadow maps; the client draws no such shadow
                foreach (var r in visual.GetComponentsInChildren<Renderer>(true))
                    if (r.bounds.min.y > 6f || r.bounds.size.x > 300f) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                yield return null;
                if (fs.Exists(files.Nif + "_fx.nif"))
                {
                    // drawn (the red lights round the buildings); its hidden strips are the building zones
                    var fx = new NifSceneBuilder { LogErrors = false }.Load(files.Nif + "_fx.nif", f.Root.transform);
                    f.Buildings = ZoneSet.From(fx, false);
                    NifParticleEffect.Spawn(files.Nif + "_fx.nif", Vector3.zero, Quaternion.identity, f.Root.transform);
                }
                if (fs.Exists(files.Nif + "_a.nif"))
                {
                    // only picked, never attached to the scene (0x7620a0 keeps it apart from the drawn nodes)
                    var holder = new GameObject("Areas");
                    holder.SetActive(false);
                    var areas = new NifSceneBuilder { LogErrors = false }.Load(files.Nif + "_a.nif", holder.transform);
                    f.Areas = ZoneSet.From(areas, true);
                    Destroy(holder);
                }
                progress?.Invoke(0.35f + 0.4f * i);
                yield return null;
                f.Colliders = new GameObject("Collision");
                f.Colliders.transform.SetParent(f.Root.transform, false);
                HitCollider(files.FloorHit, f.Colliders.transform, true);
                HitCollider(files.WallHit, f.Colliders.transform, false);
                floors.Add(f);
            }
            // the floor signs (billboards) belong to the whole stage
            if (fs.Exists(@"parkinglot\parkinglot_icon.nif"))
                new NifSceneBuilder { LogErrors = false }.Load(@"parkinglot\parkinglot_icon.nif", root);
            var b = new Bounds(Vector3.zero, new Vector3(2 * MapHalfSize, 100f, 2 * MapHalfSize));
            Bounds = b;
            // 0x761e40: level 31+ drivers with the B1 permission arrive downstairs; nobody else
            SetFloor(0);
            progress?.Invoke(1f);
        }

        /// <summary>A .hit mesh as a static MeshCollider (Gamebryo z-up converted; the y/z swap mirrors the winding).</summary>
        static void HitCollider(string path, Transform parent, bool faceUp)
        {
            var fs = DriftCityData.Files;
            if (!fs.Exists(path)) return;
            var hit = HitMesh.Parse(fs.Read(path));
            if (hit.TriangleCount == 0) return;
            var verts = new Vector3[hit.Vertices.Length];
            for (int i = 0; i < verts.Length; i++) verts[i] = DriftCityData.ToUnity(hit.Vertices[i]);
            var idx = new int[hit.Indices.Length];
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = hit.Indices[t], b = hit.Indices[t + 2], c = hit.Indices[t + 1];
                // the wheels ray-cast the floor from above: keep every floor face pointing up
                if (faceUp && Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]).y < 0f) { int s = b; b = c; c = s; }
                idx[t] = a; idx[t + 1] = b; idx[t + 2] = c;
            }
            var go = new GameObject(System.IO.Path.GetFileName(path));
            go.transform.SetParent(parent, false);
            var mesh = OwnedAssets.Track(go, new Mesh { name = go.name });
            if (verts.Length > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.triangles = idx;
            mesh.RecalculateBounds();
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        void SetFloor(int index)
        {
            if (floors.Count == 0) return;
            index = Mathf.Clamp(index, 0, floors.Count - 1);
            Floor = index;
            for (int i = 0; i < floors.Count; i++) floors[i].Root.SetActive(i == index);
        }

        /// <summary>
        /// A random arrival point of 0x762f70, dropped onto the floor; a driver on the tutorial car gets the client's special
        /// spots instead (Apprentice.ArrivalSpot: the dome centre facing the Battle Zone, or by the Battle Zone after a failed
        /// lesson).
        /// </summary>
        public (Vector3 pos, Quaternion rot) ArrivalPose()
        {
            var special = Apprentice.ArrivalSpot(PlayerProfile.Current, ApprenticeCourse.ReturnStep);
            if (special.HasValue)
            {
                var s = special.Value;
                // Gamebryo heading h faces (cos h, sin h) on the x / y plane = Unity (cos h, 0, sin h)
                var at = new Vector3(s.pos.x, s.height, s.pos.y);
                var face = Quaternion.LookRotation(new Vector3(Mathf.Cos(s.heading), 0f, Mathf.Sin(s.heading)));
                Physics.SyncTransforms();
                if (Physics.Raycast(at + Vector3.up * 10f, Vector3.down, out var h, 40f)) at = h.point;
                return (at + Vector3.up, face);
            }
            var g = Arrivals[UnityEngine.Random.Range(0, Arrivals.Length)];
            var p = new Vector3(g.x, 0f, g.y);
            var rot = Quaternion.LookRotation(new Vector3(-g.x, 0f, -g.y).normalized);
            Physics.SyncTransforms();
            if (Physics.Raycast(p + Vector3.up * 10f, Vector3.down, out var hit, 40f)) p = hit.point;
            return (p + Vector3.up, rot);
        }

        // ---------------- stage ----------------
        public void Begin(GameFlow owner, DriftCar car)
        {
            Current = this;
            flow = owner;
            Car = car;
            Active = true;
            freePos = car.transform.position;
            freeRot = car.transform.rotation;
            car.SetSpawn(freePos, freeRot);
            // the main bar's buttons (basic bar SHOP -> ITEMSHOP, NOTEBOOK -> NOTEBOOK ...) are the HUD's (Hud.SetupBar)
            if (!BoostGiven)
            {
                // 0x762f70: +100 booster (0x47beb0) and the "boost 100" voice (sound 0x4f0c)
                BoostGiven = true;
                car.BoostGauge = 1f;
                VoiceLines.ParkingLotBoost();
            }
        }

        void OnDestroy()
        {
            CloseGateSelect();
            if (Current == this) Current = null;
        }

        void Update()
        {
            if (!Active || !Car || leaving) return;
            if (holding)
            {
                // a menu or the gate list keeps the car parked; when it is gone the car goes back outside the zone,
                // as the client puts it on returning from the garage / shop stage (0x762f70 -> 0x7629b0)
                if (!GateOpen && !(flow && flow.MenuOpen)) { holding = false; Car.ExternalControl = false; Bounce(); }
                if (GateOpen && Pressed(Key.Escape)) { GateClosedFrame = Time.frameCount; CloseGateSelect(); }
                return;
            }

            var area = AreaAt(Car.transform.position);
            if (area == Area.None)
            {
                // 0x7628d0: remember the last spot outside every zone (on the ground)
                if (!Car.Airborne) { freePos = Car.transform.position; freeRot = Car.transform.rotation; }
            }
            if (area != CurrentArea)
            {
                if (CurrentArea != Area.None) AudioLibrary.PlaySfx("sfx/SID_ENTER_SUCCESS");   // sound 0x4e8d
                CurrentArea = area;
                Enter(area);
            }
            Hotkeys();
        }

        /// <summary>The zone under a world position: buildings from the _fx.nif strips, then area_* of the _a.nif override.</summary>
        public Area AreaAt(Vector3 p)
        {
            if (floors.Count == 0) return Area.None;
            var f = floors[Floor];
            var code = Area.None;
            switch (f.Buildings?.Top(p.x, p.z))
            {
                case "teamcenter": code = Area.TeamCenter; break;
                case "garage": code = Area.Garage; break;
                case "battle": code = Area.Battle; break;
                case "autoshop": code = Area.AutoShop; break;
                case "auction": code = Area.Auction; break;
                case "parts": code = Area.Parts; break;
            }
            switch (f.Areas?.Top(p.x, p.z))
            {
                case "area_exit": code = Area.Exit; break;
                case "area_f1": code = Area.F1; break;
                case "area_b1": code = Area.B1; break;
            }
            return code;
        }

        /// <summary>A point inside a zone of the current floor (tests and tools), or null.</summary>
        public Vector3? PointIn(Area area)
        {
            if (floors.Count == 0) return null;
            var f = floors[Floor];
            var zones = area == Area.Exit || area == Area.F1 || area == Area.B1 ? f.Areas : f.Buildings;
            if (zones == null) return null;
            foreach (var c in zones.Centres())
                if (AreaAt(c) == area) return c;
            return null;
        }

        void Enter(Area area)
        {
            // 0x763f30: on the tutorial car only the Battle Zone building may be entered (11285)
            if (Apprentice.Refuses(PlayerProfile.Current, area)) { Stop(); Refuse(Apprentice.RefusalText); return; }
            switch (area)
            {
                case Area.None:
                    return;
                case Area.Exit:
                    Stop();
                    OpenGateSelect();
                    return;
                case Area.Garage:
                    Stop(); Hold();
                    Menus.Garage();
                    return;
                case Area.AutoShop:
                    Stop(); Hold();
                    Menus.CarShop();
                    return;
                case Area.Parts:
                    Stop(); Hold();
                    WorkshopMenus.PartsShop();
                    return;
                case Area.F1:
                    SetFloor(0);
                    Stop();
                    return;
                case Area.B1:
                    // permission bit 0x80, then 0x763840 case 9: level 31 or higher
                    if (floors.Count < 2) { Refuse("Restricted Area"); return; }
                    if ((PlayerProfile.Current?.Level ?? 1) <= 30) { Refuse("Entry is limited to Level 31 or higher users."); return; }
                    SetFloor(1);
                    return;
                case Area.Auction:
                    // 0x763840 case 4: level >= AuctionMinLevel (11828), then the auction window (0x598f60)
                    Stop(); Hold();
                    AuctionHouse.Open();
                    return;
                case Area.Battle when ApprenticeCourse.BattleZone(true):
                    // an apprentice at the Battle Zone building: Jason's line and the Tutorial (0x57c420)
                    Stop();
                    return;
                case Area.TeamCenter:
                    // the Crew Center (TC 590000): create a crew, find one, the crew's pages
                    Stop(); Hold();
                    Social.Teams.OpenCenter();
                    return;
                default:
                    // the battle zone building: races run inside a city (the Battle Zone lobby there): the client's answer for a
                    // building the driver may not enter (permission bits of 0x763f30, text 11913)
                    Refuse("Restricted Area");
                    return;
            }
        }

        void Refuse(string message)
        {
            CurrentArea = Area.None;
            Bounce();
            UiPopup.Message(message);
        }

        void Stop()
        {
            var rb = Car.GetComponent<Rigidbody>();
            if (rb) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        }

        void Hold()
        {
            holding = true;
            Car.ExternalControl = true;
        }

        void Bounce()
        {
            Teleport(Car, freePos, freeRot);
            CurrentArea = AreaAt(freePos);
        }

        static void Teleport(DriftCar car, Vector3 pos, Quaternion rot)
        {
            var rb = car.GetComponent<Rigidbody>();
            if (rb) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            car.transform.SetPositionAndRotation(pos, rot);
            if (rb) { rb.position = pos; rb.rotation = rot; }
            if (Net.OnlineSession.Instance) Net.OnlineSession.Instance.MarkTeleport();
        }

        // ---------------- gate selection (SELGATE 3620000 / 3630000 / 3670000 / 3680000) ----------------
        static readonly string[] RowSuffix = { "1ST", "2ND", "3RD" };

        void OpenGateSelect()
        {
            Hold();
            var ui = UiManager.Instance;
            gateBg = ui.Open(3620000);      // full-screen backdrop
            gateMain = ui.Open(3630000);    // the gate rows with their congestion bars
            gatePrev = ui.Open(3680000);    // back to the parking lot
            // more gates than rows: the tab strip pages them
            gateTabs = Gates.Length > GateRows ? ui.Open(3670000) : null;
            if (gateTabs) gateTabs.SetVisible("ID_SELGATE_TAB_TABKEY", false);
            gatePage = 0;
            FillGates();
            for (int r = 0; r < GateRows; r++) { int row = r; ui.On("ID_SELGATE_MAIN_BTN" + RowSuffix[r], _ => ChooseGate(row)); }
            ui.On("ID_SELGATE_PREVIOUS", _ => CloseGateSelect());
            ui.On("ID_SELGATE_TAB_MAJOR", _ => { gatePage = 0; FillGates(); });
            ui.On("ID_SELGATE_TAB_SEMI", _ => { gatePage = Mathf.Min(1, (Gates.Length - 1) / GateRows); FillGates(); });
        }

        void FillGates()
        {
            if (!gateMain) return;
            for (int r = 0; r < GateRows; r++)
            {
                int gate = gatePage * GateRows + r;
                bool shown = gate < Gates.Length;
                string s = RowSuffix[r];
                gateMain.SetVisible("ID_SELGATE_MAIN_BTN" + s, shown);
                gateMain.SetVisible("ID_SELGATE_MAIN_STATUS" + s, shown);
                gateMain.SetVisible("ID_SELGATE_MAIN_INDICATOR_BASE" + s, shown);
                // 0x689970: three grains always, then one per capacity/12 players, at most 15
                int players = shown ? Population(Gates[gate].City) : 0;
                int lit = 3 + Mathf.Min(12, players * 12 / GateCapacity);
                for (int g = 0; g < 15; g++) gateMain.SetVisible($"ID_SELGATE_MAIN_GRAIN{s}_{g:00}", shown && g < lit);
                if (!shown) continue;
                // 0x4ec560: under 5% of capacity "Few", under 50% "Many", else "Crowded" (texts 10762-10764)
                float pct = players * 100f / GateCapacity;
                gateMain.SetText("ID_SELGATE_MAIN_STATUS" + s, pct < 5f ? "Few" : pct < 50f ? "Many" : "Crowded");
                GateLabel(gateMain.Control("ID_SELGATE_MAIN_BTN" + s), $"Gate {gate + 1} - {Gates[gate].Name}");
            }
            if (gateTabs)
            {
                gateTabs.Control("ID_SELGATE_TAB_MAJOR")?.SetChecked(gatePage == 0);
                gateTabs.Control("ID_SELGATE_TAB_SEMI")?.SetChecked(gatePage != 0);
            }
        }

        /// <summary>The gate's name over the empty top of the button art (the art only says "1/2/3 GATE").</summary>
        static void GateLabel(NuiControlView button, string text)
        {
            if (!button) return;
            var t = button.transform.Find("Gate Label")?.GetComponent<Text>();
            if (!t)
            {
                var rt = NuiView.NewRect("Gate Label", button.transform);
                rt.anchoredPosition = new Vector2(112f, -10f);
                rt.sizeDelta = new Vector2(210f, 24f);
                t = rt.gameObject.AddComponent<Text>();
                t.font = UiAssets.Font("Calibri 18 700", out int size, out FontStyle style);
                t.fontSize = size;
                t.fontStyle = style;
                t.alignment = TextAnchor.MiddleLeft;
                t.color = new Color(1f, 0.85f, 0.35f);
                t.raycastTarget = false;
                rt.gameObject.AddComponent<Outline>().effectColor = Color.black;
            }
            t.text = text;
        }

        /// <summary>Players last seen in a city: unknown offline (the bar shows its three base grains).</summary>
        static int Population(string city) => 0;

        void ChooseGate(int row)
        {
            int gate = gatePage * GateRows + row;
            if (gate >= Gates.Length || !flow) return;
            // the city unlocks (0x6f9630: level and the CITY_2ND..5TH quests; 10746 "This area is restricted.")
            var locked = QuestUnlocks.CityRefusal(Gates[gate].City);
            if (locked != null) { UiPopup.Message(locked); return; }
            UiSounds.Click();
            leaving = true;
            CloseGateSelect();
            flow.EnterGate(Gates[gate].City);
        }

        void CloseGateSelect()
        {
            var ui = UiManager.Instance;
            if (ui)
                foreach (var v in new[] { gateBg, gateMain, gatePrev, gateTabs })
                    if (v) ui.Close(v);
            gateBg = gateMain = gatePrev = gateTabs = null;
        }

        // ---------------- hotkeys ----------------
        void Hotkeys()
        {
            bool busy = flow && flow.MenuOpen;
            if (!busy)
            {
                if (Pressed(Key.I)) Menus.Inventory();
                if (Pressed(Key.G)) Menus.Garage();
                if (Pressed(Key.P)) Menus.Shop();
                if (Pressed(Key.Q)) Menus.Quests();
                if (Pressed(Key.L)) WorkshopMenus.Licences();
                if (Pressed(Key.U)) WorkshopMenus.PartsShop();
                if (Pressed(Key.C)) Menus.CityTravel();
            }
            if (Pressed(Key.M)) Menus.WorldMap();
            if (Pressed(Key.N) && flow) flow.Horn();
        }

#if !ENABLE_INPUT_SYSTEM
        enum Key { I, G, P, Q, L, U, C, M, N, Escape }
#endif

        static bool Pressed(Key key)
        {
            if (Net.ChatBox.Typing) return false;
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es && es.currentSelectedGameObject && es.currentSelectedGameObject.GetComponent<InputField>()) return false;
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            return k != null && k[key].wasPressedThisFrame;
#else
            switch (key)
            {
                case Key.I: return Input.GetKeyDown(KeyCode.I);
                case Key.G: return Input.GetKeyDown(KeyCode.G);
                case Key.P: return Input.GetKeyDown(KeyCode.P);
                case Key.Q: return Input.GetKeyDown(KeyCode.Q);
                case Key.L: return Input.GetKeyDown(KeyCode.L);
                case Key.U: return Input.GetKeyDown(KeyCode.U);
                case Key.C: return Input.GetKeyDown(KeyCode.C);
                case Key.M: return Input.GetKeyDown(KeyCode.M);
                case Key.N: return Input.GetKeyDown(KeyCode.N);
                default: return Input.GetKeyDown(KeyCode.Escape);
            }
#endif
        }

        // ---------------- zone meshes ----------------
        /// <summary>
        /// Triangles of a zone NIF in world space, picked like NiPick from far above (0x761a30 casts from z = 10000
        /// straight down and reads the first record): the highest triangle under a point names the zone.
        /// </summary>
        sealed class ZoneSet
        {
            readonly List<Vector3> a = new List<Vector3>(), b = new List<Vector3>(), c = new List<Vector3>();
            readonly List<Rect> box = new List<Rect>();
            readonly List<string> names = new List<string>();

            /// <summary><paramref name="parentNames"/>: the zone is the geometry's parent node (area_*), else the geometry itself.</summary>
            public static ZoneSet From(GameObject root, bool parentNames)
            {
                var z = new ZoneSet();
                foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    var mesh = mf.sharedMesh;
                    if (!mesh) continue;
                    string name = parentNames ? (mf.transform.parent ? mf.transform.parent.name : "") : mf.name;
                    var m = mf.transform.localToWorldMatrix;
                    var v = mesh.vertices;
                    var t = mesh.triangles;
                    for (int i = 0; i + 2 < t.Length; i += 3)
                    {
                        Vector3 p0 = m.MultiplyPoint3x4(v[t[i]]), p1 = m.MultiplyPoint3x4(v[t[i + 1]]), p2 = m.MultiplyPoint3x4(v[t[i + 2]]);
                        var n = Vector3.Cross(p1 - p0, p2 - p0);
                        // a vertical ray misses walls; drop them (and degenerate faces)
                        if (n.sqrMagnitude < 1e-8f || Mathf.Abs(n.normalized.y) < 0.1f) continue;
                        z.a.Add(p0); z.b.Add(p1); z.c.Add(p2); z.names.Add(name);
                        float x0 = Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x)), x1 = Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x));
                        float y0 = Mathf.Min(p0.z, Mathf.Min(p1.z, p2.z)), y1 = Mathf.Max(p0.z, Mathf.Max(p1.z, p2.z));
                        z.box.Add(Rect.MinMaxRect(x0, y0, x1, y1));
                    }
                }
                return z;
            }

            public string Top(float x, float z)
            {
                string best = null;
                float bestY = float.MinValue;
                var q = new Vector2(x, z);
                for (int i = 0; i < names.Count; i++)
                {
                    if (!box[i].Contains(q)) continue;
                    if (!HeightAt(a[i], b[i], c[i], x, z, out float y) || y <= bestY) continue;
                    bestY = y; best = names[i];
                }
                return best;
            }

            public IEnumerable<Vector3> Centres()
            {
                for (int i = 0; i < names.Count; i++) yield return (a[i] + b[i] + c[i]) / 3f;
            }

            static bool HeightAt(Vector3 p0, Vector3 p1, Vector3 p2, float x, float z, out float y)
            {
                y = 0f;
                float d = (p1.z - p2.z) * (p0.x - p2.x) + (p2.x - p1.x) * (p0.z - p2.z);
                if (Mathf.Abs(d) < 1e-9f) return false;
                float l1 = ((p1.z - p2.z) * (x - p2.x) + (p2.x - p1.x) * (z - p2.z)) / d;
                float l2 = ((p2.z - p0.z) * (x - p2.x) + (p0.x - p2.x) * (z - p2.z)) / d;
                float l3 = 1f - l1 - l2;
                if (l1 < 0f || l2 < 0f || l3 < 0f) return false;
                y = l1 * p0.y + l2 * p1.y + l3 * p2.y;
                return true;
            }
        }
    }
}
