# Jellyfin Remote Library

Stream media from multiple Jellyfin servers through one local Jellyfin library without downloading the remote files.

Remote Library mirrors remote metadata as tiny `.strm` and `.nfo` files, then securely proxies playback through the local Jellyfin server. Each remote server has its own Quick Connect pairing, URL, badge label, health state, and playback route.

## Features

- Multiple independently paired remote Jellyfin servers
- Password-free pairing through Jellyfin Quick Connect
- Configurable media, calendar, and review resync intervals
- Remote upcoming episodes mirrored into Jellyfin's calendar
- Offline-server detection without deleting its existing library entries
- Local-first matching for movies and individual episodes
- Missing remote episodes merged into an existing local series
- Remote-to-remote deduplication using title, year, season, and episode
- Imported remote pointers are never re-exported, preventing recursive A → B → C syncing
- Only remotely playable media is mirrored; virtual, missing, and calendar-only metadata never becomes a stream pointer
- Separate Movies, Shows, and Anime pointer folders
- `Remote Source: NAME` metadata tags for source-aware clients and UI customizations
- Range-aware playback proxy for seeking and direct play
- No remote media is downloaded

## Requirements

- Jellyfin Enhanced plugin (the standard plugin; Remote Library provides the compatibility bridge)
- Jellyfin Server 12.1.x
- A writable pointer directory mounted at `/remote-library`
- Local media mounted at `/media` when using local-first matching
- Network access from the local Jellyfin server to every remote server
- .NET 10 SDK only when building from source

For Docker installations, both paths must be visible inside the Jellyfin container. The plugin writes managed pointer files to `/remote-library` and, when filling gaps in a partly local series, to the matching series under `/media`.

## Installation from the plugin repository

1. In Jellyfin, open **Dashboard → Plugins → Repositories**.
2. Add a repository named **Remote Library** with this URL:

   `https://raw.githubusercontent.com/Joshsnook-05/jellyfin-remote-library/main/manifest.json`

3. Open **Catalog**, select **Remote Library**, and install it.
4. Restart Jellyfin when prompted.

## Manual installation

1. Download the release archive from the GitHub Releases page.
2. Stop Jellyfin.
3. Extract the archive into a new folder under Jellyfin's plugin directory, for example `plugins/Remote Library`.
4. Start Jellyfin.
5. Open **Dashboard → Plugins → Remote Library** on the local Jellyfin server.
6. Select **Add remote server**.
7. Enter a badge name, such as `FRIEND`, and the full URL of the remote Jellyfin server.
8. Select **Start Quick Connect**. Keep this page open; it displays a temporary Quick Connect code.
9. Open the remote Jellyfin server in another browser tab and sign in with the account whose libraries should be shared.
10. On the remote server, select the profile picture in the top-right corner.
11. Select **Quick Connect** from the profile menu.
12. Enter the code displayed by the local Remote Library plugin, then approve the connection.
13. Return to **Dashboard → Plugins → Remote Library** on the local server and select **Finish pairing** for that same remote server.
14. The entry should now say which remote account it is paired as. Select **Test** to verify the connection.
15. Use **Attach pointer folders** once to add `/remote-library/Movies`, `/remote-library/Shows`, and `/remote-library/Anime` to the corresponding local libraries.
16. Run **Sync now**. Future synchronization runs hourly.

Additional servers use the same three pointer folders and do not need to be attached separately.

If **Quick Connect** is not present in the remote profile menu, the remote Jellyfin administrator must enable Quick Connect. Codes are temporary; if a code expires, select **Start Quick Connect** again to generate a new one. Pair using a remote account that has access only to the libraries you intend to share.

## Building

```bash
dotnet restore Jellyfin.Plugin.RemoteLibrary/Jellyfin.Plugin.RemoteLibrary.csproj
dotnet build Jellyfin.Plugin.RemoteLibrary/Jellyfin.Plugin.RemoteLibrary.csproj -c Release --no-restore
```

The plugin DLL is written to `Jellyfin.Plugin.RemoteLibrary/bin/Release/net10.0/`.

To produce the same ZIP used by the plugin repository:

```bash
python3 scripts/package_plugin.py --output-dir dist
```

Pushing a new semantic plugin version (`major.minor.patch`) to `main` builds the ZIP, creates or updates its matching GitHub release, and adds it to `manifest.json`. The repository URL above becomes installable as soon as that workflow completes.

## How matching works

Real local files always win. For a locally present series, the plugin adds pointers only for missing season and episode numbers. If more than one remote server contains the same item, the first enabled server in the configuration is preferred.

When a server is offline, it is skipped and only that server's existing pointers are preserved. Stale pointers belonging to reachable servers are still removed, so one unavailable peer cannot prevent loop cleanup across the rest of a server mesh. Media reconciliation runs automatically after every Jellyfin startup (including the restart after a plugin upgrade), in addition to its configured scheduled-task interval; an administrator can select **Sync now** at any time.

On native/bare-metal Jellyfin installs, if the configured `/remote-library` path is not writable, the plugin automatically falls back to Jellyfin's writable data directory and saves the corrected path.

When calendar sync is enabled, the plugin first reads each remote server's authenticated `JellyfinEnhanced/arr/calendar` feed used by Jellyfin Enhanced (Sonarr/Radarr releases). If Enhanced is unavailable or returns no events, it falls back to Jellyfin's native **Upcoming** feed. Calendar data can enrich an episode that already has playable remote media, but calendar-only, missing, and virtual episode records are never written into the media library. Existing local episodes and duplicate remote entries still win according to the normal matching rules.

For contributors or coding agents implementing the companion Jellyfin Enhanced changes, see [AGENTS_JELLYFIN_ENHANCED_CALENDAR.md](AGENTS_JELLYFIN_ENHANCED_CALENDAR.md).

### Jellyfin Enhanced integration

Remote Library ships its own compatibility bridge. When Jellyfin Web loads, the plugin injects a small script which enriches the stock Jellyfin Enhanced calendar with remote entries, poster artwork, duplicate suppression, and `Remote Source` server badges. It also refreshes known Jellyfin Enhanced user-written reviews and reviewer profile pictures from paired servers every five minutes, independently of library sync. Older peers receive a full review discovery hourly; peers running the current Remote Library release use the fast bulk export every five minutes. Imported reviews are visibly tagged with their source server and tracked in a local registry; the review export endpoint excludes those imports so they can never recurse into another server. Updated peers also exchange an authenticated managed-media manifest so remote pointers are never copied through a server mesh, preventing inaccessible nested streams and repeated 404s during scans. No modified Enhanced DLL or separate companion download is required. Install the normal Jellyfin Enhanced plugin to provide the calendar/reviews UI; Remote Library supplies the bridge automatically on every server where it is installed.

The bridge is delivered through a base-URL-safe Jellyfin Web tag, Jellyfin Enhanced's own client bundle, and a writable-web-root startup fallback. Remote Library only writes an NFO `<thumb>` when the source item reports a Primary image; items without artwork therefore cannot create permanent image-proxy 404 retries during Jellyfin library scans.

Remote-source badges on normal Jellyfin cards are supplied by the bundled bridge itself and do not require server-specific CSS, a reverse-proxy theme, or a Jellyfin Enhanced setting. The bridge attaches to stock Enhanced after its client API is ready and refreshes an already-visible calendar once so the first page load includes remote entries.

The bridge does not overwrite third-party files and safely no-ops when Jellyfin Enhanced is not installed. The implementation notes for agents working on the Enhanced UI remain in `AGENTS_JELLYFIN_ENHANCED_CALENDAR.md`.

Remote Library never re-exports imported pointers. Current peers exchange an authenticated managed-media manifest, which precisely identifies pointers even when they are used to fill a gap in a local series. If a peer is older or incompatible and cannot supply that manifest, Remote Library fails closed and excludes items carrying the `Remote Library`/`Remote Source` marker tags. This prevents a server mesh from recursively duplicating pointers by default; update all peers for the most precise handling of mixed local-and-remote series.

## Security

The plugin never asks for a remote password. Quick Connect issues an access token which Jellyfin stores in the plugin configuration XML. Protect the Jellyfin configuration directory and only pair accounts whose remote library permissions are appropriate.

## License

GNU General Public License v2.0 or later (`GPL-2.0-or-later`). See [LICENSE](LICENSE).

## FAQ

<details>
<summary><strong>Is this just the same as jellyswarm?</strong></summary>

its similar in a few ways but no (TL;DR at the bottom).

Jelly swarm uses its own URL to access it, where as this combines it directly into your regular jellyfin library, so all your plugins already set up, will work exactly the same,

Jellyswarm routes playback meaning each user on your local server needs an account on the remote server, this only requires who ever is setting up the plugin to log in to the remote server, giving everybody access to the remote media as its proxied into the remote server.

Jellyswarm merges libraries into its own url to show fix duplication issues, whereas this one preferes local, but if its unavailable it will use the remote copy (if available) and also use the remote server to fill in gaps for series, if an episode is missing on your local server and the remote server has it.

This one also syncs the JE calendar, and JE reviews, whilst also adding server name badges to anything from a remote server, as well as an indicator for if the server is unreachable.

when using this one, scanning new media works by either scanning just remote servers, or by running a complete library re-scan. Its also run (by default, which can be change in the plugin settings) to scan new remote media and calendar updates onve every hour, and new remote reviews every 5 minutes.

TL;DR

Jelly swarm is combining multiple servers into 1 server, which you then have to install plugins into (which i was unable to, jellyswarm doesnt support many plugins), whereas my one adds the remote servers into you current library, meaning anything already set up, stays working

</details>

<details>
<summary><strong>if i install this, will my friend also see my library?</strong></summary>

If only you install it then you will see yours and their content, they will only see their own. However if you both install it then it will sync both of you. The plugin also prevents contents that you get from their server from being sent back out to anyone, meaning that contents wont duplicate!

</details>

<details>
<summary><strong>what about the legality? one bad actor could ruin in</strong></summary>

Only libraries that you sync into your jellyfin will show up, it doesn't link together everybody. It would be a cool idea, but would be a major piracy issue. This tool is purely for being able to link together yours and your friends (or even multiple of your own) jellyfin servers together.

</details>

<details>
<summary><strong>But Moonfin does it natively</strong></summary>

Moonfin does offer this feature, however, mine also comes with added bonuses.

whilst doing a library sync, mine also syncs together things like the JE calendar and the JE user reviews, a recommended section using all servers + seerr (if available locally), has indicators on the poster/thumbnail for if a server is unreachable whilst keeping the thumbnails still visible, and also doesnt matter how the hierarchy is formatted, it will do what it can to make it match your local hierarchy.

I also found that the jellyfin app seemed to run better on my TV than moonfin did due to it being slightly lightweight, but obviously other peoples milage may vary depending on if and what UI plugins you're running.

</details>

<details>
<summary><strong>Does the account on the remote server need to be admin?</strong></summary>

Nope, any regular user account with the ability to see media on the remote server will work for syncing it as a remote server into your local one, the only required permissions are "Quick connect" and the ability to view the media.

</details>
