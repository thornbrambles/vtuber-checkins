using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class CPHInline
{
    // Manual-only action. No Twitch trigger or chat command.
    // Every persisted global starting "ci_" is included. Twitch user variables
    // are enumerated from the documented list of names below.
    private static readonly string[] IntUserKeys = {
        "ci_total", "ci_streak", "ci_last_stream", "ci_wheel_claimed",
        "ci_wheel_earned", "ci_reward_last_stream"
    };
    private static readonly string[] StringUserKeys = {
        "ci_badge", "ci_last_redemption_id", "ci_wheel_pending_json"
    };
    private const int SchemaVersion = 1;

    public bool Execute()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "vtuber-checkins-backups");
        Directory.CreateDirectory(folder);

        var globals = new JObject();
        foreach (var variable in CPH.GetGlobalVarValues(true))
        {
            if (variable == null || String.IsNullOrWhiteSpace(variable.VariableName) ||
                !variable.VariableName.StartsWith("ci_", StringComparison.Ordinal))
                continue;
            globals[variable.VariableName] = variable.Value == null
                ? JValue.CreateNull() : JToken.FromObject(variable.Value);
        }

        var users = new Dictionary<string, JObject>(StringComparer.Ordinal);
        foreach (string key in IntUserKeys)
        {
            foreach (var record in CPH.GetTwitchUsersVar<int>(key, true))
                Put(users, record.UserId, record.UserName, record.UserLogin, key, record.Value);
        }
        foreach (string key in StringUserKeys)
        {
            foreach (var record in CPH.GetTwitchUsersVar<string>(key, true))
                Put(users, record.UserId, record.UserName, record.UserLogin, key, record.Value);
        }

        var payload = new JObject {
            ["format"] = "vtuber-checkins-backup",
            ["schemaVersion"] = SchemaVersion,
            ["createdAtUtc"] = DateTimeOffset.UtcNow.ToString("o"),
            ["source"] = "Streamer.bot persisted check-in data",
            ["globals"] = globals,
            ["users"] = new JArray(users.Values.OrderBy(x => (string)x["userId"]))
        };
        string json = payload.ToString(Formatting.Indented);
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        string output = Path.Combine(folder, "checkins-" + stamp + ".json");
        // Unique filename; never overwrite an existing backup.
        using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file, System.Text.Encoding.UTF8))
            writer.Write(json);

        CPH.LogInfo($"CHECKIN BACKUP: {users.Count} viewers and {globals.Count} globals exported to {output}");
        CPH.LogInfo("Treat exported JSON as PRIVATE viewer data. Do not commit the file to GitHub.");
        return true;
    }

    private static void Put(Dictionary<string, JObject> users, string id, string name, string login, string key, object value)
    {
        if (String.IsNullOrWhiteSpace(id)) return;
        JObject user;
        if (!users.TryGetValue(id, out user))
        {
            user = new JObject {
                ["userId"] = id,
                ["userName"] = name ?? "",
                ["userLogin"] = login ?? "",
                ["vars"] = new JObject()
            };
            users[id] = user;
        }
        ((JObject)user["vars"])[key] = value == null
            ? JValue.CreateNull() : JToken.FromObject(value);
    }
}
