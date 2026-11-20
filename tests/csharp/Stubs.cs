// Minimal test doubles for offline C# syntax/type checking.
// These are NOT the real Streamer.bot assemblies and do not prove runtime API
// compatibility. Validate with actual Streamer.bot before production deployment.
using System;
using System.Collections.Generic;

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
    public static T GetGlobalVar<T>(string key, bool persisted=true) { return default(T); }
    public static void SetGlobalVar(string key, object value, bool persisted=true) {}
    public static void UnsetGlobalVar(string key, bool persisted=true) {}
    public static List<GlobalVariableValue> GetGlobalVarValues(bool persisted=true) { return new List<GlobalVariableValue>(); }

    public static T GetTwitchUserVarById<T>(string userId, string name, bool persisted=true) { return default(T); }
    public static List<UserVariableValue<T>> GetTwitchUsersVar<T>(string name, bool persisted=true) { return new List<UserVariableValue<T>>(); }
    public static void SetTwitchUserVarById(string userId, string name, object value, bool persisted=true) {}
    public static void UnsetTwitchUserVarById(string userId, string name, bool persisted=true) {}

    public static bool TryGetArg<T>(string name, out T value) { value=default(T); return false; }
    public static TwitchUserInfo TwitchGetUserInfoById(string id) { return null; }
    public static TwitchUserInfo TwitchGetUserInfoByLogin(string name) { return null; }
    public static TwitchUserInfo TwitchGetBroadcaster() { return null; }

    public static void SendMessage(string message) {}
    public static void LogInfo(string message) {}
    public static void LogWarn(string message) {}
    public static void LogError(string message) {}
    public static bool TwitchRedemptionFulfill(string rewardId, string redemptionId) { return true; }
    public static bool TwitchRedemptionCancel(string rewardId, string redemptionId) { return true; }
    public static bool RunAction(string name, bool runImmediately=true) { return true; }
    public static List<CommandData> GetCommands() { return new List<CommandData>(); }
}
