using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;

public class CPHInline
{
    // Administrative commands are restricted at the command permissions layer AND here.
    // Syntax: !checkinadmin view|set|add|remove|reset|streak @username [number]
    private static readonly int[] Thresholds = { 5, 10, 25, 50, 100, 250, 500 };
    private static readonly string[] Badges = {
        "Sprout", "Sapling", "Grove Keeper", "Forest Friend", "Ancient Oak",
        "Woodland Guardian", "Legend of the Grove"
    };
    private const int MaxTotal = 1000000;

    public bool Execute()
    {
        string actorId, actor;
        if (!CPH.TryGetArg("userId", out actorId) || String.IsNullOrWhiteSpace(actorId) ||
            !CPH.TryGetArg("userName", out actor) || String.IsNullOrWhiteSpace(actor))
            return false;

        // Fail closed if broadcaster/mod info isn't available.
        var broadcaster = CPH.TwitchGetBroadcaster();
        var account = CPH.TwitchGetUserInfoById(actorId);
        bool authorized = (broadcaster != null && actorId == broadcaster.UserId) ||
                          (account != null && account.IsModerator);
        if (!authorized)
        {
            CPH.LogWarn("CI admin denied for: " + actor + " (" + actorId + ")");
            CPH.SendMessage("@" + actor + ", moderator or broadcaster permission required.");
            return true;
        }

        string raw = "";
        CPH.TryGetArg("rawInput", out raw);
        string[] words = (raw ?? "").Trim().Split(new [] {' '}, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            Usage();
            return true;
        }

        string operation = words[0].ToLowerInvariant();
        if (!(new [] {"view", "set", "add", "remove", "reset", "streak"}).Contains(operation))
        {
            Usage();
            return true;
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(words[1], @"^@?[A-Za-z0-9_]{4,25}$"))
        {
            CPH.SendMessage("Invalid Twitch username.");
            return true;
        }
        string login = words[1].TrimStart('@').ToLowerInvariant();
        var target = CPH.TwitchGetUserInfoByLogin(login);
        if (target == null || String.IsNullOrWhiteSpace(target.UserId))
        {
            CPH.SendMessage("Twitch user not found: " + login);
            return true;
        }
        string id = target.UserId;
        string name = String.IsNullOrWhiteSpace(target.UserName) ? login : target.UserName;
        int streamId = CPH.GetGlobalVar<int>("ci_stream_id", true);
        int last = CPH.GetTwitchUserVarById<int>(id, "ci_last_stream", true);
        int oldTotal = Math.Max(0, CPH.GetTwitchUserVarById<int>(id, "ci_total", true));
        int oldStreak = Math.Max(0, CPH.GetTwitchUserVarById<int>(id, "ci_streak", true));
        int usedCredits = Math.Max(0, CPH.GetTwitchUserVarById<int>(id, "ci_wheel_claimed", true));
        int priorEarnedCredits = Math.Max(Math.Max(usedCredits, CPH.GetTwitchUserVarById<int>(id, "ci_wheel_earned", true)), oldTotal / 10);
        bool checkedIn = streamId > 0 && last == streamId;

        if (operation == "view")
        {
            CPH.SendMessage($"[Check-in admin] @{name}: total={oldTotal}, streak={oldStreak}, lastStream={last}, checkedInNow={checkedIn}, badge={Badge(oldTotal)}, wheelCredits={priorEarnedCredits-usedCredits}");
            return true;
        }
        int amount = 0;
        if (operation != "reset")
        {
            if (words.Length != 3 || !int.TryParse(words[2], out amount) || amount < 0 || amount > MaxTotal)
            {
                CPH.SendMessage("Provide a whole number from 0 to 1000000. Example: !checkinadmin add @viewer 2");
                return true;
            }
        }
        else if (words.Length != 2)
        {
            Usage();
            return true;
        }

        int total = oldTotal;
        int streak = oldStreak;
        bool clearAttendance = false;
        switch (operation)
        {
            case "set": total = amount; break;
            case "add": total = (int)Math.Min(MaxTotal, (long)oldTotal + amount); break;
            case "remove": total = Math.Max(0, oldTotal - amount); break;
            case "reset": total = 0; streak = 0; clearAttendance = true; break;
            case "streak": streak = amount; break;
        }

        // Zero total cannot preserve a streak/attendance state.
        if (total == 0)
        {
            streak = 0;
            clearAttendance = true;
        }
        // Reset / zeroing removes this stream's attendance and permits re-check-in.
        if (clearAttendance && checkedIn)
        {
            int count = Math.Max(0, CPH.GetGlobalVar<int>("ci_stream_count", true) - 1);
            CPH.SetGlobalVar("ci_stream_count", count, true);
        }
        if (clearAttendance)
            CPH.UnsetTwitchUserVarById(id, "ci_last_stream", true);
        // Wheel credits already used are NEVER reset. Existing submissions remain in history.
        // Earned entitlement is a lifetime high-water mark, not derived solely from
        // the current editable check-in total.
        CPH.SetTwitchUserVarById(id, "ci_wheel_earned", Math.Max(priorEarnedCredits, total / 10), true);

        CPH.SetTwitchUserVarById(id, "ci_total", total, true);
        CPH.SetTwitchUserVarById(id, "ci_streak", streak, true);
        string badge = Badge(total);
        if (badge == "None") CPH.UnsetTwitchUserVarById(id, "ci_badge", true);
        else CPH.SetTwitchUserVarById(id, "ci_badge", badge, true);

        CPH.LogInfo($"CI ADMIN actor={actor}({actorId}) operation={operation} target={name}({id}) amount={amount} total:{oldTotal}->{total} streak:{oldStreak}->{streak}");
        CPH.SendMessage($"[Check-in admin] @{name}: {operation} completed by @{actor}. Lifetime {oldTotal}->{total}; streak {oldStreak}->{streak}; badge {badge}.");
        Publish(streamId);
        return true;
    }

    private static string Badge(int total)
    {
        string result = "None";
        for (int i = 0; i < Thresholds.Length; i++)
            if (total >= Thresholds[i]) result = Badges[i];
        return result;
    }

    private void Usage()
    {
        CPH.SendMessage("Admin syntax: !checkinadmin view|reset @viewer OR !checkinadmin set|add|remove|streak @viewer number");
    }

    private void Publish(int streamId)
    {
        int count = CPH.GetGlobalVar<int>("ci_stream_count", true);
        var totals = CPH.GetTwitchUsersVar<int>("ci_total", true);
        var streaks = CPH.GetTwitchUsersVar<int>("ci_streak", true);
        var last = CPH.GetTwitchUsersVar<int>("ci_last_stream", true)
            .ToDictionary(x => x.UserId, x => x.Value);
        var topTotals = totals.Where(x => x.Value > 0).OrderByDescending(x => x.Value)
            .ThenBy(x => x.UserName).Take(5)
            .Select(x => new { name = x.UserName, value = x.Value }).ToList();
        var topStreaks = streaks.Where(x => x.Value > 0 && last.ContainsKey(x.UserId) &&
                                              last[x.UserId] > 0 && last[x.UserId] >= streamId - 1)
            .OrderByDescending(x => x.Value).ThenBy(x => x.UserName).Take(5)
            .Select(x => new { name = x.UserName, value = x.Value }).ToList();
        var state = new { streamId, count, latest = "", milestone = "", total = 0, streak = 0,
                          eventId = Guid.NewGuid().ToString("N"),
                          lastCheckinAtMs = CPH.GetGlobalVar<long>("ci_last_checkin_at_ms", true),
                          topTotals, topStreaks };
        CPH.SetGlobalVar("ci_overlay_json", JsonConvert.SerializeObject(state), true);
    }
}
