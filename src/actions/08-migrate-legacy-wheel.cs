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
            InitializeCreditHighwater();
            CPH.SetGlobalVar("ci_schema_version", 5, true);
            CPH.LogInfo("Wheel migration already completed; credit high-water verified, schema 5.");
            return true;
        }

        string legacyJson = CPH.GetGlobalVar<string>("ci_wheel_entries_json", true);
        if (String.IsNullOrWhiteSpace(legacyJson))
        {
            InitializeCreditHighwater();
            CPH.SetGlobalVar("ci_wheel_legacy_migrated", true, true);
            CPH.SetGlobalVar("ci_schema_version", 5, true);
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
            // Count unique legacy entry IDs (not duplicated imported rows) so a
            // damaged legacy archive with repeated IDs cannot inflate credits.
            int historicSpent = group.Select(e => e.id)
                .Where(x => !String.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal).Count()
                + group.Count(e => String.IsNullOrWhiteSpace(e.id));
            int savedSpent = Math.Max(0, CPH.GetTwitchUserVarById<int>(userId, "ci_wheel_claimed", true));
            int spent = Math.Max(savedSpent, historicSpent);
            int total = Math.Max(0, CPH.GetTwitchUserVarById<int>(userId, "ci_total", true));
            int previousEarned = Math.Max(0, CPH.GetTwitchUserVarById<int>(userId, "ci_wheel_earned", true));
            int earned = Math.Max(Math.Max(previousEarned, spent), total / 10);
            CPH.SetTwitchUserVarById(userId, "ci_wheel_claimed", spent, true);
            CPH.SetTwitchUserVarById(userId, "ci_wheel_earned", earned, true);
        }

        InitializeCreditHighwater();
        CPH.SetGlobalVar("ci_wheel_legacy_migrated", true, true);
        CPH.SetGlobalVar("ci_schema_version", 5, true);
        CPH.LogInfo($"Wheel migration complete: {legacy.Count} legacy entries checked across {updates.Count} month partitions; source kept intact.");
        return true;
    }
    // All v3 viewers need an earned-credit baseline even when the legacy
    // wheel array is absent. Normalize saved spent/earned and current totals
    // without reducing either high-water counter.
    private void InitializeCreditHighwater()
    {
        var totals = CPH.GetTwitchUsersVar<int>("ci_total", true)
            .ToDictionary(x => x.UserId, x => x.Value);
        var spent = CPH.GetTwitchUsersVar<int>("ci_wheel_claimed", true)
            .ToDictionary(x => x.UserId, x => x.Value);
        var earned = CPH.GetTwitchUsersVar<int>("ci_wheel_earned", true)
            .ToDictionary(x => x.UserId, x => x.Value);
        var ids = new HashSet<string>(totals.Keys);
        ids.UnionWith(spent.Keys);
        ids.UnionWith(earned.Keys);
        foreach (var id in ids)
        {
            int total = totals.ContainsKey(id) ? Math.Max(0, totals[id]) : 0;
            int used = spent.ContainsKey(id) ? Math.Max(0, spent[id]) : 0;
            int prior = earned.ContainsKey(id) ? Math.Max(0, earned[id]) : 0;
            int high = Math.Max(Math.Max(used, prior), total / 10);
            CPH.SetTwitchUserVarById(id, "ci_wheel_earned", high, true);
        }
        CPH.LogInfo("Wheel migration: initialized credit high-water baselines for " + ids.Count + " Twitch viewers.");
    }

}
