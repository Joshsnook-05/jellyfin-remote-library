/* Remote Library's self-contained bridge for the stock Jellyfin Enhanced plugin.
 * It enriches Enhanced calendar responses and preserves Enhanced's local reviews
 * without replacing any third-party plugin files. */
(function () {
  "use strict";
  if (window.__remoteLibraryBridgeInstalled) return;
  window.__remoteLibraryBridgeInstalled = true;

  let featuresPromise;
  const features = () => featuresPromise ||= jfJson("/RemoteLibrary/Features").catch(() => ({}));
  const nativeFetch = window.fetch.bind(window);
  const reviewAvatarUrls = new Map();
  const sourceBadgeCache = new Map();
  const sourceBadgeRequests = new Map();
  const reachabilityCache = { checkedAt: 0, sources: [] };
  let trustPanelTimer = 0;
  const api = () => (typeof ApiClient !== "undefined" ? ApiClient : null);
  const jfJson = async (path) => {
    const jeApi = window.JellyfinEnhanced?.core?.api;
    if (jeApi?.jf) return jeApi.jf(path);
    const client = api();
    if (!client?.getJSON) return null;
    return client.getJSON(client.getUrl(path));
  };
  const sourcesFromTags = (tags) => [...new Set((Array.isArray(tags) ? tags : [])
    .filter((tag) => /^Remote Source:\s*/i.test(tag || ""))
    .map((tag) => tag.replace(/^Remote Source:\s*/i, "").trim()).filter(Boolean))];
  const sourceFromTags = (tags) => sourcesFromTags(tags)[0] || null;
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
        padding: .3rem .55rem; border: 1px solid rgba(255,255,255,.46);
        border-radius: 999px; color: #fff; background: linear-gradient(145deg,rgba(255,255,255,.3),rgba(255,255,255,.13));
        box-shadow: 0 8px 22px rgba(0,0,0,.24),inset 0 1px rgba(255,255,255,.35);font-size: .72rem;
        backdrop-filter:blur(18px) saturate(155%) brightness(1.1);-webkit-backdrop-filter:blur(18px) saturate(155%) brightness(1.1);
        font-weight: 700; letter-spacing: .06em; line-height: 1;
        text-transform: uppercase; white-space: nowrap; overflow: hidden;
        text-overflow: ellipsis; pointer-events: none;
      }
      .card .remote-library-source-badge::before {
        width: .43rem; height: .43rem; flex: 0 0 auto; margin-right: .38rem;
        border-radius: 50%; background: #7dd3fc; box-shadow: 0 0 8px rgba(125,211,252,.8);
        content: "";
      }
      .card .remote-library-source-badge.is-offline::before { background:#fb7185;box-shadow:0 0 8px rgba(251,113,133,.8); }
      #itemDetailPage .mainDetailButtons,#itemDetailPage .detailPagePrimaryContent { overflow:visible!important; }
      .remote-library-trust { position:relative;z-index:12;display:inline-flex;margin:.35rem .45rem .35rem 0;vertical-align:middle;overflow:visible; }
      .remote-library-trust__button { display:inline-flex;align-items:center;gap:.46rem;min-height:2.4rem;padding:.5rem .8rem;border:1px solid rgba(255,255,255,.46);border-radius:999px;color:#fff;background:linear-gradient(145deg,rgba(255,255,255,.3),rgba(255,255,255,.12));box-shadow:0 10px 28px rgba(0,0,0,.2),inset 0 1px rgba(255,255,255,.38);cursor:pointer;font:inherit;font-size:.82rem;font-weight:750;backdrop-filter:blur(20px) saturate(155%) brightness(1.12);-webkit-backdrop-filter:blur(20px) saturate(155%) brightness(1.12); }
      .remote-library-trust__button:focus-visible { outline:2px solid #84d8ff;outline-offset:3px; }
      .remote-library-trust__dot { width:.52rem;height:.52rem;border-radius:50%;color:#5ee6a8;background:currentColor;box-shadow:0 0 9px currentColor; }
      .remote-library-trust.is-offline .remote-library-trust__dot { color:#fb7185; }
      .remote-library-trust__panel { position:absolute;z-index:2250;top:calc(100% + .55rem);left:0;box-sizing:border-box;width:min(24rem,calc(100vw - 2rem));padding:1rem;border:1px solid rgba(255,255,255,.5);border-radius:18px;color:#fff;background:linear-gradient(145deg,rgba(255,255,255,.34),rgba(255,255,255,.14));box-shadow:0 24px 55px rgba(0,0,0,.32),inset 0 1px rgba(255,255,255,.42);backdrop-filter:blur(28px) saturate(155%) brightness(1.08);-webkit-backdrop-filter:blur(28px) saturate(155%) brightness(1.08); }
      .remote-library-trust__panel[hidden] { display:none; }
      .remote-library-trust__panel strong { display:block;margin-bottom:.35rem; }
      .remote-library-trust__panel p { margin:.3rem 0 .8rem;color:rgba(255,255,255,.8);line-height:1.45; }
      .remote-library-trust__panel label { display:block;margin:.8rem 0 .35rem;color:rgba(255,255,255,.78);font-size:.75rem;font-weight:800;letter-spacing:.06em;text-transform:uppercase; }
      .remote-library-trust__panel select { box-sizing:border-box;width:100%;min-height:2.5rem;padding:.45rem .65rem;border:1px solid rgba(255,255,255,.5);border-radius:10px;color:#17202b;background:rgba(255,255,255,.86);font:inherit; }
      .remote-library-trust__panel option { color:#17202b;background:#fff; }
      .remote-library-trust__hint { display:block;margin-top:.55rem;color:rgba(255,255,255,.68);font-size:.72rem;line-height:1.4; }
      @media(max-width:50em){.remote-library-trust__panel{position:fixed;top:auto;right:1rem;bottom:5.3rem;left:1rem;width:auto;}}
    `;
    document.head.appendChild(style);
  }

  const cardItemId = (card) => {
    const linked = card.matches("[data-id]") ? card : card.querySelector("[data-id]");
    if (linked?.dataset?.id) return linked.dataset.id;
    const href = card.querySelector('a[href*="id="], [data-action="link"][href*="id="]')?.getAttribute("href") || "";
    return href.match(/[?&]id=([^&#]+)/)?.[1] || null;
  };

  async function remoteSourcesForItem(userId, itemId) {
    const key = `${userId}:${itemId}`;
    if (sourceBadgeCache.has(key)) return sourceBadgeCache.get(key);
    let request = sourceBadgeRequests.get(key);
    if (!request) {
      request = (async () => {
        const client = api();
        const item = await client?.getItem?.(userId, itemId);
        let sources = sourcesFromTags(item?.Tags);
        if (!sources.length && ["Series", "Season"].includes(item?.Type) && client?.getItems) {
          const children = await client.getItems(userId, {
            ParentId: itemId, Recursive: true, IncludeItemTypes: "Episode", Fields: "Tags", Limit: 500
          });
          sources = [...new Set((children?.Items || []).flatMap(child => sourcesFromTags(child?.Tags)))];
        }
        sourceBadgeCache.set(key, sources);
        return sources;
      })();
      sourceBadgeRequests.set(key, request);
    }
    try { return await request; }
    finally { if (sourceBadgeRequests.get(key) === request) sourceBadgeRequests.delete(key); }
  }

  async function remoteSourceForItem(userId, itemId) {
    return (await remoteSourcesForItem(userId, itemId))[0] || null;
  }

  async function addSourceBadge(card) {
    if (document.getElementById("gladosPrimaryNav")) {
      card.querySelectorAll(".remote-library-source-badge").forEach(badge => badge.remove());
      return;
    }
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
    const itemSources = await remoteSourcesForItem(userId, itemId);
    const badge = document.createElement("span");
    badge.className = "remote-library-source-badge";
    badge.textContent = (await features())?.showServerNameOnBadges ? itemSources.join(" / ") : "Remote";
    const sources = await loadReachability();
    const health = sources.find(entry => entry.label.toLowerCase() === source.toLowerCase());
    badge.classList.toggle("is-offline", health?.online === false);
    badge.title = health?.online === false
      ? `${source} is offline; playback will return when it reconnects.`
      : `Remote source: ${source}${health ? " (online)" : ""}`;
    (card.querySelector(".cardScalable") || card.querySelector(".cardBox") || card).appendChild(badge);
  }

  let badgeObserver = null;
  let badgeScanFrame = 0;
  function scanSourceBadges() {
    badgeScanFrame = 0;
    installSourceBadgeStyles();
    if (document.getElementById("gladosPrimaryNav")) {
      document.querySelectorAll(".remote-library-source-badge").forEach(badge => badge.remove());
      features().then(settings => document.querySelectorAll('.glados-source-badge').forEach(badge => {
        const label = settings?.showServerNameOnBadges && badge.dataset.source ? badge.dataset.source : 'Remote';
        if (badge.textContent !== label) badge.textContent = label;
      })).catch(() => {});
      return;
    }
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

  async function loadReachability(force = false) {
    if (!force && Date.now() - reachabilityCache.checkedAt < 8000) return reachabilityCache.sources;
    try {
      const data = await jfJson("/RemoteLibrary/Reachability");
      reachabilityCache.sources = (data?.Servers || data?.servers || []).map(entry => ({
        label: entry.SourceLabel || entry.sourceLabel || "Remote",
        online: entry.Online ?? entry.online ?? false,
        latencyMs: entry.LatencyMs ?? entry.latencyMs ?? Number.POSITIVE_INFINITY
      }));
      reachabilityCache.checkedAt = Date.now();
    } catch (_) { /* Cached state remains useful during a transient failure. */ }
    return reachabilityCache.sources;
  }

  function detailItemId() {
    return new URLSearchParams((location.hash || "").split("?")[1] || "").get("id");
  }

  const itemHasLocalFile = item => !!item?.Path
    && !item.IsVirtualItem
    && !/\.strm$/i.test(item.Path)
    && !/^\/remote-library\//i.test(item.Path);

  const playbackChoices = new Map();
  const preferenceKey = () => `remote-library-language:${api()?.serverId?.() || location.host}:${api()?.getCurrentUserId?.()}`;
  const choiceKey = id => `${api()?.serverId?.() || location.host}:${api()?.getCurrentUserId?.()}:${id.replace(/-/g, '').toLowerCase()}`;
  const normalizeLanguage = value => {
    const code = String(value || '').toLowerCase().split(/[-_]/)[0];
    return ({eng:'en',fra:'fr',fre:'fr',deu:'de',ger:'de',spa:'es',ita:'it',por:'pt',jpn:'ja',kor:'ko',zho:'zh',chi:'zh',rus:'ru',ara:'ar',hin:'hi',nld:'nl',dut:'nl',pol:'pl',swe:'sv',nor:'no',dan:'da',fin:'fi',tur:'tr',ukr:'uk',ces:'cs',cze:'cs'})[code] || code;
  };
  const audioLanguages = source => [...new Set((source.MediaStreams || [])
    .filter(stream => stream.Type === 'Audio').map(stream => normalizeLanguage(stream.Language)).filter(code => code && code !== 'und'))];
  const quality = source => {
    const video = (source.MediaStreams || []).find(stream => stream.Type === 'Video');
    return { height: video?.Height || 0, pixels: (video?.Width || 0) * (video?.Height || 0), bitrate: source.Bitrate || video?.BitRate || 0 };
  };
  const qualityLabel = source => {
    const q = quality(source);
    return q.height ? `${q.height}p${q.bitrate ? ` · ${(q.bitrate / 1000000).toFixed(1)} Mbps` : ''}` : 'Quality unknown';
  };
  function playbackSelection(url, method) {
    if (String(method).toUpperCase() !== 'POST') return null;
    let parsed;
    try { parsed = new URL(url, location.href); }
    catch (_) { return null; }
    const match = parsed.pathname.match(/\/Items\/([^/]+)\/PlaybackInfo\/?$/i);
    // This interceptor is installed on the anonymous login page. Reject every
    // unrelated POST before consulting ApiClient, whose server state is not yet
    // initialized and may throw from getUrl() until authentication completes.
    if (!match) return null;
    const client = api();
    if (!client?.getCurrentUserId?.()) return null;
    let base;
    try { base = new URL(client.getUrl?.('') || location.href, location.href); }
    catch (_) { return null; }
    if (parsed.origin !== base.origin) return null;
    const choice = playbackChoices.get(choiceKey(match[1]));
    if (!choice) return null;
    parsed.searchParams.set('MediaSourceId', choice);
    return { url: parsed.href, choice };
  }
  function selectedBody(body, choice) {
    if (typeof body !== 'string' || !body.trim()) return body;
    try { return JSON.stringify({ ...JSON.parse(body), MediaSourceId: choice }); }
    catch (_) { return body; }
  }
  // Older Web clients use XHR; current SDK clients use fetch.
  const xhrOpen = XMLHttpRequest.prototype.open;
  const xhrSend = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function (method, url, ...args) {
    this.remoteLibrarySelection = playbackSelection(url, method);
    return xhrOpen.call(this, method, this.remoteLibrarySelection?.url || url, ...args);
  };
  XMLHttpRequest.prototype.send = function (body) {
    return xhrSend.call(this, this.remoteLibrarySelection ? selectedBody(body, this.remoteLibrarySelection.choice) : body);
  };

  let mountingDetails = false;
  async function mountTrustPanel() {
    if (mountingDetails) return;
    const client = api();
    const userId = client?.getCurrentUserId?.();
    const id = detailItemId();
    const page = document.querySelector('#itemDetailPage:not(.hide)');
    if (!userId || !id || !page) return;
    const old = page.querySelector('.remote-library-trust');
    if (old?.dataset.itemId === `${userId}:${id}`) return;
    old?.remove();
    mountingDetails = true;
    try {
      const item = await client.getItem(userId, id);
      const names = await remoteSourcesForItem(userId, id);
      if (!['Movie', 'Episode'].includes(item.Type)) {
        if (names.length && detailItemId() === id) {
          const note = document.createElement('div');
          note.className = 'remote-library-trust';
          note.dataset.itemId = `${userId}:${id}`;
          note.textContent = `Available remote servers: ${names.join(', ')}. Open an episode to compare and select its server.`;
          page.querySelector('.detailPagePrimaryContent')?.append(note);
        }
        return;
      }
      const data = await jfJson(`/Items/${encodeURIComponent(id)}/PlaybackInfo?UserId=${encodeURIComponent(userId)}`);
      if (detailItemId() !== id || client.getCurrentUserId() !== userId || !page.isConnected) return;
      const sources = data?.MediaSources || data?.mediaSources || [];
      const health = await loadReachability();
      if (detailItemId() !== id || client.getCurrentUserId() !== userId) return;
      if (!sources.length) return;
      const remote = source => source.Id?.includes('remote_');
      if (!names.length && !sources.some(remote)) return;
      const sourceName = source => remote(source) ? source.Name.split(' · ')[0]
        : itemHasLocalFile(item) ? 'Local' : names[0] || 'Remote';
      const offline = source => health.some(server => server.label.toLowerCase() === sourceName(source).toLowerCase() && !server.online);
      const host = document.createElement('div');
      host.className = 'remote-library-trust remote-library-source-box';
      host.dataset.itemId = `${userId}:${id}`;
      host.style.cssText = 'display:block;padding:1rem;margin:1rem 0;border:1px solid #ffffff40;border-radius:12px;background:#ffffff0a;max-width:48rem';
      const heading = document.createElement('strong');
      const serverLabel = document.createElement('label');
      serverLabel.textContent = 'Playback server ';
      const select = document.createElement('select');
      select.setAttribute('aria-label', 'Playback server');
      select.style.cssText = 'max-width:100%;padding:.5rem;margin:.5rem;color:#111;background:#fff';
      sources.forEach(source => {
        const option = document.createElement('option');
        option.value = source.Id;
        option.textContent = `${sourceName(source)} · ${qualityLabel(source)}${offline(source) ? " · Offline" : ""}`;
        option.disabled = offline(source);
        select.append(option);
      });
      const stored = playbackChoices.get(choiceKey(id));
      const selected = sources.find(source => source.Id === stored && !offline(source)) || sources.find(source => !offline(source)) || sources[0];
      select.value = selected.Id;
      // Pin the displayed choice so the label describes the actual playback route.
      playbackChoices.set(choiceKey(id), selected.Id);
      const summary = document.createElement('p');
      const languageLabel = document.createElement('label');
      languageLabel.textContent = 'Preferred audio language ';
      const language = document.createElement('select');
      language.setAttribute('aria-label', 'Preferred audio language');
      language.style.cssText = select.style.cssText;
      let preferred = '';
      try { preferred = normalizeLanguage(localStorage.getItem(preferenceKey())); } catch (_) { /* storage may be disabled */ }
      const codes = [...new Set(['en','fr','de','es','it','pt','ja','ko','zh','ru','ar','hi','nl','pl','sv','no','da','fi','tr','uk','cs', preferred,
        ...sources.flatMap(audioLanguages)])].filter(Boolean).sort();
      language.append(new Option('No preference', ''));
      codes.forEach(code => {
        let name = code;
        try { name = new Intl.DisplayNames([navigator.language || 'en'], {type:'language'}).of(code) || code; } catch (_) { /* use code */ }
        language.append(new Option(name, code));
      });
      language.value = preferred;
      const languageInfo = document.createElement('p');
      const status = document.createElement('small');
      status.setAttribute('role', 'status');
      status.textContent = 'Selection applies to the next playback in this browser. Quality compares reported resolution, then bitrate. Language matches audio tracks.';
      const render = () => {
        const current = sources.find(source => source.Id === select.value);
        heading.textContent = `Selected server: ${sourceName(current)}${offline(current) ? " (offline)" : ""}`;
        const ranked = sources.filter(source => !offline(source) && quality(source).pixels).sort((a,b) => quality(b).pixels - quality(a).pixels || quality(b).bitrate - quality(a).bitrate);
        const best = ranked[0];
        const ties = best ? ranked.filter(source => quality(source).pixels === quality(best).pixels && quality(source).bitrate === quality(best).bitrate) : [];
        summary.textContent = best ? `Best reported quality: ${[...new Set(ties.map(sourceName))].join(', ')} · ${qualityLabel(best)}.` : 'Best quality: metadata unavailable.';
        const matches = sources.filter(source => !offline(source) && audioLanguages(source).includes(language.value));
        languageInfo.textContent = !language.value ? 'Choose a language to see matching servers.' : matches.length
          ? `Preferred audio available on: ${[...new Set(matches.map(sourceName))].join(', ')}.`
          : 'No source reports your preferred audio language. Missing language metadata may hide a match.';
      };
      select.addEventListener('change', () => { playbackChoices.set(choiceKey(id), select.value); render(); });
      language.addEventListener('change', () => {
        try { localStorage.setItem(preferenceKey(), language.value); } catch (_) { /* preference still works for this page */ }
        render();
      });
      serverLabel.append(select);
      languageLabel.append(language);
      host.append(heading, document.createElement('br'), serverLabel, summary, languageLabel, languageInfo, status);
      render();
      const overview = page.querySelector('.overview');
      if (overview) overview.before(host);
      else page.querySelector('.detailPagePrimaryContent, .mainDetailButtons')?.append(host);
    } finally {
      mountingDetails = false;
      if (detailItemId() !== id) scheduleTrustPanel();
    }
  }

  function scheduleTrustPanel() {
    window.clearTimeout(trustPanelTimer);
    trustPanelTimer = window.setTimeout(() => mountTrustPanel().catch(() => {}), 120);
  }
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
  new MutationObserver(() => { watchInfoPopups(); scheduleBadgeScan(); scheduleTrustPanel(); })
    .observe(document.body, { childList: true, subtree: true });
  watchInfoPopups();
  scheduleBadgeScan();
  scheduleTrustPanel();
  window.addEventListener("hashchange", scheduleTrustPanel);

  window.fetch = async function (input, init) {
    const requestUrl = typeof input === 'string' || input instanceof URL ? String(input) : input.url;
    const selection = playbackSelection(requestUrl, init?.method || input?.method || 'GET');
    if (selection) {
      if (input instanceof Request) {
        const request = new Request(input, init);
        const body = await request.clone().text();
        input = new Request(selection.url, { method: request.method, headers: request.headers,
          credentials: request.credentials, signal: request.signal, mode: request.mode,
          body: selectedBody(body, selection.choice) });
        init = undefined;
      } else {
        input = selection.url;
        init = { ...init, body: selectedBody(init?.body, selection.choice) };
      }
    }
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
