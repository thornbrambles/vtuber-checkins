using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Install as "CI - Spin Wheel" in the Community Checkins BLOCKING queue.
// Trigger !spinwheel (moderator/broadcaster only) with Starts With matching.
// This action never changes check-in totals, game credits or the OBS check-in overlay.
public class CPHInline
{
    private const int SpinDurationMs = 10000;
    private const int ResultVisibleMs = 8000;
    private const int OverlayLifetimeMs = SpinDurationMs + ResultVisibleMs;
    private class Entry
    {
        public string id {get;set;}
        public string userId {get;set;}
        public string user {get;set;}
        public string game {get;set;}
        public string month {get;set;}
        public string status {get;set;}
        public bool removed {get;set;}
        public string replacedBy {get;set;}
    }
    private class Choice
    {
        public string id {get;set;}
        public string game {get;set;}
        public string user {get;set;}
        public string userId {get;set;}
    }
    private class Result
    {
        public string spinId {get;set;}
        public string month {get;set;}
        public string drawnAtUtc {get;set;}
        public int selectedIndex {get;set;}
        public int eligibleCount {get;set;}
        public Choice winner {get;set;}
        public List<Choice> choices {get;set;}
        public string actorId {get;set;}
    }

    public bool Execute()
    {
        string actorId, actor, raw;
        if (!CPH.TryGetArg("userId", out actorId) || String.IsNullOrWhiteSpace(actorId) ||
            !CPH.TryGetArg("userName", out actor)) return false;
        var broadcaster = CPH.TwitchGetBroadcaster();
        var caller = CPH.TwitchGetUserInfoById(actorId);
        bool isBroadcaster = broadcaster != null && broadcaster.UserId == actorId;
        if (!isBroadcaster && (caller == null || !caller.IsModerator))
        {
            CPH.LogWarn("WHEEL SPIN access denied for " + actorId);
            return false;
        }
        CPH.TryGetArg("rawInput", out raw);
        string[] args = (raw ?? "").Trim().Split(new[]{' '}, StringSplitOptions.RemoveEmptyEntries);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string currentMonth = EasternTime(now).ToString("yyyy-MM");
        string defaultMonth = EasternTime(now).AddMonths(-1).ToString("yyyy-MM");

        if (args.Length > 0 && args[0].Equals("hide", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length != 1) { Usage(); return true; }
            // Hide presentation, keep immutable draw result/audit untouched.
            string oldOverlay = CPH.GetGlobalVar<string>("ci_wheel_spin_overlay_json", true);
            if (!String.IsNullOrWhiteSpace(oldOverlay))
            {
                try {
                    var state = JObject.Parse(oldOverlay);
                    state["hidden"] = true;
                    CPH.SetGlobalVar("ci_wheel_spin_overlay_json", state.ToString(Formatting.None), true);
                }
                catch(Exception ex) { CPH.LogError("Could not hide wheel overlay: " + ex.Message); return false; }
            }
            CPH.SendMessage("Wheel hidden.");
            return true;
        }

        if (args.Length > 0 && args[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            // Irreversible operation requires an EXACT confirmation and broadcaster identity.
            if (!isBroadcaster || args.Length != 3 || !ValidPastMonth(args[1], currentMonth) ||
                args[2] != "RESET-" + args[1])
            {
                CPH.SendMessage("Only the broadcaster can reset a spin. Use !spinwheel reset YYYY-MM RESET-YYYY-MM");
                return true;
            }
            string key = ResultKey(args[1]);
            string existing = CPH.GetGlobalVar<string>(key, true);
            if (String.IsNullOrWhiteSpace(existing))
            {
                CPH.SendMessage("No recorded spin for " + args[1] + ".");
                return true;
            }
            // Preserve past outcomes instead of deleting audit history.
            try {
                var prior = JsonConvert.DeserializeObject<Result>(existing);
                if (prior == null || prior.winner == null) throw new InvalidOperationException("Invalid result");
                AppendAudit("RESET", actorId, args[1], prior.spinId, prior.winner.game);
                CPH.UnsetGlobalVar(key, true);
                string shown = CPH.GetGlobalVar<string>("ci_wheel_spin_overlay_json", true);
                if (!String.IsNullOrWhiteSpace(shown))
                {
                    var overlay = JObject.Parse(shown);
                    if ((string)overlay["month"] == args[1])
                    {
                        overlay["hidden"] = true;
                        CPH.SetGlobalVar("ci_wheel_spin_overlay_json", overlay.ToString(Formatting.None), true);
                    }
                }
                CPH.SendMessage("Spin reset for " + args[1] + ". The previous result remains in the audit log.");
                return true;
            }
            catch (Exception ex) { CPH.LogError("Spin reset failed: " + ex.Message); return false; }
        }

        bool replay = args.Length > 0 && args[0].Equals("replay", StringComparison.OrdinalIgnoreCase);
        string month = replay ? (args.Length == 2 ? args[1] : "") :
            (args.Length == 0 ? defaultMonth : args.Length == 1 ? args[0] : "");
        if (!ValidPastMonth(month, currentMonth)) { Usage(); return true; }

        string resultKey = ResultKey(month);
        string existingResult = CPH.GetGlobalVar<string>(resultKey, true);
        if (!String.IsNullOrWhiteSpace(existingResult))
        {
            if (!replay)
            {
                CPH.SendMessage("The " + month + " wheel has already been spun. Use !spinwheel replay " +
                    month + " to show the same result, or ask the broadcaster to reset it.");
                return true;
            }
            try
            {
                var result = JsonConvert.DeserializeObject<Result>(existingResult);
                if (result == null || result.winner == null || result.choices == null ||
                    result.choices.Count == 0 || result.selectedIndex < 0 ||
                    result.selectedIndex >= result.choices.Count ||
                    result.choices[result.selectedIndex].id != result.winner.id)
                    throw new InvalidOperationException("Saved result is inconsistent.");
                Publish(result, now, false);
                CPH.SendMessage("Replaying the " + month + " wheel result: " + result.winner.game +
                    " (" + result.winner.user + ").");
                return true;
            }
            catch(Exception ex) { CPH.LogError("Cannot replay saved spin: " + ex.Message); return false; }
        }
        if (replay)
        {
            CPH.SendMessage("No saved wheel result for " + month + ".");
            return true;
        }

        // Always read the LIVE month ledger rather than a potentially stale
        // archive. Any unreviewed game prevents an accidental unfair draw.
        List<Entry> entries;
        try
        {
            string rawEntries = CPH.GetGlobalVar<string>("ci_wheel_month_" + month.Replace('-', '_'), true);
            entries = String.IsNullOrWhiteSpace(rawEntries) ? new List<Entry>() :
                JsonConvert.DeserializeObject<List<Entry>>(rawEntries);
            if (entries == null || entries.Any(x => x == null || String.IsNullOrWhiteSpace(x.id) ||
                String.IsNullOrWhiteSpace(x.game) || String.IsNullOrWhiteSpace(x.userId)))
                throw new InvalidOperationException("Invalid game ledger.");
            if (entries.GroupBy(x=>x.id).Any(g=>g.Count()>1))
                throw new InvalidOperationException("Duplicate wheel entry IDs.");
        }
        catch(Exception ex) { CPH.LogError("Wheel spin aborted: " + ex.Message); return false; }

        int pending = entries.Count(x => !x.removed && String.IsNullOrWhiteSpace(x.replacedBy) && x.status == "pending");
        if (pending > 0)
        {
            CPH.SendMessage($"{pending} game(s) still need approval for {month}. Use !wheeladmin list {month} first.");
            return true;
        }
        var choices = entries.Where(x => !x.removed && String.IsNullOrWhiteSpace(x.replacedBy) &&
            (String.IsNullOrWhiteSpace(x.status) || x.status == "approved"))
            .Select(x=>new Choice {id=x.id,game=x.game,user=x.user,userId=x.userId})
            .ToList();
        if (choices.Count == 0) { CPH.SendMessage("No approved games for " + month + "."); return true; }
        if (choices.Count > 200) {
            CPH.LogError("Wheel has " + choices.Count + " entries; max supported by overlay is 200.");
            return false;
        }

        // Cryptographically secure uniform draw. One *submission* = one slice,
        // even if a viewer owns multiple entries. Persist BEFORE presentation.
        int winningIndex = DrawIndex(choices.Count);
        var saved = new Result {
            spinId=Guid.NewGuid().ToString("N"), month=month,
            drawnAtUtc=now.ToString("o"), eligibleCount=choices.Count,
            choices=choices, selectedIndex=winningIndex,
            winner=choices[winningIndex], actorId=actorId
        };
        // Validate audit storage BEFORE a draw result is committed.
        try { ReadAudit(); }
        catch(Exception ex) { CPH.LogError("Spin refused; audit unreadable: " + ex.Message); return false; }

        CPH.SetGlobalVar(resultKey, JsonConvert.SerializeObject(saved), true);
        try { AppendAudit("DRAW", actorId, month, saved.spinId, saved.winner.game); }
        catch(Exception ex) {
            CPH.LogError("DRAW SAVED but audit failed: " + ex.Message +
                ". Do NOT reset/re-spin. Inspect " + resultKey);
            return false;
        }
        Publish(saved, now, true);
        CPH.SendMessage($"🎡 Spinning the {month} game wheel! {choices.Count} entries.");
        CPH.LogInfo($"WHEEL DRAW month={month} spinId={saved.spinId} winnerId={saved.winner.id} game={saved.winner.game} user={saved.winner.user}");
        return true;
    }

    private void Publish(Result result, DateTimeOffset now, bool announce)
    {
        // Immutable outcome. UI only animates TO this selected index.
        var overlay = new {
            eventId=Guid.NewGuid().ToString("N"), spinId=result.spinId,
            month=result.month, startedAtMs=now.ToUnixTimeMilliseconds(),
            durationMs=SpinDurationMs, visibleMs=OverlayLifetimeMs,
            selectedIndex=result.selectedIndex,
            choices=result.choices, winner=result.winner, hidden=false, announce
        };
        CPH.SetGlobalVar("ci_wheel_spin_overlay_json", JsonConvert.SerializeObject(overlay), true);
    }

    private JArray ReadAudit()
    {
        string raw = CPH.GetGlobalVar<string>("ci_wheel_spin_audit_json", true);
        return String.IsNullOrWhiteSpace(raw) ? new JArray() : JArray.Parse(raw);
    }
    private void AppendAudit(string operation, string actorId, string month, string spinId, string game)
    {
        var audit = ReadAudit();
        audit.Add(JObject.FromObject(new {
            id=Guid.NewGuid().ToString("N"), atUtc=DateTimeOffset.UtcNow.ToString("o"),
            operation, actorId, month, spinId, game
        }));
        CPH.SetGlobalVar("ci_wheel_spin_audit_json", audit.ToString(Formatting.None), true);
    }

    private static int DrawIndex(int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException("count");
        // Reject the incomplete tail so every slice has exactly the same probability.
        ulong range = (ulong)UInt32.MaxValue + 1;
        ulong limit = range - range % (uint)count;
        var bytes = new byte[4];
        using (var rng = RandomNumberGenerator.Create())
        {
            uint value;
            do { rng.GetBytes(bytes); value = BitConverter.ToUInt32(bytes, 0); }
            while ((ulong)value >= limit);
            return (int)(value % (uint)count);
        }
    }

    private static string ResultKey(string month) { return "ci_wheel_spin_result_" + month.Replace('-', '_'); }
    private static bool ValidPastMonth(string month, string current)
    {
        return !String.IsNullOrWhiteSpace(month) &&
            Regex.IsMatch(month, @"^\d{4}-(0[1-9]|1[0-2])$") &&
            String.CompareOrdinal(month, current) < 0;
    }
    private static DateTimeOffset EasternTime(DateTimeOffset utc)
    {
        try {return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));}
        catch(TimeZoneNotFoundException) {return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));}
    }
    private void Usage()
    {
        CPH.SendMessage("Use !spinwheel [past YYYY-MM], !spinwheel replay YYYY-MM, !spinwheel hide. Broadcaster reset: !spinwheel reset YYYY-MM RESET-YYYY-MM");
    }
}
