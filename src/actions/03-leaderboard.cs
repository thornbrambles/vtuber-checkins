using System;
using System.Linq;

public class CPHInline
{
    public bool Execute()
    {
        var all = CPH.GetTwitchUsersVar<int>("ci_total", true)
                     .Where(x => x.Value > 0)
                     .OrderByDescending(x => x.Value)
                     .ThenBy(x => x.UserName)
                     .Take(5)
                     .Select((x, i) => $"{i + 1}. {x.UserName} ({x.Value})")
                     .ToArray();
        CPH.SendMessage(all.Length == 0
            ? "No check-ins recorded yet! Redeem the Stream Check-in Channel Points reward to be first."
            : "🏆 All-time check-ins: " + string.Join(" | ", all));
        return true;
    }
}
