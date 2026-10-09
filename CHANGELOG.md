# Changelog

All notable changes to this maintenance build are documented here.

## [1.4.6] - 2026-10-09

### Added

- Bounded destination-settling refresh attempts at 0.35, 1.0, and 2.5 seconds after connection, teleport, or waking.
- An on-demand refresh when a player attempts a craft that requires a higher workbench tier than the currently cached tier.
- Direct use of the player's active authorized building privilege as an additional building-membership signal.

### Changed

- Teleport and wake hooks now schedule coalesced delayed refreshes instead of scanning on the next server tick.
- A scheduled destination refresh invalidates the previous movement scan so the normal updater can also recover if needed.

### Fixed

- Home teleports could scan before the player and nearby construction colliders had settled at the destination.
- Valid building-wide workbench access could remain unavailable until the player walked into a physical workbench trigger.
- Immediate post-teleport crafting could be rejected while the cached workbench tier was stale.

## [1.4.5] - 2026-10-08

### Added

- Safe default recovery for malformed configuration files.
- Validation for non-finite range and timing values.
- Automatic cleanup of unused per-building cache entries.

### Changed

- Player update passes now use a stable pooled snapshot of the active-player list.
- Building membership and scan deduplication now use pooled hash sets.
- The movement threshold is precomputed once after configuration validation.
- Newly tracked buildings no longer perform an immediate redundant workbench rescan.
- Workbench spawn events only update buildings currently tracked by players.
- One-time build notifications now use constant-time player lookup.

### Fixed

- Pooled collections are returned even if a scan exits through an exception.
- Player-list mutations during a running update can no longer skip or mis-index players.
- Invalid `NaN` or infinite configuration values can no longer silently disable refresh logic.
- Building cache state no longer grows indefinitely as players visit new bases during a long wipe.

## [1.4.4] - 2026-10-08

### Added

- Immediate refresh after compatible player teleports and wake-up events.
- Configurable stationary fallback refresh, defaulting to 10 seconds.

### Changed

- Player scans are distributed across frames and overlapping update coroutines are prevented.
- Workbench caches periodically reconcile with the live building state.

### Fixed

- Stale workbench state after teleporting into a base.
- Duplicate player and workbench tracking.
- Boat-check configuration and missing boat-building guards.
- Permission enforcement in cupboard authorization paths.
- Unsafe queued-craft cancellation after a workbench-level reduction.
- Fast-scan range not following validated configuration.
