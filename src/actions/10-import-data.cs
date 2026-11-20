using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;

public class CPHInline
{
    // MANUAL-ONLY action: do not attach Twitch/chat/stream triggers.
    // Review the JSON and export current state FIRST.
    // Edit these two switches in Streamer.bot and recompile to restore.
    private const bool ApplyImport = false;          // default: DRY RUN
    private const bool OverwriteExisting = false;   // must explicitly opt in
    private const string ImportFilename = "import.json";

    private static readonly HashSet<string> IntUserKeys = new HashSet<string> {
        "ci_total", "ci_streak", "ci_last_stream", "ci_wheel_claimed",
        "ci_wheel_earned", "ci_reward_last_stream"
    };
    private static readonly HashSet<string> StringUserKeys = new HashSet<string> {
        "ci_badge", "ci_last_redemption_id", "ci_wheel_pending_json"
    };
    private static readonly HashSet<string> IntGlobalKeys = new HashSet<string> {
        "ci_stream_id", "ci_stream_count", "ci_schema_version"
    };
    private static readonly HashSet<string> LongGlobalKeys = new HashSet<string> {
        "ci_last_checkin_at_ms"
    };
    private static readonly HashSet<string> BoolGlobalKeys = new HashSet<string> {
        "ci_wheel_legacy_migrated"
    };

    public bool Execute()
    {
        if (!String.IsNullOrWhiteSpace(CPH.GetGlobalVar<string>("ci_checkin_pending_json", true)))
        {
            CPH.LogError("Pending check-in must be recovered by CI - Viewer Check-in before changing accounting state.");
            return false;
        }
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "vtuber-checkins-backups", ImportFilename);
        if (!File.Exists(path))
        {
            CPH.LogError("CHECKIN IMPORT: file not found: " + path);
            return false;
        }

        // Fail closed on unexpectedly large or malformed files.
        if (new FileInfo(path).Length > 20 * 1024 * 1024)
        {
            CPH.LogError("CHECKIN IMPORT: file is larger than 20 MiB; refused.");
            return false;
        }

        string checksumPath = path + ".sha256";
        if (File.Exists(checksumPath))
        {
            string sidecar = File.ReadAllText(checksumPath).Trim();
            string expected = sidecar.Split(new[]{' ', '\t'}, StringSplitOptions.RemoveEmptyEntries)[0];
            string actual;
            using (var sha = SHA256.Create())
            using (var input = File.OpenRead(path))
                actual = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
            if (!String.Equals(expected,actual,StringComparison.OrdinalIgnoreCase))
            {
                CPH.LogError("CHECKIN IMPORT refused: SHA-256 checksum mismatch.");
                return false;
            }
        }

        JObject root;
        try { root = JObject.Parse(File.ReadAllText(path)); }
        catch (Exception ex) { CPH.LogError("CHECKIN IMPORT invalid JSON: " + ex.Message); return false; }

        if ((string)root["format"] != "vtuber-checkins-backup" ||
            (int?)root["schemaVersion"] != 1 ||
            !(root["globals"] is JObject) ||
            !(root["users"] is JArray))
        {
            CPH.LogError("CHECKIN IMPORT: wrong format/version. Expected vtuber-checkins-backup schemaVersion 1.");
            return false;
        }

        var globals = (JObject)root["globals"];
        var users = (JArray)root["users"];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var globalWrites = new Dictionary<string, object>(StringComparer.Ordinal);
        var userWrites = new List<Tuple<string, string, object>>();
        try
        {
            foreach (var prop in globals.Properties())
            {
                if (!prop.Name.StartsWith("ci_", StringComparison.Ordinal))
                    throw new InvalidDataException("Unexpected global variable name: " + prop.Name);
                // Do not import debugging controls or a pending transaction from elsewhere.
                if (prop.Name == "ci_debug_enabled")
                    throw new InvalidDataException("Debug controls must not be imported.");
                if (prop.Value.Type == JTokenType.Null)
                    continue;

                object value;
                if (IntGlobalKeys.Contains(prop.Name))
                    value = ReadNonnegativeInt(prop.Value, prop.Name);
                else if (LongGlobalKeys.Contains(prop.Name))
                    value = ReadNonnegativeLong(prop.Value, prop.Name);
                else if (BoolGlobalKeys.Contains(prop.Name))
                {
                    if (prop.Value.Type != JTokenType.Boolean) throw new InvalidDataException(prop.Name + " must be boolean");
                    value = (bool)prop.Value;
                }
                else
                {
                    if (prop.Value.Type != JTokenType.String) throw new InvalidDataException(prop.Name + " must be a string");
                    value = (string)prop.Value;
                }
                globalWrites.Add(prop.Name, value);
            }

            foreach (var item in users)
            {
                if (!(item is JObject)) throw new InvalidDataException("Each user must be an object");
                var user = (JObject)item;
                string id = (string)user["userId"];
                if (String.IsNullOrWhiteSpace(id) || !id.All(char.IsDigit) || !ids.Add(id))
                    throw new InvalidDataException("Invalid or repeated Twitch userId");
                if (!(user["vars"] is JObject)) throw new InvalidDataException("Missing user vars for " + id);
                foreach (var prop in ((JObject)user["vars"]).Properties())
                {
                    if (prop.Value.Type == JTokenType.Null) continue;
                    object value;
                    if (IntUserKeys.Contains(prop.Name))
                        value = ReadNonnegativeInt(prop.Value, prop.Name);
                    else if (StringUserKeys.Contains(prop.Name))
                    {
                        if (prop.Value.Type != JTokenType.String) throw new InvalidDataException(prop.Name + " must be a string");
                        value = (string)prop.Value;
                    }
                    else throw new InvalidDataException("Unknown user variable: " + prop.Name);
                    userWrites.Add(Tuple.Create(id, prop.Name, value));
                }
            }
        }
        catch (Exception ex)
        {
            CPH.LogError("CHECKIN IMPORT validation failed, nothing imported: " + ex.Message);
            return false;
        }

        // Check for any currently set variable before writing. No partial restore on
        // conflicts. Restore into empty state OR explicitly allow overwriting.
        int conflicts = 0;
        var existingGlobals = new HashSet<string>(
            CPH.GetGlobalVarValues(true).Select(x => x.VariableName), StringComparer.Ordinal);
        conflicts += globalWrites.Keys.Count(existingGlobals.Contains);

        foreach (string key in IntUserKeys)
        {
            var saved = new HashSet<string>(
                CPH.GetTwitchUsersVar<int>(key, true).Select(x => x.UserId), StringComparer.Ordinal);
            conflicts += userWrites.Count(x => x.Item2 == key && saved.Contains(x.Item1));
        }
        foreach (string key in StringUserKeys)
        {
            var saved = new HashSet<string>(
                CPH.GetTwitchUsersVar<string>(key, true).Select(x => x.UserId), StringComparer.Ordinal);
            conflicts += userWrites.Count(x => x.Item2 == key && saved.Contains(x.Item1));
        }

        CPH.LogInfo($"CHECKIN IMPORT validation passed: {globalWrites.Count} globals, {userWrites.Count} user variables for {ids.Count} viewers; {conflicts} conflicts. ApplyImport={ApplyImport}; OverwriteExisting={OverwriteExisting}");
        if (!ApplyImport)
        {
            CPH.LogInfo("CHECKIN IMPORT DRY RUN ONLY. Make a fresh backup first; set ApplyImport=true and recompile to restore.");
            return true;
        }
        if (conflicts > 0 && !OverwriteExisting)
        {
            CPH.LogError("CHECKIN IMPORT refused: existing data would be overwritten. Export current state, review backup, then explicitly set OverwriteExisting=true.");
            return false;
        }

        // Individual Streamer.bot variable writes are not transactional.
        // Do not import during stream or while other actions are running.
        try
        {
            foreach (var item in userWrites)
                CPH.SetTwitchUserVarById(item.Item1, item.Item2, item.Item3, true);
            foreach (var item in globalWrites)
                CPH.SetGlobalVar(item.Key, item.Value, true);
        }
        catch (Exception ex)
        {
            CPH.LogError("CHECKIN IMPORT partly applied! Restore your pre-import backup after resolving error: " + ex.Message);
            return false;
        }
        CPH.LogInfo("CHECKIN IMPORT applied successfully. Revert ApplyImport=false and recompile immediately.");
        return true;
    }

    private static int ReadNonnegativeInt(JToken t, string name)
    {
        if (t.Type != JTokenType.Integer) throw new InvalidDataException(name + " must be integer");
        long n = (long)t;
        if (n < 0 || n > 1000000000L) throw new InvalidDataException(name + " out of range");
        return (int)n;
    }
    private static long ReadNonnegativeLong(JToken t, string name)
    {
        if (t.Type != JTokenType.Integer) throw new InvalidDataException(name + " must be integer");
        long n = (long)t;
        if (n < 0) throw new InvalidDataException(name + " must be nonnegative");
        return n;
    }
}
