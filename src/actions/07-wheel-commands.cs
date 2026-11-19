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
        string id, name;
        if (!CPH.TryGetArg("userId", out id) || String.IsNullOrWhiteSpace(id) ||
            !CPH.TryGetArg("userName", out name) || String.IsNullOrWhiteSpace(name)) return false;
        string raw;
        CPH.TryGetArg("rawInput", out raw);
        string sub = (raw ?? "").Trim().ToLowerInvariant();
        int total = Math.Max(0, CPH.GetTwitchUserVarById<int>(id, "ci_total", true));
        int claimed = Math.Max(0, CPH.GetTwitchUserVarById<int>(id, "ci_wheel_claimed", true));
        if (sub == "" || sub == "credits")
        {
            CPH.SendMessage($"🎮 @{name}: {Math.Max(0, total / 10 - claimed)} game credits available ({total} lifetime check-ins). Submit with !game Game Title. View current wheel: !wheel list");
            return true;
        }
        if (sub != "list") { CPH.SendMessage("Use !wheel credits or !wheel list"); return true; }
        string json = CPH.GetGlobalVar<string>("ci_wheel_entries_json", true);
        List<WheelEntry> entries;
        try { entries = String.IsNullOrWhiteSpace(json) ? new List<WheelEntry>() : JsonConvert.DeserializeObject<List<WheelEntry>>(json) ?? new List<WheelEntry>(); }
        catch (Exception ex) { CPH.LogError("Wheel entries JSON invalid: " + ex.Message); CPH.SendMessage("Wheel data is currently unavailable. Contact a moderator."); return false; }
        var month = EasternTime(DateTimeOffset.UtcNow).ToString("yyyy-MM");
        var current = entries.Where(e => e.month == month).ToList();
        if (current.Count == 0) CPH.SendMessage("🎮 No games submitted to this month's wheel yet.");
        else
        {
            // Twitch chat has a message length limit. Show the first 5 and the total.
            var first = current.Take(5).Select(e => $"{e.game} ({e.user}, {e.date})");
            CPH.SendMessage($"🎮 {month} wheel ({current.Count} entries): " + string.Join(" | ", first) + (current.Count > 5 ? $" | +{current.Count - 5} more. Full list stored in ci_wheel_entries_json." : ""));
        }
        return true;
    }
    private static DateTimeOffset EasternTime(DateTimeOffset utc)
    {
        try { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")); }
    }
}
