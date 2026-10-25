using System;
using System.Collections.Generic;
using DriftCity.Net;
using DriftCity.UI;

namespace DriftCity.Game
{
    /// <summary>
    /// Every change to money or experience goes through here. Offline it edits the local profile;
    /// online it asks the server, which computes rewards and checks purchases, then mirrors the
    /// server's copy of the driver.
    /// </summary>
    public static class Progress
    {
        static PlayerProfile P => PlayerProfile.Current;

        public struct Result { public bool Ok; public string Error; public long Exp, Mito; public int LevelUps; public string Drop; }

        /// <summary>Race points per finishing place (the licences' ladder-point conditions use them).</summary>
        public static int RacePoints(int place) => place == 1 ? 100 : place == 2 ? 60 : place == 3 ? 40 : 20;

        /// <summary>kind: chase | race | quest. Offline amounts come from <paramref name="offline"/>.</summary>
        public static void Reward(string kind, Dictionary<string, object> data, Func<Result> offline, Action<Result> done)
        {
            if (!OnlineSession.Active)
            {
                var r = offline();
                if (r.Ok) r = Extras(kind, data, r);
                P.Save();
                GameFlow.Instance?.RefreshHud();
                LogReward(r);
                done?.Invoke(r);
                return;
            }
            OnlineSession.Instance.Net.Request("reward", new Dictionary<string, object> { ["kind"] = kind, ["data"] = data ?? new Dictionary<string, object>() }, m =>
            {
                OnlineSession.ApplyDriver(m.O("driver"));
                var r = new Result { Ok = m.B("ok"), Error = m.S("error"), Exp = (long)m.N("exp"), Mito = (long)m.N("mito"), LevelUps = m.I("levelUps"), Drop = m.S("drop") };
                LogReward(r);
                done?.Invoke(r);
            });
        }

        /// <summary>The chat window's system lines for a reward, worded like the client's ("Exp. 100 Mito. 300").</summary>
        static void LogReward(Result r)
        {
            if (!r.Ok) return;
            if (r.Exp > 0 || r.Mito > 0) ChatBox.System($"Exp. {r.Exp:N0}  Mito. {r.Mito:N0}");
            if (!string.IsNullOrEmpty(r.Drop)) { ChatBox.System($"Obtained {Crafting.Name(r.Drop)}."); GameEffects.GetItem(); }
            if (r.LevelUps > 0) { ChatBox.System("Congratulations! You have leveled up!"); GameEffects.LevelUp(); }
        }

        /// <summary>Offline twin of the server's reward extras: licence bonuses, race counters, chase material drops.</summary>
        static Result Extras(string kind, Dictionary<string, object> data, Result r)
        {
            int before = P.Level;
            int expPct = 0, mitoPct = 0;
            if (kind == "chase") { expPct = mitoPct = Licences.Bonus(P, "ME_ChasingExpMito", "ME_AllExpMito"); }
            if (kind == "quest") { expPct = mitoPct = Licences.Bonus(P, "ME_AllExpMito"); }
            if (kind == "race")
            {
                string city = data != null && data.TryGetValue("city", out var c) ? c as string : null;
                int place = data != null && data.TryGetValue("place", out var pl) ? System.Convert.ToInt32(pl) : 0;
                P.RacePoints += RacePoints(place);
                if (city == "cras") P.RacesCras++; else if (city == "moonpalace") P.RacesMoon++; else if (city == "koinonia") P.RacesKoi++; else if (city == "oros") P.RacesOros++;
                expPct = Licences.Bonus(P, Licences.TrackEffect(city), "ME_AllExpMito");
                mitoPct = Licences.Bonus(P, "ME_BattlePersonalMito", "ME_AllExpMito");
            }
            // EXP / Mito drinks (ItemUse buffs; not on quests)
            expPct += ItemUse.ExpBonus(P, kind); mitoPct += ItemUse.MitoBonus(P, kind);
            // events: finished Christmas trees (chase and race), scratch-card Battle EXP goals (race EXP)
            if (kind == "chase" || kind == "race") { expPct += SeasonEvents.XmasBonusPct; mitoPct += SeasonEvents.XmasBonusPct; }
            if (kind == "race") expPct += Lotto.BattleExpBonusPct;
            // the calendar: golden time, Santa, the 3rd anniversary (Multipliers.cs; server src/multipliers.js)
            var cal = Multipliers.For(kind);
            expPct += cal.Exp; mitoPct += cal.Mito;
            long exp = r.Exp * expPct / 100, mito = r.Mito * mitoPct / 100;
            if (exp > 0) { P.AddExp(exp); r.Exp += exp; }
            if (mito > 0) { P.Mito += mito; r.Mito += mito; }
            // every 10th speed combo rolls a material (the official server's combo table isn't in the client data;
            // the chase material pool stands in, as on our server)
            if (kind == "combo")
            {
                r.Drop = UnityEngine.Random.value < 0.35f ? "i_n_00003" : Crafting.ChaseDrops[UnityEngine.Random.Range(0, Crafting.ChaseDrops.Length)];
                P.Give(r.Drop);
            }
            if (kind == "chase" && UnityEngine.Random.value < 0.6f)
            {
                r.Drop = UnityEngine.Random.value < 0.35f ? "i_n_00003" : Crafting.ChaseDrops[UnityEngine.Random.Range(0, Crafting.ChaseDrops.Length)];
                P.Give(r.Drop);
            }
            if (!string.IsNullOrEmpty(r.Drop) && ItemUse.DoubleDrops(P)) P.Give(r.Drop);   // the item drink doubles drops
            r.LevelUps += P.Level - before;
            return r;
        }

        /// <summary>A workshop/licence action: the server runs it (and rolls any dice) online; offline the local rules do.</summary>
        public static void Op(string type, Dictionary<string, object> body, Func<Result> offline, Action<Result> done)
        {
            if (!OnlineSession.Active)
            {
                var r = offline();
                if (r.Ok) { P.Save(); GameFlow.Instance?.RefreshHud(); }
                done?.Invoke(r);
                // after the caller has rebuilt its window, so the message stays on top
                if (!r.Ok && !string.IsNullOrEmpty(r.Error)) UiPopup.Message(r.Error);
                return;
            }
            OnlineSession.Instance.Net.Request(type, body, m =>
            {
                OnlineSession.ApplyDriver(m.O("driver"));
                var r = new Result { Ok = m.B("ok"), Error = m.S("error"), Exp = (long)m.N("exp"), Mito = (long)m.N("mito"), Drop = m.S("drop") };
                if (!r.Ok && !m.B("failed")) UiPopup.Message(r.Error ?? "The server refused that.");
                done?.Invoke(r);
            });
        }

        public static void BuyCar(VehicleInfo car, int price, int color, Action<Result> done)
        {
            if (!OnlineSession.Active)
            {
                if (P.Mito < price) { done(new Result { Error = "not enough Mito" }); return; }
                if (P.Cars.Exists(c => c.VehicleIndex == car.Index)) { done(new Result { Error = "already owned" }); return; }
                if (P.Cars.Count >= 40) { done(new Result { Error = "garage full" }); return; }
                P.Mito -= price;
                P.Cars.Add(new OwnedCar { VehicleIndex = car.Index, Color = color, Spent = price });
                P.CurrentCar = P.Cars.Count - 1;
                P.Save();
                done(new Result { Ok = true });
                return;
            }
            Server("shop.buyCar", new Dictionary<string, object> { ["vehicleIndex"] = car.Index, ["price"] = price, ["reqLevel"] = car.ReqLevel, ["color"] = color }, done);
        }

        public static void Upgrade(int slot, int grade, int cost, int reqLevel, Action<Result> done)
        {
            if (!OnlineSession.Active)
            {
                var c = P.Cars[slot];
                if (P.Mito < cost) { done(new Result { Error = "not enough Mito" }); return; }
                P.Mito -= cost; c.Grade = grade; c.Spent += cost; P.Save();
                done(new Result { Ok = true });
                return;
            }
            Server("shop.upgrade", new Dictionary<string, object> { ["slot"] = slot, ["grade"] = grade, ["cost"] = cost, ["reqLevel"] = reqLevel }, done);
        }

        public static void Paint(int slot, int itemId, int price, Action<Result> done)
        {
            if (!OnlineSession.Active)
            {
                if (P.Mito < price) { done(new Result { Error = "not enough Mito" }); return; }
                P.Mito -= price; P.Cars[slot].PaintItem = itemId; P.AddItem(itemId); P.Save();
                done(new Result { Ok = true });
                return;
            }
            Server("shop.paint", new Dictionary<string, object> { ["slot"] = slot, ["itemId"] = itemId, ["price"] = price }, done);
        }

        /// <summary>Buys a custom-shop part for a car and fits it.</summary>
        public static void BuyPart(int slot, PartItem part, Action<Result> done)
        {
            if (!OnlineSession.Active)
            {
                var c = P.Cars[slot];
                if (c.Parts.Contains(part.Id)) { done(new Result { Error = "already owned" }); return; }
                if (P.Mito < part.Price) { done(new Result { Error = "not enough Mito" }); return; }
                P.Mito -= part.Price; c.Parts.Add(part.Id); c.Spent += part.Price; c.Fit(part.Slot, part.Id); P.Save();
                done(new Result { Ok = true });
                return;
            }
            Server("shop.buyPart", new Dictionary<string, object> { ["slot"] = slot, ["item"] = part.Id, ["part"] = part.Slot.ToString().ToLowerInvariant(), ["price"] = part.Price }, done);
        }

        /// <summary>Fits an owned part, or the stock part with item -1.</summary>
        public static void FitPart(int slot, PartSlot partSlot, int item, Action<Result> done)
        {
            if (!OnlineSession.Active)
            {
                var c = P.Cars[slot];
                if (item >= 0 && !c.Parts.Contains(item)) { done(new Result { Error = "you don't own that part" }); return; }
                c.Fit(partSlot, item); P.Save();
                done(new Result { Ok = true });
                return;
            }
            Server("shop.fitPart", new Dictionary<string, object> { ["slot"] = slot, ["part"] = partSlot.ToString().ToLowerInvariant(), ["item"] = item }, done);
        }

        /// <summary>Sells a car (not the last one) to the reseller.</summary>
        public static void SellCar(int slot, int price, Action<Result> done)
        {
            if (!OnlineSession.Active)
            {
                if (P.Cars.Count <= 1 || slot < 0 || slot >= P.Cars.Count) { done(new Result { Error = "you can't sell your last car" }); return; }
                P.Cars.RemoveAt(slot);
                if (P.CurrentCar == slot) P.CurrentCar = 0; else if (P.CurrentCar > slot) P.CurrentCar--;
                P.Mito += price; P.Save();
                done(new Result { Ok = true, Mito = price });
                return;
            }
            Server("shop.sellCar", new Dictionary<string, object> { ["slot"] = slot, ["price"] = price }, done);
        }

        static void Server(string type, Dictionary<string, object> body, Action<Result> done)
        {
            OnlineSession.Instance.Net.Request(type, body, m =>
            {
                OnlineSession.ApplyDriver(m.O("driver"));
                var r = new Result { Ok = m.B("ok"), Error = m.S("error") };
                if (!r.Ok) UiPopup.Message(r.Error ?? "The server refused that.");
                done?.Invoke(r);
            });
        }
    }
}
