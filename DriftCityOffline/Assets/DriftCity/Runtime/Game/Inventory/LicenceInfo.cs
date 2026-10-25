using System.Collections.Generic;
using System.Linq;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// Licence info: the inventory's licence page (INVEN 3080000: the mounted licence's icon, name, description and bonus,
    /// "Drag and drop to equip a new License") and the hover boxes LISENCEINFO 1360000 (earned, normal colours) /
    /// 1370000 (not earned, grey) that the tooltip manager 0x626920 fills: name, "[Requirement]" (11263) with up to 15
    /// condition lines, "[Bonus Effect]" (11264) and the effect. The licences themselves are Licences (Workshop.cs) and
    /// the NLICENSE grid (WorkshopMenus.Licences).
    /// </summary>
    public static class LicenceInfo
    {
        static PlayerProfile P => PlayerProfile.Current;

        /// <summary>The licence's conditions with this driver's progress ("HUVs caught  12 / 50"), one line each.</summary>
        public static List<string> ConditionLines(Licence l, PlayerProfile p)
        {
            var lines = new List<string>();
            if (l == null) return lines;
            if (!l.Obtainable) { lines.Add("Needs a game mode this version doesn't have."); return lines; }
            for (int i = 0; i < l.Needs.Count; i++)
            {
                var n = l.Needs[i];
                int have = p != null ? Mathf.Min(Licences.Stat(p, n.stat), n.value) : 0;
                if (i > 0 && l.AnyOne) lines.Add("or");
                lines.Add($"{StatName(n.stat)}  {have:N0} / {n.value:N0}");
            }
            return lines;
        }

        public static string StatName(string stat) => stat.StartsWith("Licence_") ? "Licence " + (int.TryParse(stat.Substring(8), out int id) ? Licences.Get(id)?.Name ?? stat : stat) : stat switch
        {
            "HuvCaught" => "HUVs caught", "RacesEntered" => "Races", "RacesWon" => "Wins", "Quests" => "Main quests", "RacePoints" => "Race points",
            "RacesCras" => "Cras races", "RacesMoon" => "Moon Palace races", "RacesKoi" => "Koinonia races", "RacesOros" => "Oros races",
            "MaxEnhance" => "Best part enhance", _ => stat,
        };

        /// <summary>Hover box for any licence cell (the NLICENSE grid): LISENCEINFO while the pointer is over it.</summary>
        public static void AttachTooltip(RectTransform cell, Licence l)
        {
            if (!cell || l == null) return;
            if (!cell.TryGetComponent<Graphic>(out _)) cell.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
            var h = cell.TryGetComponent(out Hover haveHover) ? haveHover : cell.gameObject.AddComponent<Hover>();
            h.Licence = l;
        }

        sealed class Hover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            public Licence Licence;
            public void OnPointerEnter(PointerEventData e) => ItemTooltip.ShowLicence(Licence, (RectTransform)transform);
            public void OnPointerExit(PointerEventData e) => ItemTooltip.Hide();
        }

        public static Licence Mounted(PlayerProfile p) => p != null && p.MountedLicence >= 0 && p.Licences.Contains(p.MountedLicence) ? Licences.Get(p.MountedLicence) : null;

        internal static void SetupPage(NuiView v)
        {
            if (!v) return;
            OriginalMenus.Hide(v, "ID_MAINBUTTON_HOTKEY_SKILLBOOK", "ID_INVEN_LICENSE_HOVER1ST", "ID_INVEN_LICENSE_HOVER2ND", "ID_INVEN_LICENSE_HOVER3RD", "ID_INVEN_LICENSE_HOVER4TH");
            var pos = v.Control("ID_INVEN_LICENSE_POSITEM");
            if (!pos) return;
            var tip = pos.gameObject.TryGetComponent(out Tip haveTip) ? haveTip : pos.gameObject.AddComponent<Tip>();
            if (!pos.TryGetComponent<Graphic>(out _)) pos.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.001f);
            foreach (var g in pos.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = g.gameObject == pos.gameObject;
            tip.Licence = () => Mounted(P);
            tip.Page = v;
            tip.Click = () => WorkshopMenus.Licences();
            // "Drag and drop to equip a new License": an earned licence dragged from the bag onto the icon is mounted
            var drop = ItemSlot.On((RectTransform)pos.transform);
            drop.Accept = e => e.Kind == BagKind.Licence;
            drop.Dropped = e => Mount(e.LicenceId);
            RefreshPage(v);
        }

        /// <summary>Mounts an earned licence (licence.mount, the same request as the NLICENSE grid's EQUIP).</summary>
        public static void Mount(int id, System.Action<bool> done = null)
        {
            var l = Licences.Get(id);
            if (l == null || P == null) { done?.Invoke(false); return; }
            if (P.MountedLicence == id) { done?.Invoke(false); return; }
            var body = new Dictionary<string, object> { ["id"] = l.Id, ["effect"] = l.Effect ?? "", ["value"] = l.Value };
            Progress.Op("licence.mount", body, () =>
            {
                if (!P.Licences.Contains(l.Id)) return new Progress.Result { Error = "Not earned." };
                P.MountedLicence = l.Id;
                return new Progress.Result { Ok = true };
            }, r =>
            {
                if (r.Ok) { AudioLibrary.PlaySfx("sfx/SID_ITEM_INSTALL"); Net.ChatBox.System($"{l.Name} mounted."); }
                InventoryWindow.RefreshIfOpen();
                done?.Invoke(r.Ok);
            });
        }

        internal static void RefreshPage(NuiView v)
        {
            if (!v || P == null) return;
            var l = Mounted(P);
            var pos = v.Control("ID_INVEN_LICENSE_POSITEM");
            if (pos)
            {
                foreach (var ri in pos.GetComponentsInChildren<RawImage>(true)) if (ri.transform.parent == pos.transform) ri.enabled = l == null;
                OriginalMenus.Remove(pos.transform, "item");
                if (l != null)
                {
                    var tile = OriginalMenus.ItemTile(pos.transform, Vector2.zero, new Vector2(64, 64), l.IdName, l.Name);
                    foreach (var g in tile.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                }
            }
            int earned = P.Licences.Count, all = Licences.All.Count;
            v.SetText("ID_INVEN_LICENSE_TITLE", l != null ? l.Name : "No licence mounted");
            v.SetText("ID_INVEN_LICENSE_DESC", l != null ? ItemCatalog.Clean(l.Info) : $"Licences earned: {earned} / {all}. Click the icon to open the licences (L) and mount one for its bonus.");
            v.SetText("ID_INVEN_LICENSE_EFFECT", l != null ? "- " + ItemCatalog.Clean(l.EffectText) : "");
        }

        /// <summary>The page's icon: hover shows the licence box and the dashed frame, click opens the licence grid.</summary>
        sealed class Tip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
        {
            public System.Func<Licence> Licence;
            public System.Action Click;
            public NuiView Page;

            void Frame(bool on) { foreach (var id in new[] { "1ST", "2ND", "3RD", "4TH" }) Page?.SetVisible("ID_INVEN_LICENSE_HOVER" + id, on); }

            public void OnPointerEnter(PointerEventData e)
            {
                Frame(true);
                var l = Licence?.Invoke();
                if (l != null) ItemTooltip.ShowLicence(l, (RectTransform)transform);
            }

            public void OnPointerExit(PointerEventData e) { Frame(false); ItemTooltip.Hide(); }
            public void OnPointerClick(PointerEventData e) { UiSounds.Click(); ItemTooltip.Hide(); Click?.Invoke(); }
        }
    }
}
