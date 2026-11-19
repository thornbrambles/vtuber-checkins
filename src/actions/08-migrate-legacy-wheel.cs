using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;

public class CPHInline
{
    // Run MANUALLY ONCE in the Community Checkins blocking queue, after backup.
    // Idempotent by entry ID: retries will not append the same legacy entry twice.
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
        if (CPH.GetGlobalVar<bool>("ci_wheel_legacy_migrated", true))
        {
            CPH.LogInfo("Wheel migration already completed; no changes made.");
            return true;
        }

        string legacyJson = CPH.GetGlobalVar<string>("ci_wheel_entries_json", true);
        if (String.IsNullOrWhiteSpace(legacyJson))
        {
            CPH.SetGlobalVar("ci_wheel_legacy_migrated", true, true);
            CPH.LogInfo("Wheel migration: no legacy entries. Monthly storage ready.");
            return true;
        }

        List<WheelEntry> legacy;
        try { legacy = JsonConvert.DeserializeObject<List<WheelEntry>>(legacyJson); }
        catch (Exception ex) { CPH.LogError("Wheel migration stopped; invalid legacy JSON: " + ex.Message); return false; }
        if (legacy == null) { CPH.LogError("Wheel migration stopped: legacy JSON must be an array."); return false; }
        if (legacy.Any(e => e == null || String.IsNullOrWhiteSpace(e.month) ||
                                String.IsNullOrWhiteSpace(e.userId) || String.IsNullOrWhiteSpace(e.game)))
        {
            CPH.LogError("Wheel migration stopped: some entries lack month, userId or game. Fix the data before retrying.");
            return false;
        }
        if (legacy.Any(e => !System.Text.RegularExpressions.Regex.IsMatch(e.month, @"^\d{4}-(0[1-9]|1[0-2])$")))
        {
            CPH.LogError("Wheel migration stopped: invalid month format (expected YYYY-MM).");
            return false;
        }

        // Read and validate ALL target partitions before changing anything.
        var updates = new Dictionary<string, string>();
        foreach (var group in legacy.GroupBy(e => e.month))
        {
            string key = "ci_wheel_month_" + group.Key.Replace('-', '_');
            string existingJson = CPH.GetGlobalVar<string>(key, true);
            List<WheelEntry> merged;
            try { merged = String.IsNullOrWhiteSpace(existingJson) ? new List<WheelEntry>()
                         : JsonConvert.DeserializeObject<List<WheelEntry>>(existingJson) ?? new List<WheelEntry>(); }
            catch (Exception ex) { CPH.LogError("Wheel migration stopped: malformed " + key + ": " + ex.Message); return false; }
            var existingIds = new HashSet<string>(merged.Where(e => e != null && !String.IsNullOrWhiteSpace(e.id))
                                                  .Select(e => e.id), StringComparer.Ordinal);
            int index = 0;
            foreach (var entry in group)
            {
                // Legacy v4 assigns IDs; missing IDs are given deterministic IDs to allow retry.
                if (String.IsNullOrWhiteSpace(entry.id))
                    entry.id = "legacy-" + group.Key + "-" + (index++);
                if (existingIds.Add(entry.id)) merged.Add(entry);
            }
            updates[key] = JsonConvert.SerializeObject(merged);
        }

        // Keep the original ci_wheel_entries_json untouched as an audit/backup.
        foreach (var update in updates)
            CPH.SetGlobalVar(update.Key, update.Value, true);

        // Repair spent credits for users whose claimed counters were reset in v4.
        foreach (var group in legacy.GroupBy(e => e.userId))
        {
            string userId = group.Key;
            int historicSpent = group.Count(); // lower bound; may be > actual count of entitlements
            int savedSpent = Math.Max(0, CPH.GetTwitchUserVarById<int>(userId, "ci_wheel_claimed", true));
            int spent = Math.Max(savedSpent, historicSpent);
            int total = Math.Max(0, CPH.GetTwitchUserVarById<int>(userId, "ci_total", true));
            int previousEarned = Math.Max(0, CPH.GetTwitchUserVarById<int>(userId, "ci_wheel_earned", true));
            int earned = Math.Max(Math.Max(previousEarned, spent), total / 10);
            CPH.SetTwitchUserVarById(userId, "ci_wheel_claimed", spent, true);
            CPH.SetTwitchUserVarById(userId, "ci_wheel_earned", earned, true);
        }

        CPH.SetGlobalVar("ci_wheel_legacy_migrated", true, true);
        CPH.LogInfo($"Wheel migration complete: {legacy.Count} legacy entries checked across {updates.Count} month partitions; source kept intact.");
        return true;
    }
}
