using System;
using System.Collections.Generic;
using System.Linq;
using DriftCity.UI;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// OBEVENT 3330000 + DIALOG 3360000: the hard-coded campaign prize notices of the 2007-2008 Korean service ("open beta").
    ///
    /// Client [exact]: ui+0x36c (vtable 0x8740d4, creator 0x673540, Show 0x672f00, OK 0x673330); op 0x89 sub 1 / 4 / 5 queues
    /// prize a (1..39; sub 2 queues 7, sub 3 queues 8) in a deque, the first shows at once and OK shows the next one or
    /// closes. The table at 0x8c4514 + n * 0xe4 holds {int id, char[32] picture, char[64] title, char[128] body} in CP949
    /// only (read out with scratchpad\cbw\obdump.py); 3360000 shows the one picture control of the entry, POS_ITEM the item
    /// icon (default lotto_00001). The same object is also the generic reward popup (0x672f40) the remake's EventPopup uses.
    /// Remake: the table with the Korean title and an English rendering of each body (no US text ever existed);
    /// <see cref="Queue"/> is the 0x89 entry point; "obevent=n,m" in events.cfg (env DC_OBEVENT) queues prizes once a day
    /// for testing; the Indy4 props event (PropEvent) shows entries 32-35 as the 2008 campaign did.
    /// </summary>
    public static class ObEvent
    {
        public const int Dialog = 3330000, PictureDialog = 3360000;

        public sealed class Entry { public int N; public string Picture, TitleKr, Title, Body, Item; }

        static Entry E(int n, string pic, string kr, string title, string body, string item = null) => new Entry { N = n, Picture = pic, TitleKr = kr, Title = title, Body = body, Item = item };

        const string Raffle = "\n[See the event page]";
        /// <summary>The 39 prizes of 0x8c4514 (Korean titles kept, English bodies).</summary>
        public static readonly Entry[] Table =
        {
            E(1, "evt01_070110_001", "드라이빙 미션 이벤트", "Driving Mission Event", "Congratulations!\nYou got your licence and entered\nthe Hangame cushion raffle!" + Raffle),
            E(2, "evt01_070110_002", "드라이빙 미션 이벤트", "Driving Mission Event", "Congratulations!\nYou finished 10 chases and entered\nthe optical mouse raffle!" + Raffle),
            E(3, "evt01_070110_003", "드라이빙 미션 이벤트", "Driving Mission Event", "Congratulations!\nYou finished 15 deliveries and entered\nthe headset raffle!" + Raffle),
            E(4, "evt01_070110_004", "드라이빙 미션 이벤트", "Driving Mission Event", "Congratulations!\nYou finished 20 Battle Zone races and entered\nthe PMP raffle!" + Raffle),
            E(5, "evt01_070110_005", "드라이빙 미션 이벤트", "Driving Mission Event", "Congratulations!\nYou reached level 10 and entered\nthe notebook PC raffle!" + Raffle),
            E(6, "evt01_070110_006", "드라이빙 미션 이벤트", "Driving Mission Event", "Congratulations!\nYou finished every mission and entered\nthe Dodge Caliber raffle!" + Raffle),
            E(7, "evt02_070110_001", "PC방 1시간 무료 이벤트", "PC Cafe Free Hour", "Congratulations!\nYou made a 10 combo and received\na one-hour PC cafe coupon!" + Raffle),
            E(8, "evt01_070207_001", "PC방 10000미토 지급 이벤트", "PC Cafe 10,000 Mito", "Congratulations!\nYou logged on at a partner PC cafe\nand received 10,000 Mito!", "i_d_m_10k"),
            E(9, "evt01_070308_001", "서울모터쇼 이벤트", "Seoul Motor Show", "Congratulations!\nYou finished an instant mission and entered\nthe Seoul Motor Show raffle!"),
            E(10, "evt01_070308_002", "PC방 골든타임", "PC Cafe Golden Time", "Congratulations!\nYou logged on at a partner PC cafe\nand received 1,000 Mito!", "i_d_m_1k"),
            E(11, "evt01_070404_001", "피자헛 이벤트", "Pizza Hut Event", "Congratulations!\nYou received a free pizza coupon.\nCheck its serial number in your inventory!"),
            E(12, "evt01_070404_002", "피자헛 이벤트", "Pizza Hut Event", "Congratulations!\nYou received a pizza discount coupon.\nCheck its serial number in your inventory!"),
            E(13, "evt01_070404_003", "피자헛 이벤트", "Pizza Hut Event", "Congratulations!\nYou obtained Mittron Upgrade Solution Lite!"),
            E(14, "evt01_070404_004", "피자헛 이벤트", "Pizza Hut Event", "Congratulations!\nYou obtained Mittron Upgrade Solution Medium!"),
            E(15, "evt01_070404_005", "피자헛 이벤트", "Pizza Hut Event", "Congratulations!\nYou obtained a Mittron Enhancer!", "i_n_00003"),
            E(16, "evt01_070404_006", "피자헛 이벤트", "Pizza Hut Event", "Congratulations!\nYou obtained Mittron Fireworks!", "i_n_00011"),
            E(17, "evt01_070503_001", "해피빈 사랑의 대질주", "Happybean Charity Run", "Congratulations!\nA Happybean has been donated.\n\n(One mission a day)"),
            E(18, "evt01_070621_001", "택시4 이벤트", "Taxi 4 Event", "Congratulations!\nYou received two Taxi 4\nmovie tickets!" + Raffle),
            E(19, "evt01_070621_002", "택시4 이벤트", "Taxi 4 Event", "Congratulations!\nYou received a Peugeot cap." + Raffle),
            E(20, "evt01_070621_003", "택시4 이벤트", "Taxi 4 Event", "Congratulations!\nYou received a Maestro wristwatch." + Raffle),
            E(21, "evt01_070621_004", "택시4 이벤트", "Taxi 4 Event", "Congratulations!\nYou received an MP3 player." + Raffle),
            E(22, "evt01_070621_005", "택시4 이벤트", "Taxi 4 Event", "Congratulations!\nYou received a DVD player." + Raffle),
            E(23, "evt01_070621_006", "택시4 이벤트", "Taxi 4 Event", "Congratulations!\nYou obtained Mittron Upgrade Solution Lite!"),
            E(24, "evt01_070621_007", "택시4 이벤트", "Taxi 4 Event", "Congratulations!\nYou obtained Mittron Upgrade Solution Medium!"),
            E(25, "evt01_070621_008", "택시4 이벤트", "Taxi 4 Event", "Congratulations!\nYou obtained a Mittron Enhancer!", "i_n_00003"),
            E(26, "evt01_070621_009", "택시4 이벤트", "Taxi 4 Event", "Congratulations!\nYou obtained Mittron Fireworks!", "i_n_00011"),
            E(27, "evt01_070906_001", "코믹2.0이벤트", "Comic 2.0 Event", "Congratulations!\nYou obtained Mittron Upgrade Solution Lite!"),
            E(28, "evt01_070906_002", "코믹2.0이벤트", "Comic 2.0 Event", "Congratulations!\nYou obtained Mittron Upgrade Solution Medium!"),
            E(29, "evt01_070906_003", "코믹2.0이벤트", "Comic 2.0 Event", "Congratulations!\nYou obtained a Mittron Enhancer!", "i_n_00003"),
            E(30, "evt01_070906_004", "코믹2.0이벤트", "Comic 2.0 Event", "Congratulations!\nYou obtained Mittron Fireworks!", "i_n_00011"),
            E(31, "evt01_071205_001", "해피빈 사랑의 대질주2", "Happybean Charity Run 2", "Congratulations!\nYou received a Mittron Enhancer\nand donated a Happybean.\n\n(One mission a day)", "i_n_00003"),
            E(32, "evt01_080417_001", "인디아나 존스4 이벤트", "Indiana Jones 4 Event", "Congratulations!\nYou received Indy's fedora.", "i_n_00051"),
            E(33, "evt01_080417_002", "인디아나 존스4 이벤트", "Indiana Jones 4 Event", "Congratulations!\nYou received Indy's whip.", "i_n_00052"),
            E(34, "evt01_080417_003", "인디아나 존스4 이벤트", "Indiana Jones 4 Event", "Congratulations!\nYou received the crystal skull.", "i_n_00053"),
            E(35, "evt01_080417_004", "인디아나 존스4 이벤트", "Indiana Jones 4 Event", "Congratulations!\nYou received an Indiana Jones 4\nevent raffle ticket.", "i_n_00054"),
            E(36, "evt01_070621_006", "인디아나 존스4 이벤트", "Indiana Jones 4 Event", "Congratulations!\nYou received Mittron Upgrade Solution Lite."),
            E(37, "evt01_070621_007", "인디아나 존스4 이벤트", "Indiana Jones 4 Event", "Congratulations!\nYou received Mittron Upgrade Solution Medium."),
            E(38, "evt01_070621_008", "인디아나 존스4 이벤트", "Indiana Jones 4 Event", "Congratulations!\nYou received a Mittron Enhancer.", "i_n_00003"),
            E(39, "evt01_070621_009", "인디아나 존스4 이벤트", "Indiana Jones 4 Event", "Congratulations!\nYou received Mittron Fireworks.", "i_n_00011"),
        };

        static NuiView view, picture;
        static UiManager hooked;
        static readonly Queue<int> queue = new Queue<int>();
        public static NuiView View => view;
        public static NuiView PictureView => picture;
        public static int Showing { get; private set; }

        public static Entry Get(int n) => Table.FirstOrDefault(e => e.N == n);

        /// <summary>Op 0x89 sub 1 / 4 / 5 (sub 2 -> 7, sub 3 -> 8): queue a prize, shown at once when nothing is up.</summary>
        public static void Queue(int n)
        {
            if (Get(n) == null) return;
            queue.Enqueue(n);
            if (!view) Next();
        }

        static void Next()
        {
            var ui = UiManager.Instance;
            if (!ui) return;
            if (queue.Count == 0) { Close(); return; }
            var e = Get(queue.Dequeue());
            if (hooked != ui) { hooked = ui; ui.On("ID_OBEVENT_OK", _ => { EventUi.Sfx("SID_BUTTON_CLICK"); Next(); }); }
            if (!view) view = ui.Open(Dialog);
            if (!picture) picture = ui.Open(PictureDialog);
            if (!view) return;
            Showing = e.N;
            view.SetText("ID_OBEVENT_TITLE", e.Title);
            view.SetText("ID_OBEVENT_CONTENTS", e.Body);
            EventUi.Icon(view, "ID_OBEVENT_POS_ITEM", e.Item ?? "lotto_00001");
            if (picture) foreach (var c in picture.Controls.Concat(picture.GetComponentsInChildren<NuiControlView>(true))) if (c) c.gameObject.SetActive(c.Data.Name == e.Picture);
            EventUi.Sfx("SID_SYSWIN_CREATE");
        }

        public static void Close()
        {
            var ui = UiManager.Instance;
            queue.Clear();
            if (view && ui) ui.Close(view);
            if (picture && ui) ui.Close(picture);
            view = null; picture = null; Showing = 0;
        }

        /// <summary>"obevent=n,m" in events.cfg (env DC_OBEVENT): the listed prizes once per UTC day (a test switch).</summary>
        public static void CheckConfig(EventState st)
        {
            var cfg = SeasonEvents.ConfigValue("obevent");
            if (string.IsNullOrEmpty(cfg) || st == null) return;
            string mark = "ob:" + EventNet.Today;
            if (st.CouponSeen.Contains(mark)) return;
            st.CouponSeen.RemoveAll(x => x.StartsWith("ob:"));
            st.CouponSeen.Add(mark);
            foreach (var part in cfg.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)) if (int.TryParse(part, out int n)) Queue(n);
        }
    }
}
