// Minimal test doubles for offline C# syntax/type checking.
// These are NOT the real Streamer.bot assemblies and do not prove runtime API
// compatibility. Validate with actual Streamer.bot before production deployment.
using System;
using System.Collections.Generic;
using System.Linq;

public class GlobalVariableValue {
    public string VariableName { get; set; }
    public object Value { get; set; }
}

public class UserVariableValue<T> {
    public string VariableName { get; set; }
    public string UserId { get; set; }
    public string UserName { get; set; }
    public string UserLogin { get; set; }
    public T Value { get; set; }
    public DateTime LastWrite { get; set; }
}
public class TwitchUserInfo {
    public string UserId { get; set; }
    public string UserName { get; set; }
    public bool IsModerator { get; set; }
}

public class CommandData {
    public string Name { get; set; }
    public bool Enabled { get; set; }
    public List<string> Commands { get; set; }
}
public static class CPH {
    public static Dictionary<string, object> Globals = new Dictionary<string, object>();
    public static Dictionary<string, Dictionary<string, object>> Users = new Dictionary<string, Dictionary<string, object>>();
    public static Dictionary<string, object> Args = new Dictionary<string, object>();
    public static List<string> Fulfilled = new List<string>();
    public static List<string> Canceled = new List<string>();
    public static List<string> Messages = new List<string>();
    public static List<CommandData> Commands = new List<CommandData>();
    public static int FailAfterWrite = -1, Writes = 0;
    public static bool FulfillmentSucceeds = true;
    public static void Reset() {
        Globals.Clear(); Users.Clear(); Args.Clear(); Fulfilled.Clear(); Canceled.Clear(); Messages.Clear();
        Commands.Clear();
        FailAfterWrite = -1; Writes = 0; FulfillmentSucceeds = true;
    }
    private static void BeforeWrite() {
        if (Writes++ == FailAfterWrite) throw new InvalidOperationException("Injected write failure");
    }
    public static T GetGlobalVar<T>(string key, bool persisted=true) { return Globals.ContainsKey(key) ? (T)Globals[key] : default(T); }
    public static void SetGlobalVar(string key, object value, bool persisted=true) { BeforeWrite(); Globals[key] = value; }
    public static void UnsetGlobalVar(string key, bool persisted=true) { BeforeWrite(); Globals.Remove(key); }
    public static List<GlobalVariableValue> GetGlobalVarValues(bool persisted=true) {
        return Globals.Select(x => new GlobalVariableValue {VariableName=x.Key, Value=x.Value}).ToList();
    }

    public static T GetTwitchUserVarById<T>(string userId, string name, bool persisted=true) {
        return Users.ContainsKey(userId) && Users[userId].ContainsKey(name) ? (T)Users[userId][name] : default(T);
    }
    public static List<UserVariableValue<T>> GetTwitchUsersVar<T>(string name, bool persisted=true) {
        return Users.Where(u => u.Value.ContainsKey(name)).Select(u => new UserVariableValue<T> {
            UserId=u.Key, UserName=u.Key, UserLogin=u.Key, Value=(T)u.Value[name]
        }).ToList();
    }
    public static void SetTwitchUserVarById(string userId, string name, object value, bool persisted=true) {
        BeforeWrite();
        if (!Users.ContainsKey(userId)) Users[userId] = new Dictionary<string, object>();
        Users[userId][name] = value;
    }
    public static void UnsetTwitchUserVarById(string userId, string name, bool persisted=true) {
        BeforeWrite(); if (Users.ContainsKey(userId)) Users[userId].Remove(name);
    }

    public static bool TryGetArg<T>(string name, out T value) {
        value=Args.ContainsKey(name) ? (T)Args[name] : default(T); return Args.ContainsKey(name);
    }
    public static TwitchUserInfo TwitchGetUserInfoById(string id) { return new TwitchUserInfo {UserId=id, UserName=id, IsModerator=id=="mod"}; }
    public static TwitchUserInfo TwitchGetUserInfoByLogin(string name) {
        return name == "alice" ? new TwitchUserInfo {UserId="123", UserName="alice"} : null;
    }
    public static TwitchUserInfo TwitchGetBroadcaster() { return new TwitchUserInfo {UserId="broadcaster"}; }

    public static void SendMessage(string message) { Messages.Add(message); }
    public static void LogInfo(string message) {}
    public static void LogWarn(string message) {}
    public static void LogError(string message) {}
    public static bool TwitchRedemptionFulfill(string rewardId, string redemptionId) { Fulfilled.Add(redemptionId); return FulfillmentSucceeds; }
    public static bool TwitchRedemptionCancel(string rewardId, string redemptionId) { Canceled.Add(redemptionId); return true; }
    public static Func<string, bool> ActionRunner;
    public static bool RunAction(string name, bool runImmediately=true) { return ActionRunner != null && ActionRunner(name); }
    public static List<CommandData> GetCommands() { return Commands; }
}
