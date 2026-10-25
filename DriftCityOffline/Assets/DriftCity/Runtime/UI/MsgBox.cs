using System;

namespace DriftCity.UI
{
    /// <summary>
    /// The client's text prompt MSGBOX 3230000 (DriftCity_RE/SCREENS.md "MSGBOX"; social/notes_community.md section 4):
    /// INFO text over an edit line (TEXT on TEXTPANEL, Enter = OK) with OK / CANCEL, or DELETE / FRIEND_ADD /
    /// FRIEND_REMOVE in OK's place. The client uses it for names (add or block a friend 0x60e520, recommend a character
    /// 0x554240, delete a character / licence 0x767240 / 0x769590) and a few shops (23 references); it is not the
    /// message box. The game's message / confirm box is POPUP 3250000, which <see cref="UiPopup"/> already draws, so
    /// UiPopup stays on POPUP and only prompts that ask for text use MSGBOX.
    /// </summary>
    public static class MsgBox
    {
        public const int Dialog = 3230000;
        public enum Confirm { Ok, Delete, FriendAdd, FriendRemove }

        static readonly string[] ConfirmIds = { "ID_MSGBOX_TYPE1_OK", "ID_MSGBOX_TYPE1_DELETE", "ID_MSGBOX_TYPE1_FRIEND_ADD", "ID_MSGBOX_TYPE1_FRIEND_REMOVE" };
        static NuiView view;
        static Action<string> onOk;
        static Action onCancel;

        public static bool IsOpen => view;
        public static NuiView View => view;

        /// <summary>Asks for a line of text; <paramref name="ok"/> gets it trimmed (never called with an empty line).</summary>
        public static NuiView Prompt(string info, Action<string> ok, string prefill = "", Confirm confirm = Confirm.Ok, int maxLength = 16, Action cancel = null)
        {
            var ui = UiManager.Instance;
            if (!ui) return null;
            Close();
            view = ui.Open(Dialog);
            if (!view) return null;
            onOk = ok; onCancel = cancel;
            // the button handlers are per control id and other prompts (Friends) set them too: take them every time
            foreach (var id in ConfirmIds) ui.On(id, v => Answer(v, true));
            ui.On("ID_MSGBOX_TYPE1_CANCEL", v => Answer(v, false));
            view.SetText("ID_MSGBOX_TYPE1_INFO", info ?? "");
            for (int i = 0; i < ConfirmIds.Length; i++) view.SetVisible(ConfirmIds[i], i == (int)confirm);
            var c = view.Control("ID_MSGBOX_TYPE1_TEXT");
            if (c && c.Input)
            {
                c.Input.characterLimit = maxLength;
                c.Input.text = prefill ?? "";
                c.Input.onSubmit.RemoveAllListeners();
                c.Input.onSubmit.AddListener(_ => { if (view) Answer(view, true); });
                c.Input.Select(); c.Input.ActivateInputField();
            }
            UiPopup.BringToFront();
            return view;
        }

        public static string Text => view ? view.GetInput("ID_MSGBOX_TYPE1_TEXT") : "";

        static void Answer(NuiView v, bool ok)
        {
            if (!v || v != view) return;
            string text = v.GetInput("ID_MSGBOX_TYPE1_TEXT").Trim();
            var okCb = onOk; var cancelCb = onCancel;
            Close();
            if (!ok) { cancelCb?.Invoke(); return; }
            if (text.Length > 0) okCb?.Invoke(text);
        }

        public static void Close()
        {
            if (view) UiManager.Instance?.Close(view);
            view = null; onOk = null; onCancel = null;
        }
    }
}
