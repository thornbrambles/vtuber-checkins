using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;

public class CPHInline
{
    // This action MUST run in the same BLOCKING queue as check-ins, admin edits,
    // wheel commands and one-time migration.
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
    private class PendingSubmission
    {
        public WheelEntry entry { get; set; }
        public int spentBefore { get; set; }
        public int earnedAtSubmission { get; set; }
    }

    public bool Execute()
    {
        if (!CPH.GetGlobalVar<bool>("ci_wheel_legacy_migrated", true))
        {
            CPH.SendMessage("The game wheel needs one-time setup. A moderator must run the wheel migration action before submissions open.");
            return true;
        }

        string id, name, raw;
        if (!CPH.TryGetArg("userId", out id) || String.IsNullOrWhiteSpace(id) ||
            !CPH.TryGetArg("userName", out name) || String.IsNullOrWhiteSpace(name))
            return false;
        CPH.TryGetArg("rawInput", out raw);
        string game = (raw ?? "").Trim();

        // Finish any interrupted submission BEFORE doing a new one, including
        // when the user's new command is invalid.
        if (!RecoverPending(id)) return false;

        if (game.Length < 2 || game.Length > 100 || game.Any(char.IsControl))
        {
            CPH.SendMessage($"@{name}, use !game Game Title (2-100 characters). Example: !game Stardew Valley");
            return true;
        }

        int total = Math.Max(0, CPH.GetTwitchUserVarById<int>(id, "ci_total", true));
        int spent = Math.Max(0, CPH.GetTwitchUserVarById<int>(id, "ci_wheel_claimed", true));
        int earned = Math.Max(Math.Max(spent, CPH.GetTwitchUserVarById<int>(id, "ci_wheel_earned", true)), total / 10);

        if (earned <= spent)
        {
            CPH.SendMessage($"@{name}, no wheel credits available (earned {earned}, used {spent}). Use !checkins for your stats.");
            return true;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset local = EasternTime(now);
        string msgId;
        CPH.TryGetArg("msgId", out msgId);
        string entryId = String.IsNullOrWhiteSpace(msgId) ? Guid.NewGuid().ToString("N") : "chat-" + msgId;
        // Twitch may redeliver a chat event after a restart. Do not spend twice
        // for the same message ID even after its pending transaction was cleared.
        if (!String.IsNullOrWhiteSpace(msgId))
        {
            string existingKey = "ci_wheel_month_" + local.ToString("yyyy_MM");
            try
            {
                string existingJson = CPH.GetGlobalVar<string>(existingKey, true);
                var existing = String.IsNullOrWhiteSpace(existingJson)
                    ? new List<WheelEntry>()
                    : JsonConvert.DeserializeObject<List<WheelEntry>>(existingJson) ?? new List<WheelEntry>();
                if (existing.Any(x => x != null && x.id == entryId))
                {
                    CPH.LogInfo("Ignoring duplicate !game event " + msgId);
                    return true;
                }
            }
            catch (Exception ex)
            {
                CPH.LogError("Cannot check wheel submission duplicates: " + ex.Message);
                return false;
            }
        }
        var entry = new WheelEntry {
            id = entryId, userId = id, user = name,
            game = game, date = local.ToString("yyyy-MM-dd"),
            month = local.ToString("yyyy-MM"), utc = now.ToString("o")
        };

        var pending = new PendingSubmission { entry = entry, spentBefore = spent, earnedAtSubmission = earned };
        // One durable recovery record per user makes multi-variable writes recoverable.
        CPH.SetTwitchUserVarById(id, "ci_wheel_pending_json", JsonConvert.SerializeObject(pending), true);
        if (!RecoverPending(id)) return false;
        CPH.LogInfo($"WHEEL SUBMIT id={entry.id} user={name}({id}) game={game} date={entry.date} remaining={earned-spent-1}");
        CPH.SendMessage($"🎮 @{name} added '{game}' to the {local:MMMM yyyy} wheel! {entry.date}. Credits remaining: {earned-spent-1}.");
        return true;
    }

    private bool RecoverPending(string userId)
    {
        string json = CPH.GetTwitchUserVarById<string>(userId, "ci_wheel_pending_json", true);
        if (String.IsNullOrWhiteSpace(json)) return true;

        PendingSubmission pending;
        try { pending = JsonConvert.DeserializeObject<PendingSubmission>(json); }
        catch (Exception ex) { CPH.LogError("Cannot recover pending wheel submission for " + userId + ": " + ex.Message); return false; }

        if (pending == null || pending.entry == null || pending.entry.userId != userId ||
            String.IsNullOrWhiteSpace(pending.entry.id) ||
            !System.Text.RegularExpressions.Regex.IsMatch(pending.entry.month ?? "", @"^\d{4}-(0[1-9]|1[0-2])$") ||
            pending.spentBefore < 0 || pending.earnedAtSubmission <= pending.spentBefore)
        {
            CPH.LogError("Invalid pending wheel submission for " + userId + ". Manual repair needed; entry NOT consumed.");
            return false;
        }

        string key = "ci_wheel_month_" + pending.entry.month.Replace('-', '_');
        List<WheelEntry> entries;
        try
        {
            string monthlyJson = CPH.GetGlobalVar<string>(key, true);
            entries = String.IsNullOrWhiteSpace(monthlyJson) ? new List<WheelEntry>()
                : JsonConvert.DeserializeObject<List<WheelEntry>>(monthlyJson) ?? new List<WheelEntry>();
        }
        catch (Exception ex) { CPH.LogError("Cannot read monthly wheel ledger " + key + ": " + ex.Message); return false; }

        // Idempotent replay: do not add an already-saved transaction a second time.
        if (!entries.Any(x => x != null && x.id == pending.entry.id))
        {
            entries.Add(pending.entry);
            CPH.SetGlobalVar(key, JsonConvert.SerializeObject(entries), true);
        }

        int spent = Math.Max(0, CPH.GetTwitchUserVarById<int>(userId, "ci_wheel_claimed", true));
        int earned = Math.Max(0, CPH.GetTwitchUserVarById<int>(userId, "ci_wheel_earned", true));
        CPH.SetTwitchUserVarById(userId, "ci_wheel_earned", Math.Max(earned, pending.earnedAtSubmission), true);
        CPH.SetTwitchUserVarById(userId, "ci_wheel_claimed", Math.Max(spent, pending.spentBefore + 1), true);
        CPH.UnsetTwitchUserVarById(userId, "ci_wheel_pending_json", true);
        return true;
    }

    private static DateTimeOffset EasternTime(DateTimeOffset utc)
    {
        try { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")); }
    }
}
