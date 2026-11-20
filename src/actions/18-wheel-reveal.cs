using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Run every ~2 seconds on the same Blocking queue as the spin action.
public class CPHInline
{
    public bool Execute()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var variable in CPH.GetGlobalVarValues(true))
        {
            string key = variable.VariableName;
            if (key == null || !key.StartsWith("ci_wheel_spin_result_", StringComparison.Ordinal)) continue;
            try
            {
                var result = JObject.Parse(CPH.GetGlobalVar<string>(key, true));
                long due = (long?)result["revealAtMs"] ?? 0;
                // Legacy results predate this schedule. Do not announce historical winners.
                if (due <= 0 || due > now || (bool?)result["announced"] == true) continue;
                string month = (string)result["month"];
                string game = (string)result.SelectToken("winner.game");
                string user = (string)result.SelectToken("winner.user");
                if (String.IsNullOrWhiteSpace(month) || String.IsNullOrWhiteSpace(game))
                    throw new InvalidOperationException("Missing winning entry or month");
                // Mark before sending to retain the existing at-most-once behavior.
                result["announced"] = true;
                CPH.SetGlobalVar(key, result.ToString(Formatting.None), true);
                CPH.SendMessage("🎡 The " + month + " wheel picked '" + game + "'" +
                    (String.IsNullOrWhiteSpace(user) ? "!" : " (submitted by " + user + ")!"));
            }
            catch (Exception ex) { CPH.LogError("Wheel reveal " + key + ": " + ex.Message); }
        }
        return true;
    }
}
