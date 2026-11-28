// Dislike counts in the YouTube window, which YouTube stopped showing in 2021. The counts come from Return YouTube
// Dislike (returnyoutubedislike.com), an estimate from archived counts and its users' votes. This script only asks
// the app (YouTubeDislikes.cs) for the open video's count and writes it next to the dislike button. The app does the
// fetching, so YouTube's content security policy doesn't apply, and it caches and rate-limits the API calls.
// Watch pages only (not Shorts). This app's own code, written for it, not the extension's.
(() => {
  "use strict";
  if (window.top !== window || !window.chrome?.webview || !/(^|\.)youtube\.com$/.test(location.hostname)) return;

  const CREDIT = "Dislikes estimated by Return YouTube Dislike (returnyoutubedislike.com)";
  const counts = new Map(); // video ID -> count, null while asked, -1 when unknown
  let format;

  const videoId = () => location.pathname === "/watch" ? new URLSearchParams(location.search).get("v") : null;

  // The same short form YouTube uses for likes ("2.7k"), in the page's language.
  const text = (n) => {
    format ??= new Intl.NumberFormat(document.documentElement.lang || undefined, { notation: "compact", maximumFractionDigits: 1 });
    return format.format(n);
  };

  // Watch page: the dislike button is icon-only; it gets a label like the like button's (same classes, copied from it,
  // since YouTube renames them: yt-spec-button-shape-next__button-text-content became ytSpecButtonShapeNextButtonTextContent
  // in 2026) and the like button's icon-and-label layout.
  const showOnWatchPage = (id, count) => {
    const button = document.querySelector("ytd-watch-metadata dislike-button-view-model button, ytd-watch-metadata #segmented-dislike-button button");
    if (!button) return;
    let label = button.querySelector("[data-ge-dislikes]");
    if (!label) {
      const likeLabel = document.querySelector("ytd-watch-metadata like-button-view-model button [class*='ButtonTextContent'], "
        + "ytd-watch-metadata like-button-view-model button .yt-spec-button-shape-next__button-text-content");
      label = document.createElement("div");
      label.className = likeLabel?.className || "ytSpecButtonShapeNextButtonTextContent yt-spec-button-shape-next__button-text-content";
      label.dataset.geDislikes = "";
      const icon = button.querySelector("[class*='ShapeNextIcon'], .yt-spec-button-shape-next__icon");
      if (icon) icon.after(label); else button.appendChild(label);
      button.classList.replace("ytSpecButtonShapeNextIconButton", "ytSpecButtonShapeNextIconLeading");
      button.classList.replace("yt-spec-button-shape-next--icon-button", "yt-spec-button-shape-next--icon-leading");
    }
    if (label.dataset.geDislikes === id && label.textContent) return;
    label.dataset.geDislikes = id;
    label.textContent = text(count);
    label.title = CREDIT;
  };

  const update = () => {
    const id = videoId();
    if (!id) return;
    if (!counts.has(id)) {
      counts.set(id, null);
      chrome.webview.postMessage({ type: "dislikes", id });
      return;
    }
    const count = counts.get(id);
    if (count === null || count < 0) return;
    showOnWatchPage(id, count);
  };

  chrome.webview.addEventListener("message", (e) => {
    if (e.data?.type !== "dislikes" || typeof e.data.id !== "string") return;
    counts.set(e.data.id, typeof e.data.dislikes === "number" ? e.data.dislikes : -1);
    update();
  });
  // YouTube is a single-page app and redraws its buttons as it likes, so the label is checked twice a second.
  setInterval(update, 500);
})();
