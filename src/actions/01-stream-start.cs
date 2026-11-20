using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;

public class CPHInline
{
    public bool Execute()
    {
        if (!String.IsNullOrWhiteSpace(CPH.GetGlobalVar<string>("ci_checkin_pending_json", true)))
        {
            CPH.LogError("Pending check-in must be recovered by CI - Viewer Check-in before changing accounting state.");
            return false;
        }
        // Twitch provides startedAt. Repeated online notifications for the same
        // Twitch broadcast must NOT create multiple logical streams.
        string key;
        DateTime started;
        if (CPH.TryGetArg("startedAt", out started) && started != default(DateTime))
            key = started.ToUniversalTime().ToString("o");
        else
            key = "manual-" + DateTime.UtcNow.ToString("o");

        string previousKey = CPH.GetGlobalVar<string>("ci_stream_key", true) ?? "";
        if (key == previousKey)
        {
            CPH.LogInfo("Check-in: ignored duplicate Stream Online event.");
            return true;
        }

        int streamId = CPH.GetGlobalVar<int>("ci_stream_id", true) + 1;
        CPH.SetGlobalVar("ci_stream_key", key, true);
        CPH.SetGlobalVar("ci_stream_id", streamId, true);
        CPH.SetGlobalVar("ci_stream_count", 0, true);
        CPH.SetGlobalVar("ci_last_checkin_at_ms", 0L, true);
        Publish(streamId, 0, "", "", 0, 0);
        CPH.LogInfo("Check-in: started logical stream " + streamId);
        return true;
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
