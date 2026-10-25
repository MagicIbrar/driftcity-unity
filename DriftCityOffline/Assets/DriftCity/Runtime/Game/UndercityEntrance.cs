using System.Collections.Generic;
using System.Linq;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The Undercity entrance of a city (0x6f8e40; DriftCity_RE/UNDERCITY.md): the post named at 0x879cc8..0x879e2c
    /// (Moon Palace post_1st_00784, Koinonia post_2nd_00789, Cras post_3rd_00791, Oros post_4rd_00626) marked with
    /// effects\undercity\uc_gate.nif and the minimap's UCENTRY icon. Driving into it shows Tatiana's UC_Enter lines in
    /// QUESTDIALOG with "What is the Undercity?" (UC_Intro) and Accept; Accept shows the portal fee (UC_NeedMito),
    /// refuses a driver short of Mito (UC_Err_NeedMito) and otherwise starts <see cref="UndercityStage"/>. The client
    /// also wants a party of 2-4 in the same city (UC_Err_PlayTogether / UC_Err_SameCity / UC_Err_MaxPlayerCnt): online
    /// those are checked here and by the server, which asks the party (<see cref="UndercityParty"/>); offline AI partners
    /// make up the party (the offline party's AI drivers first).
    /// </summary>
    public sealed class UndercityEntrance : MonoBehaviour
    {
        /// <summary>Trigger distance in metres (the client's near-post test 0x6f55f0; this radius is the rebuild's).</summary>
        public float Radius = 25f;
        public int AiPartners = 3;
        public static UndercityEntrance Instance { get; private set; }
        public CitySession Session;
        public string City;
        public UndercityData.Layout Layout { get; private set; }
        /// <summary>True while the entrance dialogs are open.</summary>
        public bool DialogOpen => MissionUi.ScriptOpen && talking;

        GameObject gate;
        bool armed = true, talking;
        float nextCheck;

        /// <summary>Puts the entrance into a city session (the lead's GameFlow hook, next to StationPanel.Attach).</summary>
        public static UndercityEntrance Attach(CitySession session, string city)
        {
            if (!session) return null;
            var layout = UndercityData.For(city);
            if (layout == null) return null;
            if (!session.TryGetComponent(out UndercityEntrance e)) e = session.gameObject.AddComponent<UndercityEntrance>();
            e.Session = session;
            e.City = city;
            e.Layout = layout;
            e.armed = true;
            e.PlaceGate();
            Instance = e;
            return e;
        }

        void PlaceGate()
        {
            if (gate) Destroy(gate);
            var fs = DriftCityData.Files;
            if (fs == null || !fs.Exists(@"effects\undercity\uc_gate.nif")) return;
            try
            {
                gate = new NifSceneBuilder { LogErrors = false }.Load(@"effects\undercity\uc_gate.nif");
                gate.name = "Undercity Gate";
                foreach (var c in gate.GetComponentsInChildren<Collider>()) Destroy(c);
                var p = Layout.Entrance.Position;
                Physics.SyncTransforms();
                if (Physics.Raycast(p + Vector3.up * 40f, Vector3.down, out var hit, 120f, ~0, QueryTriggerInteraction.Ignore)) p = hit.point;
                gate.transform.position = p;
                // the gate belongs to the city: it goes with the world when the city is left
                if (Session && Session.Traffic && Session.Traffic.transform.parent) gate.transform.SetParent(Session.Traffic.transform.parent, true);
            }
            catch (System.Exception ex) { Debug.LogWarning("Undercity gate: " + ex.Message); }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (gate) Destroy(gate);
            if (talking) MissionUi.CloseScript();
        }

        void Update()
        {
            var flow = GameFlow.Instance;
            var car = flow ? flow.PlayerCar : null;
            if (!car || Layout == null || UndercityStage.Active || UndercityParty.Pending) return;
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.25f;
            float d = MissionPosts.Flat(car.transform.position, Layout.Entrance.Position);
            if (d > Radius * 2.4f) { armed = true; if (talking && !MissionUi.ScriptOpen) talking = false; }
            if (!armed || talking || d > Radius) return;
            if (GameFlow.MissionActive || flow.MenuOpen) return;
            armed = false;
            Open();
        }

        /// <summary>UC_Enter in QUESTDIALOG with WHATISUC / UCACCEPT / DENY (0x6f8e40 -> 0x688780, 0x546520).</summary>
        public void Open()
        {
            var enter = UndercityData.Text("UC_Enter");
            talking = true;
            var v = MissionUi.Script(ToScript(enter), enter.Name ?? "Undercity Admin Tatiana", true, null, () => talking = false);
            if (!v) { talking = false; return; }
            v.SetVisible("ID_QUESTDIALOG_ACCEPT", false);
            v.SetVisible("ID_QUESTDIALOG_UCACCEPT", true);
            v.SetVisible("ID_QUESTDIALOG_WHATISUC", true);
            v.SetVisible("ID_QUESTDIALOG_DENY", true);
            var ui = UiManager.Instance;
            ui.On("ID_QUESTDIALOG_WHATISUC", _ => Intro());
            ui.On("ID_QUESTDIALOG_UCACCEPT", _ => Fee());
            AudioLibrary.PlaySfx("sfx/SID_SYSWIN_CREATE");
        }

        void Intro()
        {
            var intro = UndercityData.Text("UC_Intro");
            MissionUi.Script(ToScript(intro), intro.Name ?? "Undercity Admin Tatiana", false, null, Open);
        }

        /// <summary>UC_NeedMito: the missions that will be cancelled and the portal's fee; Accept enters.</summary>
        void Fee()
        {
            var need = UndercityData.Text("UC_NeedMito");
            string script = MissionText.Printf(ToScript(need), UndercityData.FeeFor(City));
            MissionUi.Script(script, need.Name ?? "Undercity Admin Tatiana", true, () =>
            {
                MissionUi.CloseScript();
                var p = PlayerProfile.Current;
                if (p == null || p.Mito < UndercityData.FeeFor(City))
                {
                    var err = UndercityData.Text("UC_Err_NeedMito");
                    MissionUi.Script(ToScript(err), err.Name ?? "Undercity Admin Tatiana", false, null, () => talking = false);
                    return;
                }
                talking = false;
                if (Social.SocialNet.Online) RequestOnline();
                else Enter();
            }, () => talking = false);
        }

        /// <summary>Online: the party checks, then the server asks the party (WAITPLAYER / REQUESTINVITE).</summary>
        public void RequestOnline()
        {
            string err = UndercityParty.CheckParty(City);
            if (err != null) { ShowError(err); return; }
            UndercityParty.Request(City, (key, text) =>
            {
                if (key == null) return;
                if (key.StartsWith("UC_")) ShowError(key);
                else UiPopup.Message(text);
            });
        }

        /// <summary>UC_Err_PlayTogether / UC_Err_SameCity / UC_Err_MaxPlayerCnt / UC_Err_NeedMito in QUESTDIALOG.</summary>
        public void ShowError(string key)
        {
            var err = UndercityData.Text(key);
            LastError = key;
            talking = true;
            MissionUi.Script(ToScript(err), err.Name ?? "Undercity Admin Tatiana", false, null, () => talking = false);
        }

        /// <summary>The last UC_Err_* shown (tests).</summary>
        public string LastError { get; private set; }

        /// <summary>Starts the run (the missions running are given up first, as UC_NeedMito says).</summary>
        public UndercityStage Enter(float timeScale = 1f)
        {
            if (UndercityStage.Active || !Session) return UndercityStage.Active;
            if (ChaseMission.Active) Destroy(ChaseMission.Active.gameObject);
            return UndercityStage.Begin(Session, AiPartners, timeScale);
        }

        /// <summary>A UC text as the NPC script markup QUESTDIALOG reads (&lt;ucenter_img&gt; is the portrait).</summary>
        static string ToScript(UndercityData.Message m)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(m.Image)) parts.Add($"<pimg {m.Image}>");
            if (!string.IsNullOrEmpty(m.Name)) parts.Add($"<name {m.Name}>");
            for (int i = 0; i < m.Pages.Count; i++)
            {
                if (i > 0) parts.Add("<next>");
                parts.AddRange(m.Pages[i].Select(l => $"<text {l}>"));
            }
            return string.Join("", parts);
        }

        /// <summary>The UCENTRY minimap icon (game.nui 190000 ID_MINIMAP_ICON_UCENTRY).</summary>
        public List<MissionMarkers.Marker> Markers()
        {
            var list = new List<MissionMarkers.Marker>();
            if (Layout?.Entrance != null && !UndercityStage.Active)
                list.Add(new MissionMarkers.Marker { Icon = "ID_MINIMAP_ICON_UCENTRY", Position = Layout.Entrance.Position, Label = "Undercity" });
            return list;
        }
    }
}
