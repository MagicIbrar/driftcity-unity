using DriftCity.UI;

namespace DriftCity.Social
{
    /// <summary>
    /// The Community window ("Communication Zone", COMM 3970000; the main bar's ADV_COMM, F1, and the new-note notice
    /// open it, 0x5ed580): tabs My Crew (MY 3980000), Walkie-Talkie (friends, MYFRIEND 4000000) and Messages (NOTE
    /// 4010000). The default tab is Messages while an inbox note is unread, else My Crew with a crew, else the friends
    /// (0x5ed3f0). The hidden *_BTN controls (Tab key) cycle the tabs and skip My Crew without a crew.
    /// </summary>
    public static class Community
    {
        public const int Frame = 3970000;
        public enum Tab { Default, MyTeam, MyFriend, Note }

        static NuiView frame;
        static UiManager hooked;
        public static Tab Current { get; private set; }
        public static bool IsOpen => frame;
        public static NuiView View => frame;

        static void Hook()
        {
            var ui = UiManager.Instance;
            if (!ui || hooked == ui) return;
            hooked = ui;
            ui.On("ID_COMM_BASE_MYTEAM", _ => Select(Tab.MyTeam));
            ui.On("ID_COMM_BASE_MYFRIEND", _ => Select(Tab.MyFriend));
            ui.On("ID_COMM_BASE_NOTE", _ => Select(Tab.Note));
            ui.On("ID_COMM_BASE_CLOSE", _ => Close());
            foreach (var id in new[] { "ID_COMM_BASE_MYTEAM_BTN", "ID_COMM_BASE_MYFRIEND_BTN", "ID_COMM_BASE_NOTE_BTN" })
                ui.On(id, _ => Select(Current == Tab.MyTeam ? Tab.MyFriend : Current == Tab.MyFriend ? Tab.Note : Teams.HasCrew ? Tab.MyTeam : Tab.MyFriend));
        }

        /// <summary>Toggles the window like the main bar button; with a tab it always opens on that tab.</summary>
        public static void Toggle() { if (frame) Close(); else Open(); }

        public static void Open(Tab tab = Tab.Default)
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            Hook();
            if (!frame) { frame = ui.Open(Frame); SocialUi.HideStartHidden(frame); }
            if (!frame) return;
            if (tab != Tab.Default) { Select(tab); return; }
            // the default tab needs the inbox and the crew first
            Mail.Load(() => Teams.Load(() => { if (frame) Select(Mail.Unread > 0 ? Tab.Note : Teams.HasCrew ? Tab.MyTeam : Tab.MyFriend); }));
        }

        public static void Select(Tab tab)
        {
            if (!frame) { Open(tab); return; }
            if (tab == Tab.MyTeam && !Teams.HasCrew)
            {
                // 10425 "You don't have a Crew"; the remake offers the Crew Center here (the original's is in the Driver Dome)
                Teams.Load(() =>
                {
                    if (Teams.HasCrew) { Show(Tab.MyTeam); return; }
                    UiPopup.Show(SocialNet.T("10425", "You don't have a Crew") + "\nOpen the Crew Center to create one?", id => { if (id == UiPopup.Yes) { Close(); Teams.OpenCenter(); } }, UiPopup.Yes, UiPopup.No);
                });
                return;
            }
            Show(tab);
        }

        static void Show(Tab tab)
        {
            Current = tab;
            if (tab != Tab.MyTeam) Teams.HidePage();
            if (tab != Tab.MyFriend) Friends.Hide();
            if (tab != Tab.Note) Mail.Hide();
            if (frame)
            {
                frame.Control("ID_COMM_BASE_MYTEAM").Selected = tab == Tab.MyTeam;
                frame.Control("ID_COMM_BASE_MYFRIEND").Selected = tab == Tab.MyFriend;
                frame.Control("ID_COMM_BASE_NOTE").Selected = tab == Tab.Note;
            }
            switch (tab)
            {
                case Tab.MyTeam: Teams.ShowPage(); break;
                case Tab.MyFriend: Friends.Show(); break;
                case Tab.Note: Mail.Show(); break;
            }
        }

        public static void Close()
        {
            Teams.HidePage();
            Friends.Hide();
            Mail.Hide();
            SocialUi.Close(ref frame);
            Current = Tab.Default;
        }
    }
}
