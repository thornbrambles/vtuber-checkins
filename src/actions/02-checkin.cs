using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class CPHInline
{
    // Adjust thresholds/names below. Rewards are permanent earned badges,
    // chat celebrations and OBS toast notifications, NOT Twitch Channel Points.
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
        string userId, userName;
        if (!CPH.TryGetArg("userId", out userId) || String.IsNullOrWhiteSpace(userId) ||
            !CPH.TryGetArg("userName", out userName) || String.IsNullOrWhiteSpace(userName))
        {
            CPH.LogWarn("Check-in redemption: missing Twitch userId/userName. Check the selected Reward Redemption trigger.");
            CancelRedemption("missing viewer identity");
            return false;
        }

        // This action is intended ONLY for the specific Stream Check-in reward.
        // The trigger's reward selector must NOT be set to Any.
        string redemptionId, rewardId;
        if (!CPH.TryGetArg("redemptionId", out redemptionId) || String.IsNullOrWhiteSpace(redemptionId) ||
            !CPH.TryGetArg("rewardId", out rewardId) || String.IsNullOrWhiteSpace(rewardId))
        {
            CPH.LogWarn("Check-in: no redemptionId/rewardId. The old !checkin command trigger must be removed.");
            return false;
        }

        int streamId = CPH.GetGlobalVar<int>("ci_stream_id", true);
        if (streamId <= 0)
        {
            bool refunded = CancelRedemption("stream not initialized");
            CPH.SendMessage(refunded
                ? $"🌿 @{userName}, your check-in was NOT recorded because the stream has not started. Your Channel Points redemption was canceled for a refund."
                : $"⚠️ @{userName}, your check-in was NOT recorded because the stream has not started. The refund could not be confirmed; please contact a moderator.");
            return true;
        }

        int previous = CPH.GetTwitchUserVarById<int>(userId, "ci_last_stream", true);
        int lastRewardStream = CPH.GetTwitchUserVarById<int>(userId, "ci_reward_last_stream", true);
        if (previous == streamId || lastRewardStream == streamId)
        {
            string lastRedemptionId = CPH.GetTwitchUserVarById<string>(userId, "ci_last_redemption_id", true);
            if (lastRedemptionId == redemptionId)
            {
                // The same redemption may be replayed if fulfillment previously failed.
                // Do NOT refund an already-recorded successful check-in.
                if (!CPH.TwitchRedemptionFulfill(rewardId, redemptionId))
                    CPH.LogWarn("Previously recorded redemption " + redemptionId + " could not be fulfilled. Inspect the queue.");
                return true;
            }
            bool refunded = CancelRedemption("duplicate check-in");
            CPH.SendMessage(refunded
                ? $"🌿 @{userName}, you are already checked in for this stream. No extra check-in was added; this duplicate redemption was canceled for a refund."
                : $"⚠️ @{userName}, you are already checked in for this stream. No extra check-in was added, but the refund could not be confirmed. Please contact a moderator.");
            return true;
        }

        int total = CPH.GetTwitchUserVarById<int>(userId, "ci_total", true) + 1;
        int oldStreak = CPH.GetTwitchUserVarById<int>(userId, "ci_streak", true);
        int streak = previous == streamId - 1 ? oldStreak + 1 : 1;
        int count = CPH.GetGlobalVar<int>("ci_stream_count", true) + 1;

        CPH.SetTwitchUserVarById(userId, "ci_last_stream", streamId, true);
        // Immutable once-per-stream redemption marker; admin attendance resets do not clear it.
        CPH.SetTwitchUserVarById(userId, "ci_reward_last_stream", streamId, true);
        CPH.SetTwitchUserVarById(userId, "ci_last_redemption_id", redemptionId, true);
        CPH.SetTwitchUserVarById(userId, "ci_total", total, true);
        CPH.SetTwitchUserVarById(userId, "ci_streak", streak, true);
        int usedCredits = Math.Max(0, CPH.GetTwitchUserVarById<int>(userId, "ci_wheel_claimed", true));
        int previousEarnedCredits = Math.Max(usedCredits, CPH.GetTwitchUserVarById<int>(userId, "ci_wheel_earned", true));
        int earnedCredits = Math.Max(previousEarnedCredits, total / 10);
        CPH.SetTwitchUserVarById(userId, "ci_wheel_earned", earnedCredits, true);
        CPH.SetGlobalVar("ci_stream_count", count, true);

        string unlocked = "";
        for (int i = 0; i < Thresholds.Length; i++)
        {
            if (total == Thresholds[i])
            {
                unlocked = Badges[i];
                CPH.SetTwitchUserVarById(userId, "ci_badge", unlocked, true);
                break;
            }
        }

        string streakText = streak == 1 ? "a new streak started" : streak + "-stream streak";
        string welcome = total == 1 ? "Welcome to the Grove!" : "You're checked in!";
        CPH.SendMessage($"✅ 🌿 @{userName} — {welcome} Check-in #{total} • {streakText} • {count} viewer(s) checked in this stream. Your next step: !checkins for your progress.");

        if (unlocked != "")
            CPH.SendMessage($"🏆 Milestone unlocked! @{userName} earned the {unlocked} badge for {total} total check-ins!");

        if (earnedCredits > previousEarnedCredits)
        {
            int availableCredits = Math.Max(0, earnedCredits - usedCredits);
            CPH.SendMessage($"🎮 Game wheel credit earned! @{userName} now has {availableCredits} available. Submit a game with !game Game Title.");
        }

        AppendAudit(new {
            id="checkin-"+redemptionId, type="CHECKIN", atUtc=DateTimeOffset.UtcNow.ToString("o"),
            userId, userName, streamId, totalAfter=total, streakAfter=streak,
            creditEarned=earnedCredits
        }, "checkin-"+redemptionId);
        CPH.SetGlobalVar("ci_last_checkin_at_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true);
        Publish(streamId, count, userName, unlocked, total, streak);
        if (!CPH.TwitchRedemptionFulfill(rewardId, redemptionId))
            CPH.LogWarn("Check-in recorded, but Twitch could not automatically fulfill redemption " + redemptionId +
                        ". Check the Twitch reward queue manually; do NOT redeem again to fix it.");
        return true;
    }

    // Audit is derived history, not an atomic database transaction. Counters
    // remain the primary state; reconciliation detects missing events.
    private void AppendAudit(object record, string eventId)
    {
        string month = DateTimeOffset.UtcNow.ToString("yyyy_MM");
        string key = "ci_events_month_" + month;
        try
        {
            string raw = CPH.GetGlobalVar<string>(key, true);
            var events = String.IsNullOrWhiteSpace(raw) ? new JArray() : JArray.Parse(raw);
            if (!events.OfType<JObject>().Any(e => (string)e["id"] == eventId))
            {
                events.Add(JObject.FromObject(record));
                CPH.SetGlobalVar(key, events.ToString(Formatting.None), true);
            }
        }
        catch (Exception ex) { CPH.LogError("CHECKIN AUDIT FAILED: " + ex.Message); }
    }

    private bool CancelRedemption(string reason)
    {
        string rewardId, redemptionId;
        if (CPH.TryGetArg("rewardId", out rewardId) && !String.IsNullOrWhiteSpace(rewardId) &&
            CPH.TryGetArg("redemptionId", out redemptionId) && !String.IsNullOrWhiteSpace(redemptionId))
        {
            bool canceled = CPH.TwitchRedemptionCancel(rewardId, redemptionId);
            if (!canceled)
                CPH.LogWarn("Unable to cancel redemption " + redemptionId + " (" + reason + "). Check Twitch redemption queue.");
            return canceled;
        }
        return false;
    }

    private void Publish(int streamId, int count, string latest, string milestone, int total, int streak)
    {
        var totals = CPH.GetTwitchUsersVar<int>("ci_total", true);
        var streaks = CPH.GetTwitchUsersVar<int>("ci_streak", true);
        var last = CPH.GetTwitchUsersVar<int>("ci_last_stream", true)
                      .ToDictionary(x => x.UserId, x => x.Value);

        var topTotals = totals.OrderByDescending(x => x.Value)
                              .ThenBy(x => x.UserName)
                              .Take(5)
                              .Select(x => new { name = x.UserName, value = x.Value }).ToList();
        var topStreaks = streaks.Where(x => last.ContainsKey(x.UserId) && last[x.UserId] >= streamId - 1)
                                .OrderByDescending(x => x.Value)
                                .ThenBy(x => x.UserName)
                                .Take(5)
                                .Select(x => new { name = x.UserName, value = x.Value }).ToList();
        var state = new { streamId, count, latest, milestone, total, streak,
                          eventId = Guid.NewGuid().ToString("N"),
                          lastCheckinAtMs = CPH.GetGlobalVar<long>("ci_last_checkin_at_ms", true),
                          topTotals, topStreaks };
        CPH.SetGlobalVar("ci_overlay_json", JsonConvert.SerializeObject(state), true);
    }
}
