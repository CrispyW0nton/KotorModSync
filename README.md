# KotorModSync — CrispyW0nton Fork

> **Fork of [th3w1zard1/KOTORModSync](https://github.com/th3w1zard1/KOTORModSync)**  
> A multi-mod installer and manager for KOTOR 1 & 2 mods — now with critical download bug fixes, improved DeadlyStream compatibility, and a detailed development roadmap.

---

## Table of Contents

1. [What Is This?](#what-is-this)
2. [Fork Status](#fork-status-active-audit--repair)
3. [Quick Start](#quick-start)
4. [Audit Findings](#audit-findings)
   - [DeadlyStream Handler](#-deadlystream-download-handler-deadlystreamdownloadhandlercs)
   - [ThrottledStream](#-throttledstream-throttledstreamcs)
   - [DownloadHelper](#-downloadhelper-downloadhelpercs)
   - [DownloadCacheOptimizer](#-downloadcacheoptimizer-downloadcacheoptimizercs)
   - [MegaDownloadHandler](#-megadownloadhandler-megadownloadhandlercs)
   - [Architecture Issues](#-architecture-issues)
5. [Root Cause Analysis — DeadlyStream](#deadlystream-download-root-cause-analysis)
6. [Changes in This Fork](#changes-in-this-fork)
7. [Development Roadmap](#development-roadmap)
8. [Architecture Overview](#architecture-overview)
9. [Disclosure: Hidden P2P Feature](#disclosure-hidden-p2p-feature)
10. [Credits](#credits)

---

## What Is This?

KotorModSync automates the installation of multiple KOTOR (Knights of the Old Republic) mods in the correct order, handling:

- **TSLPatcher execution** on Windows, Mac, and Linux (via the HoloPatcher submodule)
- **Dependency resolution** between mods — install-before, install-after, and conflict restrictions
- **File operations** — copy, move, delete, rename across mod archives
- **Download automation** from DeadlyStream, Mega.nz, Nexus Mods, GameFront, and direct links
- **TOML-based mod lists** — a configuration format for describing complete mod installation sequences

The GUI is built with AvaloniaUI (cross-platform), with a Core library targeting .NET Standard 2.0 for maximum compatibility.

---

## Fork Status: Active Audit & Repair

This fork was created in **April 2026** following a deep source code audit of the original project. The original codebase is large and ambitious, but contains significant bugs in its download subsystem — particularly around **DeadlyStream downloads**, which is the most frequently reported failure point.

**This fork focuses on:**
1. Fixing all confirmed bugs in the download layer (✅ Done — see below)
2. Disclosing undocumented/hidden features discovered during the audit
3. Establishing a clear, prioritized development roadmap for future improvements

---

## Quick Start

### Prerequisites
- .NET SDK 8.0+ (for .NET 8 builds)
- .NET Framework 4.8 targeting pack (Windows legacy builds only)
- Git with submodule support

### Build

```bash
git clone https://github.com/CrispyW0nton/KotorModSync.git
cd KotorModSync
git submodule update --init --recursive
dotnet build KOTORModSync.sln
```

### Run GUI

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

## Audit Findings

### 🔴 DeadlyStream Download Handler (`DeadlyStreamDownloadHandler.cs`)

The DeadlyStream handler is the most critical component in the codebase from a user-impact perspective — it is the primary reported failure point, and had the highest bug density found in the audit.

| # | Severity | Issue | Status |
|---|----------|-------|--------|
| 1 | **CRITICAL** | `HttpClient.DefaultRequestHeaders` modified per-instance — shared `HttpClient` state causes header pollution across concurrent downloads | ✅ Fixed |
| 2 | **CRITICAL** | Bogus `Authorization: Bearer KOTOR_MODSYNC_PUBLIC` header sent to DeadlyStream — IPS endpoints return **401 Unauthorized** when they encounter an unexpected auth token | ✅ Fixed |
| 3 | **CRITICAL** | `NormalizeDeadlyStreamUrl()` strips ALL query parameters including `csrfKey` — the CSRF token is discarded before it can be used in the download request | ✅ Fixed |
| 4 | **HIGH** | CSRF key extraction only covers 2 of the 5 patterns used by IPS Community Software — the most common HTML form pattern (`<input name="csrfKey">`) was never matched | ✅ Fixed |
| 5 | **HIGH** | `ExtractAllDownloadLinks()` matches any `?do=download` link including navigation/pagination links — causes "No handler for URL" errors downstream | ✅ Fixed |
| 6 | **HIGH** | `HttpResponseMessage` objects not disposed in early-return code paths — leads to connection pool exhaustion under repeated downloads | ✅ Fixed |
| 7 | **MEDIUM** | `CookieContainer` shared across concurrent requests without a lock — data race that can corrupt session cookies | ✅ Fixed |
| 8 | **MEDIUM** | HTML response detection only checks `"text/html"` — `application/xhtml+xml` and error JSON pages are misidentified as binary files | ✅ Fixed |
| 9 | **MEDIUM** | No retry logic — a single transient HTTP error immediately fails the entire download | ✅ Fixed |
| 10 | **MEDIUM** | Stale Chrome 120 User-Agent (late 2023) — some IPS/Cloudflare configurations block significantly outdated UA strings | ✅ Fixed |
| 11 | **LOW** | Static bandwidth throttle of 7 MB/s — DeadlyStream rate-limits anonymous users to ~2 MB/s; setting it higher provides no benefit | ✅ Fixed |

### 🔴 ThrottledStream (`ThrottledStream.cs`)

| # | Severity | Issue | Status |
|---|----------|-------|--------|
| 1 | **CRITICAL** | `Thread.Sleep()` called on the async I/O path (`ReadAsync`/`WriteAsync`) — **blocks thread-pool threads**, causing starvation under concurrent downloads | ✅ Fixed |
| 2 | **HIGH** | `_byteCount` and `_start` fields updated without synchronization — data race under concurrent async reads | ✅ Fixed |
| 3 | **MEDIUM** | 1-second sliding window throttle algorithm causes bursty-then-block behavior — replaced with a token-bucket algorithm for smooth, consistent throttling | ✅ Fixed |

### 🔴 DownloadHelper (`DownloadHelper.cs`)

| # | Severity | Issue | Status |
|---|----------|-------|--------|
| 1 | **HIGH** | `GetTempFilePath()` generates filenames like `modname..zip.abc123.tmp` (double dot) — illegal on some systems and confusing in all cases | ✅ Fixed |
| 2 | **MEDIUM** | `MoveToFinalDestination()` on Windows creates a `.bak` backup file then deletes it — not atomic; creates a race window with multiple processes | ✅ Fixed |
| 3 | **LOW** | Redundant `cancellationToken.ThrowIfCancellationRequested()` inside the read loop — the loop's `while (!cancellationToken.IsCancellationRequested)` condition already handles this | ✅ Fixed |
| 4 | **PERF** | 8 KB read buffer for files ranging from 50–500 MB — causes excessive syscall overhead; increased to 64 KB | ✅ Fixed |

### 🟡 DownloadCacheOptimizer (`DownloadCacheOptimizer.cs`)

| # | Severity | Issue | Status |
|---|----------|-------|--------|
| 1 | **HIGH** | Uses base64-encoded strings to hide MonoTorrent type names — obscures a **peer-to-peer BitTorrent sharing system** that seeds mod files without user disclosure | ⚠️ Disclosed |
| 2 | **HIGH** | The P2P engine opens ports 6881–6889 (default BitTorrent range) and attempts UPnP port forwarding — without user consent | ⚠️ Documented |
| 3 | **MEDIUM** | `CleanupIdleSharesAsync` is marked `// TODO - STUB` with no implementation — shared resources accumulate indefinitely | 📋 Phase 4 |
| 4 | **MEDIUM** | `VerifyNatTraversalAsync` reads `s_sharingCts.Token` before `s_sharingCts` is initialized — `NullReferenceException` on first NAT check | 📋 Phase 4 |
| 5 | **LOW** | `GetBlockedContentIdCount()` locks on `s_blockedContentIds` but all other callers lock on `_lock` — inconsistent lock object | 📋 Phase 4 |

### 🟡 MegaDownloadHandler (`MegaDownloadHandler.cs`)

| # | Severity | Issue | Status |
|---|----------|-------|--------|
| 1 | **HIGH** | Large code block missing between "Retrieved node" logging and `Directory.CreateDirectory` — appears to be a deleted or never-written implementation section | 📋 Phase 2 |
| 2 | **MEDIUM** | `ConvertMegaUrl()` is `private` in production but exposed via `InternalsVisibleTo` in tests only — indicates a test visibility hack rather than proper interface design | 📋 Phase 5 |
| 3 | **LOW** | 15-second MEGA login timeout is too short for slow connections and is not configurable | 📋 Phase 2 |

### 🟡 DirectDownloadHandler (`DirectDownloadHandler.cs`)

| # | Severity | Issue | Status |
|---|----------|-------|--------|
| 1 | **MEDIUM** | `GetAsync()` without `HttpCompletionOption.ResponseHeadersRead` — loads the entire response body into memory before writing to disk. Can cause OOM on 200+ MB mods | 📋 Phase 2 |
| 2 | **LOW** | Filename extracted from URL path doesn't handle query-string-only CDN URLs (e.g. signed S3 links) — silently falls back to `"download"` with no extension | 📋 Phase 2 |

### 🟡 Architecture Issues

| Area | Issue | Status |
|------|-------|--------|
| `DownloadHandlerFactory` | Single `HttpClient` shared across all handlers — headers set by one handler pollute others | 📋 Phase 5 |
| `DownloadManager` | No per-domain concurrency limit — concurrent downloads can trigger rate limiting from DeadlyStream | 📋 Phase 2 |
| `DownloadCacheOptimizer` | Global static singleton with mutable state — not testable without the `DiagnosticsHarness` workaround already embedded in the code | 📋 Phase 5 |
| Logger | Mix of synchronous and async logging calls throughout the download path — out-of-order log entries under concurrency | 📋 Phase 5 |

---

## DeadlyStream Download: Root Cause Analysis

The most-reported issue is: **downloads from DeadlyStream fail silently, or the program downloads an HTML error page instead of a mod file.**

### How DeadlyStream Works (IPS Community Software)

DeadlyStream runs on **Invision Community (IPS)**. Downloading a file requires a specific multi-step flow:

```
1. GET  https://deadlystream.com/files/file/{id}-mod-name/
         → Browser receives the mod's detail page

2. Extract csrfKey from the page HTML
         → IPS embeds the token in: JS variables, hidden form inputs, and ips.meta() calls

3. GET  https://deadlystream.com/files/file/{id}-mod-name/?do=download&csrfKey={TOKEN}
         → IPS validates the CSRF token and responds with:
           a. A redirect to the binary file (single-file mods)
           b. A "Download your files" selector page (multi-file mods)
           c. A login prompt (members-only mods)

4. Download the actual binary file
```

### The Three Cascading Failures

The original handler broke this flow at steps 2 and 3 simultaneously:

**Failure A — Bogus Authorization header (Bug #2)**  
Every request included `Authorization: Bearer KOTOR_MODSYNC_PUBLIC`. IPS community software checks the `Authorization` header and, when it receives a value it doesn't recognize, returns a 401 response or redirects to a login page — even for mods that don't require authentication.

**Failure B — CSRF key not found (Bug #4)**  
The handler extracted the CSRF key using only two patterns:
- `csrfKey: "VALUE"` (JavaScript object literal)
- `csrfKey=VALUE` (URL parameter)

The most common IPS pattern — the hidden form input `<input type="hidden" name="csrfKey" value="VALUE">` — was never matched. When the CSRF key extraction returned empty, the download URL was built without it, causing IPS to reject the download request.

**Failure C — Bad URL normalization (Bug #3)**  
Even when the CSRF key was successfully extracted, `NormalizeDeadlyStreamUrl()` was called on the complete download URL, which stripped everything after `?` — including the `csrfKey` parameter that had just been appended. The download request reached DeadlyStream missing its authentication token.

**Result:** IPS returned an HTML response (login page or error) to what should have been a binary file download. The handler then failed to recognize this as HTML (Bug #8), attempted to save the error page as a `.zip` file, and reported a cryptic failure.

---

## Changes in This Fork

### Files Modified

| File | What Changed |
|------|-------------|
| `src/KOTORModSync.Core/Services/Download/DeadlyStreamDownloadHandler.cs` | Full rewrite — 11 bugs fixed. Proper CSRF extraction (5 patterns), per-request headers, thread-safe cookies, retry logic, strict download link filtering, full `IDisposable` compliance |
| `src/KOTORModSync.Core/Services/Download/ThrottledStream.cs` | Token-bucket algorithm replacing the sliding window; async path uses `Task.Delay` instead of `Thread.Sleep`; `Interlocked` byte counting for thread safety |
| `src/KOTORModSync.Core/Services/Download/DownloadHelper.cs` | Fixed temp filename format, simplified atomic move, 64 KB buffer, human-readable progress messages |
| `README.md` | This file — comprehensive audit findings, root cause analysis, and roadmap |

### Files Not Yet Modified (Tracked in Roadmap)

| File | Why Not Yet |
|------|-------------|
| `DownloadCacheOptimizer.cs` | Requires a consent/opt-in UI which is a GUI-layer change — planned Phase 4 |
| `MegaDownloadHandler.cs` | Missing code section needs reverse-engineering against MegaApiClient docs — planned Phase 2 |
| `DirectDownloadHandler.cs` | Low-risk fix; planned alongside Phase 2 streaming work |
| `DownloadHandlerFactory.cs` | Requires per-handler `HttpClient` instances — planned Phase 5 refactor |

---

## Development Roadmap

> Items marked ✅ are complete in this fork. Items marked 📋 are planned.  
> Phases are sequenced by dependency and risk — earlier phases unblock later ones.

---

### ✅ Phase 1 — Critical Bug Fixes (Complete)

**Goal:** Make DeadlyStream downloads actually work. Fix thread safety and resource leaks throughout the download pipeline.

| Task | Files | Complexity |
|------|-------|------------|
| ✅ Remove bogus `Authorization` header | `DeadlyStreamDownloadHandler.cs` | Trivial |
| ✅ Fix per-request header isolation (no shared `DefaultRequestHeaders`) | `DeadlyStreamDownloadHandler.cs` | Low |
| ✅ Fix `NormalizeDeadlyStreamUrl` — preserve `csrfKey` in download URLs | `DeadlyStreamDownloadHandler.cs` | Low |
| ✅ Extend CSRF extraction to cover all 5 IPS patterns | `DeadlyStreamDownloadHandler.cs` | Medium |
| ✅ Restrict `ExtractAllDownloadLinks` to `/files/file/` paths only | `DeadlyStreamDownloadHandler.cs` | Low |
| ✅ Wrap all `HttpResponseMessage` in `using` statements | `DeadlyStreamDownloadHandler.cs` | Low |
| ✅ Lock `CookieContainer` access for thread safety | `DeadlyStreamDownloadHandler.cs` | Low |
| ✅ Detect HTML responses (not just `text/html` — also `xhtml+xml`) | `DeadlyStreamDownloadHandler.cs` | Low |
| ✅ Add 3-attempt retry with exponential backoff | `DeadlyStreamDownloadHandler.cs` | Medium |
| ✅ Update User-Agent to Chrome 124 | `DeadlyStreamDownloadHandler.cs` | Trivial |
| ✅ Lower bandwidth throttle to 2 MB/s to match DeadlyStream limits | `DeadlyStreamDownloadHandler.cs` | Trivial |
| ✅ Replace `Thread.Sleep` with `Task.Delay` in `ThrottledStream` async path | `ThrottledStream.cs` | Low |
| ✅ Add `Interlocked` byte counters for thread-safe throttling | `ThrottledStream.cs` | Low |
| ✅ Implement token-bucket throttling algorithm | `ThrottledStream.cs` | Medium |
| ✅ Fix double-dot temp filenames in `GetTempFilePath` | `DownloadHelper.cs` | Low |
| ✅ Simplify `MoveToFinalDestination` to remove `.bak` race condition | `DownloadHelper.cs` | Low |
| ✅ Remove redundant `ThrowIfCancellationRequested` in read loop | `DownloadHelper.cs` | Trivial |
| ✅ Increase download buffer from 8 KB to 64 KB | `DownloadHelper.cs` | Trivial |

---

### 📋 Phase 2 — Download Subsystem Hardening

**Goal:** Make the download system robust against real-world network conditions. Fix streaming for large files, improve MEGA support, add download resumption.

**Why this comes before auth (Phase 3):** Authentication layers on top of a broken download foundation just move the failure point — we need the core download mechanics solid first.

| Task | Files | Complexity | Notes |
|------|-------|------------|-------|
| Fix `DirectDownloadHandler` — add `ResponseHeadersRead` to prevent OOM | `DirectDownloadHandler.cs` | Low | Without this, a 500 MB mod loads entirely into RAM before writing to disk |
| Fix `DirectDownloadHandler` — filename from CDN signed URLs | `DirectDownloadHandler.cs` | Low | S3/Cloudflare signed URLs have the filename in query params, not the path |
| Investigate and restore missing MEGA handler code block | `MegaDownloadHandler.cs` | High | The implementation gap between "Retrieved node" logging and directory creation must be reverse-engineered against MegaApiClient v3 |
| Make MEGA login timeout configurable | `MegaDownloadHandler.cs` | Low | Add to user settings JSON |
| Add per-domain concurrency limiter to `DownloadManager` | `DownloadManager.cs` | Medium | Max 2 concurrent connections per domain prevents triggering rate limits; use `SemaphoreSlim` keyed by host |
| HTTP Range request support for resumed downloads | `DownloadHelper.cs`, all handlers | High | Check for `Accept-Ranges: bytes` header; store `.resume` sidecar file with byte offset |
| Download integrity verification | `DownloadHelper.cs` | Medium | SHA-256 checksum post-download; mod list can optionally specify expected hash |
| Configurable per-handler timeouts | `DownloadHandlerFactory.cs` | Low | Expose in user config; default 180 min is too long for MEGA, too short for slow connections |

---

### 📋 Phase 3 — Authentication & Login Support

**Goal:** Support mods that require a DeadlyStream or NexusMods account to download. Persist sessions so users don't re-enter credentials every run.

**Why this comes after Phase 2:** Authenticated downloads are still subject to all the Phase 2 issues (OOM, no resume, etc.) — those need to be fixed first.

| Task | Files | Complexity | Notes |
|------|-------|------------|-------|
| DeadlyStream account login flow | `DeadlyStreamDownloadHandler.cs`, new UI dialog | High | POST to IPS login endpoint; store session cookie in encrypted local store; detect login-required pages by checking for login form in HTML |
| Cookie persistence between runs | `DeadlyStreamDownloadHandler.cs` | Medium | Serialize `CookieContainer` to a local JSON file; reload on startup; validate by checking expiry of session cookies |
| Session cookie refresh | `DeadlyStreamDownloadHandler.cs` | Medium | Detect 401/redirect-to-login; re-authenticate automatically and retry the download |
| Nexus Mods OAuth flow | `NexusModsDownloadHandler.cs` | High | Proper OAuth 2.0 PKCE flow instead of requiring users to paste a raw API key; store refresh token |
| Nexus Mods premium download support | `NexusModsDownloadHandler.cs` | Medium | Premium accounts get direct CDN links; free accounts require clicking through the site |
| Credential storage | New: `CredentialStore.cs` | Medium | Platform-native secure storage: DPAPI (Windows), Keychain (macOS), libsecret (Linux) |

---

### 📋 Phase 4 — P2P Cache Transparency & Fixes

**Goal:** Expose the hidden BitTorrent seeding system to users with an explicit opt-in, and fix its bugs.

**Why this is its own phase:** The P2P feature has significant privacy implications and requires UI work, user consent flows, and policy decisions that are out of scope for a purely technical fix pass.

| Task | Files | Complexity | Notes |
|------|-------|------------|-------|
| Add explicit opt-in UI for P2P seeding | `DownloadCacheOptimizer.cs`, GUI | High | Must default to **off**; show what ports are opened and what data is shared before user consents |
| Consent gate — block P2P engine start until user acknowledges | `DownloadCacheOptimizer.cs` | Medium | Check a `p2p_consent_given` flag before calling `InitializeAsync()` |
| Remove base64 obfuscation of MonoTorrent type names | `DownloadCacheOptimizer.cs` | Low | Replace `D("TW9ub1RvcnJlbnQ...")` with direct type references; obfuscation serves no purpose and impedes code review |
| Fix `CleanupIdleSharesAsync` TODO/STUB | `DownloadCacheOptimizer.cs` | Medium | Implement idle detection based on peer count + last-activity timestamp; evict shares that have been inactive > 24 hours |
| Fix `s_sharingCts` null dereference in `VerifyNatTraversalAsync` | `DownloadCacheOptimizer.cs` | Low | Guard with null check; initialize before first use |
| Fix inconsistent lock object in `GetBlockedContentIdCount` | `DownloadCacheOptimizer.cs` | Low | Standardize on `_lock` throughout |
| Update tracker list | `DownloadCacheOptimizer.cs` | Low | `opentrackr.org` and `stealth.si` — verify still active; add fallbacks |

---

### 📋 Phase 5 — Architecture Refactor

**Goal:** Replace static singletons and shared state with proper dependency injection. Create per-handler `HttpClient` instances. Make the system testable without workarounds.

**Why this is late in the sequence:** Architecture refactors affect every layer; doing them before functional fixes creates churn on unstable foundations.

| Task | Files | Complexity | Notes |
|------|-------|------------|-------|
| Per-handler `HttpClient` instances | `DownloadHandlerFactory.cs`, all handlers | Medium | Each handler should own its `HttpClient` configured for its domain; this eliminates all shared-header pollution bugs permanently |
| Introduce `IHttpClientFactory` | `DownloadHandlerFactory.cs` | Medium | Allow tests to inject mock `HttpMessageHandler`; production uses `ServiceCollection.AddHttpClient` |
| Dependency injection for `DownloadCacheOptimizer` | `DownloadCacheOptimizer.cs`, DI setup | High | Break the static singleton pattern; register as `IDownloadCacheOptimizer` in the DI container |
| Dependency injection for `TelemetryService` | All files using static telemetry | Medium | Same pattern — make it injectable and mockable |
| Standardize async logging | Logger and all callers | Medium | All download-path log calls should be async-friendly; eliminate sync-over-async patterns |
| Channel-based download pipeline in `DownloadManager` | `DownloadManager.cs` | High | Replace the current ad-hoc concurrency model with `System.Threading.Channels` for proper backpressure and ordered processing |
| Configurable handler registry | `DownloadHandlerFactory.cs` | Low | Load handler priority from config file rather than hard-coding order in factory |
| Audit `CancellationToken` propagation | All download files | Medium | Verify every `async` method accepts and propagates `CancellationToken`; many internal helpers currently discard it |

---

### 📋 Phase 6 — Testing & CI

**Goal:** Prevent regressions by building comprehensive test coverage for the scenarios that have been bugs. Establish CI for PRs.

| Task | Files | Complexity | Notes |
|------|-------|------------|-------|
| Unit tests for all 5 CSRF extraction patterns | `DeadlyStreamDownloadHandlerTests.cs` | Low | Test each regex pattern independently against realistic IPS HTML snippets |
| Integration tests for DeadlyStream download flow | `DeadlyStreamDownloadHandlerTests.cs` | Medium | Use `HttpMessageHandler` mocks; cover: success, CSRF missing, login-required page, multi-file selector, 429 retry |
| Thread safety tests for `ThrottledStream` | `ThrottledStreamTests.cs` | Medium | Concurrent `ReadAsync` calls from multiple tasks; verify no data races under load |
| Thread safety tests for `CookieContainer` locking | `DeadlyStreamDownloadHandlerTests.cs` | Low | Run parallel downloads against a mock server; verify cookies are set and read consistently |
| `DownloadHelper.GetTempFilePath` edge cases | `DownloadHelperTests.cs` | Low | Files with no extension, files with multiple dots, path-only inputs |
| `DownloadHelper.MoveToFinalDestination` race test | `DownloadHelperTests.cs` | Medium | Simulate concurrent processes trying to finalize the same download |
| MEGA handler tests against MegaApiClient mocks | `MegaDownloadHandlerTests.cs` | High | Requires resolving the missing code block first (Phase 2 dependency) |
| GitHub Actions CI matrix | `.github/workflows/build-and-test.yml` | Low | Build and test on .NET Framework 4.8 + .NET 8, Windows + Ubuntu runners |
| Code coverage reporting | CI config | Low | Fail PR if coverage drops below threshold (target: 70% for download subsystem) |

---

## Architecture Overview

```
KOTORModSync.GUI          — AvaloniaUI v11 cross-platform frontend
    └── KOTORModSync.Core — All business logic (.NET Standard 2.0)
            │
            ├── Services/Download/
            │       ├── DownloadHandlerFactory    — Creates and orders download handlers
            │       ├── DownloadManager           — Orchestrates concurrent batch downloads
            │       ├── DownloadCacheOptimizer    — ⚠️  Hidden P2P / BitTorrent cache layer
            │       │
            │       ├── IDownloadHandler          — Contract: CanHandle / Download / ResolveFilenames / GetMetadata
            │       ├── DeadlyStreamDownloadHandler  ⭐ Phase 1 target — 11 bugs fixed
            │       ├── MegaDownloadHandler          ⚠️  Missing implementation block
            │       ├── NexusModsDownloadHandler     — API key auth; needs OAuth (Phase 3)
            │       ├── GameFrontDownloadHandler     — Archive.org fallback site
            │       ├── DirectDownloadHandler        — Generic HTTP fallback (catch-all)
            │       │
            │       ├── ThrottledStream           ⭐ Phase 1 target — token-bucket fixed
            │       └── DownloadHelper            ⭐ Phase 1 target — temp file + buffer fixed
            │
            ├── Installation/                     — Install coordinator & session state
            ├── Parsing/                          — TOML / Markdown mod definition parser
            ├── Services/Checkpoints/             — Save & resume installation state
            ├── CLI/                              — Console progress display & mod build converter
            └── TSLPatcher/                       — TSLPatcher .INI compatibility layer

KOTORModSync.Tests        — NUnit test suite (150+ test files)
HoloPatcher (submodule)   — Cross-platform TSLPatcher (Python / Avalonia UI)
```

### Download Handler Priority Order

Handlers are checked in order; the first `CanHandle(url) == true` handler wins:

| Priority | Handler | Matches |
|----------|---------|---------|
| 1 | `DeadlyStreamDownloadHandler` | `deadlystream.com` URLs |
| 2 | `MegaDownloadHandler` | `mega.nz` URLs |
| 3 | `NexusModsDownloadHandler` | `nexusmods.com` URLs |
| 4 | `GameFrontDownloadHandler` | `gamefront.com` URLs |
| 5 | `DirectDownloadHandler` | Any `http://` / `https://` URL (catch-all) |

---

## Disclosure: Hidden P2P Feature

> ⚠️ **Important disclosure discovered during the April 2026 audit**

The original codebase contains a **peer-to-peer file sharing system** in `DownloadCacheOptimizer.cs`. This system:

- Uses [MonoTorrent](https://github.com/alanmcgovern/monotorrent) (a BitTorrent library) loaded via reflection using **base64-encoded obfuscated type names** — e.g. `D("TW9ub1RvcnJlbnQ...")`
- Silently opens TCP/UDP ports on the user's machine (prefers ports 6881–6889, the standard BitTorrent range)
- Attempts **UPnP/NAT-PMP port forwarding** without explicit user consent
- After a user downloads a mod file, the engine begins **seeding it to other users on the internet**
- Connects to public BitTorrent trackers (`opentrackr.org`, `stealth.si`)

**This feature is not documented anywhere in the original README, UI, or help text.**

This fork discloses it here and plans to add an explicit opt-in/opt-out mechanism in Phase 4. Until Phase 4 is complete, the P2P engine behavior is unchanged from upstream.

---

## Credits

### Original Project
- **th3w1zard1** — Original author and primary developer of KOTORModSync
- **Cortisol** — Created HoloPatcher and PyKotor
- **Snigaroo** — KOTOR modding expertise and guidance
- **JCarter426** — KOTOR file format expertise

### This Fork
- **CrispyW0nton** — Source audit, bug fixes, documentation

---

## License

This project is licensed under the **Business Source License 1.1 (BSL 1.1)**.  
See [LICENSE.txt](LICENSE.txt) for full details.

The BSL allows viewing and non-production use. Commercial use requires a separate license from the original author.
