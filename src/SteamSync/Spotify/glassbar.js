// The Spotify player bar as frosted glass over the Library page (LibraryWindow adds this script to the site's pages).
// The player itself runs in its own engine behind the page (SpotifyPlayer, spotify.html); this bar only shows what it
// reports and sends the clicks back through the app. A closed shadow root keeps the site's styles and scripts out.
(() => {
  "use strict";
  const hosts = __HOSTS__;
  const view = window.chrome && window.chrome.webview;
  if (!view || window.top !== window || !hosts.includes(location.hostname.toLowerCase())) return;

  const send = (action, value) => view.postMessage({ type: "ge-spotify", action, ...(value === undefined ? {} : { value }) });
  const icon = d => `<svg viewBox="0 0 24 24" aria-hidden="true">${d}</svg>`;
  const stroke = d => `<path fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" d="${d}"/>`;
  const fill = d => `<path fill="currentColor" d="${d}"/>`;
  const heart = "M12 20s-7.5-4.6-9.3-9.2C1.5 7.6 3.6 4.5 7 4.5c2 0 3.4 1.1 5 3 1.6-1.9 3-3 5-3 3.4 0 5.5 3.1 4.3 6.3C19.5 15.4 12 20 12 20Z";
  const I = {
    play: icon(fill("M7 4.5v15a1 1 0 0 0 1.5.86l12.4-7.5a1 1 0 0 0 0-1.72L8.5 3.64A1 1 0 0 0 7 4.5Z")),
    pause: icon(fill("M6 4h4v16H6zm8 0h4v16h-4z")),
    next: icon(fill("M5 5.2v13.6a.8.8 0 0 0 1.2.7L16 13v6h2V5h-2v6L6.2 4.5a.8.8 0 0 0-1.2.7Z")),
    prev: icon(fill("M19 5.2v13.6a.8.8 0 0 1-1.2.7L8 13v6H6V5h2v6l9.8-6.5a.8.8 0 0 1 1.2.7Z")),
    shuffle: icon(stroke("M3 7h3.5c2 0 3.3 1 4.4 2.7l2.2 4.6C14.2 16 15.5 17 17.5 17H21m0 0-3-3m3 3-3 3M3 17h3.5c1.2 0 2.1-.4 2.9-1.1M21 7h-3.5c-1.2 0-2.1.4-2.9 1.1M21 7l-3-3m3 3-3 3")),
    repeat: icon(stroke("M17 2l3 3-3 3M3 11V9a4 4 0 0 1 4-4h13M7 22l-3-3 3-3m14-3v2a4 4 0 0 1-4 4H4")),
    vol: icon(stroke("M4 9h4l5-4v14l-5-4H4Z M16.5 8.5a5 5 0 0 1 0 7M19 6a8.5 8.5 0 0 1 0 12")),
    mute: icon(stroke("M4 9h4l5-4v14l-5-4H4Zm12.5 0 5 6m0-6-5 6")),
    heart: icon(stroke(heart)),
    heartOn: icon(fill(heart)),
    up: icon(stroke("m6 15 6-6 6 6")),
    down: icon(stroke("m6 9 6 6 6-6")),
    logo: icon(fill("M12 1.5a10.5 10.5 0 1 0 0 21 10.5 10.5 0 0 0 0-21Zm4.82 15.15a.65.65 0 0 1-.9.22c-2.46-1.5-5.56-1.84-9.21-1a.66.66 0 0 1-.29-1.28c4-.91 7.43-.52 10.18 1.16.31.19.41.6.22.9Zm1.28-2.86a.82.82 0 0 1-1.12.27c-2.82-1.73-7.12-2.23-10.45-1.22a.82.82 0 1 1-.48-1.57c3.81-1.15 8.54-.6 11.78 1.4.38.23.5.74.27 1.12Zm.11-2.98C14.83 8.8 9.25 8.61 6.02 9.6a.98.98 0 1 1-.57-1.88c3.71-1.12 9.88-.9 13.77 1.41a.98.98 0 0 1-1.01 1.68Z")),
  };

  const css = `
  :host { all: initial; }
  .bar { position: fixed; left: 0; right: 0; bottom: 0; height: 92px; z-index: 2147483000; box-sizing: border-box;
    display: grid; grid-template-columns: minmax(180px, 1fr) minmax(320px, 1.6fr) minmax(180px, 1fr); align-items: center; gap: 16px; padding: 0 16px;
    font: 14px/1.4 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; color: var(--text); user-select: none;
    background: var(--glass); border-top: 1px solid var(--edge); box-shadow: 0 -10px 30px var(--shadow);
    -webkit-backdrop-filter: blur(24px) saturate(180%); backdrop-filter: blur(24px) saturate(180%);
    transform: translateY(0); transition: transform .25s ease, opacity .25s ease; }
  .bar.off { transform: translateY(110%); opacity: 0; pointer-events: none; }
  .ge { --text: #e8eaf0; --dim: #a3aabb; --faint: #7a8192; --glass: rgba(14, 17, 23, .58); --edge: rgba(255, 255, 255, .09);
    --shadow: rgba(0, 0, 0, .25); --track: rgba(255, 255, 255, .22); --accent: #1ed760; --pp: #f2f3f7; --pp-icon: #000; --ph: rgba(255, 255, 255, .08); }
  .ge.light { --text: #14161c; --dim: #4c5262; --faint: #6b7282; --glass: rgba(255, 255, 255, .55); --edge: rgba(255, 255, 255, .7);
    --shadow: rgba(20, 24, 40, .10); --track: rgba(20, 24, 40, .18); --accent: #14a44d; --pp: #14161c; --pp-icon: #fff; --ph: rgba(20, 24, 40, .08); }
  * { box-sizing: border-box; }
  button { font: inherit; color: inherit; background: none; border: 0; padding: 0; cursor: pointer; display: grid; place-items: center; }
  button:disabled { opacity: .4; cursor: default; }
  svg { width: 20px; height: 20px; display: block; }
  .np { display: flex; align-items: center; gap: 12px; min-width: 0; cursor: pointer; }
  .art { width: 56px; height: 56px; border-radius: 6px; object-fit: cover; background: var(--ph); flex: none; box-shadow: 0 4px 14px var(--shadow); }
  .txt { min-width: 0; }
  .title { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-weight: 600; }
  .artist { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--dim); font-size: 13px; }
  .like { color: var(--dim); padding: 4px; flex: none; }
  .like svg { width: 18px; height: 18px; }
  .like.on { color: var(--accent); }
  .ctl { display: flex; flex-direction: column; align-items: center; gap: 6px; }
  .btns { display: flex; align-items: center; gap: 18px; }
  .btns button { color: var(--dim); }
  .btns button:hover:not(:disabled), .like:hover, .icon:hover { color: var(--text); }
  .btns button.on { color: var(--accent); }
  .btns .pp { width: 36px; height: 36px; border-radius: 50%; background: var(--pp); color: var(--pp-icon); }
  .btns .pp:hover:not(:disabled) { color: var(--pp-icon); transform: scale(1.05); }
  .btns .pp svg { width: 18px; height: 18px; }
  .prog { display: flex; align-items: center; gap: 8px; width: 100%; max-width: 600px; font-size: 12px; color: var(--faint); font-variant-numeric: tabular-nums; }
  .prog span { width: 40px; text-align: center; }
  input[type=range] { -webkit-appearance: none; appearance: none; flex: 1; height: 4px; margin: 0; border-radius: 2px; outline: none; cursor: pointer;
    background: linear-gradient(to right, var(--text) var(--p, 0%), var(--track) var(--p, 0%)); }
  input[type=range]:hover { background: linear-gradient(to right, var(--accent) var(--p, 0%), var(--track) var(--p, 0%)); }
  input[type=range]::-webkit-slider-thumb { -webkit-appearance: none; width: 12px; height: 12px; border-radius: 50%; background: var(--text); opacity: 0; }
  input[type=range]:hover::-webkit-slider-thumb { opacity: 1; }
  .right { display: flex; align-items: center; justify-content: flex-end; gap: 10px; color: var(--dim); }
  .right svg { width: 18px; height: 18px; }
  .right input[type=range] { max-width: 110px; }
  .icon { color: var(--dim); padding: 4px; }
  .device { font-size: 12px; color: var(--accent); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 220px; }
  .here { font-size: 12px; padding: 5px 12px; border-radius: 14px; background: var(--accent); color: #000; font-weight: 600; white-space: nowrap; }
  .toast { position: fixed; left: 50%; bottom: 108px; transform: translateX(-50%); z-index: 2147483001; max-width: 80vw; padding: 10px 18px; border-radius: 10px;
    font: 14px/1.4 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; color: var(--text); background: var(--glass); border: 1px solid var(--edge);
    box-shadow: 0 6px 20px var(--shadow); -webkit-backdrop-filter: blur(24px) saturate(180%); backdrop-filter: blur(24px) saturate(180%);
    opacity: 0; transition: opacity .2s; pointer-events: none; }
  .toast.show { opacity: 1; }
  .hidden { display: none !important; }
  .bubble { position: fixed; left: 16px; bottom: 16px; width: 68px; height: 68px; border-radius: 50%; z-index: 2147483000;
    display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 2px;
    font: 600 11px/1 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; color: var(--text); letter-spacing: .01em;
    background: var(--glass); border: 1px solid var(--edge); box-shadow: 0 8px 26px var(--shadow);
    -webkit-backdrop-filter: blur(24px) saturate(180%); backdrop-filter: blur(24px) saturate(180%);
    transition: transform .25s ease, opacity .25s ease, box-shadow .2s; }
  .bubble svg { width: 26px; height: 26px; color: var(--accent); }
  .bubble:hover { transform: scale(1.06); }
  .bubble.playing { box-shadow: 0 8px 26px var(--shadow), 0 0 0 2px var(--accent); }
  .bubble.off { transform: scale(.4); opacity: 0; pointer-events: none; }
  @media (max-width: 820px) { .bar { grid-template-columns: minmax(140px, 1fr) minmax(260px, 1.6fr) auto; } .vol, .device { display: none; } }
  @media (max-width: 600px) { .shuffle, .repeat, .prog span { display: none; } }`;

  const html = `
  <div class="ge"><div class="bar off">
    <div class="np" title="Show Spotify">
      <img class="art hidden" alt=""><div class="art ph"></div>
      <div class="txt"><div class="title">Nothing playing</div><div class="artist"></div></div>
      <button class="like hidden"></button>
    </div>
    <div class="ctl">
      <div class="btns">
        <button class="shuffle" title="Shuffle">${I.shuffle}</button>
        <button class="prev" title="Previous">${I.prev}</button>
        <button class="pp" title="Play">${I.play}</button>
        <button class="next" title="Next">${I.next}</button>
        <button class="repeat" title="Repeat">${I.repeat}</button>
      </div>
      <div class="prog"><span class="pos">0:00</span><input type="range" class="seek" min="0" max="1000" value="0"><span class="dur">0:00</span></div>
    </div>
    <div class="right">
      <span class="device hidden"></span>
      <button class="here hidden" title="Move playback to Gaming Enjoyer">Play here</button>
      <button class="icon mute" title="Mute">${I.vol}</button>
      <input type="range" class="vol" min="0" max="100" value="60" title="Volume">
      <button class="icon min" title="Minimise to a bubble">${I.down}</button>
      <button class="icon expand" title="Show Spotify">${I.up}</button>
    </div>
  </div>
  <button class="bubble off" title="Show the Spotify bar">${I.logo}<span>Spotify</span></button>
  <div class="toast"></div></div>`;

  let root, bar, toastEl, room, toastTimer, seeking = false, volTimer;
  let s = { title: null, artist: "", art: "", playing: false, position: 0, duration: 0, shuffle: false, repeat: "off", liked: null, volume: 60, device: null, canHere: false };
  let receivedAt = 0, visible = false, volTouched = 0;
  // Minimised to the bubble, remembered across the site's pages (a convenience: without storage it starts as the bar).
  const minKey = "ge_spotify_minimized";
  let minimized = (() => { try { return localStorage.getItem(minKey) === "1"; } catch { return false; } })();
  const q = sel => root.querySelector(sel);
  const fmt = ms => { if (!ms || ms < 0) ms = 0; const t = Math.floor(ms / 1000); return `${Math.floor(t / 60)}:${String(t % 60).padStart(2, "0")}`; };
  const position = () => s.playing ? Math.min(s.duration, s.position + (Date.now() - receivedAt)) : s.position;

  function build() {
    const host = document.createElement("ge-spotify-bar");
    root = host.attachShadow({ mode: "closed" });
    root.innerHTML = `<style>${css}</style>${html}`;
    document.documentElement.appendChild(host);
    bar = q(".bar");
    toastEl = q(".toast");
    // Room at the end of the page (after anything the site adds later), so the last row can scroll out from under the bar.
    room = document.createElement("style");
    room.textContent = "body::after { content: ''; display: block; height: 96px; pointer-events: none; }";

    q(".min").onclick = () => setMinimized(true);
    q(".bubble").onclick = () => setMinimized(false);
    q(".pp").onclick = () => send("toggle");
    q(".next").onclick = () => send("next");
    q(".prev").onclick = () => send("previous");
    q(".shuffle").onclick = () => send("shuffle");
    q(".repeat").onclick = () => send("repeat");
    q(".like").onclick = e => { e.stopPropagation(); send("like"); };
    q(".here").onclick = () => send("here");
    q(".expand").onclick = () => send("expand");
    q(".np").onclick = () => send("expand");
    q(".mute").onclick = () => send("mute");
    const seek = q(".seek");
    seek.oninput = () => { seeking = true; q(".pos").textContent = fmt(seek.value / 1000 * s.duration); seek.style.setProperty("--p", seek.value / 10 + "%"); };
    seek.onchange = () => {
      const ms = Math.round(seek.value / 1000 * s.duration);
      seeking = false;
      s.position = ms; receivedAt = Date.now();
      send("seek", ms);
      tick();
    };
    const vol = q(".vol");
    vol.oninput = () => {
      volTouched = Date.now();
      showVolume(+vol.value);
      clearTimeout(volTimer);
      volTimer = setTimeout(() => send("volume", +vol.value), 60);
    };
    theme();
    new MutationObserver(theme).observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    setInterval(tick, 500);
  }

  // The site is dark unless <html data-theme="light"> (assets/theme/theme.js).
  function theme() { q(".ge").classList.toggle("light", document.documentElement.getAttribute("data-theme") === "light"); }

  function showVolume(v) {
    const vol = q(".vol");
    vol.value = v;
    vol.style.setProperty("--p", v + "%");
    q(".mute").innerHTML = v == 0 ? I.mute : I.vol;
  }

  function render() {
    q(".title").textContent = s.title || "Nothing playing";
    q(".artist").textContent = s.title ? s.artist || "" : "";
    const img = q("img.art");
    img.classList.toggle("hidden", !s.art); q(".art.ph").classList.toggle("hidden", !!s.art);
    if (s.art && img.getAttribute("src") !== s.art) img.src = s.art;
    const like = q(".like");
    like.classList.toggle("hidden", !s.title || s.liked === null);
    like.classList.toggle("on", s.liked === true);
    like.innerHTML = s.liked ? I.heartOn : I.heart;
    like.title = s.liked ? "Remove from your Liked Songs" : "Save to your Liked Songs";
    const pp = q(".pp");
    pp.innerHTML = s.playing ? I.pause : I.play;
    pp.title = s.playing ? "Pause" : "Play";
    const bubble = q(".bubble");
    bubble.classList.toggle("playing", !!s.playing);
    bubble.title = s.title ? `${s.title} · ${s.artist}\nShow the Spotify bar` : "Show the Spotify bar";
    q(".shuffle").classList.toggle("on", !!s.shuffle);
    const repeat = q(".repeat");
    repeat.classList.toggle("on", s.repeat !== "off");
    repeat.title = s.repeat === "track" ? "Repeat: this song" : s.repeat === "context" ? "Repeat: on" : "Repeat";
    const device = q(".device");
    device.classList.toggle("hidden", !s.device);
    device.textContent = s.device ? `Playing on ${s.device}` : "";
    q(".here").classList.toggle("hidden", !s.canHere);
    q(".dur").textContent = fmt(s.duration);
    if (Date.now() - volTouched > 1500) showVolume(s.volume); // not while the slider is being dragged
    tick();
  }

  function tick() {
    if (seeking || !bar) return;
    const pos = position();
    q(".pos").textContent = fmt(pos);
    const p = s.duration ? pos / s.duration * 1000 : 0;
    const seek = q(".seek");
    seek.value = p;
    seek.style.setProperty("--p", p / 10 + "%");
  }

  function setVisible(on) {
    visible = on;
    layOut();
  }

  function setMinimized(on) {
    minimized = on;
    try { localStorage.setItem(minKey, on ? "1" : "0"); } catch { }
    layOut();
  }

  // The bar along the bottom, or only the bubble in the bottom-left corner (no room needed at the end of the page).
  function layOut() {
    bar.classList.toggle("off", !visible || minimized);
    q(".bubble").classList.toggle("off", !visible || !minimized);
    if (visible && !minimized) document.head?.appendChild(room); else room.remove();
  }

  function toast(text) {
    toastEl.textContent = text;
    toastEl.classList.add("show");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => toastEl.classList.remove("show"), 3500);
  }

  view.addEventListener("message", e => {
    const m = e.data;
    if (!m || m.type !== "ge-spotify" || !bar) return;
    if (m.kind === "visible") setVisible(!!m.visible);
    else if (m.kind === "bar" && m.data) { s = { ...s, ...m.data }; receivedAt = Date.now(); render(); }
    else if (m.kind === "toast" && visible) toast(String(m.text || ""));
  });

  const start = () => { build(); send("ready"); };
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start, { once: true }); else start();
})();
