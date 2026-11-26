// Node built-in, no npm dependencies.
// Exercise the live dashboard's script with a no-op DOM and a fake Streamer.bot socket.
import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import vm from "node:vm";

const html = fs.readFileSync(new URL("../tools/live-dashboard.html", import.meta.url), "utf8");
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];

function load() {
  const sent = [];
  // Any DOM property is a callable no-op, so rendering code runs without a browser.
  const fake = () => new Proxy(function () {}, {get: (t, k) => k === "children" ? [] : k === "value" ? "" : fake(), apply: () => fake(), set: () => true});
  class FakeSocket {
    constructor() { this.readyState = 1; FakeSocket.last = this; }
    send(text) { sent.push(JSON.parse(text)); }
    static OPEN = 1;
  }
  const context = vm.createContext({
    document: {getElementById: fake, createElement: fake}, WebSocket: FakeSocket,
    setTimeout(){}, setInterval(){}, prompt(){}, confirm(){ return true; }, console
  });
  vm.runInContext(script, context);
  return {context, sent, socket: FakeSocket.last};
}

test("normalizes Streamer.bot globals and per-user variables", () => {
  const {context} = load();
  const g = context.globalsFrom({variables: {ci_wheel_month_2026_10: {name: "ci_wheel_month_2026_10", value: "[]"},
                                             ci_wheel_month_2026_11: {name: "ci_wheel_month_2026_11", value: "[]"}}});
  assert.deepEqual([...context.wheelMonths(g)], ["2026-11", "2026-10"]);
  const rows = context.viewersFrom({
    ci_total: {variables: [{userId: "1", userLogin: "alice", userName: "Alice", value: 12}, {userId: "2", userLogin: "bob", value: 30}]},
    ci_wheel_earned: {variables: {x: {userId: "1", value: 1}}}
  });
  assert.equal(rows[0].login, "bob");
  assert.equal(rows[1].name, "Alice");
  assert.equal(rows[1].ci_wheel_earned, 1);
});

test("offers only the wheel admin actions the action allows", () => {
  const {context} = load();
  assert.deepEqual([...context.actionsFor({status: "pending"})], ["approve", "reject", "rename", "remove"]);
  assert.deepEqual([...context.actionsFor({})], ["reject", "rename", "remove"]);
  assert.deepEqual([...context.actionsFor({status: "approved", removed: true})], ["restore"]);
  assert.deepEqual([...context.actionsFor({status: "rejected", replacedBy: "x"})], []);
});

test("runs admin commands as the broadcaster through DoAction", async () => {
  const {context, sent, socket} = load();
  const reply = (body) => socket.onmessage({data: JSON.stringify(Object.assign({status: "ok", id: sent.at(-1).id}, body))});
  const opened = socket.onopen();
  assert.equal(sent.at(-1).request, "GetBroadcaster");
  reply({platforms: {twitch: {broadcastUserId: "99", broadcastUserName: "Grove"}}});
  await opened;
  context.run("wheel", "approve abc");
  await Promise.resolve();
  const call = sent.find(m => m.request === "DoAction");
  assert.equal(call.action.name, "CI - Wheel Admin");
  assert.equal(call.args.userId, "99");
  assert.equal(call.args.rawInput, "approve abc");
});
