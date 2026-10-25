# Security

ServerTray is a read-only SSH monitor. Use it only for systems you are authorized to access. A dedicated unprivileged account with access to standard Linux `/proc` files and root filesystem statistics is sufficient. No sudo, remote installation, arbitrary command input or service control is implemented.

- Host keys are checked before authentication. Unknown or changed keys are blocked until the user verifies their SHA256 fingerprint through a trusted channel.
- Jump-host and destination identities are checked independently. SSH Agent forwarding is not used.
- Credentials are encrypted with Windows DPAPI `CurrentUser`, separately from exportable settings. A process running as the same Windows user may still access those secrets. Do not share the application's data directory.
- Exported settings include hostnames, usernames and labels. They exclude passwords, private keys, passphrases and trusted-host state.
- SSH output, configuration size, concurrency, retries, logs, chart points and retained events are bounded. Untrusted command output is parsed as data, never executed locally.
- The executable makes outbound SSH connections to configured hosts. It has no product telemetry or public inbound listener. The optional development SSH fixture binds only to loopback.

Do not include private configurations, credentials, databases, logs or dump files in issues. Before sending diagnostics, replace identifiers and review the contents. Report suspected vulnerabilities using [GitHub private vulnerability reporting](https://github.com/mengdisheng/ServerTray/security/advisories/new). Do not post exploit credentials in public issues. If the private reporting form is unavailable, open a minimal issue asking for a private contact channel without disclosing vulnerability details.

## Supported versions

The latest `0.1.0-preview` is the only maintained version. It is a preview, not a stable release. Security fixes will be provided on the default branch and in subsequent preview packages. Review [the performance report](docs/PERFORMANCE.md) for pending release checks.

Dependencies are version-locked. Portable packages include their dependency notices and SBOM. Run dependency vulnerability checks and the repository scanner before publishing. Future unsupported schema versions are rejected rather than silently downgraded.
