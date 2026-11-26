// Runs youtube.js against a minimal pretend page in Node and checks what it does. Called by ScriptletTests with the
// script's path; prints one line per check and exits 1 if any fails.
import fs from "fs";

const src = fs.readFileSync(process.argv[2], "utf8");
globalThis.window = globalThis;
globalThis.location = { hostname: "www.youtube.com" };
globalThis.document = { documentElement: {}, head: { appendChild() { } }, createElement: () => ({}), querySelector: () => null, querySelectorAll: () => [] };
globalThis.Response ??= class { json() { return Promise.resolve({}); } };
globalThis.setInterval = () => 0;
(0, eval)(src);
// What the page sees: youtube.js runs before any page script, so the page only ever gets the hooked toString.
const ts = Function.prototype.toString;

let failed = 0;
const check = (ok, what) => { console.log((ok ? "ok   " : "FAIL ") + what); if (!ok) failed++; };

// Ads and the ad-blocker popup are removed from player data; everything else stays.
const r = JSON.parse('{"adPlacements":[1],"playerAds":[2],"videoDetails":{},"auxiliaryUi":{"messageRenderers":{"enforcementMessageViewModel":{},"other":{}}},"playerResponse":{"adSlots":[3]}}');
check(!("adPlacements" in r) && !("playerAds" in r) && !("adSlots" in r.playerResponse) && "videoDetails" in r, "ad fields removed, the rest kept");
check(!("enforcementMessageViewModel" in r.auxiliaryUi.messageRenderers) && "other" in r.auxiliaryUi.messageRenderers, "ad-blocker popup removed, other messages kept");
check(JSON.parse("[1,2]").length === 2 && JSON.parse('"s"') === "s", "JSON.parse still parses");

// Hooked built-ins look exactly like the originals.
check(ts.call(JSON.parse) === "function parse() { [native code] }", "JSON.parse looks native");
check(ts.call(Promise.prototype.then) === "function then() { [native code] }", "Promise.prototype.then looks native");
check(Function.prototype.toString.call(Function.prototype.toString) === "function toString() { [native code] }", "toString looks native");
check(JSON.parse.name === "parse" && JSON.parse.length === 2, "names and lengths kept");
check(ts.call(function page() { return 42; }).includes("return 42"), "page functions still show their source");
check(Object.keys(window).every((k) => !/advoid/i.test(k)), "no global named after AdVoid");

// YouTube's abnormality handler never runs; other callbacks do.
let abnormal = false, ordinary = false;
await Promise.resolve().then(function onAbnormalityDetected() { abnormal = true; });
await Promise.resolve().then(() => { ordinary = true; });
check(!abnormal, "abnormality handler not run");
check(ordinary, "ordinary promise callbacks run");

const stats = window[Symbol.for("advoid.youtube")]();
check(stats.removed === 3 && stats.popups === 1, `counters ${JSON.stringify(stats)}`);
process.exit(failed ? 1 : 0);
