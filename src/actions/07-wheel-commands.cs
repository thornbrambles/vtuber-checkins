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
        if (!CPH.GetGlobalVar<bool>("ci_wheel_legacy_migrated", true))
        {
            CPH.SendMessage("The monthly game wheel needs one-time migration/setup by a moderator before it can be used.");
            return true;
        }
        string id, name, raw;
        if (!CPH.TryGetArg("userId", out id) || String.IsNullOrWhiteSpace(id) ||
            !CPH.TryGetArg("userName", out name) || String.IsNullOrWhiteSpace(name))
            return false;
        CPH.TryGetArg("rawInput", out raw);
        string sub = (raw ?? "").Trim().ToLowerInvariant();
        string pending = CPH.GetTwitchUserVarById<string>(id, "ci_wheel_pending_json", true);
        if (!String.IsNullOrWhiteSpace(pending))
        {
            CPH.SendMessage($"@{name}, an earlier game submission is pending recovery. Run !game with no title to finish it, then check !wheel again.");
            return true;
        }
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
        string prefix = $"🎮 {month} wheel ({entries.Count} entries): ";
        var displayed = new List<string>();
        foreach (var entry in entries.Take(5))
        {
            string item = $"{entry.game} ({entry.user}, {entry.date})";
            string trial = prefix + String.Join(" | ", displayed.Concat(new[] { item }));
            if (trial.Length > 390) break;
            displayed.Add(item);
        }
        int more = entries.Count - displayed.Count;
        CPH.SendMessage(prefix + String.Join(" | ", displayed)
            + (more > 0 ? $" | +{more} more; export monthly ledger for the rest." : ""));
        return true;
    }

    private static DateTimeOffset EasternTime(DateTimeOffset utc)
    {
        try { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")); }
    }
}
