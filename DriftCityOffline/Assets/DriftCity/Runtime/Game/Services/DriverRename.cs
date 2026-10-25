using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DriftCity.Net;
using DriftCity.UI;

namespace DriftCity.Game.Services
{
    /// <summary>
    /// Driver rename (DriftCity_RE/SERVICES.md section 4; server src/drivers.js).
    /// Client [exact]: the Driver's Name Change Kit i_changenick (visualitem type 36 "ChangeNick", vshopitem 10510, 3,500,000
    /// Mito for 7 days, "Unchangeable for 60 days after a change") used from the bag (0x5d52d0 case 0x24) shows NICNAME
    /// 3770000 (ui+0x3cc): EDIT, NEW_NAMECONFIRM (the check: 10604 "This ID is already in use." / 10605 "This ID is
    /// available."), ACCEPT, CLOSE; op 0x06b answers 10954 / 10955 or 10960 "Name change failed. Please try again.".
    /// Remake rules: the driver-name rules of the character screen (2-16 letters, digits, _ -; else 11296); ACCEPT only for
    /// the name that passed the check; the kit is spent; 60 days between changes; the new name applies at once (10955).
    /// The kit is sold here for its table price (<see cref="BuyKit"/>; the custom shop's Etc tab is not rebuilt).
    /// </summary>
    public static class DriverRename
    {
        public const int Dialog = 3770000;
        public const string Kit = "i_changenick";
        public const long KitPrice = 3500000;
        public const int KitDays = 7, LockDays = 60;
        static readonly Regex NameRe = new Regex("^[A-Za-z0-9_\\-]{2,16}$");

        static NuiView view;
        static string checkedName;
        static PlayerProfile P => PlayerProfile.Current;
        static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public static bool IsOpen => view;
        public static NuiView View => view;
        public static bool HasKit => P != null && P.Count(Kit) > 0;
        /// <summary>Days until the name may change again (0 = now).</summary>
        public static int LockedDays => P == null ? 0 : (int)Math.Ceiling(Math.Max(0, P.NameChangedAt + LockDays * 86400000L - Now) / 86400000.0);

        /// <summary>Using the kit from the bag (or the shop's entry): NICNAME 3770000.</summary>
        public static NuiView Open()
        {
            var ui = UiManager.Instance;
            if (!ui || P == null) return null;
            if (!HasKit)
            {
                UiPopup.Show($"You need a Driver's Name Change Kit.\nBuy one for {KitPrice:N0} Mito? (usable for {KitDays} days)", b => { if (b == UiPopup.Yes) BuyKit(ok => { if (ok) Open(); }); }, UiPopup.Yes, UiPopup.No);
                return null;
            }
            if (LockedDays > 0) { UiPopup.Message($"The name cannot be changed for {LockedDays} more days."); return null; }
            Close();
            view = ui.Open(Dialog);
            if (!view) return null;
            checkedName = null;
            ui.On("ID_NICNAME_NEW_NAMECONFIRM", _ => Check(Input));
            ui.On("ID_NICNAME_ACCEPT", _ => Accept());
            ui.On("ID_NICNAME_CLOSE", _ => Close());
            var c = view.Control("ID_NICNAME_EDIT");
            if (c && c.Input)
            {
                c.Input.characterLimit = 16;
                c.Input.text = "";
                c.Input.onValueChanged.RemoveAllListeners();
                c.Input.onValueChanged.AddListener(_ => Refresh());
                c.Input.onSubmit.RemoveAllListeners();
                c.Input.onSubmit.AddListener(_ => Check(Input));
                c.Input.Select(); c.Input.ActivateInputField();
            }
            Refresh();
            return view;
        }

        public static void Close()
        {
            if (view) UiManager.Instance?.Close(view);
            view = null;
        }

        static string Input => view ? view.GetInput("ID_NICNAME_EDIT").Trim() : "";

        /// <summary>Tests / tools: types a name into EDIT.</summary>
        public static void Type(string name)
        {
            var c = view ? view.Control("ID_NICNAME_EDIT") : null;
            if (c && c.Input) c.Input.text = name;
            Refresh();
        }

        static void Refresh()
        {
            if (!view) return;
            string n = Input;
            view.SetEnabled("ID_NICNAME_NEW_NAMECONFIRM", n.Length > 0);
            view.SetEnabled("ID_NICNAME_ACCEPT", checkedName != null && checkedName == n);
        }

        /// <summary>NEW_NAMECONFIRM: 10605 / 10604 (11296 for a name the rules refuse).</summary>
        public static void Check(string name, Action<bool> done = null)
        {
            name = (name ?? "").Trim();
            void Answer(bool ok, string text) { checkedName = ok ? name : null; Refresh(); UiPopup.Message(text); done?.Invoke(ok); }
            if (!NameRe.IsMatch(name)) { Answer(false, MissionText.Get("11296")); return; }
            if (!ServicesNet.Online)
            {
                bool taken = string.Equals(name, P?.Name, StringComparison.Ordinal);
                Answer(!taken, MissionText.Get(taken ? "10604" : "10605"));
                return;
            }
            ServicesNet.Request("rename.check", new Dictionary<string, object> { ["name"] = name }, r => Answer(r != null && r.B("ok"), ServicesNet.Text(r)));
        }

        /// <summary>ACCEPT: the checked name becomes the driver's (10955).</summary>
        public static void Accept(Action<bool> done = null)
        {
            string name = Input;
            if (checkedName == null || checkedName != name) { done?.Invoke(false); return; }
            if (ServicesNet.Online)
            {
                ServicesNet.Request("rename.apply", new Dictionary<string, object> { ["name"] = name }, r =>
                {
                    bool ok = r != null && r.B("ok");
                    if (ok) { Close(); UiPopup.Message(MissionText.Get("10955", r.S("name", name))); GameFlow.Instance?.RefreshHud(); }
                    else UiPopup.Message(r != null && r.S("key") == null && r.S("error") == null ? MissionText.Get("10960") : ServicesNet.Text(r));
                    done?.Invoke(ok);
                });
                return;
            }
            var p = P;
            if (p == null || !HasKit || LockedDays > 0) { UiPopup.Message(MissionText.Get("10960")); done?.Invoke(false); return; }
            p.Give(Kit, -1);
            p.Name = name;
            p.NameChangedAt = Now;
            p.Save();
            Close();
            UiPopup.Message(MissionText.Get("10955", name));
            GameFlow.Instance?.RefreshHud();
            done?.Invoke(true);
        }

        /// <summary>The kit for 3,500,000 Mito, a 7-day bag item (online rename.buyKit).</summary>
        public static void BuyKit(Action<bool> done = null)
        {
            var p = P;
            if (p == null) { done?.Invoke(false); return; }
            if (ServicesNet.Online)
            {
                ServicesNet.Request("rename.buyKit", null, r =>
                {
                    bool ok = r != null && r.B("ok");
                    if (!ok) UiPopup.Message(ServicesNet.Text(r));
                    done?.Invoke(ok);
                });
                return;
            }
            if (p.Mito < KitPrice) { UiPopup.Message("not enough Mito"); done?.Invoke(false); return; }
            p.Mito -= KitPrice;
            var it = p.Items.Find(i => i.Id == Kit);
            if (it == null) p.Items.Add(it = new StackItem { Id = Kit });
            it.Count += 1;
            it.Until = Math.Max(it.Until, Now) + KitDays * 86400000L;
            p.Save();
            GameFlow.Instance?.RefreshHud();
            done?.Invoke(true);
        }
    }
}
