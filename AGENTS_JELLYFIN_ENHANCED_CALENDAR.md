# Agent prompt: Jellyfin Enhanced calendar integration

Implement and maintain the Jellyfin Enhanced calendar integration for this plugin.

## Goal

Remote Library syncs future episodes from each configured remote Jellyfin server. Jellyfin Enhanced's calendar must display those episodes with the same poster artwork, release date, playback item, and remote-server badge as native Sonarr/Radarr entries.

## Required behavior

1. Read the remote server's authenticated Jellyfin Enhanced endpoint:
   `GET /JellyfinEnhanced/arr/calendar?start=<ISO>&end=<ISO>`.
2. Fall back to Jellyfin's native `GET /Shows/Upcoming` when Enhanced is unavailable.
3. Preserve the remote server label (`SourceLabel`, for example `BURBCO` or `SEA`) on every imported event.
4. Make the Enhanced calendar merge remote-library events with local Sonarr/Radarr events.
5. Deduplicate by normalized series title plus season and episode number. Prefer an event that has artwork and a remote-server label.
6. Use the synced Jellyfin series poster for imported events when the calendar payload does not provide one.
7. Keep `itemId`, premiere/release date, episode title, season, episode, overview, and provider IDs so clicking an event opens or plays the local Jellyfin item.
8. Do not expose access tokens in logs, responses, repository files, or generated prompts.

## Verification checklist

- Build the plugin with zero warnings and errors.
- Confirm the running Jellyfin instance loads the new assembly.
- Confirm a known remote event such as Cyberpunk: Edgerunners S02E01 appears once on the correct date.
- Confirm the calendar entry has poster artwork and a visible source badge.
- Confirm matching local Sonarr/Radarr and Remote Library events do not duplicate.
- Test at least one offline remote server: local calendar data must remain available and the sync must not delete healthy pointers.

## Scope

Keep changes compatible with Jellyfin 12 / .NET 10 and the existing Remote Library configuration. Prefer a small, documented integration over copying credentials or coupling to a specific server hostname.

## User reviews/comments integration

When the remote server has Jellyfin Enhanced user reviews enabled, the companion integration should also call the remote `GET /JellyfinEnhanced/reviews/{mediaType}/{tmdbId}` endpoint and merge those comments into the local Enhanced review panel. Preserve the original author name, text, rating, timestamps, and remote source label. Treat review access as optional: a missing Enhanced plugin or HTTP 401/403/404 must not break local reviews. Never copy remote credentials into client-side JavaScript, and never allow a remote review to be edited or deleted through the local server.
