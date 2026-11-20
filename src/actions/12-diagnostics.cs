using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class CPHInline
{
    // Manual-only action. Optional Streamer.bot timed trigger for daily diagnostics.
    // No private viewer details are written to Twitch chat.
    private class Finding {
        public string severity { get; set; }
        public string code { get; set; }
        public string detail { get; set; }
    }
    public bool Execute()
    {
        var findings = new List<Finding>();
        int streamId = CPH.GetGlobalVar<int>("ci_stream_id", true);
        int streamCount = CPH.GetGlobalVar<int>("ci_stream_count", true);
        string streamKey = CPH.GetGlobalVar<string>("ci_stream_key", true);
        string overlay = CPH.GetGlobalVar<string>("ci_overlay_json", true);
        bool migrated = CPH.GetGlobalVar<bool>("ci_wheel_legacy_migrated", true);
        int schema = CPH.GetGlobalVar<int>("ci_schema_version", true);
        var totals = CPH.GetTwitchUsersVar<int>("ci_total", true);
        var streaks = CPH.GetTwitchUsersVar<int>("ci_streak", true);
        var claimed = CPH.GetTwitchUsersVar<int>("ci_wheel_claimed", true);
        var earned = CPH.GetTwitchUsersVar<int>("ci_wheel_earned", true);
        var pending = CPH.GetTwitchUsersVar<string>("ci_wheel_pending_json", true);

        if (streamId < 0 || streamCount < 0)
            Add(findings,"error","STREAM_NEGATIVE","Stream number/count cannot be negative.");
        if (streamId > 0 && String.IsNullOrWhiteSpace(streamKey))
            Add(findings,"warn","STREAM_KEY","No stream key saved; verify stream-start action.");
        if (schema != 0 && schema != 5)
            Add(findings,"error","SCHEMA","Unexpected ci_schema_version=" + schema);
        if (schema == 0) Add(findings,"warn","SCHEMA_UNSET","Schema version not initialized; run the v3/v4 migration.");
        if (!migrated) Add(findings,"warn","MIGRATION_PENDING","Wheel migration is not marked complete.");
        if (String.IsNullOrWhiteSpace(overlay)) Add(findings,"warn","OVERLAY_UNSET","No OBS state snapshot saved.");
        else
        {
            try {
                var state = JObject.Parse(overlay);
                if (state["streamId"] == null || state["topTotals"] == null)
                    Add(findings,"warn","OVERLAY_SCHEMA","Overlay JSON missing required keys.");
            }
            catch (Exception ex) { Add(findings,"error","OVERLAY_JSON",ex.Message); }
        }

        var totalById = totals.ToDictionary(x=>x.UserId, x=>x.Value);
        var usedById = claimed.ToDictionary(x=>x.UserId,x=>x.Value);
        var earnedById = earned.ToDictionary(x=>x.UserId,x=>x.Value);
        foreach (var r in totals)
            if (r.Value < 0) Add(findings,"error","NEGATIVE_TOTAL","Negative lifetime total for user "+r.UserId);
        foreach (var r in streaks)
            if (r.Value < 0) Add(findings,"error","NEGATIVE_STREAK","Negative streak for user "+r.UserId);
        foreach (var r in claimed)
            if (r.Value < 0) Add(findings,"error","NEGATIVE_SPENT","Negative spent credits for user "+r.UserId);
        var ids = new HashSet<string>(totalById.Keys);
        ids.UnionWith(usedById.Keys); ids.UnionWith(earnedById.Keys);
        foreach (var id in ids)
        {
            int total=totalById.ContainsKey(id)?totalById[id]:0;
            int used=usedById.ContainsKey(id)?usedById[id]:0;
            int high=earnedById.ContainsKey(id)?earnedById[id]:0;
            if (high < used || high < Math.Max(0,total)/10)
                Add(findings,"error","CREDIT_INVARIANT","Invalid earned/spent credits for user "+id);
        }
        foreach (var r in pending)
        {
            if (String.IsNullOrWhiteSpace(r.Value)) continue;
            try { JObject.Parse(r.Value); Add(findings,"warn","PENDING_SUBMISSION","Unrecovered pending game for user "+r.UserId); }
            catch(Exception ex) { Add(findings,"error","BAD_PENDING","Unparseable transaction for "+r.UserId+": "+ex.Message); }
        }

        // Catch missing/disabled chat command registrations — a common cause
        // of !checkins producing no Twitch chat response. The configured command
        // phrase is in Commands, not the friendly Name field.
        try
        {
            var configured=CPH.GetCommands();
            string[] expected={"!checkins","!checkinboard","!wheel","!addgame","!checkinadmin","!wheeladmin","!checkindiag"};
            foreach(string phrase in expected)
            {
                var matching=configured.Where(cmd=>cmd!=null &&
                    cmd.Commands!=null && cmd.Commands.Any(x=>String.Equals(x,phrase,StringComparison.OrdinalIgnoreCase))).ToList();
                if(matching.Count==0) Add(findings,"warn","COMMAND_MISSING","No Streamer.bot command registered for "+phrase);
                else if(!matching.Any(cmd=>cmd.Enabled))
                    Add(findings,"warn","COMMAND_DISABLED","Command is disabled: "+phrase);
            }
        }
        catch(Exception ex) {
            Add(findings,"warn","COMMANDS_UNAVAILABLE","Unable to inspect command registrations: "+ex.Message);
        }

        var allGlobals = CPH.GetGlobalVarValues(true);
        foreach (var variable in allGlobals)
        {
            if (variable == null || String.IsNullOrWhiteSpace(variable.VariableName) ||
                !variable.VariableName.StartsWith("ci_wheel_month_",StringComparison.Ordinal)) continue;
            string json = CPH.GetGlobalVar<string>(variable.VariableName,true);
            try {
                var entries = String.IsNullOrWhiteSpace(json)?new JArray():JArray.Parse(json);
                var duplicates = entries.OfType<JObject>()
                   .Select(x=>(string)x["id"]).Where(x=>!String.IsNullOrWhiteSpace(x))
                   .GroupBy(x=>x,StringComparer.Ordinal).Where(x=>x.Count()>1).ToList();
                if (duplicates.Count>0) Add(findings,"error","WHEEL_DUPLICATES",variable.VariableName+": duplicate entry IDs "+duplicates.Count);
            }
            catch(Exception ex) { Add(findings,"error","WHEEL_JSON",variable.VariableName+": "+ex.Message); }
        }

        string serialized = JsonConvert.SerializeObject(new {
            generatedAtUtc=DateTimeOffset.UtcNow.ToString("o"), schema,
            viewerCount=ids.Count, streamId, streamCount,
            findings, errors=findings.Count(f=>f.severity=="error"),
            warnings=findings.Count(f=>f.severity=="warn")
        },Formatting.Indented);
        CPH.SetGlobalVar("ci_diagnostics_json",serialized,true);
        CPH.LogInfo($"CHECKIN DIAGNOSTICS: {ids.Count} viewers; {findings.Count(f=>f.severity=="error")} errors; {findings.Count(f=>f.severity=="warn")} warnings.");
        foreach(var finding in findings.Take(40))
            CPH.LogWarn($"{finding.severity}: {finding.code}: {finding.detail}");
        return findings.All(f=>f.severity!="error");
    }
    private static void Add(List<Finding> list,string severity,string code,string detail) {
        list.Add(new Finding{severity=severity,code=code,detail=detail});
    }
}
