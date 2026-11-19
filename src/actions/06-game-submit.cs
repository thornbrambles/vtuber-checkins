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
            !CPH.TryGetArg("userName", out name) || String.IsNullOrWhiteSpace(name)) return false;
        CPH.TryGetArg("rawInput", out raw);
        string game = (raw ?? "").Trim();
        if (game.Length < 2 || game.Length > 100 || game.Any(char.IsControl))
        {
            CPH.SendMessage($"@{name}, use !game Game Title (2–100 characters). Example: !game Stardew Valley");
            return true;
        }
        string json = CPH.GetGlobalVar<string>("ci_wheel_entries_json", true);
        List<WheelEntry> entries;
        try { entries = String.IsNullOrWhiteSpace(json) ? new List<WheelEntry>() : JsonConvert.DeserializeObject<List<WheelEntry>>(json) ?? new List<WheelEntry>(); }
        catch (Exception ex) { CPH.LogError("Wheel entries JSON invalid: " + ex.Message); CPH.SendMessage("Game submissions are temporarily unavailable. Please contact a moderator."); return false; }

        int total = Math.Max(0, CPH.GetTwitchUserVarById<int>(id, "ci_total", true));
        int earned = total / 10;
        // Credits claimed since the most recent admin reset. Older wheel entries remain in the archive.
        int claimed = Math.Max(0, CPH.GetTwitchUserVarById<int>(id, "ci_wheel_claimed", true));
        int available = Math.Max(0, earned - claimed);
        if (available == 0)
        {
            int next = (Math.Max(total / 10, claimed) + 1) * 10;
            CPH.SendMessage($"@{name}, you have no game submissions available. Lifetime: {total} check-ins; next credit at {next}.");
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        var local = EasternTime(now);
        entries.Add(new WheelEntry {
            id = Guid.NewGuid().ToString("N"), userId = id, user = name,
            game = game, date = local.ToString("yyyy-MM-dd"),
            month = local.ToString("yyyy-MM"), utc = now.ToString("o")
        });
        // Persist both while queued in the same blocking queue as admin commands.
        CPH.SetGlobalVar("ci_wheel_entries_json", JsonConvert.SerializeObject(entries), true);
        CPH.SetTwitchUserVarById(id, "ci_wheel_claimed", claimed + 1, true);
        CPH.LogInfo($"WHEEL ADD user={name}({id}) game={game} date={local:yyyy-MM-dd} remaining={available - 1}");
        CPH.SendMessage($"🎮 @{name} added '{game}' to the {local:MMMM yyyy} wheel! Date: {local:yyyy-MM-dd}. Credits remaining: {available - 1}.");
        return true;
    }

    private static DateTimeOffset EasternTime(DateTimeOffset utc)
    {
        try { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")); }
    }
}
