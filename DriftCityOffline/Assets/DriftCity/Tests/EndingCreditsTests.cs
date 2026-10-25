using System.Collections;
using System.IO;
using System.Linq;
using DriftCity.Core;
using DriftCity.Game;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace DriftCity.Tests
{
    /// <summary>
    /// The story ending (DriftCity_RE/UNLABELLED.md section 2): the credits files parse the client's way, the phases run
    /// in order and the ending hands back. Screenshots (screen captures with the IMGUI overlay) go to Preview/ending_*.png.
    /// Needs the installed client (or GameData).
    /// </summary>
    public class EndingCreditsTests
    {
        [SetUp]
        public void SetUp()
        {
            var root = DriftCityData.ResolveGameRoot(@"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha");
            Assert.IsNotNull(root, "installed client or imported data found");
            if (DriftCityData.Files == null) DriftCityData.Mount(root);
        }

        [TearDown]
        public void TearDown()
        {
            EndingCredits.TimeScale = 1f;
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Exclude).Where(g => g.transform.parent == null)) Object.Destroy(go);
        }

        [Test]
        public void CreditsFilesParse()
        {
            var fs = DriftCityData.Files;
            var main = EndingCredits.ParseMain(System.Text.Encoding.Default.GetString(fs.Read(@"game\credits\credits_main.dat")), out float fade);
            Assert.AreEqual(1f, fade, 1e-4, "fade 1.0");
            Assert.AreEqual(1, main.Count);
            Assert.AreEqual(1f, main[0].Start); Assert.AreEqual(3f, main[0].Duration); Assert.AreEqual(578f, main[0].Y); Assert.AreEqual(2, main[0].Font);
            StringAssert.Contains("DriftCity", main[0].Text);

            var roll = EndingCredits.ParseRoll(System.Text.Encoding.Default.GetString(fs.Read(@"game\credits\credits.dat")));
            Assert.Greater(roll.Count, 20);
            var first = roll[0];
            Assert.AreEqual("Action Game Team", first.Text);
            Assert.AreEqual(1, first.Font);
            Assert.AreEqual(768f, first.Y, "the first row starts one screen down");
            var photo = roll.First(l => l.Image != null);
            Assert.AreEqual("001_hkm.dds", photo.Image);
            // "Action Game Team" (45) + two spacers (90) + the photo's own 128
            Assert.AreEqual(768f + 45 + 90 + 128, photo.Y);
            Assert.IsTrue(roll.Zip(roll.Skip(1), (a, b) => b.Y >= a.Y || b.Column != a.Column).All(x => x), "rows go down the screen per column");
            Debug.Log($"Drift City ending: {roll.Count} credit rows, {roll.Count(l => l.Image != null)} photos, roll ends at y {roll.Max(l => l.Y)}");

            var team = EndingCredits.TeamLinesFor(true);
            Assert.AreEqual(5, team.Count);
            Assert.AreEqual("The race is not over yet.", team[0].Text.Trim());
            Assert.AreEqual(32f, EndingCredits.PhaseLength(team));
            var card = EndingCredits.DriverCard(new PlayerProfile { Name = "Ender", Level = 42, Mito = 1234 });
            Assert.AreEqual(43, card.Count, "header + 21 label/value pairs (10681-10701)");
            Assert.AreEqual("Ender", card[2].Text);
            Assert.AreEqual(0.5f, EndingCredits.LineAlpha(team[0], 20.5f, 1f), 1e-4);
            Assert.AreEqual(1f, EndingCredits.LineAlpha(team[0], 22f, 1f), 1e-4);
        }

        // WaitForEndOfFrame never resumes in -batchmode, so the IMGUI overlay cannot be captured there; the step logs what
        // would be on screen instead.
        static IEnumerator Capture(string name)
        {
            var e = EndingCredits.Current;
            if (e)
            {
                var lines = e.Phase == 1 ? e.TeamLines : e.Phase == 2 ? e.CardLines : e.Phase == 3 ? e.MainLines : null;
                var shown = lines != null ? lines.Where(l => EndingCredits.LineAlpha(l, e.PhaseTime, e.Fade) > 0).Select(l => l.Text)
                          : e.RollLines.Where(l => l.Y > 0 && l.Y < EndingCredits.LayoutHeight).Select(l => l.Text + (l.Image != null ? " [" + l.Image + "]" : ""));
                Debug.Log($"Drift City ending [{name}] phase {e.Phase} t {e.PhaseTime:F1}: " + string.Join(" | ", shown));
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator EndingRunsThroughItsPhases()
        {
            new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" }.GetComponent<Camera>().backgroundColor = new Color(0.2f, 0.3f, 0.4f);
            var target = GameObject.CreatePrimitive(PrimitiveType.Cube).transform;
            bool done = false;
            var e = EndingCredits.Play(false, () => done = true, target);
            Assert.IsNotNull(e);
            Assert.AreEqual(0, e.Phase);
            // team lines at 21 s of phase 1
            EndingCredits.TimeScale = 20f;
            while (e && e.Phase < 1) yield return null;
            EndingCredits.TimeScale = 1f;
            e.PhaseTime = 21f;
            yield return Capture("ending_team_lines");
            EndingCredits.TimeScale = 40f;
            while (e && e.Phase < 2) yield return null;
            EndingCredits.TimeScale = 1f;
            e.PhaseTime = 8f;
            yield return Capture("ending_driver_card");
            EndingCredits.TimeScale = 60f;
            while (e && e.Phase < 4) yield return null;
            EndingCredits.TimeScale = 1f;
            foreach (var l in e.RollLines) l.Y -= 500;   // jump into the roll
            yield return Capture("ending_roll");
            EndingCredits.TimeScale = 200f;
            float t = 0;
            while (!done && t < 120f) { t += Time.unscaledDeltaTime; yield return null; }
            Assert.IsTrue(done, "the ending hands back");
            Assert.IsNull(EndingCredits.Current);
        }
    }
}
