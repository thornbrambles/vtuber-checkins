using System;
using System.Linq;
using Newtonsoft.Json;

public class CPHInline
{
    // OFFLINE / MANUAL DEBUG TOOL. NEVER add a Twitch/chat/stream trigger!
    // Edit these constants in the Streamer.bot C# editor, compile, then
    // manually Run Action. This edits the REAL persisted viewer data.
    // Default mode is disabled and dry run.
    private const bool DebugEnabled = false;  // change to true temporarily
    private const bool DryRun = true;          // review log before changing to false

    // Enter the Twitch NUMERIC USER ID, not the display name.
    private const string TargetUserId = "REPLACE_WITH_NUMERIC_TWITCH_USER_ID";
    private const string Operation = "view";   // view | set | add | remove | streak | reset
    private const int Value = 0;               // required for set/add/remove/streak

    private const int MaxCount = 1000000;

    public bool Execute()
    {
        if (!String.IsNullOrWhiteSpace(CPH.GetGlobalVar<string>("ci_checkin_pending_json", true)) ||
            !String.IsNullOrWhiteSpace(CPH.GetGlobalVar<string>("ci_stream_start_pending_json", true)))
        {
            CPH.LogError("Recover the pending check-in or stream transition before changing accounting state.");
            return false;
        }
        if (!DebugEnabled)
        {
            CPH.LogWarn("CHECKIN DEBUG disabled. Enable only while manually testing, then disable again.");
            return true;
        }
        if (String.IsNullOrWhiteSpace(TargetUserId) ||
            !TargetUserId.All(char.IsDigit))
        {
            CPH.LogError("CHECKIN DEBUG: set TargetUserId to the numeric Twitch user ID.");
            return false;
        }

        string op = (Operation ?? "").Trim().ToLowerInvariant();
        if (!(new [] { "view", "set", "add", "remove", "streak", "reset" }).Contains(op))
        {
            CPH.LogError("CHECKIN DEBUG: invalid operation.");
            return false;
        }
        if (Value < 0 || Value > MaxCount)
        {
            CPH.LogError("CHECKIN DEBUG: Value must be between 0 and " + MaxCount);
            return false;
        }

        int total = Math.Max(0, CPH.GetTwitchUserVarById<int>(TargetUserId, "ci_total", true));
        int streak = Math.Max(0, CPH.GetTwitchUserVarById<int>(TargetUserId, "ci_streak", true));
        int last = Math.Max(0, CPH.GetTwitchUserVarById<int>(TargetUserId, "ci_last_stream", true));
        int spent = Math.Max(0, CPH.GetTwitchUserVarById<int>(TargetUserId, "ci_wheel_claimed", true));
        int priorEarned = Math.Max(Math.Max(spent,
            CPH.GetTwitchUserVarById<int>(TargetUserId, "ci_wheel_earned", true)), total / 10);

        if (op == "view")
        {
            CPH.LogInfo($"CHECKIN DEBUG VIEW id={TargetUserId} total={total}, streak={streak}, lastStream={last}, wheelEarned={priorEarned}, wheelSpent={spent}, wheelAvailable={priorEarned - spent}");
            return true;
        }

        int nextTotal = total, nextStreak = streak;
        switch (op)
        {
            case "set": nextTotal = Value; break;
            case "add": nextTotal = (int)Math.Min((long)MaxCount, (long)total + Value); break;
            case "remove": nextTotal = Math.Max(0, total - Value); break;
            case "streak": nextStreak = Value; break;
            case "reset": nextTotal = 0; nextStreak = 0; break;
        }
        if (nextTotal == 0) nextStreak = 0;
        int earned = Math.Max(priorEarned, nextTotal / 10);
        CPH.LogWarn($"CHECKIN DEBUG {(DryRun ? "DRY RUN" : "APPLY")} id={TargetUserId} op={op} " +
            $"total {total}->{nextTotal}, streak {streak}->{nextStreak}, credits earned {priorEarned}->{earned}, spent {spent} (unchanged).");

        if (DryRun) return true;

        // Debugging outside a stream does NOT simulate Channel Points
        // redemption or change ci_stream_id / ci_reward_last_stream.
        CPH.SetTwitchUserVarById(TargetUserId, "ci_total", nextTotal, true);
        CPH.SetTwitchUserVarById(TargetUserId, "ci_streak", nextStreak, true);
        CPH.SetTwitchUserVarById(TargetUserId, "ci_wheel_earned", earned, true);
        if (op == "reset")
        {
            // Historical rewards/credits are preserved. Last-stream history is
            // cleared to reflect a reset, but redemption marker stays intact.
            CPH.UnsetTwitchUserVarById(TargetUserId, "ci_last_stream", true);
        }
        CPH.LogWarn("CHECKIN DEBUG persistent update complete. Revert DebugEnabled=false and DryRun=true before saving.");
        return true;
    }
}
