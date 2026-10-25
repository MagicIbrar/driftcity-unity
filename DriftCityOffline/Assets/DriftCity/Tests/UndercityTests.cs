using System.Collections;
using System.IO;
using System.Linq;
using DriftCity.Game;
using DriftCity.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace DriftCity.Tests
{
    /// <summary>
    /// The Undercity in Cras (DriftCity_RE/UNDERCITY.md): the posts and tables the exe names, the entrance dialogs
    /// (UC_Enter, UC_Intro, UC_NeedMito), then a whole run with AI partners at a shortened script clock: the underground
    /// sky and weather, the briefing and Accept, a HUB rammed by the car and the rest taken, the shielded Main HUB
    /// (bounced off, then destroyed after the barrier HUBs), the HUV waves and boss, UCRESULT, the reward and the way
    /// back to the entrance. A second run is left through PLAYEXIT and fails.
    /// </summary>
    public class UndercityTests
    {
        string tempSave;

        [SetUp]
        public void SetUp()
        {
            tempSave = Path.Combine(Path.GetTempPath(), "driftcity_uc_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSave);
            PlayerProfile.SaveDirOverride = tempSave;
            PlayerProfile.Slot = 0;
            var p = new PlayerProfile { Name = "Rescuer", Level = 40, Mito = 100_000, City = "cras" };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 1 });
            p.Save();
            PlayerProfile.Current = null;
            UndercityStage.GlobalTimeScale = 0.05f;
        }

        [TearDown]
        public void TearDown()
        {
            UndercityStage.GlobalTimeScale = 1f;
            PlayerProfile.SaveDirOverride = null;
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            try { Directory.Delete(tempSave, true); } catch { }
        }

        static void Shot(string name)
        {
            Directory.CreateDirectory("Preview");
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name), tex.EncodeToPNG());
            Object.Destroy(tex);
        }

        static IEnumerator WaitFor(System.Func<bool> condition, float timeout, string what)
        {
            float t = 0;
            while (!condition() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(condition(), "timed out waiting for " + what);
        }

        static void Teleport(DriftCar car, Vector3 p, Quaternion rot)
        {
            var rb = car.GetComponent<Rigidbody>();
            rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
            car.transform.SetPositionAndRotation(p, rot);
            rb.position = p; rb.rotation = rot;
        }

        [UnityTest]
        public IEnumerator EnterRunAndResult()
        {
            new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipParkingLot = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InCity && CitySession.Current && flow.PlayerCar, 600, "city");
            var session = CitySession.Current;
            session.MissionOfferInterval = 1e6f;
            Assert.AreEqual("cras", flow.City);
            var P = PlayerProfile.Current;

            // ---------------- data ----------------
            foreach (var city in new[] { "moonpalace", "koinonia", "cras", "oros" })
            {
                var l = UndercityData.For(city);
                Assert.IsNotNull(l, city + " layout");
                Assert.AreEqual(15, l.Barriers.Count(b => b != null), city + " barrier posts");
                Assert.AreEqual(10, l.RedHubs.Count(b => b != null), city + " red HUB posts");
                Assert.AreEqual(10, l.Jumps.Count(b => b != null), city + " jump posts");
            }
            Assert.IsNull(UndercityData.For("taipei"));
            var cras = UndercityData.For("cras");
            Assert.AreEqual("post_3rd_00791", cras.Entrance.Id);
            Assert.AreEqual("Undercity_Start1", cras.Starts[0].Name);
            Assert.AreEqual("Undercity_MCC", cras.Mcc.Name);
            Assert.AreEqual("Undercity_Barrier_2p_1", cras.Barriers[0].Name);
            Assert.AreEqual(10, cras.BarriersFor(3).Count);
            Assert.AreEqual(34, UndercityData.HuvRows.Count, "init\\uchuvparam.xlt rows");
            var boss = UndercityData.Wave(3, 5);
            Assert.AreEqual(1, boss.Count);
            Assert.AreEqual(11012, boss[0].HuvId);
            var ready = UndercityData.Text("UC_Ready");
            Assert.AreEqual(19f, ready.Seconds);
            StringAssert.Contains("HUBs you see", ready.AllText);
            var acceptMsg = UndercityData.Text("UC_HUB_NeedAccept");
            Assert.IsTrue(acceptMsg.Accept);
            StringAssert.Contains("Rescuer", acceptMsg.AllText);
            Assert.AreEqual(2, UndercityData.Text("UC_MainHUB").Pages.Count, "UC_MainHUB has a <next>");

            // ---------------- entrance: UC_Enter -> UC_Intro -> back -> Accept -> UC_NeedMito ----------------
            var entrance = UndercityEntrance.Attach(session, flow.City);   // the lead's GameFlow hook, done here until it lands
            Assert.IsNotNull(entrance);
            Assert.AreEqual(1, entrance.Markers().Count(m => m.Icon == "ID_MINIMAP_ICON_UCENTRY"));
            Assert.IsTrue(GameObject.Find("Undercity Gate"), "uc_gate.nif at the entrance");
            var car = flow.PlayerCar;
            var gatePos = cras.Entrance.Position;
            Physics.Raycast(gatePos + Vector3.up * 40f, Vector3.down, out var gh, 120f);
            Teleport(car, gh.point + Vector3.up * 0.8f, Quaternion.identity);
            yield return WaitFor(() => UiManager.Instance.Find(600000), 5, "UC_Enter in QUESTDIALOG");
            var dlg = UiManager.Instance.Find(600000);
            Assert.IsTrue(dlg.Control("ID_QUESTDIALOG_UCACCEPT").gameObject.activeSelf);
            Assert.IsTrue(dlg.Control("ID_QUESTDIALOG_WHATISUC").gameObject.activeSelf);
            StringAssert.Contains("revive the Undercity", dlg.Control("ID_QUESTDIALOG_CONTENT_1ST").GetComponentInChildren<UnityEngine.UI.Text>(true).text);
            yield return new WaitForSeconds(0.2f);
            Shot("undercity_entrance.png");
            dlg.Click("ID_QUESTDIALOG_WHATISUC");
            yield return null;
            dlg = UiManager.Instance.Find(600000);
            StringAssert.Contains("below Mittron Island", dlg.Control("ID_QUESTDIALOG_CONTENT_1ST").GetComponentInChildren<UnityEngine.UI.Text>(true).text);
            dlg.Click("ID_QUESTDIALOG_NEXT");
            dlg.Click("ID_QUESTDIALOG_DONE");
            yield return null;
            dlg = UiManager.Instance.Find(600000);
            Assert.IsTrue(dlg && dlg.Control("ID_QUESTDIALOG_UCACCEPT").gameObject.activeSelf, "back on UC_Enter");
            dlg.Click("ID_QUESTDIALOG_UCACCEPT");
            yield return null;
            dlg = UiManager.Instance.Find(600000);
            StringAssert.Contains($"{UndercityData.FeeFor("cras")} Mito", dlg.Control("ID_QUESTDIALOG_CONTENT_1ST").GetComponentInChildren<UnityEngine.UI.Text>(true).text);
            dlg.Click("ID_QUESTDIALOG_NEXT");
            dlg.Click("ID_QUESTDIALOG_ACCEPT");
            yield return null;
            var stage = UndercityStage.Active;
            Assert.IsTrue(stage, "Undercity started");
            long exp0 = P.Exp + 0, mito0 = P.Mito; int level0 = P.Level;

            // ---------------- underground city, briefing ----------------
            yield return WaitFor(() => stage.Phase == UndercityStage.State.Ready, 30, "UC_Ready");
            Assert.IsTrue(GameObject.Find("Undercity Sky"), "Skydome_und_sky.nif");
            Assert.AreEqual(new Color(46 / 255f, 65 / 255f, 114 / 255f, 1f), RenderSettings.fogColor, "init\\weatherunder.ini fog");
            Assert.IsTrue(stage.Hud.AlertShowing);
            Assert.AreEqual("UC_Ready", stage.Hud.AlertKey);
            Assert.Less(MissionPosts.Flat(car.transform.position, cras.Starts[0].Position), 6f, "party start 1");
            Assert.IsTrue(car.ExternalControl, "held during the briefing");
            yield return new WaitForSeconds(0.3f);
            Shot("undercity_ready.png");
            yield return WaitFor(() => stage.Phase == UndercityStage.State.HubAccept, 30, "UC_HUB_NeedAccept");
            Assert.AreEqual("UC_HUB_NeedAccept", stage.Hud.AlertKey);
            stage.Accept();
            yield return WaitFor(() => stage.Phase == UndercityStage.State.Hubs, 30, "HUB phase");

            // ---------------- phase 1: HUBs ----------------
            Assert.AreEqual(10, stage.CountOf(UndercityStage.HubKind.RedTower));
            Assert.AreEqual(10, stage.CountOf(UndercityStage.HubKind.Jump));
            int towers = stage.CountOf(UndercityStage.HubKind.Tower);
            Assert.GreaterOrEqual(towers, 60, "roadside HUBs along the lanes");
            Assert.LessOrEqual(towers, 90);
            Assert.IsTrue(UiManager.Instance.Find(UndercityHud.Play1st), "PLAY1ST");
            Assert.IsTrue(UiManager.Instance.Find(UndercityHud.PartyUc), "PARTYUC");
            Assert.IsTrue(stage.Markers().Any(m => m.Icon == "ID_MINIMAP_ICON_UCTOWER_RED"));
            Assert.IsFalse(car.ExternalControl);
            stage.PartnersPaused = true;
            // ram the nearest tower: put the car on it and let the physics step report the touch
            var tower = stage.Hubs.Where(h => h && h.Alive && h.Kind == UndercityStage.HubKind.Tower)
                                  .OrderBy(h => (h.transform.position - car.transform.position).sqrMagnitude).First();
            var behind = tower.transform.position - tower.transform.forward * 8f + Vector3.up * 1f;
            Teleport(car, behind, tower.transform.rotation);
            yield return new WaitForSeconds(0.5f);
            Shot("undercity_hub.png");
            Teleport(car, tower.transform.position + Vector3.up * 1f, tower.transform.rotation);
            yield return WaitFor(() => !tower || !tower.Alive, 3, "tower destroyed by the car");
            Assert.AreEqual(1, stage.Score.Hubs);
            // a red HUB on the ground only falls to a car coming off its ramp
            var red = stage.Hubs.First(h => h && h.Alive && h.Kind == UndercityStage.HubKind.RedTower);
            var side = red.transform.position + Vector3.right * 9f;
            Physics.Raycast(side + Vector3.up * 30f, Vector3.down, out var sh, 80f);
            Teleport(car, sh.point + Vector3.up * 0.6f, Quaternion.LookRotation(Vector3.left));
            yield return new WaitForSeconds(3f);   // settled: the last time off the ground is long past
            var rbody = car.GetComponent<Rigidbody>();
            for (float t = 0; t < 1.2f && red && red.Alive; t += Time.deltaTime)
            {
                var v = red.transform.position - car.transform.position; v.y = 0;
                rbody.linearVelocity = v.normalized * 10f + Vector3.up * rbody.linearVelocity.y;
                yield return null;
            }
            Assert.IsTrue(red && red.Alive, "a red HUB stands against a car that didn't jump");
            Assert.Less(MissionPosts.Flat(car.transform.position, red.transform.position), 8f, "the car reached the red HUB");
            // a look down a ramp at its red HUB
            var ramp = stage.Hubs.First(h => h && h.Kind == UndercityStage.HubKind.Jump && h.Index == 100 + red.Index);
            var runDir = red.transform.position - ramp.transform.position; runDir.y = 0;
            var approach = ramp.transform.position - runDir.normalized * 30f;
            Physics.Raycast(approach + Vector3.up * 30f, Vector3.down, out var ah, 80f);
            Teleport(car, ah.point + Vector3.up * 0.6f, Quaternion.LookRotation(runDir.normalized));
            yield return new WaitForSeconds(0.6f);
            Shot("undercity_jump.png");
            // the partners take a few, the player the rest
            stage.PartnersPaused = false;
            var others = stage.Hubs.Where(h => h && h.Alive && h.Kind == UndercityStage.HubKind.Tower).Take(3).ToList();
            for (int i = 0; i < others.Count; i++) stage.Destroyed(others[i], 1 + i % stage.Party.Count);
            Assert.AreEqual(3, stage.Party.Sum(p => p.Score.Hubs));
            stage.DebugDestroy(0, UndercityStage.HubKind.Tower, UndercityStage.HubKind.RedTower);
            Assert.AreEqual(towers, stage.Score.Hubs + stage.Party.Sum(p => p.Score.Hubs));
            Assert.AreEqual(10, stage.Score.Jumps + stage.Party.Sum(p => p.Score.Jumps));

            // ---------------- phase 2: Main HUB ----------------
            yield return WaitFor(() => stage.Phase == UndercityStage.State.MainHub, 60, "Main HUB phase");
            Assert.AreEqual(1, stage.Attempt);
            Assert.AreEqual(15, stage.CountOf(UndercityStage.HubKind.Barrier), "barrier HUBs of a party of four");
            Assert.AreEqual(1, stage.CountOf(UndercityStage.HubKind.Main));
            Assert.IsTrue(UiManager.Instance.Find(UndercityHud.Play2nd), "PLAY2ND");
            var main = stage.Hubs.First(h => h && h.Kind == UndercityStage.HubKind.Main);
            Assert.IsTrue(main.Shielded);
            var toMain = main.transform.position - main.transform.forward * 25f + Vector3.up * 1.5f;
            Teleport(car, toMain, main.transform.rotation);
            yield return new WaitForSeconds(0.6f);
            Shot("undercity_mainhub.png");
            Teleport(car, main.transform.position + Vector3.up * 1f, main.transform.rotation);
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            yield return null;
            Assert.IsTrue(main && main.Alive, "the shield holds while barrier HUBs stand");
            stage.DebugDestroy(0, UndercityStage.HubKind.Barrier);
            Assert.IsFalse(main.Shielded, "shield down with the barriers");
            Teleport(car, main.transform.position + Vector3.up * 1f, main.transform.rotation);
            yield return WaitFor(() => stage.MainHubDown, 3, "Main HUB destroyed by the car");
            Assert.AreEqual(1, stage.Score.MainHub);
            Assert.IsTrue(stage.Hud.FlashShowing(true), "UCMAINHUVKILL");

            // ---------------- phase 3: HUV waves and the boss ----------------
            yield return WaitFor(() => stage.Phase == UndercityStage.State.HuvWave, 60, "first HUV wave");
            Assert.Greater(stage.Huvs.Count, 0);
            Assert.IsTrue(UiManager.Instance.Find(UndercityHud.Play3rd), "PLAY3RD");
            yield return new WaitForSeconds(0.5f);
            Shot("undercity_huvs.png");
            bool sawBoss = false;
            float limit = 120f;
            while (stage && stage.Phase != UndercityStage.State.Ending && stage.Phase != UndercityStage.State.Result && limit > 0)
            {
                if (stage.Phase == UndercityStage.State.BossWave && !sawBoss)
                {
                    sawBoss = true;
                    Assert.IsTrue(UiManager.Instance.Find(UndercityHud.Play4th), "PLAY4TH");
                    Assert.IsTrue(stage.Huvs.Any(h => h.Boss));
                    yield return new WaitForSeconds(0.3f);
                    Shot("undercity_boss.png");
                }
                if (stage.Phase == UndercityStage.State.HuvWave || stage.Phase == UndercityStage.State.BossWave) stage.DebugKillHuvs();
                limit -= Time.unscaledDeltaTime;
                yield return null;
            }
            Assert.IsTrue(sawBoss, "boss wave");
            Assert.IsTrue(stage.Cleared);

            // ---------------- result and the way back ----------------
            yield return WaitFor(() => stage.ResultView, 30, "UCRESULT");
            var result = stage.ResultView;
            Assert.IsTrue(result.Control("ID_UCRESULT_SUCCESS").gameObject.activeSelf);
            Assert.IsFalse(result.Control("ID_UCRESULT_FAIL").gameObject.activeSelf);
            Assert.AreEqual("Rescuer", result.Control("ID_UCRESULT_MYNAME1ST").GetComponentInChildren<UnityEngine.UI.Text>(true).text);
            Shot("undercity_result.png");
            var (exp, mito) = UndercityData.Reward(level0, stage.Score, true, UndercityData.FeeFor("cras"));
            yield return WaitFor(() => !UndercityStage.Active, 30, "back in the city");
            yield return null;
            Assert.Less(MissionPosts.Flat(car.transform.position, gatePos), 30f, "back at the entrance");
            Assert.AreEqual(mito0 + mito, P.Mito, "Mito of the run (fee taken out)");
            Assert.IsTrue(P.Level > level0 || P.Exp == exp0 + exp, "EXP of the run");
            Assert.IsFalse(GameObject.Find("Undercity Sky"), "underground sky gone");
            Assert.IsFalse(UiManager.Instance.Find(UndercityHud.Play1st) || UiManager.Instance.Find(UndercityHud.Play4th));
            yield return new WaitForSeconds(0.3f);
            Shot("undercity_back.png");

            // ---------------- a second run, left through PLAYEXIT ----------------
            var second = entrance.Enter();
            Assert.IsTrue(second);
            yield return WaitFor(() => second.Phase == UndercityStage.State.HubAccept, 60, "second briefing");
            second.Accept();
            yield return WaitFor(() => second.Phase == UndercityStage.State.Hubs, 30, "second HUB phase");
            Assert.IsTrue(UiManager.Instance.Find(UndercityHud.PlayExit), "PLAYEXIT");
            UiManager.Instance.Find(UndercityHud.PlayExit).Click("ID_UC_PLAYEXIT_EXIT");
            yield return null;
            var popup = UiManager.Instance.OpenViews.FirstOrDefault(v => v && v.Dialog.Id == 3250000);
            Assert.IsTrue(popup, "leave confirmation");
            popup.Click(UiPopup.Yes);
            yield return WaitFor(() => second.ResultView, 30, "UCRESULT after leaving");
            Assert.IsTrue(second.Failed);
            Assert.IsTrue(second.ResultView.Control("ID_UCRESULT_FAIL").gameObject.activeSelf);
            Shot("undercity_fail.png");
            yield return WaitFor(() => !UndercityStage.Active, 30, "back after the failed run");
        }
    }
}
