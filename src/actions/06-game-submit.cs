using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;

public class CPHInline
{
    // Partition wheel entries by calendar month to avoid rewriting the entire archive
    // on every submission. All actions must share the Community Checkins BLOCKING queue.
    private class WheelEntry
    {
        public string id { get; set; }
        public string userId { get; set; }
        public string user { get; set; }
        public string game { get; set; }
        public string date { get; set; }
        public string month { get; set; }
        public string utc { get; set; }
    }

    public bool Execute()
    {
        string id, name, raw;
        if (!CPH.TryGetArg("userId", out id) || String.IsNullOrWhiteSpace(id) ||
            !CPH.TryGetArg("userName", out name) || String.IsNullOrWhiteSpace(name)) return false;
        CPH.TryGetArg("rawInput", out raw);
        string game = (raw ?? "").Trim();
        if (game.Length < 2 || game.Length > 100 || game.Any(char.IsControl))
        {
            CPH.SendMessage($"@{name}, use !game Game Title (2-100 characters). Example: !game Stardew Valley");
            return true;
        }

        int total = Math.Max(0, CPH.GetTwitchUserVarById<int>(id, "ci_total", true));
        int used = Math.Max(0, CPH.GetTwitchUserVarById<int>(id, "ci_wheel_claimed", true));
        // This value must NEVER decrease, including after admin resets.
        int earned = Math.Max(Math.Max(used, CPH.GetTwitchUserVarById<int>(id, "ci_wheel_earned", true)), total / 10);
        if (earned <= used)
        {
            CPH.SendMessage($"@{name}, no wheel credits available (earned {earned}, used {used}). Use !checkins to see your progress.");
            return true;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset local = EasternTime(now);
        string month = local.ToString("yyyy-MM");
        string key = "ci_wheel_month_" + month.Replace('-', '_');
        string json = CPH.GetGlobalVar<string>(key, true);
        List<WheelEntry> entries;
        try
        {
            entries = String.IsNullOrWhiteSpace(json)
                ? new List<WheelEntry>()
                : JsonConvert.DeserializeObject<List<WheelEntry>>(json) ?? new List<WheelEntry>();
        }
        catch (Exception ex)
        {
            CPH.LogError("Wheel ledger " + key + " invalid: " + ex.Message);
            CPH.SendMessage("Game submissions unavailable: corrupted monthly wheel ledger. Ask a moderator.");
            return false;
        }

        var entry = new WheelEntry {
            id = Guid.NewGuid().ToString("N"), userId = id, user = name,
            game = game, date = local.ToString("yyyy-MM-dd"),
            month = month, utc = now.ToString("o")
        };
        entries.Add(entry);

        // Mark consumed credit BEFORE saving the entry. In a rare crash between writes,
        // an admin must manually reconcile; this favors preventing duplicate rewards.
        CPH.SetTwitchUserVarById(id, "ci_wheel_earned", earned, true);
        CPH.SetTwitchUserVarById(id, "ci_wheel_claimed", used + 1, true);
        CPH.SetGlobalVar(key, JsonConvert.SerializeObject(entries), true);
        CPH.LogInfo($"WHEEL SUBMIT id={entry.id} user={name}({id}) game={game} date={entry.date} remaining={earned-used-1}");
        CPH.SendMessage($"🎮 @{name} added '{game}' to the {local:MMMM yyyy} wheel! {entry.date}. Credits remaining: {earned-used-1}.");
        return true;
    }

    private static DateTimeOffset EasternTime(DateTimeOffset utc)
    {
        try { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")); }
    }
}
