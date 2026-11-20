using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class CPHInline
{
    // Configure a daily off-stream timed trigger in the same BLOCKING queue.
    // On the first day of each month, it prepares LAST month's approved,
    // non-removed submissions for export/spinning. No Twitch announcement,
    // game deletion or automatic winner selection.
    private class Entry {
        public string id {get;set;}
        public string userId {get;set;}
        public string user {get;set;}
        public string game {get;set;}
        public string date {get;set;}
        public string month {get;set;}
        public string status {get;set;}
        public bool removed {get;set;}
        public string replacedBy {get;set;}
    }

    public bool Execute()
    {
        DateTimeOffset now = EasternTime(DateTimeOffset.UtcNow);
        string prior = now.AddMonths(-1).ToString("yyyy-MM");
        string requested;
        // Manual invocation can supply targetMonth, e.g. to regenerate an
        // older month after a moderator reviews late submissions.
        if (CPH.TryGetArg("targetMonth", out requested) && !String.IsNullOrWhiteSpace(requested))
        {
            if (!Regex.IsMatch(requested, @"^\d{4}-(0[1-9]|1[0-2])$") ||
                String.CompareOrdinal(requested, now.ToString("yyyy-MM")) >= 0)
            {
                CPH.LogError("Month archival target must be a past YYYY-MM month.");
                return false;
            }
            prior = requested;
        }

        // A submission may have reached its journal but not the month ledger.
        try
        {
            foreach (var journal in CPH.GetTwitchUsersVar<string>("ci_wheel_pending_json", true))
            {
                if (String.IsNullOrWhiteSpace(journal.Value)) continue;
                var pendingMonth = (string)JObject.Parse(journal.Value).SelectToken("entry.month");
                if (String.IsNullOrWhiteSpace(pendingMonth) || pendingMonth == prior)
                    throw new InvalidOperationException("Recover pending game submissions before finalizing the wheel.");
            }
        }
        catch (Exception ex)
        {
            CPH.LogError("Wheel finalization blocked: " + ex.Message);
            return false;
        }

        string sourceKey = "ci_wheel_month_" + prior.Replace('-', '_');
        string raw = CPH.GetGlobalVar<string>(sourceKey, true);
        List<Entry> entries;
        try {
            entries = String.IsNullOrWhiteSpace(raw) ? new List<Entry>()
                : JsonConvert.DeserializeObject<List<Entry>>(raw);
            if(entries==null || entries.Any(x=>x==null || String.IsNullOrWhiteSpace(x.id)))
                throw new InvalidOperationException("Invalid source wheel entries.");
        }
        catch(Exception ex) {
            CPH.LogError("Wheel archival refused to parse " + sourceKey + ": " + ex.Message);
            return false;
        }
        var eligible = entries.Where(x=>!x.removed && String.IsNullOrWhiteSpace(x.replacedBy) &&
            (String.IsNullOrWhiteSpace(x.status) || x.status=="approved"))
            .Select(x=>new {x.id,x.userId,x.user,x.game,x.date}).ToList();
        var report = new {
            month=prior,
            status="ready-for-wheel",
            eligible,
            pending=entries.Count(x=>x.status=="pending" && !x.removed && String.IsNullOrWhiteSpace(x.replacedBy)),
            rejected=entries.Count(x=>x.status=="rejected" && String.IsNullOrWhiteSpace(x.replacedBy)),
            totalRecorded=entries.Count
        };
        string archiveKey = "ci_wheel_archive_" + prior.Replace('-', '_');
        string serialized = JsonConvert.SerializeObject(report, Formatting.Indented);
        string previous = CPH.GetGlobalVar<string>(archiveKey, true);
        if(previous == serialized) {
            CPH.LogInfo("Wheel archive " + prior + " unchanged.");
            return true;
        }
        CPH.SetGlobalVar(archiveKey, serialized, true);
        CPH.LogInfo($"Wheel archive {prior} refreshed: {eligible.Count} approved, {report.pending} pending, {report.rejected} rejected. Export it when ready to spin.");
        if(report.pending > 0)
            CPH.LogWarn("Wheel archive has pending games; approve/reject before drawing winners.");
        return true;
    }

    private static DateTimeOffset EasternTime(DateTimeOffset utc) {
        try { return TimeZoneInfo.ConvertTime(utc,TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")); }
        catch(TimeZoneNotFoundException) { return TimeZoneInfo.ConvertTime(utc,TimeZoneInfo.FindSystemTimeZoneById("America/New_York")); }
    }
}
