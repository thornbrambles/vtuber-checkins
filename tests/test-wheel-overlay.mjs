// Node built-in, no npm dependencies.
// Exercise the actual Browser Source script with mocked OBS DOM and WebSocket.
import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import vm from "node:vm";

const html = fs.readFileSync(new URL("../overlay/game-wheel-overlay.html", import.meta.url), "utf8");
const match = html.match(/<script>([\s\S]*?)<\/script>/);
assert.ok(match, "Browser source must contain a script");

function harness() {
  const nodes = new Map();
  function node(id) {
    if (!nodes.has(id)) {
      const flags = new Set();
      const element = {
        textContent: "", style: {},
        classList: {
          add: x => flags.add(x),
          remove: x => flags.delete(x),
          toggle: (x, enable) => { if (enable) flags.add(x); else flags.delete(x); },
          contains: x => flags.has(x)
        }
      };
      if (id === "wheel") element.getContext = () => ({
        clearRect(){}, beginPath(){}, moveTo(){}, arc(){}, closePath(){},
        fill(){}, stroke(){}, save(){}, restore(){}, translate(){},
        rotate(){}, fillText(){}
      });
      nodes.set(id, element);
    }
    return nodes.get(id);
  }
  let currentTime = 2000000000000;
  class FakeDate extends Date {
    static now() { return currentTime; }
  }
  class FakeSocket {
    constructor() { this.readyState = 0; }
    send() {}
    static OPEN = 1;
  }
  const context = vm.createContext({
    document: { getElementById: node },
    WebSocket: FakeSocket,
    Date: FakeDate,
    setInterval(){}, setTimeout(){},
    requestAnimationFrame(){ return 1; },
    cancelAnimationFrame(){},
    console, Math, Number, JSON, String, Array
  });
  vm.runInContext(match[1], context);
  const submit = data => { context.payload=data; vm.runInContext("accept(payload)",context); };
  const render = () => vm.runInContext("frame()",context);
  return { node, submit, render, setNow: n => {currentTime=n;} };
}

function spin(index, n=8, overrides={}) {
  const choices=Array.from({length:n},(_,i)=>({id:"id-"+i,game:"Game "+i,user:"viewer"+i}));
  return {
    eventId:"evt-1",spinId:"saved-result-1",month:"2026-10",
    startedAtMs:2000000000000-10000, durationMs:10000,visibleMs:18000,
    selectedIndex:index,choices,winner:choices[index],hidden:false,...overrides
  };
}

test("wheel is transparent by default until a draw is published",()=>{
  const h=harness();
  assert.equal(h.node("stage").classList.contains("visible"),false);
});

test("animation stops precisely on the PRESELECTED winning slice",()=>{
  for (const n of [1,2,3,8,23,70,200]) {
    for (const winner of [...new Set([0,Math.floor(n/2),n-1])]) {
      const h=harness();
      h.submit(spin(winner,n));
      h.render();
      const rotation=Number(h.node("wheel").style.transform.match(/rotate\(([\d.]+)rad\)/)?.[1]);
      assert.ok(Number.isFinite(rotation));
      const angle=(-Math.PI/2 + Math.PI*2*winner/n+rotation+Math.PI*200)%(Math.PI*2);
      const atTop=(angle-(-Math.PI/2)+Math.PI*2)%(Math.PI*2);
      assert.ok(Math.min(atTop,Math.PI*2-atTop)<1e-7,"incorrect final slice n="+n+" index="+winner);
      assert.equal(h.node("winner-game").textContent,"Game "+winner);
      assert.equal(h.node("winner").classList.contains("show"),true);
    }
  }
});

test("hidden and expired draws do not crowd OBS",()=>{
  const h=harness();
  h.submit(spin(2,8,{hidden:true}));
  h.render();
  assert.equal(h.node("stage").classList.contains("visible"),false);
  const h2=harness();
  h2.submit(spin(2,8,{startedAtMs:2000000000000-19000}));
  h2.render();
  assert.equal(h2.node("stage").classList.contains("visible"),false);
});

test("invalid result cannot pick a different winner in UI",()=>{
  const h=harness();
  const item=spin(2);
  item.winner=item.choices[4];
  h.submit(item);
  assert.equal(h.node("stage").classList.contains("visible"),false);
});
