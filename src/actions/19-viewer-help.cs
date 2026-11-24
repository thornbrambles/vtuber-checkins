using System;
using System.Collections.Generic;
using System.Linq;

public class CPHInline
{
    public bool Execute()
    {
        // Only advertise this project's public commands, even if other commands are enabled.
        var enabled = new HashSet<string>(CPH.GetCommands()
            .Where(command => command != null && command.Enabled && command.Commands != null)
            .SelectMany(command => command.Commands), StringComparer.OrdinalIgnoreCase);

        var help = new List<string>();
        if (enabled.Contains("!checkins")) help.Add("!checkins — your check-ins, streak and game credits");
        if (enabled.Contains("!checkinboard")) help.Add("!checkinboard — top check-ins");
        if (enabled.Contains("!wheel")) help.Add("!wheel — your game picks; !wheel list — approved games");
        if (enabled.Contains("!addgame")) help.Add("!addgame <title> — submit a game for review");

        const string reward = "To check in, redeem 🌿 Stream Check-in with Channel Points.";
        CPH.SendMessage(help.Count == 0 ? reward : "Check-in commands: " + String.Join(" | ", help) + ". " + reward);
        return true;
    }
}
