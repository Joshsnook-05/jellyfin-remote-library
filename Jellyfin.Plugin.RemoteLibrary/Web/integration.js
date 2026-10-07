/* Remote Library's self-contained bridge for the stock Jellyfin Enhanced plugin.
 * It enriches Enhanced calendar responses and preserves Enhanced's local reviews
 * without replacing any third-party plugin files. */
(function () {
  "use strict";
  if (window.__remoteLibraryBridgeInstalled) return;
  window.__remoteLibraryBridgeInstalled = true;

  const nativeFetch = window.fetch.bind(window);
  const reviewAvatarUrls = new Map();
  const sourceBadgeCache = new Map();
  const sourceBadgeRequests = new Map();
  const api = () => (typeof ApiClient !== "undefined" ? ApiClient : null);
  const jfJson = async (path) => {
    const jeApi = window.JellyfinEnhanced?.core?.api;
    if (jeApi?.jf) return jeApi.jf(path);
    const client = api();
    if (!client?.getJSON) return null;
    return client.getJSON(client.getUrl(path));
  };
  const sourceFromTags = (tags) => (Array.isArray(tags) ? tags : [])
    .filter((tag) => /^Remote Source:\s*/i.test(tag || ""))
    .map((tag) => tag.replace(/^Remote Source:\s*/i, "").trim()).filter(Boolean)[0] || null;
  const calendarDateFromTags = (tags) => (Array.isArray(tags) ? tags : [])
    .map((tag) => String(tag || "").match(/^Remote Calendar Date:\s*(.+)$/i)?.[1]?.trim())
    .find(Boolean) || null;

  function installSourceBadgeStyles() {
    if (document.getElementById("remote-library-source-badge-styles")) return;
    const style = document.createElement("style");
    style.id = "remote-library-source-badge-styles";
    style.textContent = `
      .card .remote-library-source-badge {
        position: absolute; z-index: 30; top: .55rem; left: .55rem;
        display: inline-flex; align-items: center; max-width: calc(100% - 1.1rem);
        padding: .3rem .55rem; border: 1px solid rgba(255,255,255,.22);
        border-radius: 999px; color: #fff; background: rgba(9,20,31,.88);
        box-shadow: 0 2px 8px rgba(0,0,0,.38); font-size: .72rem;
        font-weight: 700; letter-spacing: .06em; line-height: 1;
        text-transform: uppercase; white-space: nowrap; overflow: hidden;
        text-overflow: ellipsis; pointer-events: none;
      }
      .card .remote-library-source-badge::before {
        width: .43rem; height: .43rem; flex: 0 0 auto; margin-right: .38rem;
        border-radius: 50%; background: #7dd3fc; box-shadow: 0 0 8px rgba(125,211,252,.8);
        content: "";
      }
    `;
    document.head.appendChild(style);
  }

  const cardItemId = (card) => {
    const linked = card.matches("[data-id]") ? card : card.querySelector("[data-id]");
    if (linked?.dataset?.id) return linked.dataset.id;
    const href = card.querySelector('a[href*="id="], [data-action="link"][href*="id="]')?.getAttribute("href") || "";
    return href.match(/[?&]id=([^&#]+)/)?.[1] || null;
  };

  async function remoteSourceForItem(userId, itemId) {
    const key = `${userId}:${itemId}`;
    if (sourceBadgeCache.has(key)) return sourceBadgeCache.get(key);
    let request = sourceBadgeRequests.get(key);
    if (!request) {
      request = (async () => {
        const client = api();
        const item = await client?.getItem?.(userId, itemId);
        const source = sourceFromTags(item?.Tags);
        sourceBadgeCache.set(key, source);
        return source;
      })();
      sourceBadgeRequests.set(key, request);
    }
    try { return await request; }
    finally { if (sourceBadgeRequests.get(key) === request) sourceBadgeRequests.delete(key); }
  }

  async function addSourceBadge(card) {
    const client = api();
    const userId = client?.getCurrentUserId?.();
    const itemId = cardItemId(card);
    if (!userId || !itemId) return;
    if (card.dataset.remoteLibrarySourceId !== itemId) {
      card.dataset.remoteLibrarySourceId = itemId;
      card.querySelector(".remote-library-source-badge")?.remove();
    }
    if (card.querySelector(".remote-library-source-badge, .glados-source-badge")) return;
    let source = null;
    try { source = await remoteSourceForItem(userId, itemId); }
    catch (_) { sourceBadgeCache.delete(`${userId}:${itemId}`); }
    if (!source || card.dataset.remoteLibrarySourceId !== itemId
        || card.querySelector(".remote-library-source-badge, .glados-source-badge")) return;
    const badge = document.createElement("span");
    badge.className = "remote-library-source-badge";
    badge.textContent = source;
    badge.title = `Remote source: ${source}`;
    (card.querySelector(".cardScalable") || card.querySelector(".cardBox") || card).appendChild(badge);
  }

  let badgeObserver = null;
  let badgeScanFrame = 0;
  function scanSourceBadges() {
    badgeScanFrame = 0;
    installSourceBadgeStyles();
    if (!badgeObserver && "IntersectionObserver" in window) {
      badgeObserver = new IntersectionObserver((entries) => entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        badgeObserver.unobserve(entry.target);
        addSourceBadge(entry.target).catch(() => {});
      }), { rootMargin: "250px 0px" });
    }
    document.querySelectorAll(".card").forEach((card) => {
      const itemId = cardItemId(card);
      if (!itemId || card.dataset.remoteLibraryObservedSourceId === itemId) return;
      card.dataset.remoteLibraryObservedSourceId = itemId;
      if (badgeObserver) badgeObserver.observe(card);
      else addSourceBadge(card).catch(() => {});
    });
  }
  const scheduleBadgeScan = () => {
    if (!badgeScanFrame) badgeScanFrame = window.requestAnimationFrame(scanSourceBadges);
  };
  const titleKey = (event) => {
    if (!event || event.releaseType !== "Episode" || event.seasonNumber == null || event.episodeNumber == null) return null;
    return String(event.title || "").toLowerCase().replace(/[^a-z0-9]+/g, "") + "|" + event.seasonNumber + "|" + event.episodeNumber;
  };

  async function enrichCalendar(data, requestUrl) {
    if (data?.__remoteLibraryEnriched) return data;
    if (!data || !Array.isArray(data.events) || !api()?.getCurrentUserId) return data;
    try {
      const events = data.events.slice();
      const request = new URL(requestUrl, window.location.origin);
      const start = request.searchParams.get("start") || events.reduce((v, e) => e?.releaseDate && (!v || e.releaseDate < v) ? e.releaseDate : v, null);
      const end = request.searchParams.get("end") || events.reduce((v, e) => e?.releaseDate && (!v || e.releaseDate > v) ? e.releaseDate : v, null);
      if (!start || !end) return data;
      const uid = api().getCurrentUserId();
      // Remote calendar episodes retain the real release date in a tag because
      // Jellyfin metadata providers can overwrite PremiereDate with the
      // original series date during a refresh.
      const q = new URLSearchParams({ IncludeItemTypes: "Episode", Recursive: "true", Fields: "Overview,PremiereDate,SeriesName,SeriesId,ParentId,ParentIndexNumber,IndexNumber,ProviderIds,Path,Tags", EnableUserData: "false", Limit: "5000" });
      const seriesQ = new URLSearchParams({ IncludeItemTypes: "Series", Recursive: "true", Fields: "Tags", Limit: "500" });
      const [local, series] = await Promise.all([jfJson(`/Users/${encodeURIComponent(uid)}/Items?${q}`), jfJson(`/Users/${encodeURIComponent(uid)}/Items?${seriesQ}`)]);
      const sources = new Map((series?.Items || []).map((s) => [s.Id, sourceFromTags(s.Tags)]));
      for (const item of local?.Items || []) {
        const releaseDate = calendarDateFromTags(item?.Tags) || item?.PremiereDate;
        if (!releaseDate || releaseDate < start || releaseDate > end || item.ParentIndexNumber == null || item.IndexNumber == null) continue;
        const source = sourceFromTags(item.Tags) || sources.get(item.SeriesId) || null;
        if (!source && !calendarDateFromTags(item?.Tags)) continue;
        const client = api();
        events.push({ id: `jellyfin-${item.Id}`, source: source ? "Remote Library" : "Jellyfin", instanceName: source || "Jellyfin", type: "Series", title: item.SeriesName || item.Name || "Unknown Series", subtitle: `S${String(item.ParentIndexNumber).padStart(2, "0")}E${String(item.IndexNumber).padStart(2, "0")} - ${item.Name || "Unknown Episode"}`, releaseDate, releaseType: "Episode", hasFile: !!item.Path, monitored: true, itemId: item.Id, seasonNumber: item.ParentIndexNumber, episodeNumber: item.IndexNumber, episodeTitle: item.Name || "Unknown Episode", overview: item.Overview || null, posterUrl: client?.getImageUrl?.(item.SeriesId || item.ParentId || item.Id, { type: "Primary", maxWidth: 400, quality: 85 }), tvdbId: item.ProviderIds?.Tvdb || null, tmdbId: item.ProviderIds?.Tmdb || null, imdbId: item.ProviderIds?.Imdb || null });
      }
      const merged = new Map();
      for (const event of events) {
        const key = titleKey(event) || `id:${event.id}`;
        const old = merged.get(key);
        const oldRemote = old && old.instanceName && old.instanceName !== "Jellyfin";
        const newRemote = event.instanceName && event.instanceName !== "Jellyfin";
        if (!old || (!old.posterUrl && event.posterUrl) || (!oldRemote && newRemote)) merged.set(key, event);
      }
      data.events = [...merged.values()];
      Object.defineProperty(data, "__remoteLibraryEnriched", { value: true, enumerable: false });
    } catch (e) { console.warn("Remote Library calendar bridge failed", e); }
    return data;
  }

  async function enrichReviews(url, data) {
    if (data?.__remoteLibraryEnriched) return data;
    const match = url.match(/\/JellyfinEnhanced\/reviews\/(movie|tv)\/([^/?#]+)/i);
    if (!match || !data) return data;
    try {
      const localFallback = await jfJson(`/RemoteLibrary/LocalReviews/${match[1]}/${encodeURIComponent(match[2])}`);
      const fallback = localFallback?.reviews || [];
      if (fallback.length) {
        const merged = new Map();
        for (const review of [...(data.reviews || []), ...fallback]) {
          const key = `${review.userId || ""}|${review.mediaType || match[1]}|${review.tmdbId || match[2]}`;
          merged.set(key, review);
        }
        data.reviews = [...merged.values()];
      }
      for (const review of data.reviews || []) {
        if (review?.userId && review?.avatarUrl) reviewAvatarUrls.set(String(review.userId).replace(/-/g, "").toLowerCase(), review.avatarUrl);
      }
      Object.defineProperty(data, "__remoteLibraryEnriched", { value: true, enumerable: false });
    } catch (_) { /* local reviews remain available if a remote server is unavailable */ }
    return data;
  }

  const escapeHtml = (value) => String(value ?? "").replace(/[&<>\"']/g, (ch) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[ch]));
  async function mountInfoPopupReviews() {
    const id = new URLSearchParams((window.location.hash || "").split("?")[1] || "").get("id");
    const uid = api()?.getCurrentUserId?.();
    if (!id || !uid) return;
    const item = await api()?.getItem?.(uid, id);
    if (!item) return;
    const type = item.Type === "Movie" ? "movie" : "tv";
    let seriesItem = item;
    if (type === "tv" && item.Type !== "Series" && api()?.getItem) {
      const parentId = item.SeriesId || item.ParentId;
      if (parentId) seriesItem = await api().getItem(uid, parentId) || item;
    }
    let tmdbId = type === "movie" ? item.ProviderIds?.Tmdb : (seriesItem.ProviderIds?.Tmdb || item.SeriesProviderIds?.Tmdb || item.ProviderIds?.Tmdb);
    if (!tmdbId && type === "tv" && api()?.getItems) {
      const result = await api().getItems(uid, { SearchTerm: item.SeriesName || item.Name, IncludeItemTypes: "Series", Recursive: true, Limit: 25, Fields: "ProviderIds,Name" });
      const wanted = String(item.SeriesName || item.Name || "").toLowerCase().replace(/[^a-z0-9]+/g, "");
      const match = (result?.Items || []).find((candidate) => String(candidate.Name || "").toLowerCase().replace(/[^a-z0-9]+/g, "") === wanted && candidate.ProviderIds?.Tmdb);
      tmdbId = match?.ProviderIds?.Tmdb;
    }
    if (!tmdbId) return;
    // Info-popup reviews are show/movie reviews. Always use the base numeric
    // TMDB key here; season/episode keys are handled by the full Enhanced page.
    const key = String(tmdbId);
    let local = await jfJson(`/JellyfinEnhanced/reviews/${type}/${encodeURIComponent(key)}`) || { reviews: [] };
    if (!(local.reviews || []).length) local = await jfJson(`/RemoteLibrary/LocalReviews/${type}/${encodeURIComponent(key)}`) || local;
    const reviews = local.reviews || [];
    for (const dialog of document.querySelectorAll(".dialog, [role=dialog]")) {
      if (!/\bInfo\b/i.test(dialog.textContent || "") || dialog.querySelector(".je-remote-library-info-reviews")) continue;
      const section = document.createElement("section");
      section.className = "je-remote-library-info-reviews";
      section.innerHTML = `<h3>User Reviews</h3>${reviews.length ? reviews.map((review) => `<article>${review.avatarUrl ? `<img src="${escapeHtml(api()?.getUrl?.(review.avatarUrl) || review.avatarUrl)}" alt="" width="40" height="40" style="border-radius:50%;object-fit:cover;vertical-align:middle;margin-right:.5em">` : ""}<strong>${escapeHtml(review.userName || "User")}</strong>${review.source ? ` <small>(${escapeHtml(review.source)})</small>` : ""}${review.rating ? ` <span>★ ${escapeHtml(review.rating)}</span>` : ""}<p>${escapeHtml(review.content || "")}</p></article>`).join("") : "<p>No user reviews.</p>"}`;
      const body = dialog.querySelector(".dialogContent, .dialog-content, .formDialogContent") || dialog;
      body.appendChild(section);
    }
  }
  let infoPopupTimer = 0;
  const watchInfoPopups = () => {
    if (infoPopupTimer) return;
    infoPopupTimer = window.setTimeout(() => { infoPopupTimer = 0; mountInfoPopupReviews().catch(() => {}); }, 150);
  };
  new MutationObserver(() => { watchInfoPopups(); scheduleBadgeScan(); })
    .observe(document.body, { childList: true, subtree: true });
  watchInfoPopups();
  scheduleBadgeScan();

  window.fetch = async function (input, init) {
    const response = await nativeFetch(input, init);
    const url = typeof input === "string" ? input : (input?.url || "");
    if (!/JellyfinEnhanced\/(arr\/calendar|reviews\/)/i.test(url) || !response.ok) return response;
    try {
      let body = await response.clone().json();
      if (/\/arr\/calendar/i.test(url)) body = await enrichCalendar(body, url);
      else body = await enrichReviews(url, body);
      const headers = new Headers(response.headers); headers.delete("content-length");
      return new Response(JSON.stringify(body), { status: response.status, statusText: response.statusText, headers });
    } catch (_) { return response; }
  };
  // Enhanced's API client can be initialized after this bridge (and keeps its
  // own request wrapper), so hook that public API as well as the browser fetch
  // primitive. This is the path used by current stock Enhanced builds.
  let hookedApi = null;
  let hookedClient = null;
  let calendarRefreshDone = false;
  const hookItemMetadata = () => {
    const client = api();
    if (!client || client === hookedClient || typeof client.getItem !== "function") return;
    const originalGetItem = client.getItem.bind(client);
    const originalGetUrl = typeof client.getUrl === "function" ? client.getUrl.bind(client) : null;
    client.getItem = async function (...args) {
      const item = await originalGetItem(...args);
      if (item && (item.Type === "Season" || item.Type === "Episode")) {
        item.SeriesId = item.SeriesId || item.ParentId;
        if (!item.SeriesProviderIds?.Tmdb && item.SeriesId && item.SeriesId !== item.Id) {
          try {
            const parent = await originalGetItem(args[0], item.SeriesId);
            if (parent?.ProviderIds) item.SeriesProviderIds = { ...(item.SeriesProviderIds || {}), ...parent.ProviderIds };
          } catch (_) { /* Enhanced will use its normal fallback */ }
        }
      }
      return item;
    };
    if (originalGetUrl) {
      client.getUrl = function (path, ...args) {
        const match = String(path || "").match(/^\/Users\/([^/]+)\/Images\/Primary$/i);
        const avatarUrl = match && reviewAvatarUrls.get(match[1].replace(/-/g, "").toLowerCase());
        return avatarUrl ? originalGetUrl(avatarUrl) : originalGetUrl(path, ...args);
      };
    }
    hookedClient = client;
  };
  const hookEnhancedApi = () => {
    const jeApi = window.JellyfinEnhanced?.core?.api;
    if (!jeApi || jeApi === hookedApi || typeof jeApi.plugin !== "function") return;
    const originalPlugin = jeApi.plugin;
    jeApi.plugin = async function (path, options) {
      const result = await originalPlugin.call(this, path, options);
      const text = String(path || "");
      if (/\/arr\/calendar/i.test(text)) return enrichCalendar(result, text);
      if (/\/reviews\/(movie|tv)\//i.test(text)) {
        return enrichReviews(text.includes("JellyfinEnhanced") ? text : `/JellyfinEnhanced${text}`, result);
      }
      return result;
    };
    hookedApi = jeApi;
  };
  const refreshVisibleCalendar = () => {
    if (calendarRefreshDone || !hookedApi) return;
    const JE = window.JellyfinEnhanced;
    const state = JE?.internals?.calendarPage?.state;
    const calendarVisible = !!state?.pageVisible
      || !!document.querySelector("#je-calendar-page:not(.hide), .jellyfinenhanced.calendar:not(.hide), #je-calendar-container-tab");
    if (!calendarVisible || typeof JE?.calendarPage?.refresh !== "function") return;
    calendarRefreshDone = true;
    Promise.resolve(JE.calendarPage.refresh()).catch((error) => {
      calendarRefreshDone = false;
      console.warn("Remote Library calendar refresh failed", error);
    });
  };
  hookItemMetadata();
  hookEnhancedApi();
  window.setInterval(() => { hookItemMetadata(); hookEnhancedApi(); refreshVisibleCalendar(); }, 500);
  console.info("Remote Library: Jellyfin Enhanced compatibility bridge loaded");
})();
