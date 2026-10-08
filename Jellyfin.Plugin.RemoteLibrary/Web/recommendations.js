/* Combined Jellyfin, Remote Library, and optional Seerr recommendations. */
(function () {
  "use strict";
  if (window.__remoteLibraryRecommendationsManaged) return;
  window.__remoteLibraryRecommendationsManaged = true;

  const ROW_ID = "gladosRecommendations";
  const BROWSER_ID = "gladosRecommendationsBrowser";
  const state = { enabled: null, loading: false, dirty: true, userId: null, items: [], builtAt: 0 };
  let homeObserver = null;
  let observedHome = null;
  let repairTimer = 0;

  const api = () => (typeof ApiClient !== "undefined" ? ApiClient : null);
  const make = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  };
  const normalize = (value) => String(value || "").toLowerCase().replace(/[^a-z0-9]+/g, "");
  const escapeCssUrl = (value) => String(value).replace(/["\\\n\r]/g, "\\$&");
  const currentHome = () => [...document.querySelectorAll(".homePage")]
    .find((page) => !page.classList.contains("hide") && page.getAttribute("aria-hidden") !== "true") || null;
  const sourceFromTags = (tags) => (Array.isArray(tags) ? tags : [])
    .map((tag) => String(tag || "").match(/^Remote Source:\s*(.+)$/i)?.[1]?.trim()).find(Boolean) || null;

  function canonicalKey(item, mediaType) {
    const ids = item?.ProviderIds || {};
    if (ids.Tmdb) return `${mediaType || item.Type}:tmdb:${ids.Tmdb}`;
    if (ids.Imdb) return `${mediaType || item.Type}:imdb:${ids.Imdb}`;
    if (ids.Tvdb) return `${mediaType || item.Type}:tvdb:${ids.Tvdb}`;
    return `${mediaType || item?.Type}:${normalize(item?.Name || item?.title || item?.name)}:${item?.ProductionYear || String(item?.releaseDate || item?.firstAirDate || "").slice(0, 4)}`;
  }

  async function featureEnabled() {
    if (state.enabled !== null) return state.enabled;
    try {
      const client = api();
      const result = client?.getJSON ? await client.getJSON(client.getUrl("RemoteLibrary/Features")) : null;
      state.enabled = result?.enableRecommendations !== false;
    } catch (_) {
      state.enabled = true;
    }
    return state.enabled;
  }

  async function loadItems(client, userId, options) {
    const result = typeof client.getItems === "function"
      ? await client.getItems(userId, options)
      : await client.getJSON(client.getUrl(`Users/${userId}/Items`, options));
    return result?.Items || result?.items || [];
  }

  async function preferenceSeeds(client, userId) {
    const fields = "ProviderIds,Genres,UserData,ProductionYear";
    const [favourites, activity] = await Promise.all([
      loadItems(client, userId, { IncludeItemTypes: "Movie,Series", Recursive: true, Filters: "IsFavorite", Fields: fields, Limit: 12 }),
      loadItems(client, userId, { IncludeItemTypes: "Movie,Series,Episode", Recursive: true, Filters: "IsPlayed", SortBy: "DatePlayed", SortOrder: "Descending", Fields: `${fields},SeriesId`, Limit: 24 })
    ]);
    const seriesIds = [...new Set(activity.filter((item) => item.Type === "Episode" && item.SeriesId).map((item) => item.SeriesId))];
    const resolved = new Map((await Promise.allSettled(seriesIds.map((id) => client.getItem(userId, id))))
      .filter((result) => result.status === "fulfilled" && result.value?.Id).map((result) => [result.value.Id, result.value]));
    const watched = [];
    const seen = new Set();
    for (const activityItem of activity) {
      const item = activityItem.Type === "Episode" ? resolved.get(activityItem.SeriesId) : activityItem;
      if (!item?.Id || seen.has(item.Id)) continue;
      seen.add(item.Id);
      watched.push(item);
    }
    const favouriteIds = new Set(favourites.map((item) => item.Id));
    return [
      ...favourites.slice(0, 5).map((item) => ({ item, weight: 10 })),
      ...watched.filter((item) => !favouriteIds.has(item.Id)).slice(0, 3).map((item) => ({ item, weight: 3 }))
    ];
  }

  async function waitForSeerr(timeout = 12000) {
    const started = Date.now();
    while (Date.now() - started < timeout) {
      const je = window.JellyfinEnhanced;
      if (je?.jellyseerrAPI && je?.jellyseerrUI?.createJellyseerrCard) return je;
      await new Promise((resolve) => window.setTimeout(resolve, 250));
    }
    return null;
  }

  async function buildRecommendations(client, userId) {
    const seeds = await preferenceSeeds(client, userId);
    if (!seeds.length) return [];
    const seedKeys = new Set(seeds.map((seed) => canonicalKey(seed.item, seed.item.Type)));
    const jellyfin = new Map();
    await Promise.allSettled(seeds.map(async (seed) => {
      const result = await client.getJSON(client.getUrl(`Items/${seed.item.Id}/Similar`, {
        UserId: userId, Limit: 16, Fields: "ProviderIds,Overview,Genres,ProductionYear,UserData,Path,Tags"
      }));
      for (const item of result?.Items || result?.items || []) {
        if (!item?.Id || item.UserData?.Played) continue;
        const key = canonicalKey(item, item.Type);
        if (seedKeys.has(key)) continue;
        const existing = jellyfin.get(key);
        const candidate = existing || { kind: "local", item, score: 0 };
        candidate.score += seed.weight + (Number(item.CommunityRating) || 0) * .04;
        if (existing && !sourceFromTags(existing.item.Tags) && sourceFromTags(item.Tags)) candidate.item = item;
        jellyfin.set(key, candidate);
      }
    }));

    const seerr = new Map();
    const je = await waitForSeerr();
    if (je) {
      await Promise.allSettled(seeds.filter((seed) => seed.item.ProviderIds?.Tmdb).map(async (seed) => {
        const isMovie = seed.item.Type === "Movie";
        const result = isMovie
          ? await je.jellyseerrAPI.fetchRecommendedMovies(seed.item.ProviderIds.Tmdb)
          : await je.jellyseerrAPI.fetchRecommendedTvShows(seed.item.ProviderIds.Tmdb);
        for (const item of result?.results || []) {
          if (!item?.id || !item.posterPath || item.adult || item.mediaInfo?.jellyfinMediaId) continue;
          const mediaType = item.mediaType || (isMovie ? "movie" : "tv");
          const key = `${mediaType === "tv" ? "Series" : "Movie"}:tmdb:${item.id}`;
          if (jellyfin.has(key) || seedKeys.has(key)) continue;
          const entry = seerr.get(key) || { kind: "seerr", item: { ...item, mediaType }, score: 0 };
          entry.score += seed.weight + (Number(item.voteAverage) || 0) * .04;
          seerr.set(key, entry);
        }
      }));
    }
    const localRanked = [...jellyfin.values()].sort((a, b) => b.score - a.score).slice(0, 24);
    const seerrRanked = [...seerr.values()].sort((a, b) => b.score - a.score).slice(0, 24);
    const blended = [];
    while (blended.length < 40 && (localRanked.length || seerrRanked.length)) {
      if (localRanked.length) blended.push(localRanked.shift());
      if (localRanked.length && blended.length < 40) blended.push(localRanked.shift());
      if (seerrRanked.length && blended.length < 40) blended.push(seerrRanked.shift());
      if (seerrRanked.length && blended.length < 40) blended.push(seerrRanked.shift());
    }
    return blended;
  }

  const titleOf = (entry) => entry.kind === "local" ? entry.item.Name : entry.item.title || entry.item.name || "Recommended title";
  const sourceOf = (entry) => entry.kind === "local" ? sourceFromTags(entry.item.Tags) : null;
  const filterOf = (entry) => entry.kind === "seerr" ? "seerr" : sourceOf(entry) ? "remote" : "local";
  const metaOf = (entry) => {
    if (entry.kind === "local") return [entry.item.ProductionYear, entry.item.Type === "Series" ? "Series" : "Movie"].filter(Boolean).join(" · ");
    const year = String(entry.item.releaseDate || entry.item.firstAirDate || "").slice(0, 4);
    return [year, entry.item.mediaType === "tv" ? "Series" : "Movie"].filter(Boolean).join(" · ");
  };
  const imageOf = (client, entry, portrait) => {
    if (entry.kind === "local") return client.getUrl(`Items/${entry.item.Id}/Images/${portrait ? "Primary" : "Backdrop/0"}`, { maxWidth: portrait ? 600 : 900, quality: 90 });
    const path = portrait ? entry.item.posterPath || entry.item.backdropPath : entry.item.backdropPath || entry.item.posterPath;
    return path ? `https://image.tmdb.org/t/p/${portrait ? "w500" : "w780"}${path}` : "";
  };

  function activate(entry) {
    closeBrowser();
    if (entry.kind === "local") {
      window.location.hash = `#/details?id=${encodeURIComponent(entry.item.Id)}`;
      return;
    }
    const sourceCard = window.JellyfinEnhanced?.jellyseerrUI?.createJellyseerrCard?.(entry.item, true, true);
    if (!sourceCard) return;
    sourceCard.style.display = "none";
    document.body.append(sourceCard);
    (sourceCard.querySelector("button, a, [role=button], .cardImageContainer") || sourceCard)
      .dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, view: window }));
    window.setTimeout(() => sourceCard.remove(), 0);
  }

  function card(client, entry, template, portrait = false) {
    const result = make("div", template?.className || "card overflowBackdropCard card-hoverable");
    result.classList.add("glados-recommend-card", `glados-recommend-card--${entry.kind}`);
    const box = make("div", template?.querySelector(":scope > .cardBox")?.className || "cardBox cardBox-bottompadded");
    const scalable = make("div", template?.querySelector(".cardScalable")?.className || "cardScalable");
    const padder = make("div", portrait ? "cardPadder cardPadder-portrait" : (template?.querySelector(".cardPadder")?.className || "cardPadder cardPadder-backdrop"));
    const image = make("div", template?.querySelector(".cardImageContainer")?.className || "cardImageContainer coveredImage cardContent");
    const imageUrl = imageOf(client, entry, portrait);
    if (imageUrl) image.style.backgroundImage = `url("${escapeCssUrl(imageUrl)}")`;
    const button = make("button", "glados-recommend-card__button");
    button.type = "button";
    button.setAttribute("aria-label", `Open ${titleOf(entry)}`);
    button.addEventListener("click", () => activate(entry));
    scalable.append(padder, image, button);
    const source = entry.kind === "seerr" ? "Seerr" : sourceOf(entry);
    if (source) scalable.append(make("span", "glados-recommend-card__source", `From ${source}`));
    box.append(scalable, make("div", "cardText cardText-first", titleOf(entry)), make("div", "cardText cardText-secondary", metaOf(entry)));
    result.append(box);
    return result;
  }

  function sectionByTitle(home, pattern) {
    for (const heading of home.querySelectorAll(".sectionTitle, .sleekfin-section-heading, h1, h2, h3")) {
      if (!pattern.test((heading.textContent || "").trim())) continue;
      const section = heading.closest(".verticalSection, section");
      if (section?.closest(".homePage") === home) return section;
    }
    return null;
  }

  function closeBrowser() {
    document.getElementById(BROWSER_ID)?.remove();
    document.body.classList.remove("glados-recommendations-open");
  }

  function openBrowser(client, entries, template) {
    closeBrowser();
    const browser = make("main", "glados-recommendations-browser");
    browser.id = BROWSER_ID;
    const header = make("header", "glados-recommendations-browser__header");
    const close = make("button", "glados-recommendations-browser__close", "←");
    close.type = "button";
    close.addEventListener("click", closeBrowser);
    const heading = make("div");
    heading.append(make("h2", "glados-recommendations-browser__title", "You Might Like"), make("p", "glados-recommendations-browser__summary"));
    header.append(close, heading);
    const filters = make("div", "glados-recommendations-browser__filters");
    const grid = make("div", "glados-recommendations-browser__grid");
    const render = (filter) => {
      const selected = filter === "all" ? entries : entries.filter((entry) => filterOf(entry) === filter);
      filters.querySelectorAll("button").forEach((button) => button.classList.toggle("is-active", button.dataset.filter === filter));
      heading.querySelector("p").textContent = `${selected.length} recommendation${selected.length === 1 ? "" : "s"}`;
      grid.replaceChildren(...selected.map((entry) => card(client, entry, template, true)));
    };
    [["all", "All"], ["local", "Local"], ["remote", "Remote"], ["seerr", "Seerr"]].forEach(([key, label]) => {
      const button = make("button", "glados-recommendations-browser__filter", label);
      button.type = "button"; button.dataset.filter = key; button.addEventListener("click", () => render(key)); filters.append(button);
    });
    const dialog = make("section", "glados-recommendations-browser__dialog");
    dialog.append(header, filters, grid); browser.append(dialog); document.body.append(browser);
    document.body.classList.add("glados-recommendations-open"); render("all"); close.focus();
  }

  function renderRow(home, client, entries) {
    home.querySelector(`#${ROW_ID}`)?.remove();
    if (!entries.length) return;
    const anchor = sectionByTitle(home, /recently added(?: in)? shows/i) || home.querySelector(".verticalSection");
    const sourceItems = anchor?.querySelector(".itemsContainer");
    if (!anchor?.parentNode || !sourceItems) return;
    const section = anchor.cloneNode(true);
    section.id = ROW_ID; section.classList.add("glados-recommendations");
    section.querySelectorAll("[id]").forEach((node) => node.removeAttribute("id"));
    section.querySelectorAll(".emby-scrollbuttons, .scrollbuttoncontainer").forEach((node) => node.remove());
    const track = section.querySelector(".itemsContainer");
    const template = sourceItems.querySelector(".card");
    const heading = section.querySelector(".sectionTitle, h1, h2, h3");
    const trigger = make("button", "glados-recommendations__trigger");
    trigger.type = "button"; trigger.append(make("h2", heading?.className || "sectionTitle sectionTitle-cards", "You Might Like"));
    trigger.addEventListener("click", () => openBrowser(client, entries, template));
    (heading?.closest(".sectionTitleContainer") || heading?.parentElement)?.replaceChildren(trigger);
    track.replaceChildren(...entries.slice(0, 18).map((entry) => card(client, entry, template)));
    anchor.parentNode.insertBefore(section, anchor);
  }

  async function ensure() {
    const enabled = await featureEnabled();
    if (!enabled) { document.getElementById(ROW_ID)?.remove(); closeBrowser(); return; }
    const home = currentHome();
    const client = api();
    const userId = client?.getCurrentUserId?.();
    if (!home || !client || !userId || state.loading) return;
    if (state.userId !== userId) { state.userId = userId; state.dirty = true; state.items = []; }
    if (Date.now() - state.builtAt > 10 * 60 * 1000) state.dirty = true;
    if (!state.dirty && state.items.length) { if (!home.querySelector(`#${ROW_ID}`)) renderRow(home, client, state.items); return; }
    state.loading = true;
    try {
      state.items = await buildRecommendations(client, userId);
      state.dirty = false; state.builtAt = Date.now();
      if (currentHome()) renderRow(currentHome(), client, state.items);
    } catch (error) { console.warn("Remote Library recommendations could not load", error); }
    finally { state.loading = false; }
  }

  function observeHome() {
    const home = currentHome();
    if (home === observedHome) return;
    homeObserver?.disconnect(); observedHome = home; homeObserver = null;
    if (!home) return;
    homeObserver = new MutationObserver(() => {
      if (home.querySelector(`#${ROW_ID}`)) return;
      window.clearTimeout(repairTimer); repairTimer = window.setTimeout(() => ensure(), 250);
    });
    homeObserver.observe(home, { childList: true, subtree: true });
  }

  function installStyles() {
    if (document.getElementById("remote-library-recommendation-styles")) return;
    const style = document.createElement("style");
    style.id = "remote-library-recommendation-styles";
    style.textContent = `
      .glados-recommend-card__button{position:absolute;z-index:3;inset:0;border:0;background:transparent;cursor:pointer}
      .glados-recommend-card__source{position:absolute;z-index:4;top:.55rem;left:.55rem;padding:.34rem .52rem;border:1px solid rgba(255,255,255,.18);border-radius:999px;color:#fff;background:rgba(5,9,15,.82);box-shadow:0 5px 18px rgba(0,0,0,.32);font-size:.58rem;font-weight:800;letter-spacing:.075em;text-transform:uppercase;pointer-events:none}
      .glados-recommendations__trigger{display:inline-flex;padding:0;border:0;color:inherit;background:transparent;cursor:pointer;font:inherit;text-align:left}.glados-recommendations__trigger h2{margin:0}
      body.glados-recommendations-open{overflow:hidden!important}.glados-recommendations-browser{position:fixed;z-index:4000;inset:0;color:#fff;background:linear-gradient(145deg,#181f2b,#080c14 58%,#05080e)}
      .glados-recommendations-browser__dialog{display:flex;flex-direction:column;width:100%;height:100%;overflow:hidden}.glados-recommendations-browser__header{display:flex;align-items:center;gap:1rem;padding:2rem clamp(1.25rem,4vw,4rem) .75rem}
      .glados-recommendations-browser__title{margin:0;font-size:clamp(1.55rem,3vw,2.3rem)}.glados-recommendations-browser__summary{margin:.45rem 0 0;color:rgba(255,255,255,.58)}
      .glados-recommendations-browser__close{width:2.6rem;height:2.6rem;border:1px solid rgba(255,255,255,.14);border-radius:50%;color:#fff;background:rgba(255,255,255,.08);cursor:pointer;font-size:1.45rem}
      .glados-recommendations-browser__filters{display:flex;gap:.55rem;padding:.5rem clamp(1.25rem,4vw,4rem) 1.15rem}.glados-recommendations-browser__filter{min-width:5.25rem;padding:.58rem 1rem;border:1px solid rgba(255,255,255,.14);border-radius:999px;color:rgba(255,255,255,.72);background:rgba(255,255,255,.06);cursor:pointer;font:inherit}
      .glados-recommendations-browser__filter.is-active,.glados-recommendations-browser__filter:hover{border-color:rgba(105,207,255,.52);color:#fff;background:rgba(86,180,229,.2)}
      .glados-recommendations-browser__grid{display:grid;flex:1;grid-template-columns:repeat(auto-fill,minmax(11rem,1fr));align-content:start;gap:1.75rem 1.15rem;min-height:0;padding:.5rem clamp(1.25rem,4vw,4rem) 2rem;overflow:auto}
      .glados-recommendations-browser__grid .glados-recommend-card{width:auto!important;margin:0!important}.glados-recommendations-browser__grid .cardScalable{position:relative!important;width:100%!important;aspect-ratio:2/3}.glados-recommendations-browser__grid .cardPadder{display:none!important}.glados-recommendations-browser__grid .cardImageContainer{position:absolute!important;inset:0!important;width:100%!important;height:100%!important;background-position:center!important;background-size:cover!important}
      .glados-recommendations-browser__grid .cardText{display:block!important;padding:.55rem .15rem 0!important;overflow:hidden;color:#f7f9fc;text-overflow:ellipsis;white-space:nowrap}.glados-recommendations-browser__grid .cardText-secondary{padding-top:.2rem!important;color:rgba(255,255,255,.6);font-size:.8rem}
      @media(max-width:50em){.glados-recommendations-browser__header{padding:1.25rem 1rem .6rem}.glados-recommendations-browser__filters{padding:.45rem 1rem 1rem;overflow-x:auto}.glados-recommendations-browser__grid{grid-template-columns:repeat(2,minmax(0,1fr));gap:1rem .65rem;padding:.35rem 1rem 1.4rem}}
    `;
    document.head.append(style);
  }

  installStyles();
  ["hashchange", "pageshow"].forEach((name) => window.addEventListener(name, () => { closeBrowser(); observeHome(); ensure(); }));
  document.addEventListener("viewshow", () => { observeHome(); ensure(); });
  document.addEventListener("click", (event) => {
    if (event.target.closest?.(".btnUserRating, button[is=emby-ratingbutton]")) { state.dirty = true; window.setTimeout(ensure, 700); }
  }, true);
  window.setInterval(() => { observeHome(); ensure(); }, 2500);
  observeHome(); ensure();
  console.info("Remote Library: combined recommendations enabled");
})();
