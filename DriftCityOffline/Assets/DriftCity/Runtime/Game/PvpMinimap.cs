using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The PvP race minimap (DriftCity_RE/PVP.md "Unity port"; object of binder 0x628440, draw 0x627c60): dialog 1170000
    /// (a normal PvP race uses it even with the ADV interface, 0x628370). The track's picture is the dialog control whose
    /// name is the pvptrackinfo "name" (plus its "<name>_guide" controls, 0x628130); positions map linearly from the start
    /// row's minx / miny / maxx / maxy onto that picture with y up (0x6278b0), no rotation or zoom. Icons: MINE (MINE_A /
    /// MINE_B by team in team races) for me, PLAYER1 / PLAYER2 (team A) for the others, turned to their heading
    /// (pi / 2 - heading). The city minimap steps aside while it is up.
    /// </summary>
    public sealed class PvpMinimap
    {
        const int Dialog = 1170000;
        NuiView view;
        RectTransform picture;
        Rect rect;
        RaceTrack track;
        NuiControlView mine, mineA, mineB, player1, player2;
        readonly List<RectTransform> others = new List<RectTransform>();
        readonly List<GameObject> hiddenCity = new List<GameObject>();

        public bool IsOpen => view;
        public NuiView View => view;
        public bool HasPicture => picture;

        public static PvpMinimap Open(RaceTrack t)
        {
            var ui = UiManager.Instance;
            if (!ui || t == null) return null;
            var m = new PvpMinimap { track = t, view = ui.Open(Dialog) };
            if (!m.view) return null;
            m.Prepare();
            return m;
        }

        void Prepare()
        {
            NuiControlView pic = null;
            foreach (var c in view.GetComponentsInChildren<NuiControlView>(true))
            {
                string id = c.Data.IdName ?? "", nm = c.Data.Name ?? "";
                if (id.StartsWith("ID_PVP_MINIMAP_ICON_")) { c.gameObject.SetActive(false); continue; }
                if (id == "ID_PVP_MINIMAP_BASE") continue;
                bool isTrack = string.Equals(nm, track.Name, StringComparison.OrdinalIgnoreCase);
                bool guide = nm.StartsWith(track.Name + "_guide", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrEmpty(id)) c.gameObject.SetActive(isTrack || guide);
                if (isTrack && pic == null) pic = c;
            }
            mine = view.Control("ID_PVP_MINIMAP_ICON_MINE"); mineA = view.Control("ID_PVP_MINIMAP_ICON_MINE_A"); mineB = view.Control("ID_PVP_MINIMAP_ICON_MINE_B");
            player1 = view.Control("ID_PVP_MINIMAP_ICON_PLAYER1"); player2 = view.Control("ID_PVP_MINIMAP_ICON_PLAYER2");
            if (pic)
            {
                picture = (RectTransform)pic.transform;
                rect = new Rect(picture.anchoredPosition.x, -picture.anchoredPosition.y, picture.sizeDelta.x, picture.sizeDelta.y);
            }
            else
            {
                // no picture of this track: the frame's base square stands in
                var b = view.Control("ID_PVP_MINIMAP_BASE");
                var rt = b ? (RectTransform)b.transform : null;
                rect = rt ? new Rect(rt.anchoredPosition.x, -rt.anchoredPosition.y, rt.sizeDelta.x, rt.sizeDelta.y) : new Rect(10, 10, 256, 256);
            }
            // the city minimap (190000 / 200000) steps aside for the race
            var ui = UiManager.Instance;
            foreach (var id in new[] { 190000, 200000 })
            {
                var v = ui ? ui.Find(id) : null;
                if (v && v.gameObject.activeSelf) { v.gameObject.SetActive(false); hiddenCity.Add(v.gameObject); }
            }
        }

        /// <summary>Map point of a world position (0x6278b0).</summary>
        public Vector2 MapPoint(Vector3 world)
        {
            float u = track.MaxX > track.MinX ? (world.x - track.MinX) / (track.MaxX - track.MinX) : 0.5f;
            float v = track.MaxY > track.MinY ? (world.z - track.MinY) / (track.MaxY - track.MinY) : 0.5f;
            return new Vector2(rect.x + Mathf.Clamp01(u) * rect.width, rect.y + (1f - Mathf.Clamp01(v)) * rect.height);
        }

        /// <summary>Draws me and the others: (transform, team: -1 single, 0 A, 1 B).</summary>
        public void Draw(Transform me, int myTeam, IList<(Transform car, int team)> rivals)
        {
            if (!view) return;
            var meIcon = myTeam == 0 ? mineA : myTeam == 1 ? mineB : mine;
            foreach (var i in new[] { mine, mineA, mineB }) if (i) i.gameObject.SetActive(i == meIcon && me);
            if (meIcon && me) Place((RectTransform)meIcon.transform, me);
            int n = 0;
            foreach (var (car, team) in rivals)
            {
                if (!car) continue;
                var src = team == 0 ? player2 : player1;
                if (!src) continue;
                while (others.Count <= n)
                {
                    var copy = UnityEngine.Object.Instantiate(src.gameObject, src.transform.parent);
                    copy.name = "rival icon";
                    others.Add((RectTransform)copy.transform);
                }
                var icon = others[n++];
                icon.gameObject.SetActive(true);
                // the copy may have been made from the other team's icon
                var srcImg = src.GetComponentInChildren<UnityEngine.UI.RawImage>(true);
                var dstImg = icon.GetComponentInChildren<UnityEngine.UI.RawImage>(true);
                if (srcImg && dstImg) { dstImg.texture = srcImg.texture; dstImg.uvRect = srcImg.uvRect; dstImg.enabled = srcImg.texture; }
                Place(icon, car);
            }
            for (int i = n; i < others.Count; i++) if (others[i]) others[i].gameObject.SetActive(false);
            // my icon on top
            if (meIcon) meIcon.transform.SetAsLastSibling();
        }

        void Place(RectTransform icon, Transform car)
        {
            var p = MapPoint(car.position);
            var size = icon.sizeDelta;
            icon.pivot = new Vector2(0.5f, 0.5f);
            icon.anchoredPosition = new Vector2(p.x, -p.y);
            var f = car.forward;
            icon.localRotation = Quaternion.Euler(0, 0, -Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg);
            icon.sizeDelta = size;
        }

        public void Close()
        {
            foreach (var g in hiddenCity) if (g) g.SetActive(true);
            hiddenCity.Clear();
            var ui = UiManager.Instance;
            if (view) { if (ui) ui.Close(view); else UnityEngine.Object.Destroy(view.gameObject); }
            view = null;
        }
    }
}
