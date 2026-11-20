using System;
using Newtonsoft.Json.Linq;

public class CPHInline
{
    // Moderator/broadcaster only. Command is !checkindiag (Exact).
    // Runs the read-only diagnostics action inline and provides a compact
    // Twitch chat summary. Does NOT expose individual viewer IDs in chat.
    public bool Execute()
    {
        string actorId, actor;
        if (!CPH.TryGetArg("userId",out actorId) ||
            !CPH.TryGetArg("userName",out actor) ||
            String.IsNullOrWhiteSpace(actorId))
            return false;
        var broadcaster=CPH.TwitchGetBroadcaster();
        var viewer=CPH.TwitchGetUserInfoById(actorId);
        bool allowed=(broadcaster!=null && broadcaster.UserId==actorId) ||
                     (viewer!=null && viewer.IsModerator);
        if (!allowed) {
            CPH.LogWarn("CHECKIN DIAGNOSTICS denied for "+actorId);
            return false;
        }
        CPH.RunAction("CI - Diagnostics", true);
        string json=CPH.GetGlobalVar<string>("ci_diagnostics_json",true);
        if(String.IsNullOrWhiteSpace(json)) {
            CPH.SendMessage("Check-in diagnostics did not produce a report. Check Streamer.bot logs and CI - Diagnostics action name.");
            return false;
        }
        try {
            var data=JObject.Parse(json);
            int errors=(int?)data["errors"]??0;
            int warnings=(int?)data["warnings"]??0;
            int viewers=(int?)data["viewerCount"]??0;
            string state=errors>0?"ERROR":warnings>0?"WARNING":"OK";
            CPH.SendMessage($"🔎 Check-in diagnostics: {state}; {errors} errors, {warnings} warnings; {viewers} viewers. See Streamer.bot log for details.");
            return errors==0;
        }
        catch(Exception ex) {
            CPH.LogError("CHECKIN DIAGNOSTICS report invalid: "+ex.Message);
            return false;
        }
    }
}
