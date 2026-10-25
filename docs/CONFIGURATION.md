# Configuration reference

[Back to README](../README.md) · [User guide](USAGE.md)

Use the graphical settings and server editor for ordinary changes. [config.example.json](../examples/config.example.json) is a complete, fictional, disabled example. Configuration is JSON with camelCase field names and string enums. The current `schemaVersion` is `1`; future versions and unknown fields are rejected. Missing fields use current defaults.

Imports are limited to 1 MiB. They validate completely and show a difference summary before replacement. Writes are atomic and retain the immediately previous file as `.bak`. Secret material and host-key trust are stored separately from ordinary configuration.

## Global fields

| Field | Default | Accepted values / meaning |
|---|---|---|
| `schemaVersion` | `1` | Only `1` is supported. |
| `monitoring.intervalSeconds` | `15` | Integer, 5–300. |
| `monitoring.timeoutSeconds` | `10` | Integer, 1–30. |
| `monitoring.maxConcurrency` | `4` | Integer, 1–8. Performance target is 20 active hosts at the default settings. |
| `monitoring.retentionDays` | `7` | Integer, 1–30. |
| `monitoring.includeInterfaces` | `[]` | Empty means all interfaces are eligible before exclusion. |
| `monitoring.excludeInterfaces` | See example | Defaults exclude `lo`, `docker*`, `veth*`, `br-*`, `virbr*`, `tun*`, `wg*`. |
| `alerts` | See below | Global rule set. |
| `theme` | `System` | `System`, `Light` or `Dark`. |
| `timeZoneId` | `Local` | `Local` or a time zone accepted by Windows `TimeZoneInfo`. Storage remains UTC. |
| `autoStart` | `false` | Start at Windows sign-in when enabled through settings. |
| `defaultServerId` | `null` | Existing server ID or `null`. |
| `traySummary` | `Fleet` | `Fleet` or `Selected`. |
| `visibleMetrics` | All seven below | Subset of `cpu`, `memory`, `disk`, `network`, `io`, `swap`, `load`. |
| `servers` | `[]` | At most 200 saved profiles; 20 simultaneous active hosts is the tested target. |

Interface lists contain at most 32 patterns, each 1–64 characters. They support `*` and `?` wildcards and ASCII letters, digits, `_ . : @ -`. Include patterns run first, then exclusions.

## Server fields

| Field | Meaning |
|---|---|
| `id` | Unique stable identifier, 1–64 ASCII letters, digits, `-` or `_`. Used for references and local state. |
| `name` | Display name, 1–120 characters. |
| `host` | SSH host/IP, 1–253 characters, without protocol, path or username. |
| `port` | SSH port, 1–65535; defaults to 22. |
| `username` | SSH username, 1–128 characters. |
| `authentication` | `Password` or `PrivateKey`; secret values never belong in this file. |
| `group`, `tags` | Group up to 80 characters; at most 20 tags, each up to 60 characters. |
| `enabled`, `order` | Whether to monitor and the integer display order. |
| `jumpServerId` | Existing profile ID or `null`; no self-reference or multi-level jumps. |
| `intervalSeconds`, `timeoutSeconds` | `null` inherits global settings; otherwise the global ranges apply. |
| `includeInterfaces`, `excludeInterfaces` | `null` inherits its global list; `[]` explicitly supplies an empty list. |
| `alerts` | `null` inherits the global rule set. An object replaces the rule set; omitted object fields use built-in defaults, not values from global custom rules. |
| `expiresOn` | Manually set `YYYY-MM-DD` date or `null`. No provider API is queried. |

Deleting or changing server IDs changes the identity used by history and alert state. Prefer editing existing profiles in the UI. A jump profile must exist and cannot itself use a jump; it can be disabled for its own metric collection while still serving as a connection hop.

## Alert rule fields

| Field | Default | Accepted values |
|---|---|---|
| `offline`, `resources`, `cpu`, `memory`, `disk`, `expiry` | `true` | Boolean switches. `resources` gates all three individual resource switches. |
| `cpuHigh` | `85` | Percentage, 5–100. |
| `memoryHigh`, `diskHigh` | `90` | Percentage, 5–100. |
| `hysteresis` | `5` | 1 through the smallest high threshold. Recovery requires a value strictly below `high - hysteresis`. |
| `highSeconds` | `120` | Integer, 0–3600. |
| `recoverySeconds` | `60` | Integer, 0–3600. |

The offline grace is `max(75 seconds, 3 × effective sampling interval)`. Expiry milestones are 30, 7, 1 and 0 days. They are not configurable quota or shutdown rules.

## Privacy and storage

The default data directory is `%LOCALAPPDATA%\ServerTray`. It contains configuration and backup, DPAPI-encrypted credentials, trusted host fingerprints, SQLite history, alert state and bounded rolling logs. Treat the entire directory as private.

Exports omit secret material but retain hostnames, SSH usernames, groups and labels. The repository's ignore rules and scanner are safeguards for contributors; they cannot sanitize a user's arbitrary export. Do not commit or attach real exported settings. There is no configuration migration from other monitoring products, no monthly billing calculation and no remote shutdown feature.
