# Nexus Mods Browser Cookie Auth Security Audit

Scope: implementation of browser Cookie fallback authentication for Nexus Mods downloads.

## Zero Persistence

- Cookie values are not added to `AppSettings`, SQLite app tables, config files, or logs.
- The only persisted preference is `AllowNexusBrowserCookieAuth`, a boolean consent flag.
- Users can revoke the boolean preference from Settings > Mods; revocation writes `false` and clears any active in-memory lease.
- Browser SQLite files are copied to a temporary file for read stability and deleted after probing.
- Browser cookie queries and runtime filtering only admit `nexusmods.com`, `.nexusmods.com`, and subdomains ending in `.nexusmods.com`.
- Cookie request leases are created inside each actual HTTP send attempt and disposed immediately after `SendAsync` returns, including 429 retry attempts.

## Zero Upload Beyond Nexus

- Cookie headers are attached only after `NexusCookieAuthService.AssertNexusModsUri` accepts the target host.
- Accepted hosts are `nexusmods.com` and subdomains ending in `.nexusmods.com`.
- Non-Nexus hosts throw before Cookie attachment. This is covered by `NexusCookieAuthTests.AssertNexusModsUri_BlocksNonNexusDomains`.
- The downloader's real HTTP send path is covered by `NexusCookieAuthTests.ModDownloader_DoesNotSendCookieToNonNexusDomains`, which asserts that a non-Nexus download URL does not call the Cookie auth service and sends no `Cookie` header.
- `NexusCookieAuthTests.ModDownloader_SendsCookieOnlyToNexusDomains` verifies the positive Nexus-domain send path.

## In-Memory Handling

- Cookie header material is held in `SecureCookieHeader`, backed by unmanaged memory.
- Individual browser Cookie values are wrapped in `SecureCookieHeader` before being combined into the outbound header lease.
- `Dispose()` explicitly zeroes the unmanaged memory before releasing it.
- Probe failure paths dispose any already collected Cookie values before returning a sanitized error.
- Chromium encrypted cookie blobs and Local State encrypted keys are zeroed after decryption attempts.
- `NexusCookieAuthTests.SecureCookieHeader_DisposeZerosNativeMemory` checks the native memory address after disposal.
- HTTP APIs require a transient string header value; BohemiX attaches that value only to the per-send cloned `HttpRequestMessage`, never to the reusable request template, and the source lease is disposed in a short `using` scope immediately after each `SendAsync` call returns.
- Nexus API and downloader `HttpClient` instances set `AllowAutoRedirect = false` to avoid cross-domain Cookie header forwarding.

## Browser Compatibility

- Probe order is Chrome, Edge, Firefox, Brave.
- Chromium browsers:
  - Windows: DPAPI via `CryptUnprotectData`.
  - macOS/Linux: Safe Storage key lookup and AES-128-CBC fallback, plus AES-GCM support for modern Chromium `v10`/`v11` cookies when a master key is available.
- Firefox:
  - Locates `cookies.sqlite`.
  - Initializes system NSS (`nss3`) when available.
  - If NSS is unavailable, returns `FIREFOX_NSS_UNAVAILABLE` and falls back without crashing.

## Logging

Logs include browser name and sanitized error codes only. Cookie names, values, headers, and raw decrypted bytes are not logged.

## Verification

Automated on this workstation:

- `dotnet test BohemiX.sln`: passed, 34 tests.
- `dotnet build src/BohemiX.App/BohemiX.App.csproj`: passed with 0 warnings and 0 errors.

Manual matrix still required before release:

- Windows: Chrome, Edge, Firefox with NSS, Brave.
- macOS: Chrome, Edge, Firefox with NSS, Brave, including Keychain prompt behavior.
- Linux: Chrome, Edge, Firefox with NSS, Brave, including GNOME Keyring and KWallet availability.
