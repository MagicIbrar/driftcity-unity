using System.Linq;
using DriftCity.Game;
using NUnit.Framework;
using UnityEngine;

namespace DriftCity.Tests
{
    /// <summary>
    /// Tables the remake now reads (DriftCity_RE/TABLES.md 3): the Battle Zone AI drivers of init\aicarparam.xlt.
    /// Needs the installed client (or GameData).
    /// </summary>
    public class TablesTests
    {
        static void Mount()
        {
            var root = DriftCityData.ResolveGameRoot(@"D:\Drift_City_Remastered_Alpha_08-2025\Drift City Remastered Alpha");
            Assert.IsNotNull(root, "installed client or imported data found");
            if (DriftCityData.Files == null) DriftCityData.Mount(root);
            GameTables.Load();
        }

        [Test]
        public void AiDriversFromAiCarParam()
        {
            Mount();
            var all = GameTables.AiDrivers;
            int usable = all.Count(a => GameTables.AiCars(a).Count > 0);
            Debug.Log($"Drift City tables: aicarparam {all.Count} AI drivers, {usable} with a player car here; first {all[0].Name} lv {all[0].Level} cars [{string.Join(" ", all[0].CarUniqueIds)}]");
            Assert.Greater(all.Count, 200, "220 rows");
            Assert.Greater(usable, 150);
            Assert.IsTrue(all.All(a => a.Name.StartsWith("[AI]")), "the client's AI names");

            foreach (int level in new[] { 1, 12, 40, 80 })
            {
                var room = new PvpRoom { Title = "AI test", Max = 8 };
                for (int i = 0; i < 6; i++)
                {
                    var m = Pvp.NewAi(room, level);
                    room.Members.Add(m);
                    // a few names appear on two rows (two levels): one of them gave this driver
                    var rows = all.Where(a => a.Name == m.Name).ToList();
                    Assert.IsTrue(rows.Any(row => row.Level == m.Level && row.CarUniqueIds.Contains(m.Car.UniqueId)), $"{m.Name} lv {m.Level} drives one of its row's cars ({m.Car.UiName})");
                    Assert.LessOrEqual(Mathf.Abs(m.Level - level), 12, $"{m.Name} lv {m.Level} near {level}");
                }
                Assert.AreEqual(room.Members.Count, room.Members.Select(m => m.Name).Distinct().Count(), "no name twice in a room");
                Debug.Log($"Drift City tables: level {level} AI " + string.Join(", ", room.Members.Select(m => $"{m.Name} lv{m.Level} {m.Car.UiName}")));
            }
        }
    }
}
