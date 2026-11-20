using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class CPHInline
{
    private const string PendingKey = "ci_stream_start_pending_json";

    public bool Execute()
    {
        // An inline retry from the redemption action only completes the saved transition.
        string redemptionId;
        bool resumeOnly = CPH.TryGetArg("redemptionId", out redemptionId) && !String.IsNullOrWhiteSpace(redemptionId);
        DateTime started;
        string key = resumeOnly ? null : CPH.TryGetArg("startedAt", out started) && started != default(DateTime)
            ? started.ToUniversalTime().ToString("o") : "manual-" + DateTime.UtcNow.ToString("o");
        string raw = CPH.GetGlobalVar<string>(PendingKey, true);
        JObject pending = null;
        if (!String.IsNullOrWhiteSpace(raw))
        {
            // Keep the Twitch timestamp key byte-for-byte across journal replays.
            using (var reader = new JsonTextReader(new System.IO.StringReader(raw)))
            {
                reader.DateParseHandling = DateParseHandling.None;
                pending = JObject.Load(reader);
            }
        }
        bool blocked = !String.IsNullOrWhiteSpace(CPH.GetGlobalVar<string>("ci_checkin_pending_json", true));
        if (pending != null && !blocked) Finish(pending);
        if (resumeOnly || (pending != null && key.StartsWith("manual-", StringComparison.Ordinal))) return !blocked;

        string previous = blocked && pending != null ? (string)pending["key"] : CPH.GetGlobalVar<string>("ci_stream_key", true);
        if (key == previous || (previous != null && !previous.StartsWith("manual-", StringComparison.Ordinal) &&
            !key.StartsWith("manual-", StringComparison.Ordinal) && String.CompareOrdinal(key, previous) < 0))
            return !blocked;

        int streamId = blocked && pending != null ? checked((int)pending["streamId"] + 1)
            : checked(CPH.GetGlobalVar<int>("ci_stream_id", true) + 1);
        pending = JObject.FromObject(new { key, streamId });
        CPH.SetGlobalVar(PendingKey, pending.ToString(Formatting.None), true);
        if (blocked)
        {
            CPH.LogWarn("Stream start saved and will resume after the pending check-in.");
            return false;
        }
        Finish(pending);
        return true;
    }

    private void Finish(JObject pending)
    {
        string key = (string)pending["key"];
        int streamId = (int)pending["streamId"];
        int current = CPH.GetGlobalVar<int>("ci_stream_id", true);
        if (String.IsNullOrWhiteSpace(key) || streamId <= 0 || streamId < current)
            throw new InvalidOperationException("Invalid pending stream transition");
        CPH.SetGlobalVar("ci_stream_id", streamId, true);
        CPH.SetGlobalVar("ci_stream_count", 0, true);
        CPH.SetGlobalVar("ci_last_checkin_at_ms", 0L, true);
        Publish(streamId, 0, "", "", 0, 0);
        CPH.SetGlobalVar("ci_stream_key", key, true);
        CPH.UnsetGlobalVar(PendingKey, true);
        CPH.LogInfo("Check-in: started logical stream " + streamId);
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
