using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class RuntimeTests
{
    private static void Assert(bool condition, string message) {
        if (!condition) throw new Exception(message);
    }
    private static void Setup() {
        CPH.Reset();
        CPH.ActionRunner = name => name == "CI - Start Stream" && new Action01().Execute();
        CPH.Globals["ci_checkin_receipts_migrated"] = true;
        CPH.Globals["ci_stream_id"] = 7;
        CPH.Globals["ci_stream_count"] = 0;
        CPH.Users["123"] = new Dictionary<string, object> {
            {"ci_total", 9}, {"ci_streak", 1}, {"ci_last_stream", 6}
        };
        Redeem("R1");
    }
    private static void Redeem(string id) {
        CPH.Args["userId"] = "123"; CPH.Args["userName"] = "alice";
        CPH.Args["rewardId"] = "reward"; CPH.Args["redemptionId"] = id;
    }
    private static void CheckinRetries() {
        Setup();
        Assert(new Action02().Execute(), "first check-in failed");
        Assert(new Action02().Execute(), "same-stream retry failed");
        CPH.Globals["ci_stream_id"] = 8; CPH.Globals["ci_stream_count"] = 0;
        Assert(new Action02().Execute(), "cross-stream retry failed");
        Assert(CPH.GetTwitchUserVarById<int>("123", "ci_total") == 10, "retry changed total");
        Assert(CPH.GetGlobalVar<int>("ci_stream_count") == 0, "retry changed next stream count");
        Redeem("R2"); Assert(new Action02().Execute(), "new redemption failed");
        // Clear derived history and overwrite the last ID: durable receipts must suffice.
        foreach (var key in CPH.Globals.Keys.Where(k => k.StartsWith("ci_events_month_")).ToList()) CPH.Globals.Remove(key);
        CPH.Globals["ci_stream_id"] = 9; CPH.Globals["ci_stream_count"] = 0;
        Redeem("R1"); Assert(new Action02().Execute(), "older retry failed");
        Assert(CPH.GetTwitchUserVarById<int>("123", "ci_total") == 11, "older retry counted again");
        Assert(CPH.GetTwitchUserVarById<int>("123", "ci_wheel_earned") == 1, "retry minted credit");

        Setup();
        CPH.Users["123"]["ci_last_redemption_id"] = "R1";
        Assert(new Action02().Execute(), "legacy last-ID receipt failed");
        Assert(CPH.GetTwitchUserVarById<int>("123", "ci_total") == 9, "legacy ID counted again");
        Setup();
        CPH.Globals.Remove("ci_checkin_receipts_migrated");
        CPH.Globals["ci_events_month_2026_01"] = "[{\"type\":\"CHECKIN\",\"id\":\"checkin-R1\",\"userId\":\"123\"}]";
        CPH.Globals["ci_events_month_2026_02"] = "corrupt audit";
        Assert(!new Action08().Execute() && !CPH.GetGlobalVar<bool>("ci_checkin_receipts_migrated"), "bad migration was marked complete");
        Assert(!new Action02().Execute(), "check-in worked without receipt migration");
        CPH.Globals.Remove("ci_events_month_2026_02");
        Assert(new Action08().Execute(), "legacy receipt migration failed");
        CPH.Globals["ci_events_month_2026_01"] = "corrupt after migration";
        Assert(new Action02().Execute(), "migrated legacy receipt failed");
        Assert(CPH.GetTwitchUserVarById<int>("123", "ci_total") == 9, "legacy audit counted again");
        Redeem("fresh"); Assert(new Action02().Execute(), "damaged audit blocked fresh redemption");
        Assert(CPH.GetTwitchUserVarById<int>("123", "ci_total") == 10, "fresh redemption not counted");
        Setup(); new Action02().Execute();
        CPH.Users["123"]["ci_total"] = 0; CPH.Users["123"].Remove("ci_last_stream");
        CPH.Globals["ci_stream_count"] = 0;
        Redeem("R1"); Assert(new Action02().Execute(), "retry after admin reset failed");
        Assert(CPH.GetTwitchUserVarById<int>("123", "ci_total") == 0, "receipt undid admin reset");
        Redeem("R2"); Assert(new Action02().Execute(), "second redemption after reset failed");
        Assert(CPH.Canceled.Contains("R2"), "reset bypassed once-per-stream marker");
        Console.WriteLine("PASS check-in retries across streams, older receipts, and legacy records");
    }
    private static void InterruptedCheckins() {
        Setup(); new Action02().Execute(); int writes = CPH.Writes;
        for (int failure = 0; failure < writes; failure++) {
            Setup(); CPH.FailAfterWrite = failure;
            try { new Action02().Execute(); } catch (InvalidOperationException) {}
            CPH.FailAfterWrite = -1;
            if (!String.IsNullOrWhiteSpace(CPH.GetGlobalVar<string>("ci_checkin_pending_json"))) {
                int before = CPH.Writes;
                Assert(!new Action01().Execute(), "stream advanced during recovery");
                Assert(!new Action05().Execute(), "admin changed pending counters");
                Assert(!new Action06().Execute(), "submission changed pending wallet");
                Assert(!new Action08().Execute(), "migration changed pending wallet");
                Assert(!new Action10().Execute(), "import overwrote pending counters");
                Assert(!new Action11().Execute(), "debug changed pending counters");
                Assert(before == CPH.Writes, "pending guard wrote state");
            }
            Assert(new Action02().Execute(), "recovery failed at write " + failure);
            Assert(CPH.GetTwitchUserVarById<int>("123", "ci_total") == 10, "lost/duplicate total at " + failure);
            Assert(CPH.GetTwitchUserVarById<int>("123", "ci_streak") == 2, "lost/duplicate streak at " + failure);
            Assert(CPH.GetTwitchUserVarById<int>("123", "ci_wheel_earned") == 1, "lost/duplicate credit at " + failure);
            Assert(CPH.GetGlobalVar<int>("ci_stream_count") == 1, "lost/duplicate count at " + failure);
            Assert(CPH.GetTwitchUserVarById<string>("123", "ci_badge") == "Sapling", "lost badge");
            Assert(String.IsNullOrWhiteSpace(CPH.GetGlobalVar<string>("ci_checkin_pending_json")), "journal remained");
            Assert(CPH.Fulfilled.Contains("R1"), "redemption not fulfilled");
        }
        Setup(); CPH.FulfillmentSucceeds = false; new Action02().Execute();
        CPH.FulfillmentSucceeds = true; Assert(new Action02().Execute(), "fulfillment retry failed");
        Assert(CPH.GetTwitchUserVarById<int>("123", "ci_total") == 10, "fulfillment retry counted again");
        Console.WriteLine("PASS interruption recovery at all " + writes + " write boundaries and fulfillment retry");

        Setup(); CPH.FailAfterWrite = 4;
        try { new Action02().Execute(); } catch (InvalidOperationException) {}
        CPH.FailAfterWrite = -1;
        var pending = JObject.Parse(CPH.GetGlobalVar<string>("ci_checkin_pending_json"));
        pending["month"] = "2020_01";
        CPH.Globals["ci_checkin_pending_json"] = pending.ToString(Formatting.None);
        CPH.Args["userId"] = "456"; CPH.Args["userName"] = "bob"; CPH.Args["redemptionId"] = "bob-redemption";
        Assert(new Action02().Execute(), "different viewer could not recover original journal");
        Assert(CPH.GetTwitchUserVarById<int>("123", "ci_total") == 10, "original viewer lost recovery");
        Assert(CPH.GetTwitchUserVarById<int>("456", "ci_total") == 1, "new viewer was not processed");
        Assert(CPH.GetGlobalVar<int>("ci_stream_count") == 2, "cross-viewer recovery corrupted stream count");
        Assert((string)JObject.Parse(CPH.GetGlobalVar<string>("ci_checkin_receipts_month_2020_01"))["R1"] == "123", "recovery moved original receipt month");
        Console.WriteLine("PASS original viewer/month retained during another viewer's recovery");
    }
    private static void ModerationDuplicates() {
        foreach (string operation in new [] {"rename", "restore"}) {
            Setup(); CPH.Args["userId"] = "mod";
            CPH.Globals["ci_wheel_legacy_migrated"] = true;
            var entries = JArray.Parse("[{\"id\":\"chat-abcdefgh\",\"userId\":\"123\",\"game\":\"Game One\",\"status\":\"approved\"},{\"id\":\"chat-ijklmnop\",\"userId\":\"456\",\"game\":\"Game Two\",\"status\":\"approved\"}]");
            if (operation == "restore") { entries[0]["removed"] = true; entries[0]["game"] = " GAME   TWO "; }
            string key = "ci_wheel_month_2026_11";
            string original = entries.ToString(Formatting.None); CPH.Globals[key] = original;
            CPH.Args["rawInput"] = operation + " chat-abcdefgh" + (operation == "rename" ? " GAME   TWO " : "");
            Assert(new Action13().Execute(), "moderation command failed");
            Assert(CPH.GetGlobalVar<string>(key) == original, "duplicate allowed by " + operation);
            CPH.Args["rawInput"] = "rename chat-abcdefgh Distinct Game";
            Assert(new Action13().Execute(), "valid rename failed");
            if (operation == "restore") {
                CPH.Args["rawInput"] = "restore chat-abcdefgh";
                Assert(new Action13().Execute(), "valid restore failed");
                Assert((bool)JArray.Parse(CPH.GetGlobalVar<string>(key))[0]["removed"] == false, "valid restore remained removed");
            }
            Assert((string)JArray.Parse(CPH.GetGlobalVar<string>(key))[0]["game"] == "Distinct Game", "valid rename not saved");
        }
        Console.WriteLine("PASS duplicate rename/restore rejected, distinct titles accepted");
    }
    private static void AdminAdd() {
        Setup(); CPH.Args["userId"] = "mod"; CPH.Args["userName"] = "moderator";
        CPH.Globals["ci_wheel_legacy_migrated"] = true;
        string key = "ci_wheel_month_2026_10";
        CPH.Args["rawInput"] = "add @alice 2026-10 Old   Favourite";
        Assert(new Action13().Execute(), "admin add failed");
        var entry = JArray.Parse(CPH.GetGlobalVar<string>(key)).Single();
        Assert((string)entry["status"] == "approved" && (string)entry["userId"] == "admin-added" &&
            (string)entry["user"] == "alice" && (string)entry["game"] == "Old Favourite", "admin add saved wrong entry");
        Assert(CPH.GetTwitchUserVarById<int>("123", "ci_wheel_claimed") == 0, "admin add spent a viewer credit");
        Assert(JArray.Parse(CPH.GetGlobalVar<string>("ci_wheel_audit_json")).Count == 1, "admin add not audited");
        CPH.Args["rawInput"] = "add 2026-10 old favourite";
        Assert(new Action13().Execute() && JArray.Parse(CPH.GetGlobalVar<string>(key)).Count == 1, "duplicate admin add allowed");
        CPH.Args["rawInput"] = "add 2026-13 Bad Month";
        Assert(new Action13().Execute() && !CPH.Globals.ContainsKey("ci_wheel_month_2026_13"), "invalid month accepted");
        CPH.Args["rawInput"] = "approve " + (string)entry["id"];
        Assert(new Action13().Execute(), "admin-added ID not usable by other subcommands");
        Assert(new Action14().Execute(), "reconcile flagged an admin-added game");
        Console.WriteLine("PASS moderator add creates an approved, audited, credit-free game");
    }
    private static void OverlayTests() {
        Setup(); CPH.Args["userId"] = "mod"; CPH.Args["userName"] = "moderator";
        CPH.Globals["ci_overlay_json"] = "{\"count\":4,\"topTotals\":[]}";
        CPH.Args["rawInput"] = "test";
        Assert(new Action05().Execute(), "check-in overlay test failed");
        var state = JObject.Parse(CPH.GetGlobalVar<string>("ci_overlay_json"));
        Assert((int)state["count"] == 4 && (long)state["lastCheckinAtMs"] > 0 && ((string)state["eventId"]).StartsWith("test-"),
            "check-in overlay test did not trigger the overlay");
        Assert(CPH.GetTwitchUserVarById<int>("123", "ci_total") == 9, "overlay test changed counts");
        Assert(new Action17().Execute(), "wheel test failed");
        var wheel = JObject.Parse(CPH.GetGlobalVar<string>("ci_wheel_spin_overlay_json"));
        Assert((string)wheel["spinId"] == "test" && ((JArray)wheel["choices"]).Count == 6, "wheel test not published");
        Assert(!CPH.Globals.Keys.Any(k => k.StartsWith("ci_wheel_spin_result_") || k == "ci_wheel_spin_audit_json"), "wheel test saved a draw");
        CPH.Args["rawInput"] = "show";
        Assert(new Action17().Execute() && CPH.Messages.Last().StartsWith("No saved wheel result"), "show did not default to last month");
        Console.WriteLine("PASS overlay tests change no counts or draws; show defaults to last month");
    }
    private static void FrameworkDraw() {
        var draw = typeof(Action17).GetMethod("DrawIndex", BindingFlags.NonPublic | BindingFlags.Static);
        foreach (int count in new [] {1, 2, 3, 7, 200}) {
            for (int i = 0; i < 100; i++) {
                int index = (int)draw.Invoke(null, new object[] {count});
                Assert(index >= 0 && index < count, "draw outside choices");
            }
        }
        Console.WriteLine("PASS Framework-compatible random draw bounds");
    }
    private static void StreamAndRejectionRegression()
    {
        Setup(); new Action02().Execute();
        Redeem("refunded"); new Action02().Execute();
        Assert(CPH.Canceled.Contains("refunded"), "duplicate was not refunded");
        CPH.Globals["ci_stream_id"] = 8;
        new Action02().Execute();
        Assert(CPH.Canceled.Count(x => x == "refunded") == 2, "refund replay switched outcome");
        Assert(CPH.GetTwitchUserVarById<int>("123", "ci_total") == 10, "refund replay awarded attendance");

        for (int failure = 0; failure <= 6; failure++)
        {
            Setup(); CPH.Args.Clear(); CPH.Args["startedAt"] = new DateTime(2026, 11, 20);
            CPH.Globals["ci_stream_count"] = 5;
            CPH.FailAfterWrite = failure;
            try { new Action01().Execute(); } catch (InvalidOperationException) {}
            CPH.FailAfterWrite = -1;
            Assert(new Action01().Execute(), "stream-start retry failed");
            Assert(CPH.GetGlobalVar<int>("ci_stream_id") == 8 && CPH.GetGlobalVar<int>("ci_stream_count") == 0,
                "stream retry did not finish exactly once at boundary " + failure);
        }

        Setup(); CPH.FailAfterWrite = 4;
        try { new Action02().Execute(); } catch (InvalidOperationException) {}
        CPH.FailAfterWrite = -1;
        CPH.Args.Clear(); CPH.Args["startedAt"] = new DateTime(2026, 11, 20);
        Assert(!new Action01().Execute(), "transition proceeded before original journal");
        CPH.Args.Clear(); Redeem("new-stream"); CPH.Args["userId"] = "456";
        Assert(new Action02().Execute(), "new stream check-in could not recover prior state");
        Assert(CPH.GetTwitchUserVarById<int>("123", "ci_last_stream") == 7, "prior check-in moved streams");
        Assert(CPH.GetTwitchUserVarById<int>("456", "ci_last_stream") == 8 && CPH.GetGlobalVar<int>("ci_stream_count") == 1,
            "new check-in counted against stale stream");
        Console.WriteLine("PASS refund replay and stream-transition recovery");
    }

    private static void WheelJournalRegression()
    {
        Setup();
        var month = DateTimeOffset.UtcNow.AddMonths(-2).ToString("yyyy-MM");
        CPH.Args["userId"] = "mod"; CPH.Args["rawInput"] = month; CPH.Args["targetMonth"] = month;
        CPH.Users["123"]["ci_wheel_pending_json"] = JObject.FromObject(new { entry = new { month } }).ToString();
        Assert(!new Action17().Execute() && !new Action16().Execute(), "wheel finalized with unfinished submission");
        CPH.Users["123"].Remove("ci_wheel_pending_json");
        Assert(new Action16().Execute(), "archive remained blocked after recovery");
        Console.WriteLine("PASS unresolved wheel journal blocks finalization");
    }

    private static void OffStreamAdministration()
    {
        Setup(); CPH.Globals.Remove("ci_stream_id"); CPH.Args.Clear();
        CPH.Args["userId"] = "mod"; CPH.Args["userName"] = "moderator";
        CPH.Args["rawInput"] = "set @alice 20";
        Assert(new Action05().Execute() && CPH.GetTwitchUserVarById<int>("123", "ci_total") == 20,
            "moderator could not correct counts without a stream");
        CPH.Globals["ci_wheel_legacy_migrated"] = true;
        string key = "ci_wheel_month_2026_01";
        CPH.Globals[key] = "[{\"id\":\"chat-abcdefgh\",\"userId\":\"123\",\"game\":\"Game\",\"status\":\"pending\"}]";
        CPH.Args["rawInput"] = "approve chat-abcdefgh";
        Assert(new Action13().Execute() && (string)JArray.Parse(CPH.GetGlobalVar<string>(key))[0]["status"] == "approved",
            "moderator could not approve without a stream");
        CPH.Args["userId"] = "viewer";
        CPH.Args["rawInput"] = "set @alice 30";
        new Action05().Execute();
        Assert(CPH.GetTwitchUserVarById<int>("123", "ci_total") == 20, "non-moderator edited counts");
        CPH.Args["rawInput"] = "reject chat-abcdefgh";
        new Action13().Execute();
        Assert((string)JArray.Parse(CPH.GetGlobalVar<string>(key))[0]["status"] == "approved", "non-moderator changed review status");
        Console.WriteLine("PASS off-stream moderator edits and authorization");
    }

    private static void PendingTitleReservation()
    {
        Setup(); CPH.Globals["ci_wheel_legacy_migrated"] = true;
        CPH.Users["123"]["ci_wheel_earned"] = 1;
        CPH.Users["456"] = new Dictionary<string, object> {{"ci_wheel_earned", 1}};
        CPH.Args["rawInput"] = "Same Game"; CPH.Args["msgId"] = "original-id";
        CPH.FailAfterWrite = 1;
        try { new Action06().Execute(); } catch (InvalidOperationException) {}
        CPH.FailAfterWrite = -1;
        CPH.Args["userId"] = "456"; CPH.Args["msgId"] = "second-id";
        Assert(new Action06().Execute(), "duplicate rejection failed");
        var ledger = CPH.Globals.Single(kv => kv.Key.StartsWith("ci_wheel_month_"));
        Assert(JArray.Parse((string)ledger.Value).Count == 1, "both duplicate titles were recorded");
        Assert(CPH.GetTwitchUserVarById<int>("123", "ci_wheel_claimed") == 1 &&
            CPH.GetTwitchUserVarById<int>("456", "ci_wheel_claimed") == 0, "both viewers were charged");
        Console.WriteLine("PASS unresolved submission reserves its title");
    }

    private static void ReplayPreservesAnnouncement()
    {
        Setup();
        string month = DateTimeOffset.UtcNow.AddMonths(-2).ToString("yyyy-MM");
        string suffix = month.Replace('-', '_');
        CPH.Globals["ci_wheel_month_" + suffix] = "[{\"id\":\"entry-1\",\"userId\":\"123\",\"game\":\"Winning Game\",\"status\":\"approved\"}]";
        CPH.Args["userId"] = "mod"; CPH.Args["rawInput"] = month;
        Assert(new Action17().Execute(), "draw failed");
        string key = "ci_wheel_spin_result_" + suffix;
        var result = JObject.Parse(CPH.GetGlobalVar<string>(key));
        Assert((long)result["revealAtMs"] > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "draw did not schedule reveal");
        result["revealAtMs"] = 1L;
        CPH.Globals[key] = result.ToString(Formatting.None);
        CPH.Args["rawInput"] = "replay " + month;
        Assert(new Action17().Execute(), "replay failed");
        CPH.Args["rawInput"] = "hide";
        Assert(new Action17().Execute(), "hide failed");
        Assert(new Action18().Execute() && new Action18().Execute(), "reveal failed");
        Assert(CPH.Messages.Count(m => m.Contains("wheel picked 'Winning Game'")) == 1, "replay/hide lost or duplicated announcement");
        Console.WriteLine("PASS replay and hide preserve the scheduled winner announcement");
    }

    private static void ViewerHelp() {
        Setup(); CPH.Globals.Clear(); CPH.Args.Clear();
        CPH.Commands.Add(new CommandData {Enabled = true, Commands = new List<string> {"!CHECKINS", "!wheel", "!wheeladmin", "!unrelated"}});
        CPH.Commands.Add(new CommandData {Enabled = false, Commands = new List<string> {"!addgame"}});
        Assert(new Action19().Execute(), "viewer help failed off stream");
        string reply = CPH.Messages.Single();
        Assert(reply.Contains("!checkins") && reply.Contains("!wheel list") && reply.Contains("Channel Points"), "missing viewer help");
        Assert(!reply.Contains("!addgame") && !reply.Contains("!checkinboard") &&
            !reply.Contains("!wheeladmin") && !reply.Contains("!unrelated"), "advertised an unavailable or private command");
        CPH.Commands[1].Enabled = true;
        Assert(new Action19().Execute() && CPH.Messages.Last().Contains("!addgame <title>"), "newly enabled command was omitted");
        Console.WriteLine("PASS viewer help lists only enabled public check-in commands");
    }

    public static int Main() {
        CheckinRetries(); InterruptedCheckins(); ModerationDuplicates(); AdminAdd(); OverlayTests(); FrameworkDraw();
        StreamAndRejectionRegression(); WheelJournalRegression();
        OffStreamAdministration(); PendingTitleReservation(); ReplayPreservesAnnouncement();
        ViewerHelp();
        return 0;
    }
}
