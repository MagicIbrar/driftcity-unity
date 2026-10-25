using System;
using System.Collections;
using DriftCity.Game;
using DriftCity.UI;
using NUnit.Framework;
using UnityEngine;

namespace DriftCity.Tests
{
    /// <summary>Walks the original front end (LOGIN -> server select -> character select -> new character) for the play tests.</summary>
    public static class FrontEndSteps
    {
        static IEnumerator Wait(Func<bool> condition, float seconds, string what)
        {
            float t = 0;
            while (!condition() && t < seconds) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(condition(), "timed out waiting for " + what);
        }

        static UiManager Ui => UiManager.Instance;

        /// <summary>Login screen -> server list -> the server at <paramref name="url"/> (null: offline) -> character select.</summary>
        public static IEnumerator ToCharacterSelect(GameFlow flow, string url = null, string account = "", string pass = "", Action<string> shot = null)
        {
            yield return Wait(() => Ui && Ui.Find(10000), 60, "login screen");
            var login = Ui.Find(10000);
            login.Control("ID_LOGIN_ID").Input.text = account;
            login.Control("ID_LOGIN_PASS").Input.text = pass;
            yield return null;
            shot?.Invoke("front_1_login.png");
            login.Click("ID_LOGIN_ENTER");
            yield return Wait(() => Ui.Find(4130000) && Ui.Find(4120000), 10, "server select");
            yield return null;
            shot?.Invoke("front_2_servers.png");
            flow.ChooseServer(url, url == null ? null : "Test server");
            if (url == null) yield return Wait(() => Ui.Find(4140000), 10, "character select");
        }

        /// <summary>Character select with no licence yet: the big Create Character button -> licence registration (50000 + 140000) -> Select Car.</summary>
        public static IEnumerator NewCharacter(string name, int avatarClicks = 2, Action<string> shot = null)
        {
            // these tests drive a new driver straight into the city; ApprenticeTests covers the client's parking-lot tutorial start
            Game.ApprenticeCourse.SendNewDriversToLot = false;
            yield return Wait(() => Ui.Find(4140000), 15, "character select");
            yield return null;
            shot?.Invoke("front_3_select_empty.png");
            Ui.Find(4140000).Click("ID_CREATE_CHARACTER_ENTER");
            yield return Wait(() => Ui.Find(140000) && Ui.Find(50000), 120, "licence registration");
            var card = Ui.Find(140000);
            for (int i = 0; i < avatarClicks; i++) card.Click("ID_CREATECHARACTER_NEW_RIGHT");
            card.Control("ID_CREATECHARACTER_NEW_NAMEINPUT").Input.text = name;
            yield return null;
            shot?.Invoke("front_4_create.png");
            Ui.Find(50000).Click("ID_CHARCREATESELECT_BG_NEXT");
            yield return Wait(() => Ui.Find(150000), 30, "car selection");
        }

        /// <summary>Character select -> Enter on the selected licence.</summary>
        public static IEnumerator EnterSelected(Action<string> shot = null)
        {
            yield return Wait(() => Ui.Find(4140000), 15, "character select");
            yield return null;
            shot?.Invoke("front_5_select.png");
            Ui.Find(4140000).Click("ID_SELECT_CHARACTER_ENTER");
        }
    }
}
