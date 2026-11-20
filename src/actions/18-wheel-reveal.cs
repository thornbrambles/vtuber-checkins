using System;
using Newtonsoft.Json.Linq;

// Install as "CI - Wheel Reveal" in the Community Checkins BLOCKING queue.
// Run with a local timed trigger every ~2 seconds. NO chat command or Twitch
// trigger. A winner is announced only after its preselected animation finishes.
public class CPHInline
{
    public bool Execute()
    {
        string text = CPH.GetGlobalVar<string>("ci_wheel_spin_overlay_json", true);
        if (String.IsNullOrWhiteSpace(text)) return true;
        JObject state;
        try { state = JObject.Parse(text); }
        catch(Exception ex) { CPH.LogError("Wheel reveal: malformed overlay: " + ex.Message); return false; }

        if ((bool?)state["hidden"] == true || (bool?)state["announce"] != true) return true;
        string spinId = (string)state["spinId"];
        string month = (string)state["month"];
        if (String.IsNullOrWhiteSpace(spinId) || String.IsNullOrWhiteSpace(month)) return false;
        long start = (long?)state["startedAtMs"] ?? 0;
        long duration = (long?)state["durationMs"] ?? 0;
        long lifetime = (long?)state["visibleMs"] ?? 0;
        long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start;
        if (start <= 0 || duration <= 0 || lifetime < duration ||
            elapsed < duration || elapsed > lifetime + 5000) return true;

        // Refuse to announce a result that no longer matches the saved
        // authoritative draw. A replay never sets announce=true.
        var savedRaw = CPH.GetGlobalVar<string>("ci_wheel_spin_result_" + month.Replace('-', '_'), true);
        if (String.IsNullOrWhiteSpace(savedRaw)) return false;
        JObject saved;
        try { saved = JObject.Parse(savedRaw); }
        catch(Exception ex) { CPH.LogError("Wheel reveal: malformed result: " + ex.Message); return false; }
        if ((string)saved["spinId"] != spinId) return false;

        string key = "ci_wheel_spin_last_announced_id";
        if (CPH.GetGlobalVar<string>(key, true) == spinId) return true;
        var winner = saved["winner"];
        string game = (string)(winner == null ? null : winner["game"]);
        string user = (string)(winner == null ? null : winner["user"]);
        if (String.IsNullOrWhiteSpace(game)) return false;

        // At-most-once announcement: mark before chat. A crash between writes
        // can omit the chat message, but cannot produce repeated announcements.
        CPH.SetGlobalVar(key, spinId, true);
        CPH.SendMessage("🎡 The " + month + " wheel picked '" + game + "'" +
            (String.IsNullOrWhiteSpace(user) ? "!" : " (submitted by " + user + ")!"));
        return true;
    }
}
