using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

public class CPHInline
{
    // Moderator/broadcaster ONLY, also restrict command permissions in UI.
    // !wheeladmin list [YYYY-MM] | add [@user] YYYY-MM Title | approve ID | reject ID | rename ID New Title | remove ID | restore ID
    // Existing entries are NEVER deleted; removed entries retain their credit spend.
    // Owner of moderator-added games: matches no viewer, so no credit or monthly slot is used.
    public const string AdminUserId="admin-added";
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
        public string status {get;set;}
        public string slotId {get;set;}
        public string replacedBy {get;set;}
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
                (x.removed?"[removed]":(x.status??"approved")+" ") +(x.id??"missing") + ":" + (x.game??"").Substring(0,Math.Min(20,(x.game??"").Length))));
            CPH.SendMessage($"Wheel {month}: {entries.Count(x=>!x.removed && (String.IsNullOrWhiteSpace(x.status) || x.status=="approved"))} approved / {entries.Count(x=>x.status=="pending")} pending / {entries.Count} recorded. First 3: {preview}. Check Streamer.bot logs for all IDs.");
            foreach(var e in entries) CPH.LogInfo($"WHEEL {month} id={e.id} game={e.game} user={e.user} date={e.date} status={e.status??"approved"} removed={e.removed} replacedBy={e.replacedBy}");
            return true;
        }
        if (op=="add") return Add(parts,actorId,actor);
        if (!(op=="remove"||op=="restore"||op=="rename"||op=="approve"||op=="reject") || parts.Length<(op=="rename"?3:2))
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
        var audit=LoadAudit();
        if(audit==null) return false;
        string before=item.game;
        bool priorRemoved=item.removed;
        string priorStatus=item.status;
        if(op=="approve" || op=="reject") {
            if(item.removed || !String.IsNullOrWhiteSpace(item.replacedBy)) {
                CPH.SendMessage("This game was removed or replaced and cannot be reviewed.");
                return true;
            }
            if(op=="approve") {
                if(HasApprovedDuplicate(found, item, item.game)) {
                    CPH.SendMessage("That game is already approved. Reject this entry or choose another title.");
                    return true;
                }
                item.status="approved";
            } else {
                item.status="rejected";
            }
        } else if(op=="rename") {
            string title=String.Join(" ",parts.Skip(2));
            if(title.Length<2 || title.Length>100 || title.Any(char.IsControl)) {
                CPH.SendMessage("Game title must contain 2-100 characters.");return true;
            }
            if (!item.removed && IsApproved(item) && HasApprovedDuplicate(found, item, title)) {
                CPH.SendMessage("That game is already approved. Choose another title.");
                return true;
            }
            item.game=title;
        } else if(op=="remove") {
            item.removed=true;
            item.removedBy=actorId;
            item.removedAtUtc=DateTimeOffset.UtcNow.ToString("o");
        } else {
            if (IsApproved(item) && String.IsNullOrWhiteSpace(item.replacedBy) &&
                HasApprovedDuplicate(found, item, item.game)) {
                CPH.SendMessage("That game is already approved. Rename this entry before restoring it.");
                return true;
            }
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
            before,after=item.game,removedBefore=priorRemoved,removedAfter=item.removed,
            statusBefore=priorStatus,statusAfter=item.status
        }));
        CPH.SetGlobalVar(AuditKey,audit.ToString(Formatting.None),true);
        CPH.LogInfo($"WHEELADMIN operation={op} actor={actor}({actorId}) entry={target} {before} -> {item.game}");
        if(op=="reject")
            CPH.SendMessage($"@{item.user}, '{item.game}' wasn't approved. Use !addgame New Title to replace it this month. No extra credit needed.");
        else if(op=="approve")
            CPH.SendMessage($"🎮 '{item.game}' is approved for the wheel!");
        else
            CPH.SendMessage($"Wheel entry {target}: {op} done. No credits refunded.");
        return true;
    }
    // Adds an already-approved game, e.g. one collected before the bot existed.
    private bool Add(string[] parts,string actorId,string actor) {
        int i=1;
        string who="";
        if(parts.Length>i && parts[i].StartsWith("@")) who=parts[i++].Substring(1);
        if(parts.Length<i+2 || !ValidMonth(parts[i]) ||
           (who!="" && !Regex.IsMatch(who,@"^[A-Za-z0-9_]{1,25}$"))) {Usage();return true;}
        string month=parts[i];
        string title=String.Join(" ",parts.Skip(i+1));
        if(title.Length<2 || title.Length>100 || title.Any(char.IsControl)) {
            CPH.SendMessage("Game title must contain 2-100 characters.");return true;
        }
        var entries=Load(month);
        if(entries==null) return false;
        var audit=LoadAudit();
        if(audit==null) return false;
        var now=DateTimeOffset.UtcNow;
        var entry=new Entry {
            id=Guid.NewGuid().ToString("N"), userId=AdminUserId, user=who, game=title,
            date=EasternTime(now).ToString("yyyy-MM-dd"), month=month, utc=now.ToString("o"),
            status="approved"
        };
        entry.slotId=entry.id;
        if(HasApprovedDuplicate(entries,entry,title)) {
            CPH.SendMessage("That game is already approved for "+month+".");return true;
        }
        entries.Add(entry);
        CPH.SetGlobalVar("ci_wheel_month_"+month.Replace('-','_'),JsonConvert.SerializeObject(entries),true);
        audit.Add(JObject.FromObject(new {
            id=Guid.NewGuid().ToString("N"), atUtc=now.ToString("o"), actorId, actor,
            operation="add", entryId=entry.id, month, before=(string)null, after=title,
            removedBefore=false, removedAfter=false, statusBefore=(string)null, statusAfter="approved"
        }));
        CPH.SetGlobalVar(AuditKey,audit.ToString(Formatting.None),true);
        CPH.LogInfo($"WHEELADMIN operation=add actor={actor}({actorId}) entry={entry.id} month={month} game={title} credit={who}");
        CPH.SendMessage($"🎮 '{title}' added to the {month} wheel{(who==""?"":" for "+who)} (ID {entry.id}). No credits used.");
        return true;
    }
    private const string AuditKey="ci_wheel_audit_json";
    // Validate the audit ledger BEFORE any change: a corrupt trail must not allow silent edits.
    private JArray LoadAudit() {
        var auditText=CPH.GetGlobalVar<string>(AuditKey,true);
        try { return String.IsNullOrWhiteSpace(auditText)?new JArray():JArray.Parse(auditText); }
        catch(Exception ex) {CPH.LogError("Wheel admin refused: audit JSON invalid: "+ex.Message);return null;}
    }
    private static bool IsApproved(Entry entry) {
        return String.IsNullOrWhiteSpace(entry.status) || entry.status == "approved";
    }
    private static bool HasApprovedDuplicate(List<Entry> entries, Entry item, string title) {
        string normalized = Regex.Replace((title ?? "").Trim(), @"\s+", " ").ToLowerInvariant();
        return entries.Any(x => x != item && !x.removed && String.IsNullOrWhiteSpace(x.replacedBy) &&
            IsApproved(x) && Regex.Replace((x.game ?? "").Trim(), @"\s+", " ").ToLowerInvariant() == normalized);
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
    private void Usage() {CPH.SendMessage("!wheeladmin list [YYYY-MM] | add [@user] YYYY-MM Title | approve ID | reject ID | rename ID Title | remove ID | restore ID");}
}
