// Copyright 2021-2025 KOTORModSync
// Licensed under the Business Source License 1.1 (BSL 1.1).
// See LICENSE.txt file in the project root for full license information.
//
// AUDIT FIXES (CrispyW0nton fork):
// 1. [BUG] Thread.Sleep on the async I/O path causes thread-pool starvation under concurrent downloads
//    — Fixed: async ReadAsync/WriteAsync overrides now use Task.Delay instead
// 2. [BUG] Throttle window resets every second but _byteCount is not protected — race condition under async
//    — Fixed: Interlocked operations for byte counting
// 3. [BUG] CanWrite exposed as true even though base stream may not support writes — pass-through corrected
// 4. [PERF] Per-read Throttle() call is heavy; replaced with token-bucket algorithm for smoother throttling

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KOTORModSync.Core.Services.Download
{
    /// <summary>
    /// Token-bucket throttled stream wrapper.
    /// Replaces the original Thread.Sleep implementation to avoid blocking thread-pool threads
    /// during async downloads. Uses Interlocked for thread-safe byte counting.
    /// </summary>
    public sealed class ThrottledStream : Stream
    {
        private readonly Stream _baseStream;
        private readonly long _maximumBytesPerSecond;

        // Token bucket state (thread-safe via Interlocked)
        private long _bucketTokens;
        private long _lastRefillTick;
        private const long TicksPerSecond = TimeSpan.TicksPerSecond;

        public ThrottledStream(Stream baseStream, long maximumBytesPerSecond)
        {
            _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
            _maximumBytesPerSecond = maximumBytesPerSecond;
            _lastRefillTick = DateTime.UtcNow.Ticks;
            // Start with a full bucket
            _bucketTokens = maximumBytesPerSecond;
        }

        public override bool CanRead => _baseStream.CanRead;
        public override bool CanSeek => _baseStream.CanSeek;
        public override bool CanWrite => _baseStream.CanWrite;
        public override long Length => _baseStream.Length;

        public override long Position
        {
            get => _baseStream.Position;
            set => _baseStream.Position = value;
        }

        public override void Flush() => _baseStream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _baseStream.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => _baseStream.Seek(offset, origin);
        public override void SetLength(long value) => _baseStream.SetLength(value);

        // ---------- Synchronous path (used minimally, kept for interface compliance) ----------

        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrottleSync(count);
            int bytesRead = _baseStream.Read(buffer, offset, count);
            ConsumeTokens(bytesRead);
            return bytesRead;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrottleSync(count);
            _baseStream.Write(buffer, offset, count);
            ConsumeTokens(count);
        }

        // ---------- Async path (FIX #1: no Thread.Sleep) ----------

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_maximumBytesPerSecond > 0)
                await ThrottleAsync(count, cancellationToken).ConfigureAwait(false);

            int bytesRead = await _baseStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            if (bytesRead > 0)
                ConsumeTokens(bytesRead);
            return bytesRead;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_maximumBytesPerSecond > 0)
                await ThrottleAsync(count, cancellationToken).ConfigureAwait(false);

            await _baseStream.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            ConsumeTokens(count);
        }

        // ---------- Token bucket implementation ----------

        private void RefillBucket()
        {
            long now = DateTime.UtcNow.Ticks;
            long lastRefill = Interlocked.Read(ref _lastRefillTick);
            long elapsed = now - lastRefill;

            if (elapsed <= 0)
                return;

            // Tokens to add = (elapsed / TicksPerSecond) * _maximumBytesPerSecond
            long newTokens = elapsed * _maximumBytesPerSecond / TicksPerSecond;

            if (newTokens <= 0)
                return;

            // Atomically update last refill time and add tokens
            if (Interlocked.CompareExchange(ref _lastRefillTick, now, lastRefill) == lastRefill)
            {
                long current = Interlocked.Read(ref _bucketTokens);
                long capped = Math.Min(current + newTokens, _maximumBytesPerSecond); // Cap at 1 second's worth
                Interlocked.Exchange(ref _bucketTokens, capped);
            }
        }

        private void ConsumeTokens(long bytes)
        {
            if (_maximumBytesPerSecond <= 0)
                return;
            Interlocked.Add(ref _bucketTokens, -bytes);
        }

        private void ThrottleSync(int requestedBytes)
        {
            if (_maximumBytesPerSecond <= 0)
                return;

            RefillBucket();

            // FIX #1: Kept synchronous path with Thread.Sleep only when no async alternative available
            while (Interlocked.Read(ref _bucketTokens) < requestedBytes)
            {
                int sleepMs = (int)Math.Min(
                    ((requestedBytes - Interlocked.Read(ref _bucketTokens)) * 1000L / _maximumBytesPerSecond),
                    100);
                if (sleepMs < 1)
                    break;
                Thread.Sleep(sleepMs);
                RefillBucket();
            }
        }

        private async Task ThrottleAsync(int requestedBytes, CancellationToken cancellationToken)
        {
            if (_maximumBytesPerSecond <= 0)
                return;

            RefillBucket();

            // FIX #1: Use Task.Delay instead of Thread.Sleep on the async path
            while (Interlocked.Read(ref _bucketTokens) < requestedBytes)
            {
                long deficit = requestedBytes - Interlocked.Read(ref _bucketTokens);
                int waitMs = (int)Math.Min(deficit * 1000L / _maximumBytesPerSecond, 100);
                if (waitMs < 1)
                    break;

                await Task.Delay(waitMs, cancellationToken).ConfigureAwait(false);
                RefillBucket();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _baseStream?.Dispose();
            base.Dispose(disposing);
        }

        public override string ToString() =>
            $"ThrottledStream (Max: {_maximumBytesPerSecond / 1024} KB/s, Tokens: {Interlocked.Read(ref _bucketTokens)})";
    }
}
