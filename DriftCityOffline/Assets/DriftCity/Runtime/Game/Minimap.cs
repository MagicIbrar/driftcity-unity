using System.Collections.Generic;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// The original minimap (MINIMAP 190000 frame), drawn the way DriftCity.exe does it (DriftCity_RE/MINIMAP.md):
    /// the city's own street image (_cras.dds ..., parking lot _parking.dds / _parkingB1.dds; picked by 0x5f2050) on a
    /// 50-vertex disc of radius 103.5 px about the BASE ring's centre (0x5f6200), its alpha taken from the MASK image,
    /// scrolled so the car's world position (city bounds table 0x871a48, image crop 0x871ac8) sits in the middle and
    /// scaled by the zoom table {1, 2, 3, 4, 5.5, 8, 10} * 0.0575 (0x604526; +/- buttons and numpad +/- step it).
    /// North is up and the player arrow turns, unless goption.ini RotateMinimap (or the MAPROT button) turns the map.
    /// Other cars are icons placed by 0x5f6840: offset / world width * 1800 / zoom px, pinned to the rim beyond 90 px.
    /// </summary>
    public sealed class Minimap : MonoBehaviour
    {
        /// <summary>Index into <see cref="CityMapInfo.Zoom"/> (this+0x150 in the client).</summary>
        public int ZoomIndex = 1;
        /// <summary>The map turns with the car (DAT_008d28dc, goption.ini [Game] RotateMinimap).</summary>
        public bool Rotate;
        /// <summary>Remote players appear (DAT_008d28e8, goption.ini [Game] ShowAnotherUserInMinimap).</summary>
        public bool ShowOtherUsers = true;
        public CityMapInfo Map { get; private set; }
        /// <summary>The disc graphic (null until shown).</summary>
        public MinimapDisc Disc => disc;
        /// <summary>The player arrow (ID_MINIMAP_ICON_MINE).</summary>
        public RectTransform Arrow => arrow;

        NuiView view;
        GameFlow flow;
        MinimapDisc disc;
        RectTransform blips, arrow, huvBlip;
        readonly List<RectTransform> others = new List<RectTransform>();
        string lotImage;
        TrafficCar huvCar;
        ChaseMission huvFor;

        public void Show(GameFlow owner)
        {
            flow = owner;
            var ui = UiManager.Instance;
            view = ui.Open(190000);
            if (!view) return;
            foreach (var c in view.Controls)
            {
                var id = c.Data.IdName ?? "";
                if (id.StartsWith("ID_MINIMAP_ICON_") || id.Contains("VIRTUAL_CHASING") || id.EndsWith("_KOR")) c.gameObject.SetActive(false);
            }
            // the MASK control only lends its texture to the disc (stage 1 alpha); BASE is the ring frame
            view.SetVisible("ID_MINIMAP_MASK", false);
            view.SetVisible("ID_MINIMAP_BASE", true);
            // unnamed 1024x1024 per-city images in the layout are drawn by the disc instead
            foreach (Transform child in view.GetComponentsInChildren<Transform>(true))
                if (child.TryGetComponent<NuiControlView>(out var cv) && string.IsNullOrEmpty(cv.Data.IdName))
                {
                    DriftCity.Core.NuiControl.TryVec2(cv.Data.I("SZ1"), out float w, out float h);
                    if (w >= 600 || h >= 600) cv.gameObject.SetActive(false);
                }

            var lot = owner.InParkingLot ? ParkingLotStage.Current : null;
            lotImage = lot ? lot.MapImage : null;
            Map = lot ? CityMapInfo.ForParkingImage(lotImage) : CityMapInfo.ForCity(owner.City);
            ZoomIndex = Map != null ? Map.DefaultZoom : 1;
            ReadOptions();

            ui.On("ID_MINIMAP_PLUS", _ => ZoomIn());
            ui.On("ID_MINIMAP_MINUS", _ => ZoomOut());
            ui.On("ID_MINIMAP_BUTTON_ENTIREMAP", _ => Menus.WorldMap());
            ui.On("ID_MINIMAP_BUTTON_MAPROT", _ => SetRotate(true));
            ui.On("ID_MINIMAP_BUTTON_MAPFIX", _ => SetRotate(false));
            SetRotate(Rotate);

            // the disc needs a second UV set for the mask
            var canvas = view.GetComponentInParent<Canvas>();
            if (canvas) canvas.rootCanvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1;

            var baseCtl = view.Control("ID_MINIMAP_BASE");
            Transform host = baseCtl ? baseCtl.transform : view.transform;
            Vector2 centre;
            if (baseCtl)
            {
                var size = ((RectTransform)baseCtl.transform).sizeDelta;
                centre = new Vector2(size.x / 2f, -size.y / 2f);
            }
            else centre = new Vector2(140, -620);

            var discRt = NuiView.NewRect("Minimap Disc", host);
            discRt.SetAsFirstSibling();   // under the ring image
            discRt.pivot = new Vector2(0.5f, 0.5f);
            discRt.anchoredPosition = centre;
            discRt.sizeDelta = Vector2.one * (2f * CityMapInfo.DiscRadius);
            discRt.gameObject.AddComponent<CanvasRenderer>();
            disc = discRt.gameObject.AddComponent<MinimapDisc>();
            disc.raycastTarget = false;
            disc.MapTexture = Map != null ? UiAssets.Texture(Map.Image) : null;
            var maskCtl = view.Control("ID_MINIMAP_MASK");
            disc.MaskTexture = maskCtl ? UiAssets.Texture(maskCtl.Data.I("I1_1")) : null;
            disc.enabled = Map != null && disc.MapTexture;

            blips = NuiView.NewRect("Minimap Icons", host);
            blips.anchoredPosition = centre;
            arrow = MakeBlip("ID_MINIMAP_ICON_MINE", "Me", new Color(1f, 0.85f, 0.1f));
            huvBlip = MakeBlip("ID_MINIMAP_ICON_HUV", "HUV", new Color(1f, 0.15f, 0.1f));
            huvBlip.gameObject.SetActive(false);
        }

        public void Hide()
        {
            if (view && UiManager.Instance) UiManager.Instance.Close(view);
            view = null; disc = null; blips = arrow = huvBlip = null;
            others.Clear();
            huvCar = null; huvFor = null;
        }

        public void ZoomIn() { if (ZoomIndex > 0) ZoomIndex--; }                                    // 0x5f23b0(1)
        public void ZoomOut() { if (ZoomIndex + 1 < CityMapInfo.Zoom.Length) ZoomIndex++; }         // 0x5f23b0(0)

        /// <summary>MAPROT turns rotation on (0x5368a0), MAPFIX off (0x536900); only the other mode's button shows (0x5f3010).</summary>
        public void SetRotate(bool on)
        {
            Rotate = on;
            if (!view) return;
            view.SetVisible("ID_MINIMAP_BUTTON_MAPROT", !on);
            view.SetVisible("ID_MINIMAP_BUTTON_MAPFIX", on);
        }

        void ReadOptions()
        {
            // goption.ini through GameOptions (the player's copy, written by the options screen)
            Rotate = GameOptions.RotateMinimap;
            ShowOtherUsers = GameOptions.ShowAnotherUserInMinimap;
        }

        /// <summary>A copy of an icon control's images, centred on its own origin (the icons' POS1 is -w/2, -h/2).</summary>
        RectTransform MakeBlip(string idName, string name, Color fallback)
        {
            var rt = NuiView.NewRect(name, blips);
            rt.gameObject.AddComponent<CanvasGroup>().blocksRaycasts = false;
            var template = view.Control(idName);
            bool any = false;
            if (template)
                foreach (Transform el in template.transform)
                {
                    var src = el.GetComponent<RawImage>();
                    if (!src || !src.texture) continue;
                    var e = NuiView.NewRect(el.name, rt);
                    var srt = (RectTransform)el;
                    e.anchoredPosition = srt.anchoredPosition;
                    e.sizeDelta = srt.sizeDelta;
                    var ri = e.gameObject.AddComponent<RawImage>();
                    ri.texture = src.texture; ri.uvRect = src.uvRect; ri.color = src.color; ri.raycastTarget = false;
                    any = true;
                }
            if (!any)
            {
                var e = NuiView.NewRect("dot", rt);
                e.anchoredPosition = new Vector2(-4, 4); e.sizeDelta = new Vector2(8, 8);
                var img = e.gameObject.AddComponent<Image>(); img.color = fallback; img.raycastTarget = false;
            }
            return rt;
        }

        /// <summary>Clockwise angle from north (Unity +z = Gamebryo +y) of a transform's forward, in radians.</summary>
        static float Bearing(Transform t)
        {
            var f = t.forward;
            return Mathf.Atan2(f.x, f.z);
        }

        void LateUpdate()
        {
            if (disc == null && view == null) return;
            // the loading screen closes every dialog, including our frame
            if (!view || !disc) { Hide(); return; }
            if (!flow || !flow.PlayerCar || Map == null) return;

            var lot = flow.InParkingLot ? ParkingLotStage.Current : null;
            if (lot && lot.MapImage != lotImage)
            {
                lotImage = lot.MapImage;
                Map = CityMapInfo.ForParkingImage(lotImage);
                disc.MapTexture = UiAssets.Texture(Map.Image);
            }
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null)
            {
                if (kb.numpadPlusKey.wasPressedThisFrame) ZoomIn();       // DIK_ADD (0x4e) -> 0x5f23b0(1), 0x4e1edb
                if (kb.numpadMinusKey.wasPressedThisFrame) ZoomOut();     // DIK_SUBTRACT (0x4a) -> 0x5f23b0(0)
            }

            var car = flow.PlayerCar.transform;
            Vector3 me = car.position;
            float bearing = Bearing(car);
            float theta = Rotate ? bearing : 0f;
            disc.Set(Map.TexUv(me), Map.UvRadius(ZoomIndex), Map.VRadiusScale, theta);
            arrow.anchoredPosition = Vector2.zero;
            arrow.localEulerAngles = new Vector3(0, 0, -(bearing - theta) * Mathf.Rad2Deg);

            // HUV (icon type 4)
            var chase = ChaseMission.Active;
            var beacon = chase ? GameObject.Find("HUV Beacon") : null;
            if (beacon)
            {
                if (huvFor != chase) { huvFor = chase; huvCar = FindHuv(); }
                Place(huvBlip, beacon.transform.position, huvCar ? Bearing(huvCar.transform) : (float?)null, me, theta);
                huvBlip.gameObject.SetActive(true);
            }
            else huvBlip.gameObject.SetActive(false);

            // remote players (type 2, only with ShowAnotherUserInMinimap) and party members (type 3)
            int n = 0;
            var session = Net.OnlineSession.Instance;
            if (session && session.Remotes)
            {
                var party = PartyNames(session);
                foreach (var rc in session.Remotes.All)
                {
                    if (!rc) continue;
                    bool member = rc.DriverName != null && party.Contains(rc.DriverName);
                    if (!member && !ShowOtherUsers) continue;
                    var b = OtherBlip(n++, member);
                    Place(b, rc.transform.position, Bearing(rc.transform), me, theta);
                }
            }
            for (int i = n; i < others.Count; i++) if (others[i]) others[i].gameObject.SetActive(false);

            // mission targets: delivery stops, patrol posts, instant-mission spots
            int k = 0;
            foreach (var m in MissionMarkers.All())
            {
                if (k >= missionBlips.Count) missionBlips.Add(null);
                if (!missionBlips[k] || missionBlips[k].name != m.Icon) { if (missionBlips[k]) Destroy(missionBlips[k].gameObject); missionBlips[k] = MakeBlip(m.Icon, m.Icon, new Color(1f, 0.8f, 0.1f)); }
                missionBlips[k].gameObject.SetActive(true);
                Place(missionBlips[k++], m.Position, null, me, theta);
            }
            for (int i = k; i < missionBlips.Count; i++) if (missionBlips[i]) missionBlips[i].gameObject.SetActive(false);
        }

        readonly List<RectTransform> missionBlips = new List<RectTransform>();

        static HashSet<string> PartyNames(Net.OnlineSession s)
        {
            var set = new HashSet<string>();
            if (s.Party != null && s.Party.TryGetValue("members", out var m) && m is List<object> list)
                foreach (var o in list)
                    if (o is Dictionary<string, object> d && d.TryGetValue("name", out var nm) && nm is string str) set.Add(str);
            return set;
        }

        RectTransform OtherBlip(int i, bool party)
        {
            string want = party ? "Party" : "Player";
            if (i < others.Count && others[i] && others[i].name != want) { Destroy(others[i].gameObject); others[i] = null; }
            if (i >= others.Count) others.Add(null);
            if (!others[i]) others[i] = MakeBlip(party ? "ID_MINIMAP_ICON_PARTY" : "ID_MINIMAP_ICON_PLAYER", want, party ? new Color(0.3f, 1f, 0.3f) : Color.white);
            others[i].gameObject.SetActive(true);
            return others[i];
        }

        static TrafficCar FindHuv()
        {
            foreach (var t in FindObjectsByType<TrafficCar>(FindObjectsSortMode.None))
                if (t && t.name.StartsWith("HUV ")) return t;
            return null;
        }

        /// <summary>
        /// 0x5f6840: the icon of something at <paramref name="world"/>, turned to its own heading (icon types 1-4 and
        /// 0x23 turn), pinned at alpha 0x55 to the rim when more than 90 px out.
        /// </summary>
        void Place(RectTransform blip, Vector3 world, float? otherBearing, Vector3 me, float theta)
        {
            var ppu = Map.PixelsPerUnit(ZoomIndex);
            var e = new Vector2((world.x - me.x) * ppu.x, (world.z - me.z) * ppu.y);   // px right / up with north up
            if (theta != 0f)
            {
                float c = Mathf.Cos(theta), s = Mathf.Sin(theta);
                e = new Vector2(c * e.x - s * e.y, s * e.x + c * e.y);
            }
            float len = e.magnitude;
            bool outside = len > CityMapInfo.IconRadius;
            if (outside) e = new Vector2(e.x / len * CityMapInfo.IconRimX, e.y / len * CityMapInfo.IconRimY);
            blip.anchoredPosition = e;
            blip.localEulerAngles = new Vector3(0, 0, otherBearing.HasValue ? -(otherBearing.Value - theta) * Mathf.Rad2Deg : 0f);
            var g = blip.GetComponent<CanvasGroup>();
            if (g) g.alpha = outside ? 0x55 / 255f : 1f;
        }
    }
}
