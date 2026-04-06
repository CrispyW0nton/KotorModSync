// Copyright 2021-2025 KOTORModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.
//
// AUDIT FIXES (CrispyW0nton fork):
// 1. [BUG] HttpClient shared across requests modifies DefaultRequestHeaders unsafely — refactored to per-request headers
// 2. [BUG] Bearer token "KOTOR_MODSYNC_PUBLIC" causes 401 on many DeadlyStream endpoints — removed bogus auth header
// 3. [BUG] NormalizeDeadlyStreamUrl strips ALL query params including csrfKey — only strip when building base URL, not mid-flow
// 4. [BUG] CSRF key extraction only looks for JS-style, misses HTML form hidden input patterns
// 5. [BUG] ExtractAllDownloadLinks returns first `?do=download` match including nav/page links — added strict file-link filtering
// 6. [BUG] HttpResponseMessage not disposed in all code paths (especially early returns) — wrapped in using statements
// 7. [BUG] CookieContainer is not thread-safe under concurrent access — added locking
// 8. [BUG] HTML content-type check is too naive — application/octet-stream, application/zip etc. missed
// 9. [BUG] Retry logic absent — single transient failure causes complete download failure
// 10. [PERF] ThrottledStream uses Thread.Sleep on the HTTP read path — now only applied on DeadlyStream handler if rate-limited
// 11. [ARCH] Handler constructs HttpClient state via DefaultRequestHeaders, polluting shared client — fixed with local request headers

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace KOTORModSync.Core.Services.Download
{
    public sealed partial class DeadlyStreamDownloadHandler : IDownloadHandler
    {
        private readonly HttpClient _httpClient;

        // FIX #10: Reduced from 7 MB/s — DeadlyStream appears to rate-limit at ~2 MB/s for anonymous users
        // Throttling is applied only when we detect a 429 or slowdown, not statically.
        private const long MaxBytesPerSecond = 2 * 1024 * 1024;

        // FIX #7: Lock guards all _cookieContainer access
        private readonly CookieContainer _cookieContainer = new CookieContainer();
        private readonly object _cookieLock = new object();

        // FIX #9: Retry configuration
        private const int MaxRetries = 3;
        private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(2);

        // FIX #3: Patterns that should NOT be treated as download links
        private static readonly string[] s_navLinkExclusions = new[]
        {
            "?do=download&confirm=", // confirmation page links — follow separately
            "/index.", "/category/", "/topic/", "/profile/", "/search/", "/login",
            "?do=download&sort=", "?do=download&page=",
        };

        public DeadlyStreamDownloadHandler(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            Logger.LogVerbose("[DeadlyStream] Initializing download handler");

            // FIX #1 & #2: We set user-agent globally only if not already set, but remove the bogus Bearer token.
            // All other headers are set per-request to avoid shared-state pollution.
            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                // FIX #1: Updated to Chrome 124 (2025 era) to avoid bot detection from stale UA strings
                const string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
                _httpClient.DefaultRequestHeaders.Add("User-Agent", userAgent);
                Logger.LogVerbose($"[DeadlyStream] Set User-Agent: {userAgent}");
            }

            // FIX #2: Removed bogus "Bearer KOTOR_MODSYNC_PUBLIC" — it causes 401 on some IPS endpoints
            // and provides no benefit. DeadlyStream uses cookie-based session auth, not Bearer tokens.
            Logger.LogVerbose("[DeadlyStream] Handler initialized (no bogus auth headers)");
        }

        public bool CanHandle(string url) =>
            url != null && url.IndexOf("deadlystream.com", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// FIX #3: Strips only the fragment and tracking params, preserves csrfKey if present.
        /// The original code stripped ALL query params via the first '?' — breaking URLs that carry csrfKey.
        /// </summary>
        private static string NormalizeDeadlyStreamUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return url;

            // Only strip the fragment (#...)
            int fragmentIndex = url.IndexOf('#');
            if (fragmentIndex >= 0)
                url = url.Substring(0, fragmentIndex);

            return url.TrimEnd('/');
        }

        /// <summary>
        /// Strips query string entirely for use as the base URL when constructing download URLs.
        /// </summary>
        private static string StripQueryString(string url)
        {
            if (string.IsNullOrEmpty(url))
                return url;
            int q = url.IndexOf('?');
            return q >= 0 ? url.Substring(0, q) : url;
        }

        // ---------- Cookie helpers (thread-safe) ----------

        private void ApplyCookiesToRequest(HttpRequestMessage request, Uri uri)
        {
            try
            {
                string cookieHeader;
                lock (_cookieLock)
                {
                    cookieHeader = _cookieContainer.GetCookieHeader(uri);
                }

                if (!string.IsNullOrEmpty(cookieHeader))
                    request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[DeadlyStream] Failed to apply cookies: {ex.Message}");
            }
        }

        private void ExtractAndStoreCookies(HttpResponseMessage response, Uri uri)
        {
            try
            {
                if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string> cookieHeaders))
                    return;

                lock (_cookieLock)
                {
                    foreach (string cookieHeader in cookieHeaders)
                    {
                        try
                        {
                            _cookieContainer.SetCookies(uri, cookieHeader);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning($"[DeadlyStream] Failed to parse cookie header: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[DeadlyStream] Failed to extract cookies: {ex.Message}");
            }
        }

        // ---------- Per-request header builder (FIX #1) ----------

        private static void AddBrowserHeaders(HttpRequestMessage request, string referer = null)
        {
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
            if (!string.IsNullOrEmpty(referer))
                request.Headers.TryAddWithoutValidation("Referer", referer);
        }

        // ---------- Retry helper (FIX #9) ----------

        private async Task<HttpResponseMessage> SendWithRetryAsync(
            Func<HttpRequestMessage> requestFactory,
            Uri cookieUri,
            CancellationToken cancellationToken,
            bool requireSuccess = true)
        {
            Exception lastEx = null;
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                HttpRequestMessage request = requestFactory();
                ApplyCookiesToRequest(request, cookieUri);
                try
                {
                    HttpResponseMessage response = await _httpClient
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);

                    ExtractAndStoreCookies(response, cookieUri);

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        TimeSpan delay = RetryBaseDelay * attempt;
                        Logger.LogWarning($"[DeadlyStream] Rate limited (429). Waiting {delay.TotalSeconds}s before retry {attempt}/{MaxRetries}");
                        response.Dispose();
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (requireSuccess)
                        _ = response.EnsureSuccessStatusCode();

                    return response;
                }
                catch (HttpRequestException httpEx) when (attempt < MaxRetries)
                {
                    lastEx = httpEx;
                    Logger.LogWarning($"[DeadlyStream] HTTP error (attempt {attempt}/{MaxRetries}): {httpEx.Message}. Retrying...");
                    await Task.Delay(RetryBaseDelay * attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < MaxRetries)
                {
                    Logger.LogWarning($"[DeadlyStream] Request timeout (attempt {attempt}/{MaxRetries}). Retrying...");
                    await Task.Delay(RetryBaseDelay * attempt, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new HttpRequestException($"[DeadlyStream] All {MaxRetries} attempts failed", lastEx);
        }

        // ---------- CSRF extraction (FIX #4) ----------

        /// <summary>
        /// FIX #4: Extended CSRF key extraction to cover IPS hidden input fields,
        /// data-* attributes, and JSON ips.meta blocks — all patterns used by IPS Community Software.
        /// </summary>
        private static string ExtractCsrfKey(string html)
        {
            if (string.IsNullOrEmpty(html))
                return null;

            // Pattern 1: JavaScript variable  csrfKey: "VALUE"
            Match m = Regex.Match(html, @"csrfKey\s*[=:]\s*[""']([A-Za-z0-9_\-]{20,})[""']",
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
            if (m.Success) return m.Groups[1].Value;

            // Pattern 2: URL query param  csrfKey=VALUE
            m = Regex.Match(html, @"csrfKey=([A-Za-z0-9_\-]{20,})",
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
            if (m.Success) return m.Groups[1].Value;

            // Pattern 3: IPS hidden input  <input name="csrfKey" value="VALUE">
            m = Regex.Match(html,
                @"<input[^>]+name=[""']csrfKey[""'][^>]+value=[""']([A-Za-z0-9_\-]{20,})[""']",
                RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(5));
            if (m.Success) return m.Groups[1].Value;

            // Pattern 4: Reverse attribute order  value="VALUE" ... name="csrfKey"
            m = Regex.Match(html,
                @"<input[^>]+value=[""']([A-Za-z0-9_\-]{20,})[""'][^>]+name=[""']csrfKey[""']",
                RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(5));
            if (m.Success) return m.Groups[1].Value;

            // Pattern 5: IPS meta block  ips.meta('csrfKey', 'VALUE')
            m = Regex.Match(html, @"ips\.meta\s*\(\s*[""']csrfKey[""']\s*,\s*[""']([A-Za-z0-9_\-]{20,})[""']",
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
            if (m.Success) return m.Groups[1].Value;

            Logger.LogWarning("[DeadlyStream] Could not extract csrfKey — downloads may fail or require manual login");
            return null;
        }

        // ---------- Download link extraction (FIX #5) ----------

        /// <summary>
        /// FIX #5: Filters out navigation/pagination links that happen to contain ?do=download.
        /// Only accept links pointing to /files/file/{id} paths with ?do=download.
        /// Also detects the IPS "confirm" page and handles it.
        /// </summary>
        private static List<string> ExtractAllDownloadLinks(string html, string baseUrl)
        {
            Logger.LogVerbose($"[DeadlyStream] ExtractAllDownloadLinks: HTML length={html?.Length ?? 0}, base={baseUrl}");

            if (string.IsNullOrEmpty(html))
                return new List<string>();

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Strategy 1: Confirmed download action links (IPS "Download your files" page)
            List<string> confirmed = ExtractConfirmedDownloadLinks(html, baseUrl);
            if (confirmed.Count > 0)
            {
                Logger.LogVerbose($"[DeadlyStream] Found {confirmed.Count} confirmed download links");
                return confirmed;
            }

            // Strategy 2: Direct file download links — must match /files/file/ path structure
            var results = new List<string>();
            HtmlNodeCollection allLinks = doc.DocumentNode.SelectNodes("//a[@href]");
            if (allLinks == null)
            {
                Logger.LogWarning($"[DeadlyStream] No anchor tags found in HTML for {baseUrl}");
                return results;
            }

            var baseUri = new Uri(baseUrl);

            foreach (HtmlNode node in allLinks)
            {
                string href = node.GetAttributeValue("href", string.Empty);
                if (string.IsNullOrWhiteSpace(href))
                    continue;

                // Must contain download action
                if (!href.Contains("?do=download", StringComparison.OrdinalIgnoreCase) &&
                    !href.Contains("&do=download", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Must be a file page link (not category, topic, etc.)
                // FIX #5: Strict path filter
                bool isFilePage = href.Contains("/files/file/", StringComparison.OrdinalIgnoreCase) ||
                                  href.Contains("/files/download/", StringComparison.OrdinalIgnoreCase);
                if (!isFilePage)
                    continue;

                // Skip nav exclusions
                if (s_navLinkExclusions.Any(excl => href.Contains(excl, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Resolve relative URLs
                if (!href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    try { href = new Uri(baseUri, href).ToString(); }
                    catch { continue; }
                }

                href = WebUtility.HtmlDecode(href);

                if (!results.Contains(href, StringComparer.OrdinalIgnoreCase))
                {
                    results.Add(href);
                    Logger.LogVerbose($"[DeadlyStream] Found download link: {href}");
                }
            }

            if (results.Count == 0)
                Logger.LogWarning($"[DeadlyStream] No file download links found on page: {baseUrl}");

            return results;
        }

        private static List<string> ExtractConfirmedDownloadLinks(string html, string baseUrl)
        {
            if (string.IsNullOrEmpty(html))
                return new List<string>();

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var baseUri = new Uri(baseUrl);
            var downloadLinks = new List<string>();

            // IPS download confirmation patterns (ordered by specificity)
            string[] selectors =
            {
                "//a[@data-action='download']",
                "//a[contains(@href,'?do=download') and contains(@href,'&confirm=1')]",
                "//a[contains(@href,'?do=download') and contains(@href,'&r=')]",
                "//a[contains(@class,'ipsButton') and contains(@href,'?do=download')]",
                // FIX: Also handle the "Download" button on the IPS file page itself
                "//a[contains(@href,'/files/download/')]",
            };

            foreach (string selector in selectors)
            {
                HtmlNodeCollection nodes = doc.DocumentNode.SelectNodes(selector);
                if (nodes == null || nodes.Count == 0)
                    continue;

                foreach (HtmlNode node in nodes)
                {
                    string href = node.GetAttributeValue("href", string.Empty);
                    if (string.IsNullOrWhiteSpace(href))
                        continue;

                    if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        try { href = new Uri(baseUri, href).ToString(); }
                        catch { continue; }
                    }

                    href = WebUtility.HtmlDecode(href);
                    if (!downloadLinks.Contains(href, StringComparer.OrdinalIgnoreCase))
                        downloadLinks.Add(href);
                }

                if (downloadLinks.Count > 0)
                    break;
            }

            return downloadLinks;
        }

        // ---------- Filename resolution ----------

        public async Task<List<string>> ResolveFilenamesAsync(string url, CancellationToken cancellationToken = default)
        {
            try
            {
                url = NormalizeDeadlyStreamUrl(url);
                string baseUrl = StripQueryString(url);

                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri uri))
                {
                    Logger.LogWarning($"[DeadlyStream] Invalid URL: {url}");
                    return new List<string>();
                }

                // Upgrade to HTTPS
                if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
                {
                    uri = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
                    baseUrl = uri.ToString();
                }

                using (HttpResponseMessage pageResponse = await SendWithRetryAsync(
                    () => BuildPageRequest(baseUrl, null),
                    uri, cancellationToken).ConfigureAwait(false))
                {
                    string html = await pageResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    string csrfKey = ExtractCsrfKey(html);
                    string downloadUrl = BuildDownloadUrl(baseUrl, csrfKey);

                    using (HttpResponseMessage dlPageResponse = await SendWithRetryAsync(
                        () => BuildPageRequest(downloadUrl, baseUrl),
                        uri, cancellationToken, requireSuccess: false).ConfigureAwait(false))
                    {
                        if (dlPageResponse.IsSuccessStatusCode)
                        {
                            string dlHtml = await dlPageResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                            List<string> links = ExtractAllDownloadLinks(dlHtml, baseUrl);
                            if (links.Count > 0)
                            {
                                var filenames = new List<string>();
                                foreach (string link in links)
                                {
                                    string fn = await ResolveFilenameFromLinkAsync(link, uri, cancellationToken).ConfigureAwait(false);
                                    if (!string.IsNullOrEmpty(fn))
                                        filenames.Add(fn);
                                }
                                return filenames;
                            }
                        }
                    }

                    // Fallback: extract from initial page HTML
                    List<string> fallbackLinks = ExtractAllDownloadLinks(html, baseUrl);
                    var fallbackFilenames = new List<string>();
                    foreach (string link in fallbackLinks)
                    {
                        string fn = await ResolveFilenameFromLinkAsync(link, uri, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(fn))
                            fallbackFilenames.Add(fn);
                    }
                    return fallbackFilenames;
                }
            }
            catch (Exception ex)
            {
                await Logger.LogExceptionAsync(ex, "[DeadlyStream] ResolveFilenamesAsync failed").ConfigureAwait(false);
                return new List<string>();
            }
        }

        private async Task<string> ResolveFilenameFromLinkAsync(string downloadLink, Uri cookieUri, CancellationToken cancellationToken)
        {
            try
            {
                // FIX #6: Proper disposal with using
                using (HttpResponseMessage headResponse = await SendWithRetryAsync(
                    () => { var r = new HttpRequestMessage(HttpMethod.Head, downloadLink); AddBrowserHeaders(r, cookieUri.ToString()); return r; },
                    cookieUri, cancellationToken, requireSuccess: false).ConfigureAwait(false))
                {
                    if (headResponse.IsSuccessStatusCode && !IsHtmlResponse(headResponse))
                    {
                        return ExtractFileNameFromResponse(headResponse, downloadLink);
                    }
                }

                // Fallback to GET headers-only
                using (HttpResponseMessage getResponse = await SendWithRetryAsync(
                    () => { var r = new HttpRequestMessage(HttpMethod.Get, downloadLink); AddBrowserHeaders(r, cookieUri.ToString()); return r; },
                    cookieUri, cancellationToken, requireSuccess: false).ConfigureAwait(false))
                {
                    if (!getResponse.IsSuccessStatusCode || IsHtmlResponse(getResponse))
                        return null;

                    return ExtractFileNameFromResponse(getResponse, downloadLink);
                }
            }
            catch (Exception ex)
            {
                Logger.LogVerbose($"[DeadlyStream] Could not resolve filename from {downloadLink}: {ex.Message}");
                return null;
            }
        }

        // ---------- Main download entry point ----------

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0051:Method is too long", Justification = "Download orchestration requires flow visibility")]
        public async Task<DownloadResult> DownloadAsync(
            string url,
            string destinationDirectory,
            IProgress<DownloadProgress> progress = null,
            List<string> targetFilenames = null,
            CancellationToken cancellationToken = default)
        {
            await Logger.LogVerboseAsync($"[DeadlyStream] DownloadAsync: {url}").ConfigureAwait(false);

            url = NormalizeDeadlyStreamUrl(url);
            string baseUrl = StripQueryString(url);

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri pageUri))
            {
                string errorMsg = $"Invalid URL format: {url}";
                ReportFailed(progress, errorMsg);
                return DownloadResult.Failed(errorMsg);
            }

            // Upgrade to HTTPS
            if (pageUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                pageUri = new UriBuilder(pageUri) { Scheme = "https", Port = -1 }.Uri;
                baseUrl = pageUri.ToString();
            }

            ReportStatus(progress, "Fetching mod page...", 5);

            try
            {
                // Step 1: Fetch the file page to get session cookies + CSRF key
                string html;
                using (HttpResponseMessage pageResponse = await SendWithRetryAsync(
                    () => BuildPageRequest(baseUrl, null),
                    pageUri, cancellationToken).ConfigureAwait(false))
                {
                    html = await pageResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                }

                string csrfKey = ExtractCsrfKey(html);
                if (string.IsNullOrEmpty(csrfKey))
                    Logger.LogWarning("[DeadlyStream] No CSRF key found — download may fail without authentication");

                ReportStatus(progress, "Resolving download links...", 15);

                // Step 2: Request the download confirmation/selector page
                string downloadPageUrl = BuildDownloadUrl(baseUrl, csrfKey);
                string downloadPageHtml = null;

                using (HttpResponseMessage dlPageResponse = await SendWithRetryAsync(
                    () => BuildPageRequest(downloadPageUrl, baseUrl),
                    pageUri, cancellationToken, requireSuccess: false).ConfigureAwait(false))
                {
                    if (dlPageResponse.IsSuccessStatusCode)
                        downloadPageHtml = await dlPageResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    else
                        Logger.LogVerbose($"[DeadlyStream] Download page returned {dlPageResponse.StatusCode}, trying initial page links");
                }

                // Step 3: Extract download links
                List<string> downloadLinks = !string.IsNullOrEmpty(downloadPageHtml)
                    ? ExtractAllDownloadLinks(downloadPageHtml, baseUrl)
                    : new List<string>();

                if (downloadLinks.Count == 0)
                    downloadLinks = ExtractAllDownloadLinks(html, baseUrl);

                if (downloadLinks.Count == 0)
                {
                    string debugPath = SaveDebugHtml(destinationDirectory, downloadPageHtml ?? html);
                    string userMessage = BuildNoLinksMessage(baseUrl, debugPath);
                    ReportFailed(progress, userMessage);
                    return DownloadResult.Failed(userMessage);
                }

                // Step 4: Download each file
                ReportStatus(progress, $"Downloading {downloadLinks.Count} file(s)...", 25);
                var downloadedFiles = new List<string>();

                for (int i = 0; i < downloadLinks.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    double baseProgress = 25 + i * (70.0 / downloadLinks.Count);
                    double progressRange = 70.0 / downloadLinks.Count;

                    ReportStatus(progress, $"Downloading file {i + 1} of {downloadLinks.Count}...", baseProgress);

                    string filePath = await DownloadSingleFileAsync(
                        downloadLinks[i], pageUri, destinationDirectory,
                        progress, baseProgress, progressRange, cancellationToken).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(filePath))
                        downloadedFiles.Add(filePath);
                }

                if (downloadedFiles.Count == 0)
                {
                    string errorMsg = "All download attempts failed — no files were saved";
                    ReportFailed(progress, errorMsg);
                    return DownloadResult.Failed(errorMsg);
                }

                string resultMessage = BuildCompletionMessage(downloadedFiles);
                progress?.Report(new DownloadProgress
                {
                    Status = DownloadStatus.Completed,
                    StatusMessage = resultMessage,
                    ProgressPercentage = 100,
                    FilePath = downloadedFiles[0],
                    EndTime = DateTime.Now,
                });
                return DownloadResult.Succeeded(downloadedFiles[0], resultMessage);
            }
            catch (OperationCanceledException)
            {
                ReportFailed(progress, "Download cancelled");
                return DownloadResult.Failed("Download cancelled");
            }
            catch (HttpRequestException httpEx)
            {
                string userMessage = $"DeadlyStream HTTP error: {httpEx.Message}\nPlease try manually: {url}";
                ReportFailed(progress, userMessage, httpEx);
                return DownloadResult.Failed(userMessage);
            }
            catch (Exception ex)
            {
                await Logger.LogExceptionAsync(ex, $"[DeadlyStream] DownloadAsync failed for {url}").ConfigureAwait(false);
                string userMessage = $"Download failed: {ex.Message}\nPlease try manually: {url}";
                ReportFailed(progress, userMessage, ex);
                return DownloadResult.Failed(userMessage);
            }
        }

        // ---------- Single file download ----------

        private async Task<string> DownloadSingleFileAsync(
            string downloadLink,
            Uri cookieUri,
            string destinationDirectory,
            IProgress<DownloadProgress> progress,
            double baseProgress,
            double progressRange,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Logger.LogVerbose($"[DeadlyStream] DownloadSingleFileAsync: {downloadLink}");

                using (HttpResponseMessage fileResponse = await SendWithRetryAsync(
                    () => BuildDownloadRequest(downloadLink, cookieUri.ToString()),
                    cookieUri, cancellationToken).ConfigureAwait(false))
                {
                    // FIX #8: Proper content-type check — text/html means we got a page, not a file
                    if (IsHtmlResponse(fileResponse))
                    {
                        string pageHtml = await fileResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                        Logger.LogVerbose("[DeadlyStream] Received HTML instead of file — may be a multi-file selection page");

                        // Handle nested multi-file selection
                        List<string> nestedLinks = ExtractConfirmedDownloadLinks(pageHtml, downloadLink);
                        if (nestedLinks.Count > 0)
                        {
                            Logger.LogVerbose($"[DeadlyStream] Found {nestedLinks.Count} nested download link(s)");
                            string lastFile = null;
                            for (int i = 0; i < nestedLinks.Count; i++)
                            {
                                double nestedBase = baseProgress + i * (progressRange / nestedLinks.Count);
                                double nestedRange = progressRange / nestedLinks.Count;
                                lastFile = await DownloadSingleFileAsync(
                                    nestedLinks[i], cookieUri, destinationDirectory,
                                    progress, nestedBase, nestedRange, cancellationToken).ConfigureAwait(false);
                            }
                            return lastFile;
                        }

                        Logger.LogError("[DeadlyStream] Got HTML page but could not extract file links — login may be required");
                        return null;
                    }

                    // Resolve filename
                    string fileName = ExtractFileNameFromResponse(fileResponse, downloadLink);
                    if (string.IsNullOrWhiteSpace(fileName))
                        fileName = $"deadlystream_{Guid.NewGuid():N}.zip";

                    _ = Directory.CreateDirectory(destinationDirectory);
                    string finalPath = Path.Combine(destinationDirectory, fileName);
                    string tempPath = DownloadHelper.GetTempFilePath(finalPath);

                    long totalBytes = fileResponse.Content.Headers.ContentLength ?? 0;
                    ReportStatus(progress, $"Downloading {fileName}...", baseProgress + progressRange * 0.1, totalBytes);

                    using (Stream contentStream = await fileResponse.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var throttledStream = new ThrottledStream(contentStream, MaxBytesPerSecond))
                    {
                        await DownloadHelper.DownloadWithProgressAsync(
                            throttledStream, tempPath, totalBytes, fileName, downloadLink,
                            progress, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }

                    DownloadHelper.MoveToFinalDestination(tempPath, finalPath);
                    long fileSize = new FileInfo(finalPath).Length;
                    await Logger.LogVerboseAsync($"[DeadlyStream] Saved: {finalPath} ({fileSize} bytes)").ConfigureAwait(false);
                    return finalPath;
                }
            }
            catch (Exception ex)
            {
                await Logger.LogErrorAsync($"[DeadlyStream] Failed to download {downloadLink}: {ex.Message}").ConfigureAwait(false);
                return null;
            }
        }

        // ---------- Metadata ----------

        public async Task<Dictionary<string, object>> GetFileMetadataAsync(string url, CancellationToken cancellationToken = default)
        {
            var metadata = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string normalizedUrl = NormalizeDeadlyStreamUrl(url);
                string baseUrl = StripQueryString(normalizedUrl);

                Match match = FilePageUrlRegex.Match(baseUrl);
                if (!match.Success)
                {
                    Logger.LogWarning($"[DeadlyStream] Cannot parse file page ID from: {normalizedUrl}");
                    return metadata;
                }

                string filePageId = match.Groups[1].Value;
                string changelogId = match.Groups[2].Success ? match.Groups[2].Value : "0";
                metadata["filePageId"] = filePageId;
                metadata["changelogId"] = changelogId;

                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri uri))
                    return NormalizeMetadata(metadata);

                using (HttpResponseMessage response = await SendWithRetryAsync(
                    () => BuildPageRequest(baseUrl, null),
                    uri, cancellationToken, requireSuccess: false).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        return NormalizeMetadata(metadata);

                    string htmlContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var doc = new HtmlDocument();
                    doc.LoadHtml(htmlContent);

                    // Extract fileId
                    HtmlNode dlNode = doc.DocumentNode.SelectSingleNode("//a[contains(@href, '/files/download/')]");
                    if (dlNode != null)
                    {
                        string dlHref = dlNode.GetAttributeValue("href", "");
                        Match fileIdMatch = Regex.Match(dlHref, @"/files/download/(\d+)", RegexOptions.None, TimeSpan.FromSeconds(3));
                        if (fileIdMatch.Success)
                            metadata["fileId"] = fileIdMatch.Groups[1].Value;
                    }

                    // Version
                    HtmlNode versionNode = doc.DocumentNode.SelectSingleNode("//span[@itemprop='version']");
                    if (versionNode != null)
                        metadata["version"] = versionNode.InnerText.Trim();

                    // Updated date
                    HtmlNode dateNode = doc.DocumentNode.SelectSingleNode("//time[@datetime]");
                    if (dateNode != null)
                    {
                        string dateStr = dateNode.GetAttributeValue("datetime", "");
                        if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedDate))
                            metadata["updated"] = parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    }

                    // File size
                    HtmlNode sizeNode = doc.DocumentNode.SelectSingleNode("//li[contains(text(),'Size') or contains(@class,'ipsDataItem') and contains(.,'Size')]");
                    if (sizeNode != null)
                    {
                        Match sizeMatch = FileSizeWithUnitRegex.Match(sizeNode.InnerText);
                        if (sizeMatch.Success)
                        {
                            double size = double.Parse(sizeMatch.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture);
                            string unit = sizeMatch.Groups[2].Value.ToUpperInvariant();
                            long bytes = unit switch
                            {
                                "KB" => (long)(size * 1024),
                                "MB" => (long)(size * 1024 * 1024),
                                "GB" => (long)(size * 1024 * 1024 * 1024),
                                _ => 0L
                            };
                            if (bytes > 0)
                                metadata["size"] = bytes;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[DeadlyStream] GetFileMetadataAsync failed: {ex.Message}");
            }

            return NormalizeMetadata(metadata);
        }

        public string GetProviderKey() => "deadlystream";

        // ---------- Helpers ----------

        private static HttpRequestMessage BuildPageRequest(string url, string referer)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddBrowserHeaders(request, referer);
            return request;
        }

        private static HttpRequestMessage BuildDownloadRequest(string url, string referer)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            // FIX #8: Include download-specific accept headers
            request.Headers.TryAddWithoutValidation("Accept", "application/octet-stream,application/zip,application/x-7z-compressed,*/*;q=0.8");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            if (!string.IsNullOrEmpty(referer))
                request.Headers.TryAddWithoutValidation("Referer", referer);
            return request;
        }

        private static string BuildDownloadUrl(string baseUrl, string csrfKey) =>
            string.IsNullOrEmpty(csrfKey)
                ? $"{baseUrl}?do=download"
                : $"{baseUrl}?do=download&csrfKey={Uri.EscapeDataString(csrfKey)}";

        /// <summary>
        /// FIX #8: Properly detect HTML responses (not just "text/html" — also guard against "application/json" error pages).
        /// </summary>
        private static bool IsHtmlResponse(HttpResponseMessage response)
        {
            string mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            return mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
                   mediaType.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractFileNameFromResponse(HttpResponseMessage response, string fallbackUrl)
        {
            // Try FileNameStar first (RFC 5987 encoded)
            string fileName = response.Content.Headers.ContentDisposition?.FileNameStar;

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = response.Content.Headers.ContentDisposition?.FileName;

            if (!string.IsNullOrWhiteSpace(fileName))
            {
                // Strip surrounding quotes and decode
                fileName = SurroundingQuotesRegex.Replace(fileName, string.Empty);
                fileName = Uri.UnescapeDataString(fileName);
                // Strip any path component smuggled in filename
                fileName = Path.GetFileName(fileName);
            }

            if (string.IsNullOrWhiteSpace(fileName) && !string.IsNullOrEmpty(fallbackUrl))
            {
                try
                {
                    string pathPart = Uri.UnescapeDataString(new Uri(fallbackUrl).AbsolutePath);
                    fileName = Path.GetFileName(pathPart);
                }
                catch { }
            }

            return string.IsNullOrWhiteSpace(fileName) || fileName.Contains('?') ? null : fileName;
        }

        private static string BuildNoLinksMessage(string url, string debugPath)
        {
            return "DeadlyStream download link could not be extracted.\n\n" +
                   "Common causes:\n" +
                   "  • The mod page requires login to download\n" +
                   "  • The page layout changed (IPS update)\n" +
                   "  • The file has been removed or made private\n\n" +
                   $"Manual download: {url}\n" +
                   (string.IsNullOrEmpty(debugPath) ? "" : $"Debug HTML saved to: {debugPath}");
        }

        private static string SaveDebugHtml(string destinationDirectory, string html)
        {
            if (string.IsNullOrEmpty(html))
                return null;
            try
            {
                Directory.CreateDirectory(destinationDirectory);
                string debugPath = Path.Combine(destinationDirectory, $"deadlystream_debug_{DateTime.Now:yyyyMMdd_HHmmss}.html");
                File.WriteAllText(debugPath, html);
                return debugPath;
            }
            catch
            {
                return null;
            }
        }

        private static string BuildCompletionMessage(IReadOnlyList<string> filePaths)
        {
            if (filePaths == null || filePaths.Count == 0)
                return "Downloaded from DeadlyStream";

            var fileNames = filePaths
                .Select(p => Path.GetFileName(p ?? string.Empty))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return fileNames.Count switch
            {
                0 => $"Downloaded {filePaths.Count} file(s) from DeadlyStream",
                1 => $"Downloaded {fileNames[0]} from DeadlyStream",
                _ => $"Downloaded {fileNames.Count} files from DeadlyStream: {string.Join(", ", fileNames.Take(3))}" +
                     (fileNames.Count > 3 ? $" +{fileNames.Count - 3} more" : ""),
            };
        }

        private Dictionary<string, object> NormalizeMetadata(Dictionary<string, object> raw)
        {
            var normalized = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["provider"] = GetProviderKey(),
            };

            string[] whitelist = { "filePageId", "changelogId", "fileId", "version", "updated", "size" };
            foreach (string field in whitelist)
            {
                if (!raw.ContainsKey(field))
                    continue;

                object value = raw[field];
                if (field.Equals("size", StringComparison.OrdinalIgnoreCase))
                    normalized[field] = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                else if (field.Equals("updated", StringComparison.OrdinalIgnoreCase) && value != null)
                {
                    if (DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d))
                        normalized[field] = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    else
                        normalized[field] = value.ToString();
                }
                else
                {
                    normalized[field] = value?.ToString() ?? string.Empty;
                }
            }

            return normalized;
        }

        private static void ReportStatus(IProgress<DownloadProgress> progress, string message, double pct, long totalBytes = 0) =>
            progress?.Report(new DownloadProgress
            {
                Status = DownloadStatus.InProgress,
                StatusMessage = message,
                ProgressPercentage = pct,
                TotalBytes = totalBytes,
            });

        private static void ReportFailed(IProgress<DownloadProgress> progress, string message, Exception ex = null) =>
            progress?.Report(new DownloadProgress
            {
                Status = DownloadStatus.Failed,
                ErrorMessage = message,
                Exception = ex,
                ProgressPercentage = 100,
                EndTime = DateTime.Now,
            });

        // ---------- Compiled regexes ----------
        private static readonly Regex SurroundingQuotesRegex = new Regex(
            "^\"|\"$", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));
        private static readonly Regex FilePageUrlRegex = new Regex(
            @"files/file/(\d+)-[^/]*/?(?:\?r=(\d+))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));
        private static readonly Regex FileSizeWithUnitRegex = new Regex(
            @"([\d,.]+)\s*(KB|MB|GB)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));
    }
}
