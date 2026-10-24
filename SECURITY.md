# Security

ServerTray is a read-only SSH monitor. Use it only for systems you are authorized to access. A dedicated unprivileged account with access to standard Linux `/proc` files and root filesystem statistics is sufficient. No sudo, remote installation, arbitrary command input or service control is implemented.

- Host keys are checked before authentication. Unknown or changed keys are blocked until the user verifies their SHA256 fingerprint through a trusted channel.
- Jump-host and destination identities are checked independently. SSH Agent forwarding is not used.
- Credentials are encrypted with Windows DPAPI `CurrentUser`, separately from exportable settings. A process running as the same Windows user may still access those secrets. Do not share the application's data directory.
- Exported settings include hostnames, usernames and labels. They exclude passwords, private keys, passphrases and trusted-host state.
- SSH output, configuration size, concurrency, retries, logs, chart points and retained events are bounded. Untrusted command output is parsed as data, never executed locally.
- The executable makes outbound SSH connections to configured hosts. It has no product telemetry or public inbound listener. The optional development SSH fixture binds only to loopback.

Do not include private configurations, credentials, databases, logs or dump files in issues. Before sending diagnostics, replace identifiers and review the contents. Report suspected vulnerabilities privately to the repository maintainer when a public hosting location is established; do not post exploit credentials publicly.

Dependencies are version-locked. Portable packages include their dependency notices and SBOM. Run dependency vulnerability checks and the repository scanner before publishing. Future unsupported schema versions are rejected rather than silently downgraded.
