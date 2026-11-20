using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class CPHInline
{
    // Moderator/broadcaster ONLY, also restrict command permissions in UI.
    // !wheeladmin list [YYYY-MM] | rename ID New Title | remove ID | restore ID
    // Existing entries are NEVER deleted; removed entries retain their credit spend.
    private class Entry {
        public string id {get;set;}
        public string userId {get;set;}
        public string user {get;set;}
        public string game {get;set;}
        public string date {get;set;}
        public string month {get;set;}
        public string utc {get;set;}
        public bool removed {get;set;}
        public string removedAtUtc {get;set;}
        public string removedBy {get;set;}
    }
    public bool Execute()
    {
        string actorId, actor, raw;
        if (!CPH.TryGetArg("userId",out actorId) ||
            !CPH.TryGetArg("userName",out actor) ||
            String.IsNullOrWhiteSpace(actorId)) return false;
        var broadcaster=CPH.TwitchGetBroadcaster();
        var caller=CPH.TwitchGetUserInfoById(actorId);
        if (!((broadcaster!=null && actorId==broadcaster.UserId) ||
              (caller!=null && caller.IsModerator))) {
            CPH.LogWarn("WHEEL ADMIN DENIED "+actorId);
            return false;
        }
        if (!CPH.GetGlobalVar<bool>("ci_wheel_legacy_migrated",true)) {
            CPH.SendMessage("Wheel migration must run before administration.");
            return true;
        }
        CPH.TryGetArg("rawInput",out raw);
        string[] parts=(raw??"").Trim().Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length<1) { Usage();return true; }
        string op=parts[0].ToLowerInvariant();

        string month=EasternTime(DateTimeOffset.UtcNow).ToString("yyyy-MM");
        if (op=="list") {
            if(parts.Length>2) {Usage();return true;}
            if(parts.Length==2) month=parts[1];
            if (!ValidMonth(month)) {Usage();return true;}
            var entries=Load(month);
            if(entries==null) return false;
            var preview=String.Join(" | ",entries.Take(3).Select(x =>
                (x.removed?"[removed]":"")+(x.id??"missing") + ":" + (x.game??"").Substring(0,Math.Min(20,(x.game??"").Length))));
            CPH.SendMessage($"Wheel {month}: {entries.Count(x=>!x.removed)} active / {entries.Count} recorded. First 3: {preview}. Check Streamer.bot logs for all IDs.");
            foreach(var e in entries) CPH.LogInfo($"WHEEL {month} id={e.id} game={e.game} user={e.user} date={e.date} removed={e.removed}");
            return true;
        }
        if (!(op=="remove"||op=="restore"||op=="rename") || parts.Length<(op=="rename"?3:2))
        {Usage();return true;}
        string target=parts[1];
        if (!System.Text.RegularExpressions.Regex.IsMatch(target,@"^(?:[a-fA-F0-9]{32}|chat-[A-Za-z0-9_\-]{8,100}|legacy-[A-Za-z0-9_\-]{5,100})$")) {
            CPH.SendMessage("Invalid wheel entry ID. Run !wheeladmin list first.");
            return true;
        }

        // Find across month partitions (including older months), not just current.
        var globals=CPH.GetGlobalVarValues(true);
        string foundKey=null;
        List<Entry> found=null;
        Entry item=null;
        foreach(var v in globals) {
            if(v==null || String.IsNullOrWhiteSpace(v.VariableName) ||
               !System.Text.RegularExpressions.Regex.IsMatch(v.VariableName,@"^ci_wheel_month_\d{4}_\d{2}$")) continue;
            string foundMonth=v.VariableName.Substring("ci_wheel_month_".Length).Replace('_','-');
            var list=Load(foundMonth);
            if(list==null) return false;
            var match=list.FirstOrDefault(x=>x!=null&&x.id==target);
            if(match==null) continue;
            if(item!=null) { CPH.LogError("Duplicate wheel ID across monthly partitions: "+target);return false; }
            item=match;found=list;foundKey=v.VariableName;
        }
        if(item==null) { CPH.SendMessage("Wheel entry ID not found.");return true; }
        // Validate the audit ledger BEFORE modifying the entry. A corrupt audit
        // trail must not allow silent wheel changes.
        string auditKey="ci_wheel_audit_json";
        var auditText=CPH.GetGlobalVar<string>(auditKey,true);
        JArray audit;
        try { audit=String.IsNullOrWhiteSpace(auditText)?new JArray():JArray.Parse(auditText); }
        catch(Exception ex) {CPH.LogError("Wheel admin refused: audit JSON invalid: "+ex.Message);return false;}
        string before=item.game;
        bool priorRemoved=item.removed;
        if(op=="rename") {
            string title=String.Join(" ",parts.Skip(2));
            if(title.Length<2 || title.Length>100 || title.Any(char.IsControl)) {
                CPH.SendMessage("Game title must contain 2-100 characters.");return true;
            }
            item.game=title;
        } else if(op=="remove") {
            item.removed=true;
            item.removedBy=actorId;
            item.removedAtUtc=DateTimeOffset.UtcNow.ToString("o");
        } else {
            item.removed=false;
            item.removedBy=null;
            item.removedAtUtc=null;
        }
        // Write the entry first, then append audit. Audit is best effort and
        // should be reconciled against repository backups if a write crashes.
        CPH.SetGlobalVar(foundKey,JsonConvert.SerializeObject(found),true);
        audit.Add(JObject.FromObject(new {
            id=Guid.NewGuid().ToString("N"),
            atUtc=DateTimeOffset.UtcNow.ToString("o"),actorId,actor,
            operation=op, entryId=target, month=item.month,
            before,after=item.game,removedBefore=priorRemoved,removedAfter=item.removed
        }));
        CPH.SetGlobalVar(auditKey,audit.ToString(Formatting.None),true);
        CPH.LogInfo($"WHEELADMIN operation={op} actor={actor}({actorId}) entry={target} {before} -> {item.game}");
        CPH.SendMessage($"Wheel entry {target}: {op} complete. Game credits were NOT refunded.");
        return true;
    }
    private List<Entry> Load(string month) {
        string key="ci_wheel_month_"+month.Replace('-','_');
        try {
            string raw=CPH.GetGlobalVar<string>(key,true);
            return String.IsNullOrWhiteSpace(raw)?new List<Entry>():
                JsonConvert.DeserializeObject<List<Entry>>(raw)??new List<Entry>();
        }
        catch(Exception ex) {CPH.LogError("Cannot load wheel "+key+": "+ex.Message);return null;}
    }
    private static bool ValidMonth(string s) {
        return System.Text.RegularExpressions.Regex.IsMatch(s??"", @"^\d{4}-(0[1-9]|1[0-2])$");
    }
    private static DateTimeOffset EasternTime(DateTimeOffset utc) {
        try { return TimeZoneInfo.ConvertTime(utc,TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")); }
        catch(TimeZoneNotFoundException) {return TimeZoneInfo.ConvertTime(utc,TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));}
    }
    private void Usage() {CPH.SendMessage("!wheeladmin list [YYYY-MM] | remove ID | restore ID | rename ID New Title");}
}
