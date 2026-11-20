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
            CPH.LogWarn("Leave this redemption pending for manual review of its identity.");
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

        // Recover the global journal first, even when this event belongs to another viewer.
        // Other accounting writers refuse to run while it exists.
        try
        {
            string pending = CPH.GetGlobalVar<string>("ci_checkin_pending_json", true);
            if (!String.IsNullOrWhiteSpace(pending)) CompleteCheckin(JObject.Parse(pending));
            if (!String.IsNullOrWhiteSpace(CPH.GetGlobalVar<string>("ci_stream_start_pending_json", true)))
            {
                CPH.RunAction("CI - Start Stream", true);
                if (!String.IsNullOrWhiteSpace(CPH.GetGlobalVar<string>("ci_stream_start_pending_json", true)))
                    throw new InvalidOperationException("Pending stream transition could not be finished by CI - Start Stream");
            }
            if (!CPH.GetGlobalVar<bool>("ci_checkin_receipts_migrated", true))
                throw new InvalidOperationException("Run action 08 to migrate legacy redemption receipts before enabling check-ins");
            bool? awarded = RecordedOutcome(userId, redemptionId);
            if (awarded.HasValue)
            {
                if (awarded.Value) Fulfill(rewardId, redemptionId);
                else RetryCancellation(rewardId, redemptionId);
                return true;
            }
        }
        catch (Exception ex)
        {
            CPH.LogError("Check-in recovery/receipt validation failed: " + ex.Message);
            return false;
        }

        int streamId = CPH.GetGlobalVar<int>("ci_stream_id", true);
        if (streamId <= 0)
        {
            bool refunded = CancelRedemption("stream not initialized");
            CPH.SendMessage(refunded
                ? $"@{userName}, check-ins aren't open yet. Your points were refunded."
                : $"@{userName}, check-ins aren't open yet. Couldn't refund your points — please ask a mod.");
            return true;
        }

        int previous = CPH.GetTwitchUserVarById<int>(userId, "ci_last_stream", true);
        int lastRewardStream = CPH.GetTwitchUserVarById<int>(userId, "ci_reward_last_stream", true);
        if (previous == streamId || lastRewardStream == streamId)
        {
            bool refunded = CancelRedemption("duplicate check-in");
            CPH.SendMessage(refunded
                ? $"@{userName}, you're already checked in! Your points were refunded."
                : $"@{userName}, you're already checked in. Couldn't refund the extra redemption — please ask a mod.");
            return true;
        }

        int total = CPH.GetTwitchUserVarById<int>(userId, "ci_total", true) + 1;
        int oldStreak = CPH.GetTwitchUserVarById<int>(userId, "ci_streak", true);
        int streak = previous == streamId - 1 ? oldStreak + 1 : 1;
        int count = CPH.GetGlobalVar<int>("ci_stream_count", true) + 1;

        int usedCredits = Math.Max(0, CPH.GetTwitchUserVarById<int>(userId, "ci_wheel_claimed", true));
        int previousEarnedCredits = Math.Max(usedCredits, CPH.GetTwitchUserVarById<int>(userId, "ci_wheel_earned", true));
        int earnedCredits = Math.Max(previousEarnedCredits, total / 10);

        string unlocked = "";
        for (int i = 0; i < Thresholds.Length; i++)
        {
            if (total == Thresholds[i])
            {
                unlocked = Badges[i];
                break;
            }
        }

        var transaction = JObject.FromObject(new {
            userId, userName, rewardId, redemptionId, streamId, total, streak, count,
            earnedCredits, unlocked, atMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            month = DateTimeOffset.UtcNow.ToString("yyyy_MM")
        });
        // No attendance writes occur before this durable journal.
        CPH.SetGlobalVar("ci_checkin_pending_json", transaction.ToString(Formatting.None), true);
        CompleteCheckin(transaction);

        CPH.SendMessage($"🌿 @{userName} checked in! {total} total check-ins, a {streak}-stream streak, and {count} people checked in this stream.");

        if (unlocked != "")
            CPH.SendMessage($"🏆 @{userName} got the {unlocked} badge!");

        if (earnedCredits > previousEarnedCredits)
        {
            int availableCredits = Math.Max(0, earnedCredits - usedCredits);
            CPH.SendMessage($"🎮 @{userName} earned a game pick! {availableCredits} left. Use !addgame <title>.");
        }

        return true;
    }

    private bool? RecordedOutcome(string userId, string redemptionId)
    {
        // Rejection is saved before contacting Twitch and stays terminal across streams.
        if (!String.IsNullOrWhiteSpace(CPH.GetGlobalVar<string>("ci_checkin_rejected_" + redemptionId, true)))
            return false;
        // Backward compatibility for receipts created before this journal existed.
        if (CPH.GetTwitchUserVarById<string>(userId, "ci_last_redemption_id", true) == redemptionId)
            return true;
        foreach (var variable in CPH.GetGlobalVarValues(true))
        {
            string key = variable.VariableName ?? "";
            string raw;
            if (key.StartsWith("ci_checkin_receipts_month_", StringComparison.Ordinal))
            {
                raw = CPH.GetGlobalVar<string>(key, true);
                if (String.IsNullOrWhiteSpace(raw)) continue;
                var receipt = JObject.Parse(raw)[redemptionId];
                if (receipt != null)
                {
                    if ((string)receipt != userId) throw new InvalidOperationException("Redemption owner mismatch");
                    return true;
                }
            }
        }
        return null;
    }

    private void CompleteCheckin(JObject transaction)
    {
        string userId = (string)transaction["userId"];
        string redemptionId = (string)transaction["redemptionId"];
        string rewardId = (string)transaction["rewardId"];
        string month = (string)transaction["month"];
        if (String.IsNullOrWhiteSpace(userId) || String.IsNullOrWhiteSpace(redemptionId) ||
            String.IsNullOrWhiteSpace(rewardId) || !System.Text.RegularExpressions.Regex.IsMatch(month ?? "", @"^\d{4}_(0[1-9]|1[0-2])$"))
            throw new InvalidOperationException("Invalid pending check-in identity");
        int streamId = transaction["streamId"].Value<int>();
        int total = transaction["total"].Value<int>();
        int streak = transaction["streak"].Value<int>();
        int count = transaction["count"].Value<int>();
        int earned = transaction["earnedCredits"].Value<int>();
        long atMs = transaction["atMs"].Value<long>();
        if (streamId <= 0 || total <= 0 || streak <= 0 || count <= 0 || earned < 0 || atMs <= 0 ||
            streamId != CPH.GetGlobalVar<int>("ci_stream_id", true))
            throw new InvalidOperationException("Invalid pending check-in counters/stream");
        string receiptKey = "ci_checkin_receipts_month_" + month;
        string raw = CPH.GetGlobalVar<string>(receiptKey, true);
        var receipts = String.IsNullOrWhiteSpace(raw) ? new JObject() : JObject.Parse(raw);
        if (receipts[redemptionId] != null && (string)receipts[redemptionId] != userId)
            throw new InvalidOperationException("Redemption owner mismatch");
        CPH.SetTwitchUserVarById(userId, "ci_last_stream", streamId, true);
        CPH.SetTwitchUserVarById(userId, "ci_reward_last_stream", streamId, true);
        CPH.SetTwitchUserVarById(userId, "ci_last_redemption_id", redemptionId, true);
        CPH.SetTwitchUserVarById(userId, "ci_total", total, true);
        CPH.SetTwitchUserVarById(userId, "ci_streak", streak, true);
        CPH.SetTwitchUserVarById(userId, "ci_wheel_earned", earned, true);
        CPH.SetGlobalVar("ci_stream_count", count, true);
        string unlocked = (string)transaction["unlocked"];
        if (!String.IsNullOrWhiteSpace(unlocked)) CPH.SetTwitchUserVarById(userId, "ci_badge", unlocked, true);
        AppendAudit(new {
            id = "checkin-" + redemptionId, type = "CHECKIN",
            atUtc = DateTimeOffset.FromUnixTimeMilliseconds(atMs).ToString("o"),
            userId, userName = (string)transaction["userName"], streamId,
            totalAfter = total, streakAfter = streak, creditEarned = earned
        }, "checkin-" + redemptionId, month);
        CPH.SetGlobalVar("ci_last_checkin_at_ms", atMs, true);
        receipts[redemptionId] = userId;
        CPH.SetGlobalVar(receiptKey, receipts.ToString(Formatting.None), true);
        CPH.UnsetGlobalVar("ci_checkin_pending_json", true);
        Publish(streamId, count, (string)transaction["userName"], unlocked, total, streak);
        Fulfill(rewardId, redemptionId);
    }

    private void Fulfill(string rewardId, string redemptionId)
    {
        if (!CPH.TwitchRedemptionFulfill(rewardId, redemptionId))
            CPH.LogWarn("Check-in recorded but could not fulfill redemption " + redemptionId + ". Inspect the Twitch queue.");
    }

    // Audit is derived history, not an atomic database transaction. Counters
    // remain the primary state; reconciliation detects missing events.
    private void AppendAudit(object record, string eventId, string month)
    {
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
            CPH.SetGlobalVar("ci_checkin_rejected_" + redemptionId, reason, true);
            return RetryCancellation(rewardId, redemptionId);
        }
        return false;
    }

    private bool RetryCancellation(string rewardId, string redemptionId)
    {
        bool canceled = CPH.TwitchRedemptionCancel(rewardId, redemptionId);
        if (!canceled) CPH.LogWarn("Unable to cancel redemption " + redemptionId + ". Check Twitch redemption queue.");
        return canceled;
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
