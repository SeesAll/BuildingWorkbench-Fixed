# BuildingWorkbench

A reliability and performance-focused maintenance build of MJSU's Building Workbench plugin for Rust/uMod.

The plugin keeps the original name and permissions while extending workbench access throughout an authorized building.

## Reliability changes in 1.4.4

- Refreshes building/workbench state immediately after compatible player teleports and wake-up events.
- Adds a bounded stationary fallback refresh (10 seconds by default) so one missed scan cannot remain cached indefinitely.
- Prevents overlapping player-update coroutines on busy servers.
- Periodically repairs stale workbench caches after building changes or merges.
- Avoids duplicate player/workbench tracking.
- Correctly honors the boat-check configuration and guards missing boat building data.
- Enforces `buildingworkbench.use` in cupboard authorization hooks.
- Recalculates workbench level before cancelling invalid queued crafts and safely iterates the queue.
- Validates scan configuration and repairs the fast-scan range after clamping.

## Configuration

Existing configurations are preserved. Version 1.4.4 adds one setting:

```json
"Maximum time between checks while stationary (Seconds)": 10.0
```

Movement-based scans remain the primary path. The stationary refresh is deliberately low-frequency and player updates are spread across frames, making the fallback suitable for larger servers.

## Credits and license

Original plugin by MJSU. This maintenance build retains the original plugin identity and is distributed under the same MIT license.
