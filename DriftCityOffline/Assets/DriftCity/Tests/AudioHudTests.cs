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
    /// Once before the PlayMode suite: the word filter off (the suite's drivers are called "Tester", which the client's
    /// operatormisrepresent list refuses); the filter's own tests turn it on. The server does the same under the test runner.
    /// </summary>
    [SetUpFixture]
    public class NameFilterDefaults
    {
        [OneTimeSetUp]
        public void FilterOff()
        {
            NameFilter.Enabled = false;
            // the Node servers the online tests start inherit this (server.js: DC_NAMEFILTER=0 turns its filter off)
            System.Environment.SetEnvironmentVariable("DC_NAMEFILTER", "0");
        }
    }

    /// <summary>
    /// DriftCity_RE/AUDIO.md: the env.xlt ambience, the voice lines and stand-ins for missing sounds, the word filter,
    /// vshopcategory tabs, the HUD panels (NOTEBOOK 4060000, ITEMSHOP 4070000, SPEEDITEM 340000), the character deletion
    /// timer and the tutorial car's parking-lot refusals / arrival spots. Writes Preview/audiohud_*.png.
    /// </summary>
    public class AudioHudTests
    {
        string tempDir;

        [SetUp]
        public void SetUp()
        {
            var root = DriftCityData.ResolveGameRoot(@"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha");
            if (root == null) Assert.Ignore("game data not found");
            if (DriftCityData.Files == null) DriftCityData.Mount(root);
            GameTables.Load();
            tempDir = Path.Combine(Path.GetTempPath(), "driftcity_audiohud_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            PlayerProfile.SaveDirOverride = tempDir;
            PlayerProfile.Slot = 0;
            PlayerProfile.Current = null;
            Directory.CreateDirectory("Preview");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            NameFilter.Enabled = false;
            CityAmbience.CityOverride = null;
            CityAmbience.ListenerOverride = null;
            CityAmbience.OffRoadOverride = null;
            CharacterDeletion.Now = () => System.DateTime.UtcNow;
            ApprenticeCourse.ReturnStep = 0;
            PlayerProfile.SaveDirOverride = null;
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
            yield return new WaitForSecondsRealtime(0.5f);
            try { Directory.Delete(tempDir, true); } catch { }
        }

        static IEnumerator WaitFor(System.Func<bool> condition, float timeout, string what)
        {
            float t = 0;
            while (!condition() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(condition(), "timed out waiting for " + what);
        }

        static void EnsureCamera()
        {
            if (Camera.main) return;
            var cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" }.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.14f, 0.18f);
        }

        static void Shot(string name)
        {
            var tex = UiManager.Instance.Capture(Camera.main);
            File.WriteAllBytes(Path.Combine("Preview", name), tex.EncodeToPNG());
            Object.Destroy(tex);
            Debug.Log("Drift City audio/HUD: wrote Preview/" + name);
        }

        // ------------------------------------------------------------------ audio

        [Test]
        public void EnvTableFollowsTheClient()
        {
            EnvTable.ResetCache();
            var all = EnvTable.All;
            Assert.AreEqual(155, all.Count, "init\\env.xlt: 155 rows");
            var first = all[0];
            Assert.AreEqual("SID_ENV_0002_00", first.Sid);
            Assert.AreEqual("SID_ENV_0002.wav", first.File);
            Assert.AreEqual(3, first.City, "cras");
            Assert.AreEqual(1, first.Group);
            Assert.AreEqual(2, first.Type);
            Assert.AreEqual(5, first.Interval);
            Assert.AreEqual(new Vector3(6406f, 879.2f, 2632.4f), first.Point, "Gamebryo (x, y, z) -> Unity (x, z, y)");
            Assert.AreEqual(0x9c42, first.SoundId, "40001 + group index (0x520920)");
            var p = all[30];
            Assert.AreEqual(1, p.Type);
            Assert.AreEqual(400f, p.RadMin); Assert.AreEqual(440f, p.RadMax);
            foreach (var s in all) Assert.IsTrue(AudioLibrary.Exists("env/" + s.File), "audio\\env\\" + s.File);
            Assert.AreEqual(32, all.Count(s => s.Type == 1), "32 point sounds (30 in Cras, one each in Koinonia and Moon Palace)");
            CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4 }, all.Select(s => s.City).Distinct().ToArray(), "Moon Palace .. Oros");
            Assert.AreEqual(3, EnvTable.CityNumber("cras"));
            // cras: 30 point sounds; the groups of types 2 / 3 / 4
            Assert.AreEqual(30, EnvTable.Points(3).Count);
            var groups = EnvTable.Groups(3);
            var g1 = groups.First(g => g.Group == 1);
            Assert.AreEqual(1, g1.Clusters.Count);
            Assert.AreEqual(6, g1.Clusters[0].Points.Count, "rows 0..5");
            var pts = g1.Clusters[0].Points;
            var centre = (pts[0] + pts[1] + pts[2]) / 3f;   // inside the fan's first triangle
            Assert.IsTrue(g1.Contains(centre), "inside the fan of points");
            Assert.IsFalse(g1.Contains(centre + new Vector2(3000, 0)), "far outside");
            var g45 = EnvTable.Groups(2).First(g => g.Group == 45);
            Assert.AreEqual(3, g45.Clusters.Count, "group 45: misc 3, 4, 5");
            // the city ambience of each city: type 4 at (0, 0, 0) with interval 5 (only played off road)
            for (int c = 1; c <= 4; c++) Assert.IsTrue(EnvTable.Groups(c).Any(g => g.Type == 4 && g.Interval == 5), "type 4 in city " + c);
            Assert.IsFalse(EnvTable.Groups(3).First(g => g.Group == 57).Contains(Vector2.zero), "a single-point cluster builds no fan");
        }

        [UnityTest]
        public IEnumerator AmbiencePlaysByDistanceAreaAndRoad()
        {
            EnsureCamera();
            AudioLibrary.SoundOn = true; AudioLibrary.SfxVolume = 1f;
            var amb = CityAmbience.Ensure();
            CityAmbience.OffRoadOverride = false;
            CityAmbience.ListenerOverride = new Vector3(0, -5000, 0);
            CityAmbience.CityOverride = "cras";
            yield return null; yield return null;
            Assert.AreEqual("cras", CityAmbience.City);
            Assert.AreEqual(30, amb.PointCount, "cras point sounds");
            Assert.Greater(amb.GroupCount, 5);
            Assert.AreEqual(0, amb.PlayingPoints + amb.PlayingGroups, "nothing near the listener");

            // a type 1 point (SID_ENV_0018_00 at Gamebryo (6578.4, 2509, 736.3), radMAX 440): starts inside the radius
            var row = EnvTable.All[30];
            CityAmbience.ListenerOverride = row.Point + new Vector3(100, 0, 0);
            yield return null; yield return null;
            Assert.GreaterOrEqual(amb.PlayingPoints, 1, "point sound within radMAX");
            CollectionAssert.Contains(amb.PlayingFiles.ToList(), "SID_ENV_0018.wav");
            // leaving: a 1 s fade, then stopped
            CityAmbience.ListenerOverride = row.Point + new Vector3(5000, 0, 0);
            yield return null;
            Assert.GreaterOrEqual(amb.PlayingPoints, 1, "still fading");
            yield return new WaitForSecondsRealtime(1.3f);
            Assert.AreEqual(0, amb.PlayingPoints, "stopped after the fade");

            // a type 3 area (group 3, SID_ENV_0006_00): the listener inside its fan
            var g3 = EnvTable.Groups(3).First(g => g.Group == 3);
            var c = g3.Clusters[0].Points;
            var inside = (c[0] + c[1] + c[2]) / 3f;
            Assert.IsTrue(g3.Contains(inside));
            CityAmbience.ListenerOverride = new Vector3(inside.x, 0, inside.y);
            yield return null; yield return null;
            CollectionAssert.Contains(amb.PlayingFiles.ToList(), "SID_ENV_0006.wav", "area loop inside its cluster");

            // type 4: the city ambience plays off the road
            CityAmbience.ListenerOverride = new Vector3(0, -5000, 0);
            yield return new WaitForSecondsRealtime(3.5f);   // the area loop fades out over 3 s
            Assert.IsFalse(amb.PlayingFiles.Contains("SID_ENV_0006.wav"), "area loop faded out");
            CityAmbience.OffRoadOverride = true;
            yield return null; yield return null;
            CollectionAssert.Contains(amb.PlayingFiles.ToList(), "SID_ENV_0001.wav", "off-road ambience (type 4)");

            // leaving the city drops every sound
            CityAmbience.CityOverride = null;
            yield return null; yield return null;
            Assert.IsNull(CityAmbience.City);
            Assert.AreEqual(0, amb.PointCount);
        }

        [Test]
        public void VoiceLinesAndStandIns()
        {
            Assert.AreEqual("sfx/SID_PVP_VO_BOOST100", AudioLibrary.SoundFile(0x4f0c), "effect.txt 236 = exe id 0x4f0c");
            Assert.AreEqual("sfx/SID_PVP_VO_1ST", AudioLibrary.SoundFile(0x4f07));
            Assert.AreEqual("sfx/SID_PVP_VO_FIRST", AudioLibrary.SoundFile(0x4f11));
            Assert.AreEqual("sfx/SID_SYSWIN_DESTROY", AudioLibrary.SoundFile(0x4e8b));
            Assert.AreEqual(VoiceLines.RankFile(1), AudioLibrary.SoundFile(0x4f07));
            Assert.AreEqual(VoiceLines.RankFile(2), AudioLibrary.SoundFile(0x4f08));
            Assert.AreEqual(VoiceLines.RankFile(3), AudioLibrary.SoundFile(0x4f09));
            Assert.IsNull(VoiceLines.RankFile(4));
            // 0x726f20's jump table: type -> sound id
            int[] gateIds = { 0x4f0a, 0x4f0b, 0x4f0c, 0x4f10, 0x4f13, 0x4f0f, 0x4f0e, 0x4f0d };
            for (int t = 0; t < gateIds.Length; t++) Assert.AreEqual(AudioLibrary.SoundFile(gateIds[t]), VoiceLines.GateEffectFile(t), "gate effect " + t);
            Assert.IsNull(VoiceLines.GateEffectFile(8));
            for (int t = 0; t < 8; t++) Assert.IsTrue(AudioLibrary.Exists(VoiceLines.GateEffectFile(t)));
            Assert.IsTrue(AudioLibrary.Exists(VoiceLines.First) && AudioLibrary.Exists(VoiceLines.Jump));
            // the four sounds the US data lacks
            foreach (var id in new[] { "SID_GUARD", "SID_PATROL_PARTY_COUNT", "SID_VALENTAIN_COMPLETE", "SID_XMAS_EVENT_SUCCESS" })
            {
                Assert.IsFalse(AudioLibrary.Exists("sfx/" + id), id + " is not shipped");
                var sub = AudioLibrary.Substitute("sfx/" + id);
                Assert.IsNotNull(sub, id + " has a stand-in");
                Assert.IsTrue(AudioLibrary.Exists(sub.File), sub.File);
            }
            Assert.AreEqual(10.5f, AudioLibrary.Substitute("sfx/SID_PATROL_PARTY_COUNT").TailSeconds, "the countdown's last 10.5 s");
            Assert.IsNull(AudioLibrary.Substitute("sfx/SID_CLICK_YES"), "shipped sounds play themselves");
            foreach (var kv in AudioLibrary.Substitutes) Assert.IsTrue(AudioLibrary.Exists(kv.Value.File), kv.Key + " -> " + kv.Value.File);
        }

        // ------------------------------------------------------------------ word filter

        [Test]
        public void NameFilterFollowsTheClient()
        {
            NameFilter.Enabled = true;
            NameFilter.ResetCache();
            Assert.AreEqual(6, NameFilter.Slang.Count, "slang.xlt");
            Assert.AreEqual(201, NameFilter.OperatorNames.Count, "operatormisrepresent.xlt");
            Assert.AreEqual("10606", NameFilter.CheckDriverName("GM_Kim"));
            Assert.AreEqual("10606", NameFilter.CheckDriverName("SuperAdmin"));
            Assert.AreEqual("10606", NameFilter.CheckDriverName("Duffy2"), "staff names");
            Assert.AreEqual("10606", NameFilter.CheckDriverName("Smile=)"), "slang");
            Assert.IsNull(NameFilter.CheckDriverName("Speedy"));
            Assert.AreEqual("10608", NameFilter.CheckRoomTitle("Race =w="));
            Assert.IsNull(NameFilter.CheckRoomTitle("GM race"), "titles: slang only (0x57a3ee)");
            StringAssert.Contains("Room name is not allowed", NameFilter.Text("10608"));
            StringAssert.Contains("ID is not allowed", NameFilter.Text("10606"));
            var c = NameFilter.Chat("hi =) there");
            Assert.AreEqual("hi =) there".Length, c.Length);
            StringAssert.DoesNotContain("=)", c);
            Assert.IsTrue("!@#$%^&*".Contains(c[3]) && "!@#$%^&*".Contains(c[4]));
            NameFilter.Enabled = false;
            Assert.IsNull(NameFilter.CheckDriverName("GM_Kim"), "off for the rest of the suite");
        }

        // ------------------------------------------------------------------ vshopcategory

        [Test]
        public void ShopCategoriesFromTheTable()
        {
            VShopCategories.ResetCache();
            Assert.AreEqual(106, VShopCategories.All.Count, "init\\vshopcategory.xlt: 106 rows");
            Assert.IsFalse(VShopCategories.TopMenuEnabled(0), "TOP_COLOR: Top Menu Enable 0 in the US table");
            for (int t = 1; t <= 4; t++) Assert.IsTrue(VShopCategories.TopMenuEnabled(t), "top " + t);
            Assert.AreEqual("Bumper", VShopCategories.Name(8020));
            Assert.AreEqual("Flame", VShopCategories.Name(4000));
            Assert.IsTrue(VShopCategories.Multiple(8020), "AERO_COMBI_BUMPER: multiple selection");
            Assert.IsTrue(VShopCategories.Stackable(9800), "BATTLEZONE_UNTOUCHABLE: stackable");
            Assert.IsFalse(VShopCategories.Stackable(8020));
            Assert.AreEqual("AERO", VShopCategories.MainOfTab("ID_VSHOP_TAB_TUNNING_AEROCOMBI"));
            Assert.AreEqual("PAINTHIGHGLOSS", VShopCategories.MainOfTab("ID_VSHOP_TAB_DECORATION_ADV_HIGHGLOSS"));
            Assert.AreEqual("Air Duct", VShopCategories.MainOfTab("ID_VSHOP_TAB_TUNNING_AIRINTAKE"));
            foreach (var id in new[] { "ID_VSHOP_TAB_COLOR_PAINT", "ID_VSHOP_TAB_COLOR_UNDERNEON", "ID_VSHOP_TAB_DECORATION_DECAL", "ID_VSHOP_TAB_DECORATION_ROOF", "ID_VSHOP_TAB_TUNNING_SPOILER", "ID_VSHOP_TAB_ETC_DRINK" })
                Assert.IsTrue(VShopCategories.HasTab(id), id);
            Assert.AreEqual(1000, VShopCategories.SubOf(10000), "vshopitem 10000 (Silver paint): CATEGORY_PAINT_SINGLE");
            Assert.IsTrue(CustomShop.TopAllowed(0), "COLOR kept on in the classic layout [remake]");
            Assert.IsTrue(CustomShop.TopAllowed(1) && CustomShop.TopAllowed(2) && CustomShop.TopAllowed(3));
            var bumper = PartsCatalog.All.FirstOrDefault(pi => VShopCategories.SubOf(pi.Id) == 8020);
            Assert.IsNotNull(bumper);
            Assert.AreEqual("Bumper", VShopCategories.ListName(new[] { bumper.Id }));
        }

        // ------------------------------------------------------------------ HUD panels

        [UnityTest]
        public IEnumerator NotebookItemShopAndSpeedItems()
        {
            EnsureCamera();
            UiManager.Ensure();
            yield return null;
            Notebook.Open();
            var nb = UiManager.Instance.Find(Notebook.Dialog);
            Assert.IsTrue(nb, "NOTEBOOK 4060000");
            foreach (var id in new[] { "ID_NOTEBOOK_COUPONBOOK", "ID_NOTEBOOK_MISSIONBOOK", "ID_NOTEBOOK_LICENSE", "ID_NOTEBOOK_CLOSE" })
                Assert.IsTrue(nb.Control(id) && nb.Control(id).gameObject.activeInHierarchy, id);
            yield return null;
            Shot("audiohud_notebook.png");
            nb.Click("ID_NOTEBOOK_CLOSE");
            yield return null;
            Assert.IsFalse(Notebook.IsOpen, "CLOSE hides the notebook");
            Notebook.Toggle(); Assert.IsTrue(Notebook.IsOpen); Notebook.Toggle(); Assert.IsFalse(Notebook.IsOpen, "the bar button toggles it");

            ItemShop.Open();
            var shop = UiManager.Instance.Find(ItemShop.Dialog);
            Assert.IsTrue(shop, "ITEMSHOP 4070000");
            Assert.IsTrue(shop.Control("ID_ITEMSHOP_VSHOP") && shop.Control("ID_ITEMSHOP_CAPSULEBOX"));
            yield return null;
            Shot("audiohud_itemshop.png");
            shop.Click("ID_ITEMSHOP_CLOSE");
            yield return null;
            Assert.IsFalse(ItemShop.IsOpen);

            // SPEEDITEM: up to six entries, newest first, gone after 3 s + a 1 s fade
            for (int i = 0; i < 7; i++) SpeedItems.Add(i % 2 == 0 ? "i_n_00067" : "i_d_m_10k");
            Assert.AreEqual(SpeedItems.Max, SpeedItems.Count, "a seventh drops the oldest");
            var si = SpeedItems.View;
            Assert.IsTrue(si, "SPEEDITEM opened");
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.IsTrue(si.Control(si.Dialog.Id == SpeedItems.Dialog ? "ID_SPEEDITEM_ITEM06" : "ID_SPEEDITEM_ADV_ITEM06").gameObject.activeSelf);
            Shot("audiohud_speeditems.png");
            yield return new WaitForSecondsRealtime(SpeedItems.Stay + SpeedItems.FadeOut + 0.3f);
            Assert.AreEqual(0, SpeedItems.Count, "entries expired");
            Assert.IsFalse(SpeedItems.View, "the panel hides when empty");
        }

        // ------------------------------------------------------------------ character deletion

        [Test]
        public void DeletionTimerRules()
        {
            Assert.AreEqual("2 Day(s)", CharacterDeletion.TimeText(2 * 86400 + 5));
            Assert.AreEqual("23 Hour(s)", CharacterDeletion.TimeText(23 * 3600 + 3599));
            Assert.AreEqual("5 Minute(s)", CharacterDeletion.TimeText(5 * 60 + 20));
            Assert.AreEqual("42 Second(s)", CharacterDeletion.TimeText(42));
            StringAssert.Contains("Speedy", CharacterDeletion.FinalQuestion("Speedy"));
            var p = new PlayerProfile { Name = "Speedy", Level = 3 };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 1 });
            p.Save();
            var t0 = new System.DateTime(2026, 10, 25, 12, 0, 0, System.DateTimeKind.Utc);
            CharacterDeletion.Now = () => t0;
            Assert.IsFalse(CharacterDeletion.Marked(0));
            CharacterDeletion.Mark(0);
            Assert.IsTrue(CharacterDeletion.Marked(0));
            Assert.AreEqual((long)CharacterDeletion.Delay.TotalSeconds, CharacterDeletion.Remaining(0));
            Assert.IsFalse(CharacterDeletion.Due(0));
            CharacterDeletion.Cancel(0);
            Assert.IsFalse(CharacterDeletion.Marked(0), "DEL_CANCEL");
            CharacterDeletion.Mark(0);
            CharacterDeletion.Now = () => t0 + CharacterDeletion.Delay + System.TimeSpan.FromSeconds(1);
            Assert.IsTrue(CharacterDeletion.Due(0), "time up: 11099");
            CharacterDeletion.Delete(0);
            Assert.IsNull(PlayerProfile.Peek(0), "deleted");
            Assert.IsFalse(CharacterDeletion.Marked(0));
        }

        [UnityTest]
        public IEnumerator DeletionTimerOnTheSelectScreen()
        {
            EnsureCamera();
            var p = new PlayerProfile { Name = "Speedy", Level = 12 };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 1 });
            p.Save();
            PlayerProfile.Current = null;
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipIntro = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return WaitFor(() => UiManager.Instance.Find(4140000), 15, "character select");
            var v = UiManager.Instance.Find(4140000);
            yield return null;
            // DEL_BTN -> MSGBOX 10530, the driver's name -> marked
            v.Click("ID_SELECT_CHARACTER1_DEL_BTN");
            yield return WaitFor(() => MsgBox.IsOpen, 5, "MSGBOX 10530");
            MsgBox.View.Control("ID_MSGBOX_TYPE1_TEXT").Input.text = "Speedy";
            Assert.IsTrue(MsgBox.View.Control("ID_MSGBOX_TYPE1_DELETE").gameObject.activeSelf, "DELETE in OK's place");
            MsgBox.View.Click("ID_MSGBOX_TYPE1_DELETE");
            yield return null;
            Assert.IsTrue(CharacterDeletion.Marked(0), "marked for deletion");
            Assert.IsTrue(v.Control("ID_SELECT_CHARACTER1_DEL_MARK").gameObject.activeSelf, "DEL_MARK");
            Assert.IsTrue(v.Control("ID_SELECT_CHARACTER1_DEL_CANCEL").gameObject.activeSelf, "DEL_CANCEL");
            Assert.IsFalse(v.Control("ID_SELECT_CHARACTER1_DEL_BTN").gameObject.activeSelf);
            var time = v.Control("ID_SELECT_CHARACTER1_TIME").GetComponentInChildren<UnityEngine.UI.Text>(true).text;
            Assert.AreEqual(CharacterDeletion.TimeText(CharacterDeletion.Remaining(0)), time);
            StringAssert.Contains("Day", time, "24 hours left: 11087 \"1 Day(s)\"");
            yield return null;
            Shot("audiohud_delete_marked.png");
            // DEL_CANCEL -> 11098 Yes
            v.Click("ID_SELECT_CHARACTER1_DEL_CANCEL");
            yield return WaitFor(() => UiPopup.IsOpen, 5, "11098");
            UiManager.Instance.Find(3250000).Click("ID_POPUP_YES");
            yield return null;
            Assert.IsFalse(CharacterDeletion.Marked(0), "deletion cancelled");
            // time up: 11099 and the driver is gone
            CharacterDeletion.Mark(0);
            CharacterDeletion.Now = () => System.DateTime.UtcNow + CharacterDeletion.Delay + System.TimeSpan.FromMinutes(1);
            yield return WaitFor(() => UiPopup.IsOpen, 5, "11099");
            Shot("audiohud_delete_final.png");
            UiManager.Instance.Find(3250000).Click("ID_POPUP_YES");
            yield return null;
            Assert.IsNull(PlayerProfile.Peek(0), "deleted after the final question");
        }

        // ------------------------------------------------------------------ the tutorial car in the parking lot

        [Test]
        public void TutorialCarRules()
        {
            var p = new PlayerProfile { Name = "Rookie", Apprentice = true };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 1 });
            Assert.IsTrue(Apprentice.OnTutorialCar(p));
            Assert.IsTrue(Apprentice.Refuses(p, ParkingLotStage.Area.Garage));
            Assert.IsTrue(Apprentice.Refuses(p, ParkingLotStage.Area.Exit));
            Assert.IsFalse(Apprentice.Refuses(p, ParkingLotStage.Area.Battle), "the Battle Zone building leads to the tutorial");
            Assert.IsFalse(Apprentice.Refuses(p, ParkingLotStage.Area.None));
            StringAssert.Contains("Tutorial", Apprentice.RefusalText);
            var spot = Apprentice.ArrivalSpot(p, 0).Value;
            Assert.AreEqual(new Vector2(0.5f, -0.8f), spot.pos);
            Assert.AreEqual(Mathf.PI * 0.165f, spot.heading, 1e-5f);
            Assert.AreEqual(new Vector2(75f, 45f), Apprentice.ArrivalSpot(p, Apprentice.DriftFailed).Value.pos);
            Assert.AreEqual(new Vector2(75f, 45f), Apprentice.ArrivalSpot(p, Apprentice.BoosterFailed).Value.pos);
            p.Apprentice = false;
            Assert.IsFalse(Apprentice.Refuses(p, ParkingLotStage.Area.Garage));
            Assert.IsNull(Apprentice.ArrivalSpot(p, 0));
        }

        [UnityTest]
        public IEnumerator TutorialCarInTheParkingLot()
        {
            EnsureCamera();
            var p = new PlayerProfile { Name = "Rookie", Level = 1, Apprentice = true };
            p.Cars.Add(new OwnedCar { VehicleIndex = 0, Color = 1 });
            p.Save();
            PlayerProfile.Current = null;
            ApprenticeHost.Ensure();
            ApprenticeHost.ResetSession();
            var flow = new GameObject("Game").AddComponent<GameFlow>();
            flow.SkipIntro = true;
            yield return FrontEndSteps.ToCharacterSelect(flow);
            yield return FrontEndSteps.EnterSelected();
            yield return WaitFor(() => flow.InParkingLot && ParkingLotStage.Current && flow.PlayerCar, 600, "parking lot");
            var car = flow.PlayerCar;
            var stage = ParkingLotStage.Current;
            var at = car.transform.position;
            Assert.Less(new Vector2(at.x - 0.5f, at.z + 0.8f).magnitude, 6f, "the tutorial car arrives at the dome centre (0x762f70)");
            var fwd = car.transform.forward;
            Assert.Greater(Vector3.Dot(new Vector3(fwd.x, 0, fwd.z).normalized, new Vector3(102f, 0f, 57f).normalized), 0.9f, "facing the Battle Zone");
            // the walkie-talkie line first
            yield return WaitFor(() => UiManager.Instance.Find(600000), 10, "Erina's line");
            UiManager.Instance.Find(600000).Click("ID_QUESTDIALOG_DONE");
            yield return null;
            // the garage refuses: 11285 and the car goes back
            var garage = stage.PointIn(ParkingLotStage.Area.Garage);
            Assert.IsTrue(garage.HasValue);
            var rb = car.GetComponent<Rigidbody>();
            var g = garage.Value;
            if (Physics.Raycast(g + Vector3.up * 3f, Vector3.down, out var hit, 20f)) g = hit.point;
            g += Vector3.up * 0.8f;
            rb.linearVelocity = Vector3.zero; car.transform.position = g; rb.position = g;
            yield return WaitFor(() => UiPopup.IsOpen, 5, "11285");
            Assert.IsFalse(OriginalMenus.IsOpen, "no garage on the tutorial car");
            Assert.AreEqual(ParkingLotStage.Area.None, stage.AreaAt(car.transform.position), "bounced out");
            yield return null;
            Shot("audiohud_tutorial_refusal.png");
            UiManager.Instance.Find(3250000).Click("ID_POPUP_OKONLY");
            yield return null;
        }
    }
}
