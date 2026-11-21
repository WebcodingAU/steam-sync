// AdVoid's YouTube ad remover. Runs in every youtube.com document before YouTube's own scripts.
//
// YouTube doesn't load video ads from ad servers a request blocker could stop: the player is told which ads to play,
// and when, inside the same JSON that describes the video (ytInitialPlayerResponse on the first page, then the
// /youtubei/v1/player and /next responses). Removing those fields before the player reads them means it never
// schedules an ad. A fallback skips any ad that still starts (muted, jumped to the end, skip pressed).
(() => {
  "use strict";
  if (!/(^|\.)youtube(-nocookie)?\.com$/.test(location.hostname)) return;

  const AD_KEYS = ["adPlacements", "adSlots", "playerAds", "adBreakHeartbeatParams", "adBreakParams"];
  let removed = 0;

  const prune = (o) => {
    if (!o || typeof o !== "object") return o;
    for (const key of AD_KEYS) if (key in o) { delete o[key]; removed++; }
    if (o.playerResponse && typeof o.playerResponse === "object") prune(o.playerResponse);
    // /next and the home feed: ad tiles in the page's item lists.
    return o;
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
  const parse = JSON.parse;
  JSON.parse = function (...args) { return prune(parse.apply(this, args)); };
  const json = Response.prototype.json;
  Response.prototype.json = function (...args) { return json.apply(this, args).then(prune); };

  // Ad boxes in the page itself (feed tiles, banners, the player's ad overlay), hidden from the start.
  const css = `
    ytd-ad-slot-renderer, ytd-in-feed-ad-layout-renderer, ytd-banner-promo-renderer, ytd-statement-banner-renderer,
    ytd-display-ad-renderer, ytd-promoted-sparkles-web-renderer, ytd-promoted-video-renderer, ytd-compact-promoted-video-renderer,
    ytd-player-legacy-desktop-watch-ads-renderer, ytd-engagement-panel-section-list-renderer[target-id="engagement-panel-ads"],
    #masthead-ad, #player-ads, #panels > ytd-engagement-panel-section-list-renderer[target-id="engagement-panel-ads"],
    ytd-rich-item-renderer:has(> #content > ytd-ad-slot-renderer), .ytp-ad-overlay-container, .ytp-ad-image-overlay,
    ytm-promoted-sparkles-web-renderer, ad-slot-renderer { display: none !important; }`;
  const addStyle = () => {
    const style = document.createElement("style");
    style.textContent = css;
    (document.head || document.documentElement).appendChild(style);
  };
  if (document.documentElement) addStyle(); else document.addEventListener("readystatechange", addStyle, { once: true });

  // Fallback: an ad that plays anyway is muted, run to its end and skipped.
  const skip = () => {
    const player = document.querySelector(".html5-video-player");
    if (!player || !player.classList.contains("ad-showing")) return;
    const video = player.querySelector("video");
    if (video) {
      video.muted = true;
      if (isFinite(video.duration) && video.duration > 0) video.currentTime = video.duration;
    }
    document.querySelectorAll(".ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-skip-ad-button").forEach((b) => b.click());
    removed++;
  };
  setInterval(skip, 300);

  // For the host's counter: how many ad schedules and ads were removed on this page so far.
  Object.defineProperty(window, "__advoidYouTubeRemoved", { get: () => removed });
})();
