// AdVoid's YouTube ad remover. Runs in every youtube.com document before YouTube's own scripts.
//
// YouTube doesn't load video ads from ad servers a request blocker could stop: the player is told which ads to play,
// and when, inside the same JSON that describes the video (ytInitialPlayerResponse on the first page, then the
// /youtubei/v1/player and /next responses). Removing those fields before the player reads them means it never
// schedules an ad. A fallback skips any ad that still starts (muted, jumped to the end, skip pressed).
//
// YouTube also looks for ad blockers, and once it decides you use one it shows "Ad blockers are not allowed on
// YouTube" and pauses the video (seen 2026-11-26, signed in). So this script also:
// - Hooks functions without showing it: every replaced built-in is a Proxy of the original, so its toString() still
//   says "[native code]". A plain replacement function shows its source, a giveaway the page can check.
// - Never runs YouTube's "abnormality" handler, the client-side check that reports a tampered page.
// - Removes the popup itself: from the player data (auxiliaryUi.messageRenderers.enforcementMessageViewModel) and, as
//   a fallback, from the page, then lets the video play.
(() => {
  "use strict";
  if (!/(^|\.)youtube(-nocookie)?\.com$/.test(location.hostname)) return;

  const AD_KEYS = ["adPlacements", "adSlots", "playerAds", "adBreakHeartbeatParams", "adBreakParams"];
  const POPUP_KEYS = ["enforcementMessageViewModel"];
  let removed = 0;
  let popups = 0;

  const prune = (o) => {
    if (!o || typeof o !== "object") return o;
    for (const key of AD_KEYS) if (key in o) { delete o[key]; removed++; }
    // The "ad blockers are not allowed" popup comes with the player data as one of its message renderers.
    const messages = o.auxiliaryUi && o.auxiliaryUi.messageRenderers;
    if (messages && typeof messages === "object")
      for (const key of POPUP_KEYS) if (key in messages) { delete messages[key]; popups++; }
    if (o.playerResponse && typeof o.playerResponse === "object") prune(o.playerResponse);
    return o;
  };

  // Replaces owner[name] with a Proxy of it: calls go through `apply`, and the function still looks native. A
  // Proxy's toString() loses the name ("function () { [native code] }" instead of "function parse() ..."), so
  // Function.prototype.toString is hooked too (below) to describe each hooked function by its original.
  const originals = new WeakMap();
  const hook = (owner, name, apply) => {
    const original = owner[name];
    if (typeof original !== "function") return;
    try {
      const proxy = new Proxy(original, { apply });
      originals.set(proxy, original);
      owner[name] = proxy;
    } catch { }
  };

  // The first page carries the player response as a global the player reads once.
  let initial;
  try {
    Object.defineProperty(window, "ytInitialPlayerResponse", {
      configurable: true,
      get: () => initial,
      set: (value) => { initial = prune(value); },
    });
  } catch { }

  // Later videos (YouTube is a single-page app) come from fetch: as JSON, or as text parsed by JSON.parse.
  hook(JSON, "parse", (target, self, args) => prune(Reflect.apply(target, self, args)));
  hook(Response.prototype, "json", (target, self, args) => Reflect.apply(target, self, args).then(prune));

  // YouTube's abnormality check hands its result to a promise callback named onAbnormalityDetected; that callback
  // is swapped for one that does nothing. Each function is looked at once (toString is slow on big functions).
  const toString = Function.prototype.toString;
  const checked = new WeakMap();
  const isAbnormalityHandler = (fn) => {
    let found = checked.get(fn);
    if (found === undefined) {
      try { found = Reflect.apply(toString, fn, []).includes("onAbnormalityDetected"); } catch { found = false; }
      checked.set(fn, found);
    }
    return found;
  };
  const noop = () => { };
  hook(Promise.prototype, "then", (target, self, args) => {
    if (typeof args[0] === "function" && isAbnormalityHandler(args[0])) args[0] = noop;
    return Reflect.apply(target, self, args);
  });
  hook(Function.prototype, "toString", (target, self, args) => Reflect.apply(target, originals.get(self) ?? self, args));

  // Ad boxes in the page itself (feed tiles, banners, the player's ad overlay), hidden from the start; and the
  // ad-blocker popup with the dimmed backdrop behind it, so it doesn't flash before the fallback removes it.
  const css = `
    ytd-ad-slot-renderer, ytd-in-feed-ad-layout-renderer, ytd-banner-promo-renderer, ytd-statement-banner-renderer,
    ytd-display-ad-renderer, ytd-promoted-sparkles-web-renderer, ytd-promoted-video-renderer, ytd-compact-promoted-video-renderer,
    ytd-player-legacy-desktop-watch-ads-renderer, ytd-engagement-panel-section-list-renderer[target-id="engagement-panel-ads"],
    #masthead-ad, #player-ads, #panels > ytd-engagement-panel-section-list-renderer[target-id="engagement-panel-ads"],
    ytd-rich-item-renderer:has(> #content > ytd-ad-slot-renderer), .ytp-ad-overlay-container, .ytp-ad-image-overlay,
    ytm-promoted-sparkles-web-renderer, ad-slot-renderer,
    tp-yt-paper-dialog:has(ytd-enforcement-message-view-model), ytd-enforcement-message-view-model,
    body:has(ytd-enforcement-message-view-model) tp-yt-iron-overlay-backdrop { display: none !important; }`;
  const addStyle = () => {
    const style = document.createElement("style");
    style.textContent = css;
    (document.head || document.documentElement).appendChild(style);
  };
  if (document.documentElement) addStyle(); else document.addEventListener("readystatechange", addStyle, { once: true });

  // Fallbacks: an ad that plays anyway is muted, run to its end and skipped; an ad-blocker popup that shows anyway
  // is closed (its own close button, so YouTube's dialog manager stays in step) and the video it paused plays on.
  const skip = () => {
    const player = document.querySelector(".html5-video-player");
    if (player && player.classList.contains("ad-showing")) {
      const video = player.querySelector("video");
      if (video) {
        video.muted = true;
        if (isFinite(video.duration) && video.duration > 0) video.currentTime = video.duration;
      }
      document.querySelectorAll(".ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-skip-ad-button").forEach((b) => b.click());
      removed++;
    }
    const popup = document.querySelector("ytd-enforcement-message-view-model");
    if (popup) {
      const dialog = popup.closest("tp-yt-paper-dialog");
      const close = dialog && dialog.querySelector("#dismiss-button button, button[aria-label='Close'], yt-button-shape#dismiss-button button");
      if (close) close.click();
      (dialog || popup).remove();
      document.querySelectorAll("tp-yt-iron-overlay-backdrop").forEach((b) => b.remove());
      const video = document.querySelector("#movie_player video");
      if (video && video.paused) video.play().catch(noop);
      popups++;
    }
  };
  setInterval(skip, 300);

  // For the host and its checks: what this page removed so far. Behind a symbol, so the page can't find it by name.
  Object.defineProperty(window, Symbol.for("advoid.youtube"), { value: () => ({ removed, popups }) });
})();
