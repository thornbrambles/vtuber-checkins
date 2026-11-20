using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class CPHInline
{
    // Read-only, manual-only ledger reconciliation. Handles legacy + monthly
    // entries without counting the same entry ID twice.
    public bool Execute()
    {
        var issues = new List<string>();
        var seen = new Dictionary<string,JObject>(StringComparer.Ordinal);
        foreach (var variable in CPH.GetGlobalVarValues(true))
        {
            if(variable==null || String.IsNullOrWhiteSpace(variable.VariableName)) continue;
            string key=variable.VariableName;
            if(key!="ci_wheel_entries_json" &&
               !System.Text.RegularExpressions.Regex.IsMatch(key,@"^ci_wheel_month_\d{4}_\d{2}$"))
                continue;
            string raw=CPH.GetGlobalVar<string>(key,true);
            if(String.IsNullOrWhiteSpace(raw)) continue;
            JArray entries;
            try { entries=JArray.Parse(raw); }
            catch(Exception ex) {issues.Add(key+" invalid JSON: "+ex.Message);continue;}
            for(int i=0;i<entries.Count;i++)
            {
                if(!(entries[i] is JObject)) {issues.Add(key+" nonobject entry "+i);continue;}
                var entry=(JObject)entries[i];
                string id=(string)entry["id"];
                if(String.IsNullOrWhiteSpace(id)) id=key+":"+i;
                JObject prior;
                if(seen.TryGetValue(id,out prior))
                {
                    if((string)prior["game"]!=(string)entry["game"] ||
                       (string)prior["userId"]!=(string)entry["userId"])
                        issues.Add("Conflicting game entry ID "+id);
                    continue;
                }
                seen[id]=entry;
            }
        }

        var countByUser=seen.Values.Where(x=>!String.IsNullOrWhiteSpace((string)x["userId"]))
            .GroupBy(x=>(string)x["userId"])
            .ToDictionary(x=>x.Key,x=>x.Select(y=>(string)y["slotId"] ?? (string)y["id"]).Distinct().Count());
        var totals=CPH.GetTwitchUsersVar<int>("ci_total",true)
            .ToDictionary(x=>x.UserId,x=>x.Value);
        var spent=CPH.GetTwitchUsersVar<int>("ci_wheel_claimed",true)
            .ToDictionary(x=>x.UserId,x=>x.Value);
        var earned=CPH.GetTwitchUsersVar<int>("ci_wheel_earned",true)
            .ToDictionary(x=>x.UserId,x=>x.Value);
        var ids=new HashSet<string>(totals.Keys);
        ids.UnionWith(spent.Keys);ids.UnionWith(earned.Keys);ids.UnionWith(countByUser.Keys);
        foreach(string id in ids)
        {
            int total=totals.ContainsKey(id)?totals[id]:0;
            int used=spent.ContainsKey(id)?spent[id]:0;
            int high=earned.ContainsKey(id)?earned[id]:0;
            int gameCount=countByUser.ContainsKey(id)?countByUser[id]:0;
            if(total<0 || high<0 || used<0)
                issues.Add("Negative stored counter: user "+id);
            if(used>high) issues.Add("Spent credits exceed earned: user "+id);
            if(high<Math.Max(0,total)/10)
                issues.Add("Earned high-water below total/10: user "+id);
            if(gameCount>used)
                issues.Add("Historical wheel entries exceed spent credits: user "+id+" entries="+gameCount+" spent="+used);
        }
        string report=JsonConvert.SerializeObject(new {
            generatedAtUtc=DateTimeOffset.UtcNow.ToString("o"),
            users=ids.Count, wheelEntries=seen.Count, findings=issues,
            errors=issues.Count
        },Formatting.Indented);
        CPH.SetGlobalVar("ci_reconciliation_json",report,true);
        CPH.LogInfo($"CHECKIN RECONCILE users={ids.Count} gameEntries={seen.Count} findings={issues.Count}");
        foreach(string issue in issues.Take(60)) CPH.LogWarn("CHECKIN RECONCILE "+issue);
        return issues.Count==0;
    }
}
