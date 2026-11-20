using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class CPHInline
{
    // Run on the same BLOCKING queue as wheel commands and moderation.
    // Eastern Time determines the month; a pending transaction retains its
    // original month even if recovered after midnight or DST rollover.
    private const int MonthlyLimit = 3;

    private class WheelEntry
    {
        public string id { get; set; }
        public string userId { get; set; }
        public string user { get; set; }
        public string game { get; set; }
        public string date { get; set; }
        public string month { get; set; }
        public string utc { get; set; }
        public string status { get; set; }     // pending | approved | rejected; missing = legacy approved
        public string slotId { get; set; }     // Stable submission slot, shared by replacements
        public string replacedBy { get; set; } // Set on rejected entry when replacement is saved
        public bool removed { get; set; }
    }
    private class PendingSubmission
    {
        public WheelEntry entry { get; set; }
        public int spentBefore { get; set; }
        public int earnedAtSubmission { get; set; }
        public string replacesId { get; set; }
    }

    public bool Execute()
    {
        if (!String.IsNullOrWhiteSpace(CPH.GetGlobalVar<string>("ci_checkin_pending_json", true)) ||
            !String.IsNullOrWhiteSpace(CPH.GetGlobalVar<string>("ci_stream_start_pending_json", true)))
        {
            CPH.LogError("Recover the pending check-in or stream transition before changing accounting state.");
            return false;
        }
        if (!CPH.GetGlobalVar<bool>("ci_wheel_legacy_migrated", true))
        {
            CPH.SendMessage("The game wheel isn't set up yet. Please ask a mod.");
            return true;
        }
        string id, name, raw;
        if (!CPH.TryGetArg("userId", out id) || String.IsNullOrWhiteSpace(id) ||
            !CPH.TryGetArg("userName", out name) || String.IsNullOrWhiteSpace(name))
            return false;
        CPH.TryGetArg("rawInput", out raw);
        string game = (raw ?? "").Trim();
        // Recovery is a separate operation. Do not process a fresh submission
        // in the same call as a recovered transaction.
        string pendingJson = CPH.GetTwitchUserVarById<string>(id, "ci_wheel_pending_json", true);
        if (!String.IsNullOrWhiteSpace(pendingJson))
        {
            bool recovered = RecoverPending(id);
            if (recovered) CPH.SendMessage($"@{name}, your previous game submission was recovered. Send !addgame again for a new game.");
            return recovered;
        }
        if (game.Length < 2 || game.Length > 100 || game.Any(char.IsControl))
        {
            CPH.SendMessage($"@{name}, use !addgame Game Title. Example: !addgame Stardew Valley");
            return true;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset eastern = EasternTime(now);
        string month = eastern.ToString("yyyy-MM");
        string ledgerKey = "ci_wheel_month_" + eastern.ToString("yyyy_MM");
        string msgId;
        CPH.TryGetArg("msgId", out msgId);
        if (String.IsNullOrWhiteSpace(msgId))
        {
            CPH.LogError("!addgame missing msgId. Refusing unsafe submission without a stable Twitch message identity.");
            CPH.SendMessage($"@{name}, couldn't save your game. Please ask a mod to check the command setup.");
            return false;
        }
        string entryId = "chat-" + msgId;

        // Duplicate Twitch delivery MUST be checked before the credit or
        // monthly allowance. Scan every partition, not just today's month:
        // replay after midnight must never consume an additional credit.
        try
        {
            foreach (var variable in CPH.GetGlobalVarValues(true))
            {
                if (variable == null || !Regex.IsMatch(variable.VariableName ?? "", @"^ci_wheel_month_\d{4}_\d{2}$")) continue;
                var past = Load(variable.VariableName);
                if (past.Any(x => x.id == entryId))
                {
                    CPH.LogInfo("Ignoring duplicate !addgame event " + entryId);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            CPH.LogError("Unable to scan submission IDs: " + ex.Message);
            return false; // Fail closed rather than risk double-spending.
        }

        List<WheelEntry> entries;
        try
        {
            // Finish earlier submissions for this month before accepting another title.
            foreach (var journal in CPH.GetTwitchUsersVar<string>("ci_wheel_pending_json", true))
            {
                if (String.IsNullOrWhiteSpace(journal.Value)) continue;
                var pendingMonth = (string)JObject.Parse(journal.Value).SelectToken("entry.month");
                if (!Regex.IsMatch(pendingMonth ?? "", @"^\d{4}-(0[1-9]|1[0-2])$") ||
                    (pendingMonth == month && !RecoverPending(journal.UserId)))
                    throw new InvalidOperationException("Resolve unfinished submissions before accepting new games.");
            }
            entries = Load(ledgerKey);
        }
        catch (Exception ex)
        {
            CPH.LogError("Cannot load monthly wheel: " + ex.Message);
            CPH.SendMessage("Wheel entries are unavailable. Please ask a mod.");
            return false;
        }

        // A rejected entry can be replaced within the SAME month without
        // spending another credit or consuming a fourth monthly slot.
        WheelEntry rejected = entries.FirstOrDefault(x =>
            x.userId == id && x.status == "rejected" && String.IsNullOrWhiteSpace(x.replacedBy) && !x.removed);
        string normalized = Normalize(game);
        if (entries.Any(x =>
            !x.removed && x.id != (rejected == null ? "" : rejected.id) &&
            (String.IsNullOrWhiteSpace(x.status) || x.status == "approved" || x.status == "pending") &&
            Normalize(x.game) == normalized))
        {
            CPH.SendMessage($"@{name}, '{game}' is already on this month's list. Try a different game.");
            return true;
        }

        int usedSlots = entries.Where(x => x.userId == id)
            .Select(x => String.IsNullOrWhiteSpace(x.slotId) ? x.id : x.slotId)
            .Distinct(StringComparer.Ordinal).Count();
        if (rejected == null && usedSlots >= MonthlyLimit)
        {
            CPH.SendMessage($"@{name}, you've used all {MonthlyLimit} picks for {eastern:MMMM}. Saved credits carry over to next month.");
            return true;
        }
        int total = Math.Max(0, CPH.GetTwitchUserVarById<int>(id, "ci_total", true));
        int spent = Math.Max(0, CPH.GetTwitchUserVarById<int>(id, "ci_wheel_claimed", true));
        int earned = Math.Max(Math.Max(spent, CPH.GetTwitchUserVarById<int>(id, "ci_wheel_earned", true)), total / 10);
        if (rejected == null && earned <= spent)
        {
            CPH.SendMessage($"@{name}, no game credits left. Check !checkins.");
            return true;
        }

        var entry = new WheelEntry {
            id = entryId, slotId = rejected == null ? entryId :
                (String.IsNullOrWhiteSpace(rejected.slotId) ? rejected.id : rejected.slotId),
            userId = id, user = name, game = game,
            date = eastern.ToString("yyyy-MM-dd"), month = month,
            utc = now.ToString("o"), status = "pending"
        };
        var pending = new PendingSubmission {
            entry = entry, spentBefore = spent, earnedAtSubmission = earned,
            replacesId = rejected == null ? null : rejected.id
        };
        CPH.SetTwitchUserVarById(id, "ci_wheel_pending_json", JsonConvert.SerializeObject(pending), true);
        if (!RecoverPending(id)) return false;
        if (rejected != null)
            CPH.SendMessage($"🎮 @{name} replaced their rejected game with '{game}'. Waiting for mod approval.");
        else
            CPH.SendMessage($"🎮 @{name} submitted '{game}'! {usedSlots + 1}/{MonthlyLimit} picks this month. Waiting for mod approval.");
        return true;
    }

    private static string Normalize(string game)
    {
        return Regex.Replace((game ?? "").Trim(), @"\s+", " ").ToLowerInvariant();
    }

    private List<WheelEntry> Load(string key)
    {
        string raw = CPH.GetGlobalVar<string>(key, true);
        var entries = String.IsNullOrWhiteSpace(raw) ? new List<WheelEntry>()
            : JsonConvert.DeserializeObject<List<WheelEntry>>(raw);
        if (entries == null || entries.Any(x => x == null || String.IsNullOrWhiteSpace(x.id) || String.IsNullOrWhiteSpace(x.userId)))
            throw new InvalidOperationException("Invalid wheel data in " + key);
        return entries;
    }

    private bool RecoverPending(string userId)
    {
        string json = CPH.GetTwitchUserVarById<string>(userId, "ci_wheel_pending_json", true);
        if (String.IsNullOrWhiteSpace(json)) return true;
        PendingSubmission pending;
        try { pending = JsonConvert.DeserializeObject<PendingSubmission>(json); }
        catch (Exception ex) { CPH.LogError("Cannot recover pending submission: " + ex.Message); return false; }
        bool replacement = !String.IsNullOrWhiteSpace(pending == null ? null : pending.replacesId);
        if (pending == null || pending.entry == null || pending.entry.userId != userId ||
            String.IsNullOrWhiteSpace(pending.entry.id) ||
            !Regex.IsMatch(pending.entry.month ?? "", @"^\d{4}-(0[1-9]|1[0-2])$") ||
            pending.spentBefore < 0 || (!replacement && pending.earnedAtSubmission <= pending.spentBefore))
        {
            CPH.LogError("Invalid pending game submission for " + userId);
            return false;
        }

        // A journal from an older v5 build may predate moderation fields.
        // Never let recovery silently bypass the new approval queue.
        if (String.IsNullOrWhiteSpace(pending.entry.slotId))
            pending.entry.slotId = pending.entry.id;
        if (String.IsNullOrWhiteSpace(pending.entry.status))
            pending.entry.status = "pending";

        string key = "ci_wheel_month_" + pending.entry.month.Replace('-', '_');
        List<WheelEntry> entries;
        try { entries = Load(key); }
        catch (Exception ex) { CPH.LogError("Cannot recover wheel: " + ex.Message); return false; }
        bool alreadySaved = entries.Any(x => x.id == pending.entry.id);
        if (!alreadySaved && entries.Any(x => !x.removed && String.IsNullOrWhiteSpace(x.replacedBy) &&
            (String.IsNullOrWhiteSpace(x.status) || x.status == "approved" || x.status == "pending") &&
            Normalize(x.game) == Normalize(pending.entry.game)))
        {
            CPH.LogError("Pending title is already reserved. Review the month ledger and journal before retrying.");
            return false;
        }
        if (replacement)
        {
            var original = entries.FirstOrDefault(x => x.id == pending.replacesId && x.userId == userId);
            if (original == null || original.status != "rejected" ||
                (!String.IsNullOrWhiteSpace(original.replacedBy) && original.replacedBy != pending.entry.id) ||
                (String.IsNullOrWhiteSpace(original.slotId) ? original.id : original.slotId) != pending.entry.slotId)
            {
                CPH.LogError("Rejected game slot no longer available for replacement: " + pending.replacesId);
                return false;
            }
            original.replacedBy = pending.entry.id;
        }
        if (!alreadySaved)
            entries.Add(pending.entry);
        // Persist both the replacement pointer and new entry together.
        CPH.SetGlobalVar(key, JsonConvert.SerializeObject(entries), true);
        if (!replacement)
        {
            int spent = Math.Max(0, CPH.GetTwitchUserVarById<int>(userId, "ci_wheel_claimed", true));
            int earned = Math.Max(0, CPH.GetTwitchUserVarById<int>(userId, "ci_wheel_earned", true));
            CPH.SetTwitchUserVarById(userId, "ci_wheel_earned", Math.Max(earned, pending.earnedAtSubmission), true);
            CPH.SetTwitchUserVarById(userId, "ci_wheel_claimed", Math.Max(spent, pending.spentBefore + 1), true);
        }
        AppendAudit(pending.entry, replacement ? "GAME_REPLACEMENT" : "GAME_SUBMISSION");
        CPH.UnsetTwitchUserVarById(userId, "ci_wheel_pending_json", true);
        return true;
    }

    private void AppendAudit(WheelEntry entry, string eventType)
    {
        try
        {
            // Recorded by submission date, not recovery date.
            string key = "ci_events_month_" + entry.month.Replace('-', '_');
            string raw = CPH.GetGlobalVar<string>(key, true);
            var events = String.IsNullOrWhiteSpace(raw) ? new JArray() : JArray.Parse(raw);
            string id = "game-" + entry.id;
            if (events.OfType<JObject>().Any(x => (string)x["id"] == id)) return;
            events.Add(JObject.FromObject(new {
                id, type = eventType, atUtc = DateTimeOffset.UtcNow.ToString("o"),
                userId = entry.userId, userName = entry.user, entryId = entry.id,
                game = entry.game, month = entry.month, slotId = entry.slotId
            }));
            CPH.SetGlobalVar(key, events.ToString(Formatting.None), true);
        }
        catch (Exception ex) { CPH.LogError("WHEEL AUDIT FAILED: " + ex.Message); }
    }

    private static DateTimeOffset EasternTime(DateTimeOffset utc)
    {
        try { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")); }
    }
}
