using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;

public class CPHInline
{
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
            !CPH.TryGetArg("userName", out name) || String.IsNullOrWhiteSpace(name))
            return false;
        CPH.TryGetArg("rawInput", out raw);
        string sub = (raw ?? "").Trim().ToLowerInvariant();
        int total = Math.Max(0, CPH.GetTwitchUserVarById<int>(id, "ci_total", true));
        int used = Math.Max(0, CPH.GetTwitchUserVarById<int>(id, "ci_wheel_claimed", true));
        int earned = Math.Max(Math.Max(used, CPH.GetTwitchUserVarById<int>(id, "ci_wheel_earned", true)), total / 10);

        if (sub == "" || sub == "credits")
        {
            CPH.SendMessage($"🎮 @{name}: {earned - used} game credits available (earned {earned}, used {used}, lifetime check-ins {total}). Submit: !game Game Title. Current wheel: !wheel list");
            return true;
        }
        if (sub != "list")
        {
            CPH.SendMessage("Use !wheel, !wheel credits or !wheel list");
            return true;
        }

        string month = EasternTime(DateTimeOffset.UtcNow).ToString("yyyy-MM");
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
            CPH.SendMessage("Wheel data is unavailable. Contact a moderator.");
            return false;
        }
        if (entries.Count == 0)
        {
            CPH.SendMessage("🎮 No games submitted to this month's wheel yet.");
            return true;
        }
        var first = entries.Take(5).Select(e => $"{e.game} ({e.user}, {e.date})");
        CPH.SendMessage($"🎮 {month} wheel ({entries.Count} entries): " + string.Join(" | ", first)
                        + (entries.Count > 5 ? $" | +{entries.Count - 5} more; export the monthly ledger for all entries." : ""));
        return true;
    }

    private static DateTimeOffset EasternTime(DateTimeOffset utc)
    {
        try { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")); }
    }
}
