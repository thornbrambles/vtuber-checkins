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
        CPH.Globals["ci_events_month_2026_01"] = "[{\"id\":\"checkin-R1\",\"userId\":\"123\"}]";
        Assert(new Action02().Execute(), "legacy audit receipt failed");
        Assert(CPH.GetTwitchUserVarById<int>("123", "ci_total") == 9, "legacy audit counted again");
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

    private static void DashboardPreconditions()
    {
        Setup();
        var edits = JObject.Parse(@"{""globals"":{},""users"":[{""userId"":""123"",""vars"":{""ci_total"":4}}],
            ""expected"":{""globals"":{},""users"":{""123"":{""ci_total"":9,""ci_wheel_claimed"":null}}}}");
        var validate = typeof(Action10).GetMethod("CheckDashboardPreconditions", BindingFlags.NonPublic | BindingFlags.Instance);
        validate.Invoke(new Action10(), new object[] { edits });
        CPH.Users["123"]["ci_wheel_claimed"] = 1;
        try { validate.Invoke(new Action10(), new object[] { edits }); }
        catch (TargetInvocationException e) when (e.InnerException is System.IO.InvalidDataException)
        {
            Console.WriteLine("PASS dashboard importer rejects stale wallet dependency");
            return;
        }
        throw new Exception("Stale dashboard edit passed validation");
    }

    public static int Main() {
        CheckinRetries(); InterruptedCheckins(); ModerationDuplicates(); FrameworkDraw();
        StreamAndRejectionRegression(); WheelJournalRegression(); DashboardPreconditions();
        return 0;
    }
}
