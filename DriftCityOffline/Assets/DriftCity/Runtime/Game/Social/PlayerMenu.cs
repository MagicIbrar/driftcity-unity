using System.Collections.Generic;
using DriftCity.Game;
using DriftCity.Net;
using DriftCity.UI;

namespace DriftCity.Social
{
    /// <summary>
    /// The player command menu COMMAND 700000 (0x5bdce0), opened on another driver: User Info, Trade, Add Friend, the
    /// crew join request (the BADBOY button sends 0x367, 10163 / 10164), Party Join (410000), Send Letter (2940000) and
    /// Own License. The driver's name is in USERNAME.
    /// </summary>
    public static class PlayerMenu
    {
        public const int Dialog = 700000;
        static NuiView view;
        static string target;
        static UiManager hooked;
        public static NuiView View => view;
        public static string Target => target;

        static void Hook()
        {
            var ui = UiManager.Instance;
            if (!ui || hooked == ui) return;
            hooked = ui;
            ui.On("ID_COMMAND_USERINFO", _ => { var t = target; Close(); DriftCity.Game.DriverInfoScreen.Other(t); });
            ui.On("ID_COMMAND_TRADE", _ => { var t = target; Close(); TradeWindow.Request(t); });
            ui.On("ID_COMMAND_ADDFRIEND", _ => { var t = target; Close(); Friends.Add(t); });
            ui.On("ID_COMMAND_BADBOY", _ => { var t = target; Close(); Teams.RequestJoin(t); });
            ui.On("ID_COMMAND_PARTYJOIN", _ => { var t = target; Close(); PartyWindow.Invite(t); });
            ui.On("ID_COMMAND_SENDLETTER", _ => { var t = target; Close(); Mail.Compose(t); });
            ui.On("ID_COMMAND_OWNLICENSE", _ => { var t = target; Close(); DriftCity.Game.LicencePages.ShowOther(t); });   // OTHERLISENCEBASE 3070000
        }

        /// <summary>Opens the menu for a driver (the remote player picker / name plate calls this).</summary>
        public static void Open(string name)
        {
            var ui = UiManager.Instance;
            if (!ui || string.IsNullOrWhiteSpace(name) || name == PlayerProfile.Current?.Name) return;
            Hook();
            Close();
            view = ui.Open(Dialog);
            if (!view) return;
            target = name;
            view.SetText("ID_COMMAND_USERNAME", name);
        }

        public static void Close() => SocialUi.Close(ref view);

        /// <summary>User Info: level, crew and where the driver is (friend.locate, "Find a Friend" 10220).</summary>
        static void Info(string name)
        {
            if (!SocialNet.Online)
            {
                var ai = SocialStore.AiName(name);
                if (ai == null) return;
                var i = SocialStore.AiInfo(ai);
                UiPopup.Message($"{ai}  Lv {i.level}\nChannel {i.channel} - {SocialUi.CityName(i.city)}");
                return;
            }
            SocialNet.Request("friend.locate", new Dictionary<string, object> { ["name"] = name }, r =>
            {
                if (!r.B("ok")) { UiPopup.Message(SocialNet.Err(r)); return; }
                string crew = r.S("crew", "");
                UiPopup.Message($"{r.S("name")}  Lv {r.I("level")}{(crew.Length > 0 ? $"\n{crew} Crew" : "")}\n{r.O("location")?.S("text") ?? "Offline"}");
            });
        }
    }
}
