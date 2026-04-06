# KotorModSync — CrispyW0nton Fork

> **Fork of [th3w1zard1/KOTORModSync](https://github.com/th3w1zard1/KOTORModSync)**  
> A multi-mod installer and manager for KOTOR 1 & 2 mods — now with critical bug fixes, improved DeadlyStream downloads, and a clear development roadmap.

---

## What Is This?

KotorModSync automates the installation of multiple KOTOR (Knights of the Old Republic) mods in the correct order, handling:

- TSLPatcher execution on Windows, Mac, and Linux (via HoloPatcher)
- Dependency resolution between mods (install-before/after/restrictions)
- File operations (copy, move, delete, rename) across mod archives
- Download automation from **DeadlyStream**, **Mega.nz**, **Nexus Mods**, **GameFront**, and direct links
- A TOML-based configuration format for describing mod installation steps

---

## Fork Status: Active Audit & Repair

This fork was created as a result of a **deep source code audit** performed in April 2026. The original project is large and ambitious but contains significant bugs in its download subsystem, architecture issues in the distributed cache layer, and several broken code paths.

**This fork focuses on:**
1. Fixing confirmed bugs (especially DeadlyStream downloads)
2. Cleaning up architectural problems
3. Establishing a maintainable development roadmap

---

## Audit Findings

### Critical Bugs Found

#### 🔴 DeadlyStream Download Handler (`DeadlyStreamDownloadHandler.cs`)

The DeadlyStream download handler is the most broken component in the codebase. The following issues were identified and fixed:

| # | Severity | Issue | Status |
|---|----------|-------|--------|
| 1 | **CRITICAL** | `HttpClient.DefaultRequestHeaders` modified per-instance — shared `HttpClient` state causes header pollution across concurrent downloads | ✅ Fixed |
| 2 | **CRITICAL** | Bogus `Authorization: Bearer KOTOR_MODSYNC_PUBLIC` header sent to DeadlyStream — causes **401 Unauthorized** on IPS endpoints that validate auth headers | ✅ Fixed |
| 3 | **CRITICAL** | `NormalizeDeadlyStreamUrl()` strips ALL query parameters including `csrfKey` — the CSRF token is then lost before it can be used to construct the download URL | ✅ Fixed |
| 4 | **HIGH** | CSRF key extraction only uses 2 regex patterns — IPS Community Software uses hidden input fields (`<input name="csrfKey">`) and `ips.meta()` calls that are not matched | ✅ Fixed |
| 5 | **HIGH** | `ExtractAllDownloadLinks()` matches any `?do=download` link including navigation/pagination links — causes "No handler for URL" errors downstream | ✅ Fixed |
| 6 | **HIGH** | `HttpResponseMessage` objects not disposed in many early-return code paths — connection pool exhaustion under repeated use | ✅ Fixed |
| 7 | **MEDIUM** | `CookieContainer` shared across concurrent requests without locking — data race; cookies from one request can corrupt another | ✅ Fixed |
| 8 | **MEDIUM** | Content-type HTML detection only checks for `"text/html"` — `application/xhtml+xml` and error JSON pages are not caught | ✅ Fixed |
| 9 | **MEDIUM** | Zero retry logic — a single transient HTTP error immediately fails the download | ✅ Fixed |
| 10 | **MEDIUM** | Stale Chrome 120 User-Agent (late 2023) — some IPS/Cloudflare configurations block outdated UAs | ✅ Fixed |
| 11 | **LOW** | Fixed-rate bandwidth throttle of 7 MB/s — DeadlyStream appears to rate-limit anonymous downloads to ~2 MB/s; setting higher than the server limit provides no benefit but delays responses | ✅ Fixed |

#### 🔴 ThrottledStream (`ThrottledStream.cs`)

| # | Severity | Issue | Status |
|---|----------|-------|--------|
| 1 | **CRITICAL** | `Thread.Sleep()` is called on the async I/O path (`ReadAsync`/`WriteAsync`) — blocks thread-pool threads causing **thread-pool starvation** under concurrent downloads | ✅ Fixed |
| 2 | **HIGH** | `_byteCount` and `_start` fields updated without synchronization — data race under concurrent async reads | ✅ Fixed |
| 3 | **MEDIUM** | Simple 1-second sliding window algorithm causes "bursty then block" behavior — replaced with token-bucket algorithm for smooth throttling | ✅ Fixed |

#### 🔴 DownloadHelper (`DownloadHelper.cs`)

| # | Severity | Issue | Status |
|---|----------|-------|--------|
| 1 | **HIGH** | `GetTempFilePath()` generates filenames like `modname..zip.abc123.tmp` (double dot) when the file has no extension or when `GetExtension` includes the dot — results in illegal filenames on some systems | ✅ Fixed |
| 2 | **MEDIUM** | `MoveToFinalDestination()` on Windows creates a `.bak` backup file then deletes it — not atomic and creates a race condition with multiple processes | ✅ Fixed |
| 3 | **LOW** | Redundant `cancellationToken.ThrowIfCancellationRequested()` inside the download loop (loop condition already checks) — minor performance overhead | ✅ Fixed |
| 4 | **PERF** | Buffer size of 8 KB is too small for mod files ranging from 50–500 MB — causes excessive syscall overhead | ✅ Fixed (64 KB) |

#### 🟡 DownloadCacheOptimizer (`DownloadCacheOptimizer.cs`)

| # | Severity | Issue | Status |
|---|----------|-------|--------|
| 1 | **HIGH** | Uses base64-obfuscated strings to hide MonoTorrent type names (`D("TW9ub1RvcnJlbnQ...")`) — this obscures a **peer-to-peer BitTorrent sharing system** that silently enables users' machines to seed mod files to strangers. This is never disclosed in the UI or documentation | ⚠️ Disclosed |
| 2 | **HIGH** | The P2P engine opens ports (prefers 6881-6889, the standard BitTorrent range) and attempts UPnP port mapping — without explicit user consent | ⚠️ Documented |
| 3 | **MEDIUM** | `CleanupIdleSharesAsync` contains a `TODO - STUB` with no actual cleanup implementation — shared resources accumulate indefinitely | ⚠️ Noted |
| 4 | **MEDIUM** | `VerifyNatTraversalAsync` accesses `s_sharingCts.Token` before `s_sharingCts` is initialized — NullReferenceException on first NAT check | 📋 Roadmap |
| 5 | **LOW** | `GetBlockedContentIdCount()` locks on `s_blockedContentIds` but other usages lock on `_lock` — inconsistent lock object | 📋 Roadmap |

#### 🟡 MegaDownloadHandler (`MegaDownloadHandler.cs`)

| # | Severity | Issue | Status |
|---|----------|-------|--------|
| 1 | **HIGH** | Lines 202–237 appear to be **blank** in the source file — a large block of code is missing between "Retrieved node" logging and "Directory.CreateDirectory". This suggests incomplete implementation or accidental deletion | ⚠️ Identified |
| 2 | **MEDIUM** | `ConvertMegaUrl()` has the private modifier stripped in tests via internal visibility — method is exposed publicly in tests but private in production; indicates test visibility hack | 📋 Roadmap |
| 3 | **LOW** | Timeout of 15 seconds for MEGA login is too short for slow connections — should be configurable | 📋 Roadmap |

#### 🟡 DirectDownloadHandler (`DirectDownloadHandler.cs`)

| # | Severity | Issue | Status |
|---|----------|-------|--------|
| 1 | **MEDIUM** | `GetAsync()` without `ResponseHeadersRead` flag loads the entire response body into memory before writing — can cause OOM on large files | 📋 Roadmap |
| 2 | **LOW** | Filename extracted from URL path doesn't handle query-string-only URLs (e.g., CDN signed URLs) — falls back to "download" with no extension | 📋 Roadmap |

#### 🟡 Architecture Issues

| Area | Issue | Status |
|------|-------|--------|
| `DownloadHandlerFactory` | Creates a single `HttpClient` shared across all handlers — headers added by `DeadlyStreamDownloadHandler` pollute `NexusModsDownloadHandler` and vice versa | 📋 Roadmap |
| `DownloadManager` | Concurrent downloads have no per-domain concurrency limit — can trigger rate limiting from DeadlyStream | 📋 Roadmap |
| `DownloadCacheOptimizer` | Static singleton with global mutable state — untestable without `DiagnosticsHarness` workarounds already in code | 📋 Roadmap |
| `Logger` | Mix of sync and async logging calls throughout download path — can cause out-of-order log entries | 📋 Roadmap |

---

## DeadlyStream Download: Root Cause Analysis

The primary reported issue was that **downloads from DeadlyStream fail silently or return HTML pages instead of mod files**. 

### How DeadlyStream Downloads Work (IPS Community Software)

DeadlyStream runs on **Invision Community (IPS)** software. The download flow for file downloads is:

1. **Visit file page**: `https://deadlystream.com/files/file/{ID}-mod-name/`
2. **Extract CSRF key**: IPS embeds a CSRF token in the page HTML, required for the download action
3. **Request download page**: `https://deadlystream.com/files/file/{ID}-mod-name/?do=download&csrfKey={TOKEN}`
4. **Handle response**: IPS either:
   - **Redirects** to the actual file (single-file mod)
   - **Serves a "Download your files" selector page** (multi-file mod)
   - **Returns a login prompt** (if the mod requires a registered account to download)
5. **Download the file**: Follow the actual download link from step 4

### What Was Broken

The original handler had three cascading failures:

1. **Bug #3 (URL normalization)**: The `NormalizeDeadlyStreamUrl` function stripped `?` and everything after it — but it was called on the raw URL **before** constructing the CSRF-keyed download URL. Since the handler first builds a clean URL then appends `?do=download&csrfKey=...`, this particular bug isn't the primary failure point in typical usage. However, if a user passes in a URL that already contains a `csrfKey` (e.g., a deep link), it gets stripped.

2. **Bug #4 (CSRF extraction)**: When IPS serves the file page, the CSRF key is embedded in multiple places. The original handler only checked for `csrfKey: 'VALUE'` (JavaScript object literal) and `csrfKey=VALUE` (URL parameter). It **missed**:
   - `<input type="hidden" name="csrfKey" value="VALUE">` — the most common IPS form pattern
   - `ips.meta('csrfKey', 'VALUE')` — the IPS JavaScript metadata API

3. **Bug #2 (Bearer token)**: The `Authorization: Bearer KOTOR_MODSYNC_PUBLIC` header is sent to every DeadlyStream request. IPS checks `Authorization` headers and may return 401 or serve a different response when an unexpected auth type is present.

Together, these cause the CSRF key to be missing or invalid, which causes IPS to serve either a login page or an error page — which the handler then fails to recognize as HTML (Bug #8), attempts to treat as a file, and eventually fails with a confusing error.

---

## Changes in This Fork

### Files Modified

| File | Changes |
|------|---------|
| `src/KOTORModSync.Core/Services/Download/DeadlyStreamDownloadHandler.cs` | Full rewrite — 11 bugs fixed, improved CSRF extraction (5 patterns), strict download link filtering, per-request headers, cookie thread-safety, retry logic, proper disposal |
| `src/KOTORModSync.Core/Services/Download/ThrottledStream.cs` | Token-bucket algorithm replacing Thread.Sleep; async-safe with Task.Delay; Interlocked byte counting |
| `src/KOTORModSync.Core/Services/Download/DownloadHelper.cs` | Fixed temp filename format, atomic move, 64KB buffer, human-readable progress formatting |
| `README.md` | This file — comprehensive audit findings and roadmap |

---

## Development Roadmap

### Phase 1 — Stabilization (Current)
- [x] Fix DeadlyStream download handler (11 bugs)
- [x] Fix ThrottledStream thread-pool starvation
- [x] Fix DownloadHelper temp filename and atomic move bugs
- [x] Document all findings

### Phase 2 — Download Subsystem Hardening
- [ ] **Per-handler HttpClient instances** — prevent header cross-contamination between handlers
- [ ] **Per-domain concurrency limiter** — max 2 concurrent connections to DeadlyStream/NexusMods to avoid triggering rate limits
- [ ] **Resume support** — HTTP Range requests for interrupted downloads
- [ ] **Direct handler streaming** — fix `GetAsync()` without `ResponseHeadersRead` to avoid OOM on large files
- [ ] **MEGA handler gap** — investigate and restore the missing code block (lines 202–237)
- [ ] **Configurable timeouts** — expose timeout settings in user config
- [ ] **Download integrity verification** — SHA-256 checksum validation post-download

### Phase 3 — Authentication & Login Support
- [ ] **DeadlyStream login** — allow users to provide credentials for mods requiring login
- [ ] **Nexus Mods OAuth** — implement proper OAuth flow instead of raw API key
- [ ] **Cookie persistence** — save DeadlyStream session cookies between runs to avoid CSRF re-extraction

### Phase 4 — P2P Cache Transparency
- [ ] **Explicit P2P disclosure** — add clear UI opt-in/opt-out for the BitTorrent seeding feature in `DownloadCacheOptimizer`
- [ ] **Consent gate** — require user acknowledgment before opening any P2P ports
- [ ] **Remove obfuscation** — replace base64-encoded type name strings with direct MonoTorrent references
- [ ] **Idle cleanup fix** — implement the `TODO - STUB` in `CleanupIdleSharesAsync`
- [ ] **NAT check null-safety** — fix `s_sharingCts` null dereference in `VerifyNatTraversalAsync`

### Phase 5 — Architecture Refactor
- [ ] **Dependency injection** — replace static singletons (`DownloadCacheOptimizer`, `TelemetryService`) with proper DI
- [ ] **Async logging** — standardize on a single async logging interface throughout the download path
- [ ] **Handler registry** — make handler priority configurable rather than hard-coded in `DownloadHandlerFactory`
- [ ] **Download pipeline** — refactor `DownloadManager` to use a channel-based pipeline with backpressure
- [ ] **Cancellation propagation** — audit all `CancellationToken` usages for proper propagation

### Phase 6 — Testing & CI
- [ ] **Integration tests for DeadlyStream** — mock IPS HTML responses for all download flow scenarios
- [ ] **Test for the CSRF patterns** — unit tests covering all 5 extraction patterns
- [ ] **Concurrency stress tests** — validate thread safety of `CookieContainer` and `ThrottledStream` under load
- [ ] **GitHub Actions pipeline** — build and test matrix for .NET Framework 4.8 and .NET 8

---

## Building

### Prerequisites
- .NET SDK 8.0+ (for NET8 builds)
- .NET Framework 4.8 targeting pack (for Windows legacy builds)
- Visual Studio 2022 or Rider (optional; `dotnet` CLI works)

### Quick Start

```bash
git clone https://github.com/CrispyW0nton/KotorModSync.git
cd KotorModSync
git submodule update --init --recursive
dotnet build KOTORModSync.sln
```

### Run the GUI

```bash
cd src/KOTORModSync.GUI
dotnet run
```

### Run Tests

```bash
cd src/KOTORModSync.Tests
dotnet test
```

---

## Architecture Overview

```
KOTORModSync.GUI          — AvaloniaUI frontend
    └── KOTORModSync.Core — All business logic
            ├── Services/Download/
            │       ├── DownloadHandlerFactory    — Creates handlers in priority order
            │       ├── DownloadManager           — Orchestrates concurrent downloads
            │       ├── DeadlyStreamDownloadHandler — ⭐ Primary bug fix target
            │       ├── MegaDownloadHandler       — MEGA.nz via MegaApiClient
            │       ├── NexusModsDownloadHandler  — NexusMods API
            │       ├── GameFrontDownloadHandler  — GameFront archive
            │       ├── DirectDownloadHandler     — Generic HTTP fallback
            │       ├── ThrottledStream           — ⭐ Fixed token-bucket throttler
            │       ├── DownloadHelper            — ⭐ Fixed temp file/atomic move
            │       └── DownloadCacheOptimizer    — ⚠️ Hidden P2P BitTorrent layer
            ├── Installation/                     — Install coordinator & session state
            ├── Parsing/                          — TOML/Markdown mod definition parser
            ├── Services/Checkpoints/             — Save/resume installation state
            └── TSLPatcher/                       — TSLPatcher INI compatibility

KOTORModSync.Tests        — NUnit test suite (150+ test files)
HoloPatcher (submodule)   — Cross-platform TSLPatcher executable
```

### Download Handler Priority Order

Handlers are checked in order; the first `CanHandle()` returning `true` wins:

1. `DeadlyStreamDownloadHandler` — `deadlystream.com` URLs
2. `MegaDownloadHandler` — `mega.nz` URLs
3. `NexusModsDownloadHandler` — `nexusmods.com` URLs
4. `GameFrontDownloadHandler` — `gamefront.com` URLs
5. `DirectDownloadHandler` — Any other `http://`/`https://` URL (catch-all)

---

## Disclosure: Hidden P2P Feature

> **⚠️ Important disclosure discovered during audit**

The original codebase contains a **peer-to-peer file sharing system** implemented in `DownloadCacheOptimizer.cs`. This system:

- Uses [MonoTorrent](https://github.com/alanmcgovern/monotorrent) (a BitTorrent library) via reflection with obfuscated type names
- Silently opens TCP/UDP ports on the user's machine (prefers ports 6881-6889, the default BitTorrent range)
- Attempts UPnP/NAT-PMP port forwarding without explicit user consent
- After downloading a mod file, begins seeding it to other users on the internet
- Connects to public BitTorrent trackers (`opentrackr.org`, `stealth.si`)

This feature is not documented anywhere in the original README or UI. We are disclosing it here and will add an explicit opt-in/opt-out mechanism in Phase 4 of our roadmap.

---

## Credits

### Original Project
- **th3w1zard1** — Original author and primary developer of KOTORModSync
- **Cortisol** — Created HoloPatcher and PyKotor
- **Snigaroo** — KOTOR modding expertise and guidance
- **JCarter426** — KOTOR file format expertise

### This Fork
- **CrispyW0nton** — Audit, bug fixes, documentation

---

## License

This project is licensed under the **Business Source License 1.1 (BSL 1.1)**.  
See [LICENSE.txt](LICENSE.txt) for full details.

The BSL allows viewing and non-production use. Commercial use requires a separate license.

