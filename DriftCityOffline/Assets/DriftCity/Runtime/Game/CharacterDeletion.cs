using System;
using System.IO;
using UnityEngine;

namespace DriftCity.Game
{
    /// <summary>
    /// The character deletion timer of the select screen (SELECTCHARACTER 4140000, refresh 0x766304..0x7665ec; DriftCity_RE/
    /// AUDIO.md 9). DEL_BTN asks for the driver's name in MSGBOX 3230000 (10530 "Enter the name of character to delete.",
    /// DELETE in OK's place, 0x767c80); the server then marks the character row with state 'R' (+0x3e = 0x52) and a deletion
    /// time (+0x42, seconds). While marked, the licence shows DEL_MARK / DEL_COVER / DEL_CANCEL and TIME with the time left:
    /// 11087 "%d Day(s)" when a day or more is left, else 11088 "%d Hour(s)", 11089 "%d Minute(s)", 11097 "%d Second(s)".
    /// DEL_CANCEL asks 11098 "Cancel the deletion of this character?". Once the time has passed the screen asks 11099 "Are
    /// you sure you want to delete this ID : %s ?" (0x768010, once per row) and Yes deletes it (0x767b70).
    /// The wait was the server's: the remake waits <see cref="Delay"/> (24 hours) [remake]. Offline the mark is a small file
    /// beside the slot's save (driftcity_offline_profile*.json.delete, Unix seconds).
    /// </summary>
    public static class CharacterDeletion
    {
        /// <summary>How long a marked driver waits before the final question [remake].</summary>
        public static TimeSpan Delay = TimeSpan.FromHours(24);
        /// <summary>Tests: the clock (UTC).</summary>
        public static Func<DateTime> Now = () => DateTime.UtcNow;

        static string MarkPath(int slot) => PlayerProfile.SlotPath(slot) + ".delete";

        static long UnixNow => new DateTimeOffset(Now()).ToUnixTimeSeconds();

        /// <summary>The deletion time of a slot (Unix seconds), or null when it is not marked.</summary>
        public static long? When(int slot)
        {
            try
            {
                var p = MarkPath(slot);
                if (File.Exists(p) && long.TryParse(File.ReadAllText(p).Trim(), out var t)) return t;
            }
            catch (Exception) { }
            return null;
        }

        public static bool Marked(int slot) => When(slot).HasValue;

        /// <summary>Seconds left (0 when due; -1 when not marked).</summary>
        public static long Remaining(int slot)
        {
            var w = When(slot);
            return w.HasValue ? Math.Max(0, w.Value - UnixNow) : -1;
        }

        /// <summary>The time has passed: the final question is due.</summary>
        public static bool Due(int slot) { var w = When(slot); return w.HasValue && w.Value - UnixNow < 0; }

        /// <summary>The server's mark: state 'R' with the deletion time now + Delay.</summary>
        public static void Mark(int slot)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarkPath(slot)));
                File.WriteAllText(MarkPath(slot), (UnixNow + (long)Delay.TotalSeconds).ToString());
            }
            catch (Exception e) { Debug.LogWarning("deletion mark: " + e.Message); }
        }

        /// <summary>DEL_CANCEL + 11098 Yes.</summary>
        public static void Cancel(int slot)
        {
            try { if (File.Exists(MarkPath(slot))) File.Delete(MarkPath(slot)); } catch (Exception) { }
        }

        /// <summary>11099 Yes: the driver and its mark are removed.</summary>
        public static void Delete(int slot)
        {
            Cancel(slot);
            PlayerProfile.DeleteSlot(slot);
        }

        /// <summary>The TIME text of 0x7664dd: the largest whole unit left (days, hours, minutes, seconds).</summary>
        public static string TimeText(long seconds)
        {
            if (seconds < 0) return "";
            long minutes = seconds / 60, hours = minutes / 60, days = hours / 24;
            if (days > 0) return Format("11087", days, "{0} Day(s)");
            if (hours > 0) return Format("11088", hours, "{0} Hour(s)");
            if (minutes > 0) return Format("11089", minutes, "{0} Minute(s)");
            return Format("11097", seconds, "{0} Second(s)");
        }

        static string Format(string id, long n, string fallback)
        {
            var t = GameTables.Text(id);
            if (string.IsNullOrEmpty(t) || t == id || !t.Contains("%d")) return string.Format(fallback, n);
            return t.Replace("%d", n.ToString());
        }

        /// <summary>11099 with the driver's name.</summary>
        public static string FinalQuestion(string name)
        {
            var t = GameTables.Text("11099");
            if (string.IsNullOrEmpty(t) || t == "11099") t = "Are you sure you want to delete this ID : %s ?";
            return t.Replace("%s", name ?? "");
        }
    }
}
