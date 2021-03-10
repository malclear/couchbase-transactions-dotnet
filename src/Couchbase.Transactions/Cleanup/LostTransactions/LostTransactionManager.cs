﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Couchbase.Core.Compatibility;
using Couchbase.Transactions.DataAccess;
using Couchbase.Transactions.Internal.Test;
using Microsoft.Extensions.Logging;

namespace Couchbase.Transactions.Cleanup.LostTransactions
{
    internal interface ILostTransactionManager : IAsyncDisposable
    {
        // TODO: When we implement ExtCustomMetadataCollection, we'll need an overload that handles that
        Task StartAsync(CancellationToken token);
    }

    internal class LostTransactionManager : IAsyncDisposable
    {
        private const int DiscoverBucketsPeriodMs = 10_000;
        private readonly ILogger<LostTransactionManager> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ConcurrentDictionary<string, PerBucketCleaner> _discoveredBuckets = new ConcurrentDictionary<string, PerBucketCleaner>();
        private readonly ICluster _cluster;
        private readonly TimeSpan _cleanupWindow;
        private readonly TimeSpan? _keyValueTimeout;
        private readonly Timer _discoverBucketsTimer;
        private readonly CancellationTokenSource _overallCancellation = new CancellationTokenSource();
        private readonly SemaphoreSlim _timerCallbackMutex = new SemaphoreSlim(1);

        public string ClientUuid { get; }
        public ICleanupTestHooks TestHooks { get; set; } = DefaultCleanupTestHooks.Instance;
        public int DiscoveredBucketCount => _discoveredBuckets.Count;
        public int RunningCount => _discoveredBuckets.Where(pbc => pbc.Value.Running).Count();
        public long TotalRunCount => _discoveredBuckets.Sum(pbc => pbc.Value.RunCount);

        internal LostTransactionManager(ICluster cluster, ILoggerFactory loggerFactory, TimeSpan cleanupWindow, TimeSpan? keyValueTimeout, string? clientUuid = null, bool startDisabled = false)
        {
            ClientUuid = clientUuid ?? Guid.NewGuid().ToString();
            _logger = loggerFactory.CreateLogger<LostTransactionManager>();
            _loggerFactory = loggerFactory;
            _cluster = cluster;
            _cleanupWindow = cleanupWindow;
            _keyValueTimeout = keyValueTimeout;
            _discoverBucketsTimer = new Timer(TimerCallback, null, startDisabled ? -1 : 0, DiscoverBucketsPeriodMs);
        }

        public void Start()
        {
            _discoverBucketsTimer.Change(0, DiscoverBucketsPeriodMs);
        }

        public async ValueTask DisposeAsync()
        {
            _logger.LogDebug("Shutting down.");
            _discoverBucketsTimer.Change(-1, DiscoverBucketsPeriodMs);
            _overallCancellation.Cancel();
            await RemoveClientEntries().CAF();
            await _discoverBucketsTimer.DisposeAsync().CAF();
        }

        private async Task RemoveClientEntries()
        {
            try
            {
                await _timerCallbackMutex.WaitAsync().CAF();
                while (!_discoveredBuckets.IsEmpty)
                {
                    var buckets = _discoveredBuckets.ToArray();
                    foreach (var bkt in buckets)
                    {
                        try
                        {
                            _logger.LogDebug("Shutting down cleaner for '{bkt}", bkt.Value);
                            await bkt.Value.DisposeAsync().CAF();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("Error while shutting down lost transaction cleanup for '{bkt}': {ex}", bkt.Value, ex);
                        }
                        finally
                        {
                            _discoveredBuckets.TryRemove(bkt.Key, out _);
                        }
                    }
                }
            }
            finally
            {
                _timerCallbackMutex.Release();
            }
        }

        private async void TimerCallback(object? state)
        {
            try
            {
                await DiscoverBuckets(startDisabled: false).CAF();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Bucket discovery failed: {ex}", ex);
            }
        }

        public async Task DiscoverBuckets(bool startDisabled, Func<ICleanupTestHooks>? setupTestHooks = null)
        {
            try
            {
                var passed = await _timerCallbackMutex.WaitAsync(DiscoverBucketsPeriodMs, _overallCancellation.Token).CAF();
                if (!passed)
                {
                    // stopped waiting due to cancellation
                    return;
                }

                foreach (var existingBucket in _discoveredBuckets.ToArray())
                {
                    if (!existingBucket.Value.Running
                        && _discoveredBuckets.TryRemove(existingBucket.Key, out var removedBucket))
                    {
                        _logger.LogInformation("Cleaner for bucket '{bkt}' was  not running and was removed.", removedBucket.FullBucketName);
                    }
                }

                var buckets = await _cluster.Buckets.GetAllBucketsAsync().CAF();
                foreach (var bkt in buckets)
                {
                    var bucketName = bkt.Key;
                    _logger.LogDebug("Discovered {bkt}", bucketName);
                    if (!_discoveredBuckets.TryGetValue(bucketName, out var existingCleaner))
                    {
                        var newCleaner = await CleanerForBucket(bucketName, startDisabled).CAF();
                        setupTestHooks ??= () => TestHooks;
                        newCleaner.TestHooks = setupTestHooks() ?? newCleaner.TestHooks;
                        _logger.LogDebug("New Bucket Cleaner: {cleaner}", newCleaner);
                        _discoveredBuckets.TryAdd(bucketName, newCleaner);
                    }
                    else
                    {
                        _logger.LogDebug("Existing Bucket Cleaner: {cleaner}", existingCleaner.FullBucketName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Discover bucket failed: {ex}", ex);
            }
            finally
            {
                _timerCallbackMutex.Release();
            }
        }

        private async Task<PerBucketCleaner> CleanerForBucket(string bucketName, bool startDisabled)
        {
            // TODO: Support ExtCustomMetadataCollection
            _logger.LogDebug("New cleaner for bucket {bkt}", bucketName);
            var bucket = await _cluster.BucketAsync(bucketName).CAF();
            var collection = await bucket.DefaultCollectionAsync().CAF();
            var repository = new CleanerRepository(collection, _keyValueTimeout);
            var cleaner = new Cleaner(_cluster, _keyValueTimeout, _loggerFactory, creatorName: nameof(LostTransactionManager));

            return new PerBucketCleaner(ClientUuid, cleaner, repository, _cleanupWindow, _loggerFactory, startDisabled) { TestHooks = TestHooks };
        }
    }
}