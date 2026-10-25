using System;
using System.Collections.Generic;
using DriftCity.Net;
using DriftCity.Social;
using DriftCity.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// Driver info (DriftCity_RE/SCREENS.md "Driver info"): the main bar's MYINFO opens USERINFO 1810000 (0x5edaf0 ->
    /// 0x54c560), another driver's User Info opens USERINFO2ND 1830000 (packet 0x295 -> 0x54c770).
    /// USERINFO fill 0x54b660, show 0x54b4a0, binder 0x54bfe0 [exact]:
    /// NAME; RACINGTEAM the crew name in its class colour (0x54b450) with the crew mark at TEAMMARK (0x54b250); the
    /// driver's portrait (USERCHAR 1890000 cell ch_u_000N) at IMG_POS; LEVEL "%3u"; EXP and MITO with separators;
    /// BATTLERECORD_1ST 10159 "%5u [ %5u Wins] (Ind.)"; BATTLERECORD_2ND 10160 "... (Team)"; QUICKSERVICE_REC 10161 "%5dx"
    /// (Delivery Service runs); TOTALKM 10729 "%u Km"; LADDERRECORD the crew's ladder entry with its cover hidden, or "-"
    /// under the cover without a crew; the car's stat grid (CAR / PARTS / USER / TOTAL and the POINT row), name plate and
    /// grade badge; LICENSE while a licence is mounted; TEAMROO and TIMEATTACK for a Team ROO driver (+0xed == 1).
    /// USERINFO2ND (0x54c770, handler 0x54d0b0) is the same card for someone else, MITO masked "***,***,***", without the
    /// stat grid, and buttons ADDFRIEND (10162 "Add to Friend List?"), JOIN_RANCINGTEAM (enabled when the driver has a
    /// crew: 10163 / 10164), BUY_GOODS (the personal shop, not in the remake: disabled) and the 3D car (CarPreview in the
    /// dark panel; CAMERA_LEFT / RIGHT turn it, RESTORE resets it). Online the card comes from the server (driver.info); offline an AI driver's card is made
    /// from its seeded level / grade / portrait.
    /// </summary>
    public static class DriverInfoScreen
    {
        public const int OwnDialog = 1810000, OtherDialog = 1830000;

        /// <summary>One driver's card as USERINFO / USERINFO2ND show it.</summary>
        public sealed class Card
        {
            public string Name = "", Crew = "", CrewGrade = "C";
            public int Avatar = 1, Level = 1, CrewMark, Races, Wins, TeamRaces, TeamWins, Deliveries, Licence = -1, Vehicle = -1, Grade;
            public long Exp, Mito;
            public float Km;
            public bool Roo, Online, Mine;
            public string Ladder;   // crew ladder entry (null: no crew -> "-" under the cover)
        }

        static PlayerProfile P => PlayerProfile.Current;
        static NuiView other;
        static Card otherCard;
        static CarPreview preview;
        static UiManager hooked;

        public static NuiView OwnView => OriginalMenus.IsOpen && UiManager.Instance ? UiManager.Instance.Find(OwnDialog) : null;
        public static NuiView OtherView => other;
        public static Card Shown => otherCard;

        /// <summary>The current driver's card.</summary>
        public static Card MyCard()
        {
            var p = P;
            if (p == null) return null;
            var crew = Teams.Mine;
            return new Card
            {
                Name = p.Name, Avatar = p.Avatar, Level = p.Level, Exp = p.Exp, Mito = p.Mito, Mine = true,
                Crew = crew?.Name ?? "", CrewMark = crew?.Mark ?? 0, CrewGrade = crew?.Grade ?? "C",
                Ladder = crew == null ? null : crew.LadderRank > 0 ? $"{crew.LadderClass} Class  {crew.LadderRank}" : crew.LadderClass,
                Races = p.PvpRaces, Wins = p.PvpWins, Deliveries = MissionRecords.Current.DeliveriesCompleted, Km = p.DistanceKm,
                Licence = p.MountedLicence, Roo = p.Faction == Factions.Roo,
                Vehicle = p.Car?.VehicleIndex ?? -1, Grade = p.Car?.Grade ?? 0, Online = true,
            };
        }

        // ================= USERINFO 1810000 =================
        /// <summary>MYINFO: the driver's own card (an OriginalMenus screen: Esc / CLOSE close it).</summary>
        public static NuiView Own()
        {
            var c = MyCard();
            if (c == null) return null;
            var v = OriginalMenus.OpenScreen(OwnDialog);
            if (!v) return null;
            UiManager.Instance.On("ID_USERINFO_CLOSE", _ => OriginalMenus.Close());
            FillOwn(v, c);
            // the crew is fetched once per driver; redraw when it arrives
            if (Teams.Mine == null)
                Teams.Load(() => { var open = OwnView; if (open && Teams.Mine != null) FillOwn(open, MyCard()); });
            return v;
        }

        static void FillOwn(NuiView v, Card c)
        {
            const string P_ = "ID_USERINFO_";
            FillCommon(v, P_, c);
            v.SetText(P_ + "MITO", c.Mito.ToString("N0"));
            v.SetVisible(P_ + "TIMEATTACK", c.Roo);
            var car = P?.Car;
            if (car == null) return;
            OriginalMenus.FillStats(v, P_ + "CARINFO_", car);
        }

        // ================= USERINFO2ND 1830000 =================
        /// <summary>User Info on another driver (the player menu, the friend list): asks the server, or makes an AI card offline.</summary>
        public static void Other(string name, Action<bool> done = null)
        {
            if (string.IsNullOrWhiteSpace(name)) { done?.Invoke(false); return; }
            if (P != null && string.Equals(name.Trim(), P.Name, StringComparison.OrdinalIgnoreCase)) { Own(); done?.Invoke(true); return; }
            if (SocialNet.Online)
            {
                SocialNet.Request("driver.info", new Dictionary<string, object> { ["name"] = name.Trim() }, r =>
                {
                    if (r == null || !r.B("ok")) { UiPopup.Message(SocialNet.Err(r)); done?.Invoke(false); return; }
                    ShowOther(FromServer(r));
                    done?.Invoke(true);
                });
                return;
            }
            var ai = SocialStore.AiName(name);
            if (ai == null) { UiPopup.Message($"{name.Trim()} does not exist."); done?.Invoke(false); return; }
            ShowOther(AiCard(ai));
            done?.Invoke(true);
        }

        public static Card FromServer(Dictionary<string, object> r)
        {
            var car = r.O("car");
            return new Card
            {
                Name = r.S("name", ""), Avatar = Mathf.Clamp(r.I("avatar", 1), 1, 8), Level = Mathf.Max(1, r.I("level", 1)), Exp = (long)r.N("exp"),
                Crew = r.S("crew", ""), CrewMark = r.I("crewMark"), CrewGrade = r.S("crewGrade", "C"), Races = r.I("races"), Wins = r.I("wins"), TeamRaces = r.I("teamRaces"), TeamWins = r.I("teamWins"),
                Deliveries = r.I("deliveries"), Km = (float)r.N("km"), Licence = r.ContainsKey("licence") ? r.I("licence", -1) : -1, Roo = r.B("roo"), Online = r.B("online"),
                Vehicle = car != null ? car.I("vehicle", -1) : -1, Grade = car != null ? car.I("grade") : 0,
            };
        }

        /// <summary>An AI driver's card: SocialStore's seeded level / grade / portrait and steady records.</summary>
        public static Card AiCard(string ai)
        {
            var i = SocialStore.AiInfo(ai);
            int seed = 0; foreach (char ch in ai) seed = seed * 31 + ch;
            seed &= 0x7fffffff;
            var starters = GameTables.StarterCars;
            int races = i.level * 7 % 90 + 10;
            return new Card
            {
                Name = ai, Avatar = i.avatar, Level = i.level, Exp = 1000L * i.level * i.level / 2, Races = races, Wins = i.level * 3 % 30 % races,
                Deliveries = seed % 40, Km = 50f + seed % 900, Online = true,
                Vehicle = starters.Count > 0 ? starters[seed % starters.Count].Index : -1, Grade = Mathf.Max(0, i.grade - 1),
            };
        }

        public static NuiView ShowOther(Card c)
        {
            var ui = UiManager.Instance;
            if (!ui || c == null) return null;
            Hook(ui);
            SocialUi.Close(ref other);
            other = ui.Open(OtherDialog);
            if (!other) return null;
            otherCard = c;
            const string P_ = "ID_USERINFO2ND_";
            FillCommon(other, P_, c);
            other.SetText(P_ + "MITO", "***,***,***");
            other.SetEnabled(P_ + "JOIN_RANCINGTEAM", c.Crew.Length > 0);
            other.SetEnabled(P_ + "BUY_GOODS", false);
            // the driver's car turning in the dark panel under the card (CAMERA_LEFT / RIGHT turn it, RESTORE resets)
            ShowCar(c);
            var owner = other;
            other.gameObject.AddComponent<ReleaseOnDestroy>().Run = () => { if (other == owner || !other) ReleaseCar(); };
            UiPopup.BringToFront();
            return other;
        }

        public static void CloseOther() { ReleaseCar(); SocialUi.Close(ref other); otherCard = null; }

        /// <summary>True while USERINFO2ND shows the driver's car in 3D.</summary>
        public static bool CarShown => preview != null;

        static void ShowCar(Card c)
        {
            ReleaseCar();
            var anchor = other ? other.Control("ID_USERINFO2ND_BASE2") : null;
            if (!anchor || c == null || c.Vehicle < 0 || GameTables.Car(c.Vehicle) == null) return;
            preview = CarPreview.Create(anchor, new Vector2(526f, 246f), "ID_USERINFO2ND_CAMERA_");
            preview?.Show(new OwnedCar { VehicleIndex = c.Vehicle, Grade = c.Grade });
            // fit the view inside the panel's frame (the strips have an 18 px border) from under the name plate to the
            // button bar, and crop the preview's portrait render to that shape instead of stretching it
            var cell = anchor.transform.parent.Find("carview") as RectTransform;
            var img = cell ? cell.GetComponent<RawImage>() : null;
            if (cell && img)
            {
                var size = new Vector2(490f, 236f);
                cell.anchoredPosition = ((RectTransform)anchor.transform).anchoredPosition + new Vector2(18f, 0f);
                cell.sizeDelta = size;
                var rt = img.texture;
                float h = rt ? Mathf.Clamp01(rt.width / (float)rt.height * size.y / size.x) : 1f;
                img.uvRect = new Rect(0f, (1f - h) * 0.5f, 1f, h);
            }
        }

        static void ReleaseCar()
        {
            preview?.Release();
            preview = null;
        }

        sealed class ReleaseOnDestroy : MonoBehaviour
        {
            public Action Run;
            void OnDestroy() => Run?.Invoke();
        }

        static void Hook(UiManager ui)
        {
            if (hooked == ui) return;
            hooked = ui;
            ui.On("ID_USERINFO2ND_CLOSE", _ => CloseOther());
            // the personal shop: the other driver's fitted custom parts (a server switch; the client's button had no handler)
            ui.On("ID_USERINFO2ND_BUY_GOODS", _ => { if (otherCard != null) Services.PersonalShop.Open(otherCard.Name); });
            if (other) other.SetEnabled("ID_USERINFO2ND_BUY_GOODS", Services.ServerSwitches.PersonalShop);
            ui.On("ID_USERINFO2ND_CAMERA_RESTORE", _ => { if (otherCard != null) ShowCar(otherCard); });
            ui.On("ID_USERINFO2ND_ADDFRIEND", _ =>
            {
                var c = otherCard;
                if (c == null) return;
                UiPopup.Show(MissionText.Get("10162"), id => { if (id == UiPopup.Yes) Friends.Add(c.Name); }, UiPopup.Yes, UiPopup.No);
            });
            ui.On("ID_USERINFO2ND_JOIN_RANCINGTEAM", _ =>
            {
                var c = otherCard;
                if (c == null) return;
                if (c.Crew.Length == 0) { UiPopup.Message(MissionText.Get("10164", c.Name)); return; }
                CloseOther();
                Teams.RequestJoin(c.Name);
            });
        }

        // ================= shared =================
        static void FillCommon(NuiView v, string P_, Card c)
        {
            v.SetText(P_ + "NAME", c.Name);
            bool crew = !string.IsNullOrEmpty(c.Crew);
            v.SetVisible(P_ + "RACINGTEAM", crew);
            v.SetText(P_ + "RACINGTEAM", c.Crew);
            // 0x54b450: the crew name in its class colour (0x514530 -> 0x871bc8: C white, B blue, A yellow, S red)
            var team = v.Control(P_ + "RACINGTEAM");
            var label = team ? team.GetComponentInChildren<Text>(true) : null;
            if (label) label.color = c.CrewGrade == "S" ? new Color32(0xff, 0x2e, 0x00, 0xff) : c.CrewGrade == "A" ? new Color32(0xff, 0xff, 0x00, 0xff)
                                   : c.CrewGrade == "B" ? new Color32(0x00, 0x99, 0xff, 0xff) : new Color32(0xff, 0xff, 0xff, 0xff);
            SocialUi.ShowMark(v, P_ + "TEAMMARK", crew ? c.CrewMark : 0);
            Portrait(v, P_ + "IMG_POS", c.Avatar);
            v.SetText(P_ + "LEVEL", MissionText.Printf("%3u", c.Level));
            v.SetText(P_ + "EXP", c.Exp.ToString("N0"));
            v.SetText(P_ + "BATTLERECORD_1ST", MissionText.Get("10159", c.Races, c.Wins));
            v.SetText(P_ + "BATTLERECORD_2ND", MissionText.Get("10160", c.TeamRaces, c.TeamWins));
            v.SetText(P_ + "QUICKSERVICE_REC", MissionText.Get("10161", c.Deliveries));
            v.SetText(P_ + "TOTALKM", MissionText.Get("10729", (long)c.Km));
            // 0x54b968: in a crew the crew's ladder entry and no cover; else "-" under the cover plate
            v.SetText(P_ + "LADDERRECORD", c.Ladder ?? "-");
            v.SetVisible(P_ + "LADDERRECORD_COVER", c.Ladder == null);
            v.SetVisible(P_ + "LICENSE", c.Licence >= 0);
            v.SetVisible(P_ + "TEAMROO", c.Roo);
            var info = c.Vehicle >= 0 ? GameTables.Car(c.Vehicle) : null;
            v.SetVisible(P_ + "CARNAME_POS", info != null);
            v.SetVisible(P_ + "CARGRADE_POS", info != null);
            if (info == null) return;
            var grade = ShopData.Grade(info, c.Grade);
            OriginalMenus.Plate(v, P_ + "CARNAME_POS", grade?.UiName ?? info.UiName, info);
            OriginalMenus.Emblem(v, P_ + "CARGRADE_POS", grade);
        }

        /// <summary>Portrait N (1..8): USERCHAR 1890000's INDEX(N-1) image (a 128 px cell of ch_u_001.dds) laid over
        /// IMG_POS, which game.nui draws with portrait 1 (the client renders the USERCHAR cell there, 0x542ca0).</summary>
        static void Portrait(NuiView v, string id, int avatar)
        {
            var c = v ? v.Control(id) : null;
            if (!c) return;
            var dlg = UiAssets.Layout.Get(1890000);
            string want = $"ID_USERCHAR_INDEX{Mathf.Clamp(avatar, 1, 8) - 1:00}";
            string reference = null;
            if (dlg != null) foreach (var ctl in dlg.Controls) if (ctl.IdName == want) reference = ctl.I("I1_1");
            var tex = reference != null ? UiAssets.Texture(reference) : null;
            var t = c.transform.Find("portrait");
            RawImage img = t ? t.GetComponent<RawImage>() : null;
            if (!img)
            {
                var rt = NuiView.NewRect("portrait", c.transform);
                rt.sizeDelta = ((RectTransform)c.transform).sizeDelta;
                img = rt.gameObject.AddComponent<RawImage>();
                img.raycastTarget = false;
            }
            img.texture = tex;
            img.uvRect = new Rect(0, 1, 1, -1);   // DDS rows top-down, like NuiView's own images
            img.enabled = tex != null;
        }
    }
}
