# Changelog

All notable changes to WireShift are documented here.
This project follows [Semantic Versioning](https://semver.org/).

## [1.1.0] - 2026-10-31

### Added

- **Automatic service startup.** Launching `NetBinder.UI.exe` now starts `NetBinder.Service.exe`
  when it is not already running, so only one program has to be opened. Startup failures are
  reported in the UI status line instead of failing silently.
- **Portable and Standalone release archives.** `tools/package-release.ps1` builds both editions
  and zips them into `artifacts/`, each with both executables side by side, the WinDivert driver
  files, `QUICKSTART.txt`, and a `WireShift.bat` launcher.
- Central version metadata in `Directory.Build.props`, applied to every project.

### Fixed

- **Named pipe framing corrupted large or fragmented messages.** The service read the length
  prefix and payload with a single `ReadAsync` call, so a short read desynchronized the stream
  and dropped the client. Framing now lives in one shared `PipeFraming` helper used by both
  ends, which loops until each frame is complete and rejects truncated or oversized messages.
- **Reset metric did not restore the system value.** The "original" metric was captured after the
  override had already been applied, so resetting re-applied the custom value.
- **Bindings matched by process name, not path.** A different executable with the same file name
  could inherit another app's binding. Matching is now path-only.
- **Reverse NAT depended on a loopback flag.** If a proxy reply was delivered on a physical
  interface, the rewrite was skipped and the connection stalled. Rewriting now matches on the
  proxy port plus a live NAT entry.
- **Silent packet injection failures.** `WinDivertSend` results are now checked and logged
  (throttled) instead of discarded.
- **SOCKS5 port conflicts broke the proxy.** A busy port meant no proxy and a stale port in the
  UI; the manager now falls back to the next free port in the reserved range.
- **Config could be lost on a crash during save.** Writes go to a temporary file and are then
  moved into place, and an unreadable config is backed up before defaults are loaded.

### Changed

- The transparent relay now listens on loopback only; it previously bound all interfaces even
  though the redirector rewrites traffic to `127.0.0.1`.
- README build and launch instructions describe the single-launch workflow and both editions.

## [1.0.0]

- Initial public release: per-app interface binding, transparent WinDivert redirection,
  SOCKS5 fallback, WFP filter management, and interface metric control.
