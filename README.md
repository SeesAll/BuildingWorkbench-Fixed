# BuildingWorkbench Fixed

A reliability and performance-focused maintenance build of MJSU's [Building Workbench](https://umod.org/plugins/building-workbench) plugin for Rust/uMod.

It keeps the original plugin name, permissions, configuration keys, and intended gameplay: an authorized building's highest-level workbench is available throughout that building. Existing installations can replace `BuildingWorkbench.cs` without renaming their configuration or permission grants.

## Why this build exists

The original plugin can retain stale player/building state after teleports, sleeping, building changes, or a missed movement scan. The most visible symptom is an intermittent “workbench level required” message until the player walks close to a physical workbench.

This build repairs those stale-state paths and adds bounded fallback refreshes while keeping the recurring workload spread across frames.

## Differences from the upstream version

| Area | Upstream behavior | Fixed build |
| --- | --- | --- |
| Teleports and waking | State may remain stale until a later movement scan | Coalesces teleport/wake events and refreshes after the destination has settled, with bounded retries |
| Crafting after movement | A stale cached tier can reject a valid craft | Rechecks on demand only when a blueprint needs a higher tier than the player's cached tier |
| Stationary players | A missed update may remain cached indefinitely | Performs a configurable low-frequency fallback refresh |
| Busy servers | Update coroutines can overlap and the live player list can change during iteration | Prevents overlap and scans a stable pooled player snapshot |
| Workbench/building changes | Cached workbench lists may become stale | Repairs workbench caches periodically and reacts to build/kill hooks |
| Long wipes | Visited-building cache can grow for the life of the plugin | Releases building cache entries when no tracked players use them |
| Building membership checks | Repeated linear membership searches | Uses pooled hash sets for constant-time membership and deduplication |
| Configuration | Invalid or non-finite timing/range values can break scanning | Restores safe defaults or clamps unsafe values |
| Permissions and boats | Some event paths did not consistently honor configuration/permission state | Consistently checks `buildingworkbench.use` and the boat setting |
| Craft cancellation | Level changes could cancel while state was stale or mutate the queue unsafely | Recalculates first and walks the crafting queue safely |

## Version 1.4.6 reliability update

- Defers teleport refreshes until the player has settled at the destination.
- Performs three bounded post-teleport/wake checks over 2.5 seconds to tolerate delayed physics and trigger updates.
- Coalesces overlapping teleport, wake, and connection refresh schedules per player.
- Refreshes on demand when a craft requires a higher workbench tier than the player's cached tier.
- Uses the player's active authorized building privilege as an additional building-membership signal.
- Keeps monument teleports safe by clearing stale building state through the same low-cost, event-driven refresh path.

## Version 1.4.5 hardening

- Uses a stable snapshot of connected players during each update pass.
- Caches the squared movement threshold instead of recalculating it per player.
- Uses pooled `HashSet<uint>` collections for building membership and collider deduplication.
- Returns every pooled collection in `finally` blocks, including when an exception occurs.
- Prevents unbounded building-cache growth during long wipes.
- Avoids caching workbench spawns for buildings that no player is currently tracking.
- Handles malformed, `NaN`, and infinite configuration values safely.
- Avoids an unnecessary immediate workbench-cache rebuild after a building is first tracked.
- Uses a hash set for one-time player notifications.

See [CHANGELOG.md](CHANGELOG.md) for the full release history.

## Installation

1. Download `BuildingWorkbench.cs` from the latest release.
2. Place it in `oxide/plugins`.
3. Grant the desired permissions.

The plugin compiles and reloads automatically on a normal uMod server. The file and plugin class remain named `BuildingWorkbench`, so existing `oxide/config/BuildingWorkbench.json` files are retained.

## Permissions

- `buildingworkbench.use` — enables building-wide workbench access.
- `buildingworkbench.cancelcraft` — cancels queued crafts that no longer meet the player's refreshed workbench level.

## Configuration

Existing upstream settings remain supported. This maintenance build adds:

```json
"Required distance from last update (Meters)": 5.0,
"Maximum time between checks while stationary (Seconds)": 10.0
```

Movement-based scans remain the primary path. The stationary refresh is deliberately low-frequency, and player updates are distributed across frames for larger servers.

Recommended production defaults:

```json
{
  "Display workbench built notification": true,
  "Display cancel craft notification": true,
  "Inside building check frequency (Seconds)": 3.0,
  "Enable Fast Building Check (Only checks above and below a player)": false,
  "Enable Boat Check": true,
  "Distance from base to be considered inside building (Meters)": 16.0,
  "Required distance from last update (Meters)": 5.0,
  "Maximum time between checks while stationary (Seconds)": 10.0
}
```

## Compatibility

- Rust dedicated server with uMod/Oxide
- Preserves the original `BuildingWorkbench` plugin identity
- Supports the `OnPlayerTeleported` hook used by NTeleportation
- Retains optional GameTipAPI notifications

## Credits and license

Original plugin by MJSU. This maintenance build retains the original plugin identity and is distributed under the MIT license included in this repository.
