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

When a server is offline, it is skipped and its existing pointers are preserved. Once it becomes reachable, it is included in the next hourly sync or an administrator can select **Sync now**.

On native/bare-metal Jellyfin installs, if the configured `/remote-library` path is not writable, the plugin automatically falls back to Jellyfin's writable data directory and saves the corrected path.

When calendar sync is enabled, the plugin first reads each remote server's authenticated `JellyfinEnhanced/arr/calendar` feed used by Jellyfin Enhanced (Sonarr/Radarr releases). If Enhanced is unavailable or returns no events, it falls back to Jellyfin's native **Upcoming** feed. It creates normal episode metadata with the remote premiere date, which makes those entries appear in the local Jellyfin calendar. Existing local episodes and duplicate remote entries still win according to the normal matching rules.

For contributors or coding agents implementing the companion Jellyfin Enhanced changes, see [AGENTS_JELLYFIN_ENHANCED_CALENDAR.md](AGENTS_JELLYFIN_ENHANCED_CALENDAR.md).

### Jellyfin Enhanced integration

Remote Library ships its own compatibility bridge. When Jellyfin Web loads, the plugin injects a small script which enriches the stock Jellyfin Enhanced calendar with remote entries, poster artwork, duplicate suppression, and `Remote Source` server badges. It also refreshes known Jellyfin Enhanced user-written reviews and reviewer profile pictures from paired servers every five minutes, independently of library sync. Older peers receive a full review discovery hourly; peers running the current Remote Library release use the fast bulk export every five minutes. Imported reviews are visibly tagged with their source server and tracked in a local registry; the review export endpoint excludes those imports so they can never recurse into another server. Updated peers also exchange an authenticated managed-media manifest so remote pointers are never copied through a server mesh, preventing inaccessible nested streams and repeated 404s during scans. No modified Enhanced DLL or separate companion download is required. Install the normal Jellyfin Enhanced plugin to provide the calendar/reviews UI; Remote Library supplies the bridge automatically on every server where it is installed.

The bridge is delivered through a base-URL-safe Jellyfin Web tag, Jellyfin Enhanced's own client bundle, and a writable-web-root startup fallback. Remote Library only writes an NFO `<thumb>` when the source item reports a Primary image; items without artwork therefore cannot create permanent image-proxy 404 retries during Jellyfin library scans.

Remote-source badges on normal Jellyfin cards are supplied by the bundled bridge itself and do not require server-specific CSS, a reverse-proxy theme, or a Jellyfin Enhanced setting. The bridge attaches to stock Enhanced after its client API is ready and refreshes an already-visible calendar once so the first page load includes remote entries.

The bridge does not overwrite third-party files and safely no-ops when Jellyfin Enhanced is not installed. The implementation notes for agents working on the Enhanced UI remain in `AGENTS_JELLYFIN_ENHANCED_CALENDAR.md`.

Remote Library also ignores items tagged `Remote Library` (or located under a `remote-library` path) when reading a remote server. This prevents two servers that sync each other from recursively duplicating pointers.

## Security

The plugin never asks for a remote password. Quick Connect issues an access token which Jellyfin stores in the plugin configuration XML. Protect the Jellyfin configuration directory and only pair accounts whose remote library permissions are appropriate.

## License

MIT

## FAQ

<details>
<summary><strong>Is this just the same as Jellyswarm?</strong></summary>

It is similar in a few ways, but no.

Jellyswarm uses its own URL as the single entry point. Remote Library combines remote content directly into your regular Jellyfin library, so your existing plugins and configuration continue to work normally.

Jellyswarm routes playback through the selected upstream server. That means each user on your local server generally needs a corresponding account on the remote server. Remote Library only requires the person configuring the connection to authenticate to the remote server; playback is proxied through that connection, so other local users can access the remote media.

Jellyswarm merges libraries into its own virtual catalogue and handles duplicate media there. Remote Library prefers the local copy. If it is unavailable, a remote copy can be used instead, and remote servers can fill gaps in a series when they have episodes that the local server does not.

Remote Library also synchronizes the Jellyfin Enhanced calendar and reviews, adds remote-server badges to remote items, and shows when a source server is unreachable.

When using Remote Library, new media can be found by scanning the remote libraries or by running a complete local library scan. By default, the plugin checks for new remote media and calendar updates hourly, and new remote reviews every five minutes. These intervals can be changed in the plugin settings.

**TL;DR:** Jellyswarm combines multiple servers into a separate server that you may need to configure with its own plugins. Remote Library adds remote servers to your existing Jellyfin library, so the plugins and features you already use keep working.

</details>

<details>
<summary><strong>If I install this, will my friend also see my library?</strong></summary>

If only you install and configure it, you will see your own content and the remote content you have connected, while your friend will continue to see only their own library.

If both of you install and configure Remote Library, each server can sync the other server's content. The plugin prevents content imported from a remote server from being exported again, so it will not endlessly duplicate through the connection.

</details>

<details>
<summary><strong>What about the legality? Could one bad actor ruin it?</strong></summary>

Remote Library only syncs the libraries that you explicitly connect to your Jellyfin server. It does not link together every user or every server on the internet.

This is intended for connecting your own servers or servers belonging to friends and family. It does not provide a public catalogue or a global media-sharing network.

</details>

<details>
<summary><strong>But Moonfin does this natively.</strong></summary>

Moonfin does offer remote-library functionality. Remote Library also provides additional integrations: remote calendar and Jellyfin Enhanced review synchronization, a recommendation section using connected servers and local Seerr when available, server-reachability indicators that keep artwork visible while a server is offline, and hierarchy matching that adapts remote content to your local library structure.

In testing, the standard Jellyfin app also ran better on some TVs than Moonfin because it was slightly lighter. Your results may vary depending on the device and the UI plugins you use.

</details>

<details>
<summary><strong>Does the account on the remote server need to be an administrator?</strong></summary>

No. A regular user account works as long as it can view the media you want to sync. The required permissions are Quick Connect and access to the relevant libraries.

</details>
