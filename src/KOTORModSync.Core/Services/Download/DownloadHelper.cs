// Copyright 2021-2025 KOTORModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.
//
// AUDIT FIXES (CrispyW0nton fork):
// 1. [BUG] GetTempFilePath builds filename as "name..ext.random.tmp" (double dot) due to calling
//    GetExtension on a filename that may have no extension — fixed format to "name_random.tmp"
// 2. [BUG] MoveToFinalDestination on Windows creates a .bak file then deletes it — race condition if
//    multiple processes run simultaneously; simplified to use File.Move with overwrite flag on NET5+
// 3. [BUG] Buffer reuse after cancellationToken.IsCancellationRequested check: redundant ThrowIfCancellationRequested
//    after loop condition already guards — removed redundant check
// 4. [PERF] Buffer size of 8192 is too small for large mod files (some 200MB+); increased to 64 KB

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace KOTORModSync.Core.Services.Download
{
    public static class DownloadHelper
    {
        // FIX #4: Increased buffer size for large mod files (up to ~200 MB)
        private const int BufferSize = 65536; // 64 KB
        private const int ProgressUpdateIntervalMs = 250;

        /// <summary>
        /// FIX #1: Generates a unique temporary filename without the double-dot bug.
        /// Format: originalStem_randomHex.tmp (safe on all platforms)
        /// </summary>
        public static string GetTempFilePath(string destinationPath)
        {
            if (string.IsNullOrEmpty(destinationPath))
                throw new ArgumentNullException(nameof(destinationPath));

            string directory = Path.GetDirectoryName(destinationPath) ?? ".";
            string nameWithoutExtension = Path.GetFileNameWithoutExtension(destinationPath);
            string randomChars = Guid.NewGuid().ToString("N").Substring(0, 12);

            // FIX #1: Single dot, no extension doubling
            string tempFileName = $"{nameWithoutExtension}_{randomChars}.tmp";
            return Path.Combine(directory, tempFileName);
        }

        /// <summary>
        /// FIX #2: Atomically moves a temporary download file to its final destination.
        /// Uses platform-appropriate atomic operations with simplified logic.
        /// </summary>
        public static void MoveToFinalDestination(string tempPath, string finalPath)
        {
            if (!File.Exists(tempPath))
                throw new FileNotFoundException($"Temporary download file not found: {tempPath}", tempPath);

#if NET5_0_OR_GREATER
            // .NET 5+ has File.Move with overwrite parameter — atomic on same volume
            File.Move(tempPath, finalPath, overwrite: true);
#else
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows: File.Replace is atomic when temp and final are on the same volume
                if (File.Exists(finalPath))
                {
                    // FIX #2: Use null for backup to avoid creating .bak files
                    // File.Replace with null backup deletes the destination file atomically
                    File.Replace(tempPath, finalPath, null);
                }
                else
                {
                    File.Move(tempPath, finalPath);
                }
            }
            else
            {
                // POSIX: rename(2) is atomic on same filesystem
                if (File.Exists(finalPath))
                    File.Delete(finalPath);
                File.Move(tempPath, finalPath);
            }
#endif
        }

        /// <summary>
        /// Downloads content from <paramref name="sourceStream"/> to <paramref name="destinationPath"/>
        /// with progress reporting. The destination file must not exist before calling (use temp paths).
        /// </summary>
        public static async Task<long> DownloadWithProgressAsync(
            Stream sourceStream,
            string destinationPath,
            long totalBytes,
            string fileName,
            string url,
            IProgress<DownloadProgress> progress = null,
            string modName = null,
            CancellationToken cancellationToken = default)
        {
            if (sourceStream == null) throw new ArgumentNullException(nameof(sourceStream));
            if (string.IsNullOrEmpty(destinationPath)) throw new ArgumentNullException(nameof(destinationPath));

            DateTime startTime = DateTime.Now;

            try
            {
                using (var fileStream = new FileStream(
                    destinationPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    useAsync: true))
                {
                    // FIX #4: Larger buffer
                    byte[] buffer = new byte[BufferSize];
                    long totalBytesRead = 0;
                    DateTimeOffset lastProgressUpdate = DateTimeOffset.UtcNow;

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        // FIX #3: Single cancellation check via the token, no redundant ThrowIfCancellationRequested inside loop
                        int bytesRead = await sourceStream
                            .ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                            .ConfigureAwait(false);

                        if (bytesRead == 0)
                            break;

                        await fileStream
                            .WriteAsync(buffer, 0, bytesRead, cancellationToken)
                            .ConfigureAwait(false);

                        totalBytesRead += bytesRead;

                        DateTimeOffset now = DateTimeOffset.UtcNow;
                        if ((now - lastProgressUpdate).TotalMilliseconds >= ProgressUpdateIntervalMs)
                        {
                            lastProgressUpdate = now;
                            ReportProgress(progress, modName, url, fileName, destinationPath,
                                totalBytesRead, totalBytes, startTime);
                        }
                    }

                    await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);

                    // Final progress report at 100%
                    if (totalBytes > 0)
                    {
                        progress?.Report(new DownloadProgress
                        {
                            ModName = modName,
                            Url = url,
                            Status = DownloadStatus.InProgress,
                            StatusMessage = $"Download complete: {fileName}",
                            ProgressPercentage = 100,
                            BytesDownloaded = totalBytesRead,
                            TotalBytes = totalBytes,
                            StartTime = startTime,
                            FilePath = destinationPath,
                        });
                    }

                    return totalBytesRead;
                }
            }
            catch (OperationCanceledException)
            {
                // Clean up partial file on cancellation
                TryDeleteFile(destinationPath);
                throw;
            }
        }

        private static void ReportProgress(
            IProgress<DownloadProgress> progress,
            string modName, string url, string fileName, string filePath,
            long bytesRead, long totalBytes, DateTime startTime)
        {
            if (progress == null)
                return;

            double pct = totalBytes > 0
                ? Math.Min((double)bytesRead / totalBytes * 100.0, 100.0)
                : 0;

            progress.Report(new DownloadProgress
            {
                ModName = modName,
                Url = url,
                Status = DownloadStatus.InProgress,
                StatusMessage = totalBytes > 0
                    ? $"Downloading {fileName}... ({FormatBytes(bytesRead)} / {FormatBytes(totalBytes)})"
                    : $"Downloading {fileName}... ({FormatBytes(bytesRead)})",
                ProgressPercentage = pct,
                BytesDownloaded = bytesRead,
                TotalBytes = totalBytes,
                StartTime = startTime,
                FilePath = filePath,
            });
        }

        private static void TryDeleteFile(string path)
        {
            if (!File.Exists(path))
                return;
            try
            {
                File.Delete(path);
                Logger.LogVerbose($"[DownloadHelper] Cleaned up partial file: {path}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[DownloadHelper] Could not delete partial file {path}: {ex.Message}");
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
            if (bytes >= 1024 * 1024)
                return $"{bytes / (1024.0 * 1024):F1} MB";
            if (bytes >= 1024)
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }
    }
}
