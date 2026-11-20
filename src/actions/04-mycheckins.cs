using System;
using System.Linq;

public class CPHInline
{
    private static readonly int[] Thresholds = { 5, 10, 25, 50, 100, 250, 500 };
    private static readonly string[] Badges = {
        "Sprout",
        "Sapling",
        "Grove Keeper",
        "Forest Friend",
        "Ancient Oak",
        "Woodland Guardian",
        "Legend of the Grove"
    };

    public bool Execute()
    {
        string id, name;
        if (!CPH.TryGetArg("userId", out id) || String.IsNullOrWhiteSpace(id) ||
            !CPH.TryGetArg("userName", out name) || String.IsNullOrWhiteSpace(name))
            return false;

        int streamId = CPH.GetGlobalVar<int>("ci_stream_id", true);
        int last = CPH.GetTwitchUserVarById<int>(id, "ci_last_stream", true);
        int total = CPH.GetTwitchUserVarById<int>(id, "ci_total", true);
        int rawStreak = CPH.GetTwitchUserVarById<int>(id, "ci_streak", true);
        int streak = last > 0 && last >= streamId - 1 ? rawStreak : 0;
        bool checkedIn = streamId > 0 && last == streamId;
        string badge = "None";
        int next = 0;
        for (int i = 0; i < Thresholds.Length; i++)
        {
            if (total >= Thresholds[i]) badge = Badges[i];
            else { next = Thresholds[i]; break; }
        }

        var ranked = CPH.GetTwitchUsersVar<int>("ci_total", true)
            .OrderByDescending(x => x.Value).ThenBy(x => x.UserName).ToList();
        int index = ranked.FindIndex(x => x.UserId == id);
        string rank = index >= 0 && total > 0 ? "#" + (index + 1) : "unranked";
        int usedCredits = Math.Max(0, CPH.GetTwitchUserVarById<int>(id, "ci_wheel_claimed", true));
        int earnedCredits = Math.Max(Math.Max(usedCredits, CPH.GetTwitchUserVarById<int>(id, "ci_wheel_earned", true)), total / 10);
        int wheelCredits = earnedCredits - usedCredits;
        string progress = next == 0 ? "all milestones earned" : (next - total) + " to next reward";
        CPH.SendMessage($"@{name}: {total} lifetime | {streak} stream streak | Rank {rank} | Badge: {badge} | " +
            (checkedIn ? "checked in this stream" : "not checked in this stream") + $" | {progress} | Game submissions: {wheelCredits} (!addgame Title)");
        return true;
    }
}
