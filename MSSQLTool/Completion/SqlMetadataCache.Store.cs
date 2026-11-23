using Microsoft.Data.SqlClient;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MSSQLTool.Completion
{
    /// <summary>
    /// Cache state, loading pipeline, freshness checks, incremental refresh and diagnostics.
    /// <para>
    /// The catalog queries themselves stay in SqlMetadataCache.cs; this file decides <em>when</em>
    /// they run.  Three rules shape it:
    /// </para>
    /// <list type="bullet">
    /// <item>A completion request never waits for the network once any snapshot exists: the previous
    /// snapshot is served while its replacement loads (stale-while-revalidate).</item>
    /// <item>Invalidation marks an entry dirty instead of deleting it.  Deleting made the next popup
    /// wait for a full database reload after every DDL statement.</item>
    /// <item>Freshness is proven by a cheap catalog signature, not by a blind 30-minute timer.</item>
    /// </list>
    /// </summary>
    internal static partial class SqlMetadataCache
    {
        private sealed class Entry
        {
            public string Key;
            public string Database;
            public string Scope;
            public ScriptFactoryAccess.ConnectionInfo Info;
            public Func<CancellationToken, bool, Task<MetadataSnapshot>> Loader;
            public string InteractiveMessage;

            public readonly object Gate = new object();
            public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();

            /// <summary>Published snapshot. Never mutated in place after publication.</summary>
            public volatile MetadataSnapshot Snapshot;
            /// <summary>Single-flight full load.</summary>
            public volatile Task<MetadataSnapshot> Loading;
            public volatile bool Dirty;

            public MetadataEntryState State = MetadataEntryState.Empty;
            public int ValidationRunning;
            public int PersistRunning;

            public long Hits;
            public long Misses;
            public long Refreshes;
            public long DiskSeeds;
            public double LastLoadMilliseconds;

            private long lastAccessTicks = DateTime.UtcNow.Ticks;
            private long lastValidatedTicks = DateTime.MinValue.Ticks;
            private long lastPersistTicks = DateTime.MinValue.Ticks;

            public DateTime LastAccessUtc => new DateTime(Interlocked.Read(ref lastAccessTicks), DateTimeKind.Utc);
            public DateTime LastValidatedUtc => new DateTime(Interlocked.Read(ref lastValidatedTicks), DateTimeKind.Utc);
            public DateTime LastPersistUtc => new DateTime(Interlocked.Read(ref lastPersistTicks), DateTimeKind.Utc);

            public MetadataSignature Signature;
            public bool HasSignature;

            public void Touch() => Interlocked.Exchange(ref lastAccessTicks, DateTime.UtcNow.Ticks);
            public void MarkValidated() => Interlocked.Exchange(ref lastValidatedTicks, DateTime.UtcNow.Ticks);
            public void MarkPersisted() => Interlocked.Exchange(ref lastPersistTicks, DateTime.UtcNow.Ticks);
        }

        private static readonly ConcurrentDictionary<string, Entry> Cache =
            new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Background freshness safety net, not a user-visible reload: an expired snapshot is
        /// still served while the replacement loads.</summary>
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan MinimumValidationGap = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PersistThrottle = TimeSpan.FromSeconds(60);
        /// <summary>A disk seed younger than this is trusted for the first keystrokes without waiting
        /// for the live load; older seeds immediately start a background refresh.</summary>
        private static readonly TimeSpan DiskSeedMaxAge = TimeSpan.FromMinutes(5);

        /// <summary>Caps concurrent catalog loads so opening several databases cannot open a dozen
        /// metadata connections at once.</summary>
        private static readonly SemaphoreSlim FullLoadGate = new SemaphoreSlim(2, 2);
        private static readonly SemaphoreSlim PatchGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim DetailGate = new SemaphoreSlim(1, 1);
        private static readonly object EvictionGate = new object();

        private static int revisionCounter;

        /// <summary>
        /// Databases this login could not open.  A cross-database script asks for the same
        /// unavailable database on every keystroke, and each attempt used to cost a full login
        /// timeout on a background thread.  A failure is remembered for a short while instead of
        /// being retried, and its reason travels with the completion snapshot so the popup and the
        /// diagnostics page can say which database is missing instead of silently showing fewer
        /// objects.
        /// </summary>
        private static readonly ConcurrentDictionary<string, DatabaseUnavailable> UnavailableDatabases =
            new ConcurrentDictionary<string, DatabaseUnavailable>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan UnavailableLifetime = TimeSpan.FromMinutes(10);

        private sealed class DatabaseUnavailable
        {
            public DateTime UntilUtc;
            public string Message;
        }

        private static bool TryGetUnavailable(string key, out string message)
        {
            message = null;
            if (string.IsNullOrEmpty(key) || !UnavailableDatabases.TryGetValue(key, out DatabaseUnavailable mark)) return false;
            if (mark == null || mark.UntilUtc <= DateTime.UtcNow)
            {
                UnavailableDatabases.TryRemove(key, out _);
                return false;
            }
            message = mark.Message;
            return true;
        }

        /// <summary>Remembers that a referenced database cannot be opened, so the next keystroke of
        /// the same script does not pay for another login attempt.</summary>
        private static void NoteUnavailableDatabase(ScriptFactoryAccess.ConnectionInfo info, string database, Exception exception)
        {
            if (info == null || string.IsNullOrWhiteSpace(database) || exception == null) return;
            if (!SqlMetadataCache.IsUnavailableDatabase(exception)) return;
            try
            {
                string key = GetKey(info.FullConnectionString, database);
                UnavailableDatabases[key] = new DatabaseUnavailable
                {
                    UntilUtc = DateTime.UtcNow.Add(UnavailableLifetime),
                    Message = "database '" + database + "': " + FirstLine(exception.Message)
                };
                CompletionTrace.Write("referenced database '" + database + "' marked unavailable");
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "SQL completion metadata could not remember an unavailable database.");
            }
        }

        /// <summary>Raised after a snapshot became readable. The controller re-renders an open popup.</summary>
        internal static event Action<MetadataSnapshotChange> SnapshotPublished;

        /// <summary>Raised when entries were dropped or replaced wholesale, so dependent caches can
        /// (for example the merged cross-database snapshots) be discarded.</summary>
        internal static event Action Invalidated;

        // ------------------------------------------------------------------
        // Progress reported to the status bar and the popup footer.
        // ------------------------------------------------------------------

        /// <summary>
        /// Progress of the metadata load that is currently running: which step it is on, how much it
        /// has read, and how long it has been going.  The status bar and the completion popup both
        /// read it, so a slow server no longer looks like a frozen popup.
        /// </summary>
        internal static class LoadProgress
        {
            private static readonly Stopwatch Clock = new Stopwatch();
            private static string message = string.Empty;
            private static string step = string.Empty;
            private static int items;

            internal static string Current => Volatile.Read(ref message);

            internal static void Reset(string statusMessage)
            {
                Clock.Restart();
                Volatile.Write(ref step, statusMessage ?? string.Empty);
                Volatile.Write(ref items, 0);
                Volatile.Write(ref message, statusMessage ?? string.Empty);
            }

            internal static void Step(string stepName, int count = 0)
            {
                if (string.IsNullOrWhiteSpace(stepName)) return;
                Volatile.Write(ref step, stepName);
                Volatile.Write(ref items, count);
                Publish();
            }

            /// <summary>Called while a reader is streaming, to keep the count (and the clock) moving.</summary>
            internal static void Report(string stepName, int count)
            {
                Volatile.Write(ref step, stepName);
                Volatile.Write(ref items, count);
                Publish();
            }

            private static void Publish()
            {
                string text = LocalizationManager.Format("{0} ({1}: {2}, {3:0.0}s)", LocalizationManager.T("Loading SQL completion metadata"),
                    Volatile.Read(ref step), Volatile.Read(ref items), Clock.Elapsed.TotalSeconds);
                Volatile.Write(ref message, text);
                StatusFeedback.Update(text);
            }
        }

        // ------------------------------------------------------------------
        // Read path
        // ------------------------------------------------------------------

        public static Task<MetadataSnapshot> GetAsync(ScriptFactoryAccess.ConnectionInfo info, CancellationToken token)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString)) return Task.FromResult(MetadataSnapshot.Empty);
            try
            {
                string key = GetKey(info.FullConnectionString);
                Entry entry = GetOrCreate(key, info, null, null,
                    (ct, interactive) => LoadCoreAsync(info, null, ct, interactive),
                    LocalizationManager.T("Loading SQL completion metadata..."));
                return AcquireAsync(entry, token);
            }
            catch (Exception ex)
            {
                CompletionTrace.Failure("metadata cache could not start a load", ex);
                return Task.FromResult(new MetadataSnapshot { ErrorMessage = ex.Message });
            }
        }

        /// <summary>
        /// Snapshot for one keystroke, but never for longer than <paramref name="waitTimeout"/>.
        /// A cold cache used to park the whole completion request behind the catalog read (6.4 s
        /// was measured on a 9 700-object database), during which the popup showed nothing at all.
        /// Returning null means "still loading": the caller keeps the loading frame up and the
        /// publication refreshes it when the snapshot arrives.
        /// </summary>
        internal static async Task<MetadataSnapshot> GetAsync(ScriptFactoryAccess.ConnectionInfo info, CancellationToken token, TimeSpan waitTimeout)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString)) return MetadataSnapshot.Empty;
            try
            {
                string key = GetKey(info.FullConnectionString);
                Entry entry = GetOrCreate(key, info, null, null,
                    (ct, interactive) => LoadCoreAsync(info, null, ct, interactive),
                    LocalizationManager.T("Loading SQL completion metadata..."));
                entry.Touch();
                MetadataSnapshot snapshot = entry.Snapshot;
                if (snapshot != null)
                {
                    Interlocked.Increment(ref entry.Hits);
                    MaybeValidate(entry);
                    return snapshot;
                }

                Interlocked.Increment(ref entry.Misses);
                Task<MetadataSnapshot> load = EnsureLoad(entry, true, false);
                Task finished = await Task.WhenAny(load, Task.Delay(waitTimeout, CancellationToken.None)).ConfigureAwait(false);
                if (!ReferenceEquals(finished, load)) return null;
                return await AwaitWithCancellation(load, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                CompletionTrace.Failure("metadata cache could not start a load", ex);
                return new MetadataSnapshot { ErrorMessage = ex.Message };
            }
        }

        public static Task<MetadataSnapshot> GetDatabaseAsync(ScriptFactoryAccess.ConnectionInfo info, string database, CancellationToken token)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString) || string.IsNullOrWhiteSpace(database))
                return Task.FromResult(MetadataSnapshot.Empty);
            try
            {
                string key = GetKey(info.FullConnectionString, database);
                if (TryGetUnavailable(key, out string unavailableMessage))
                {
                    CompletionTrace.Write("referenced database '" + database + "' skipped: " + unavailableMessage);
                    return Task.FromResult(new MetadataSnapshot
                    {
                        Scope = (info.ServerName ?? string.Empty) + "/" + database,
                        ErrorMessage = unavailableMessage
                    });
                }
                string message = LocalizationManager.Format("Loading SQL completion metadata for database {0}...", database);
                Entry entry = GetOrCreate(key, info, database, null,
                    (ct, interactive) => LoadCoreAsync(info, database, ct, interactive), message);
                return AcquireAsync(entry, token);
            }
            catch (Exception ex)
            {
                CompletionTrace.Failure("metadata cache could not start a database load", ex);
                return Task.FromResult(new MetadataSnapshot { ErrorMessage = ex.Message });
            }
        }

        public static Task<MetadataSnapshot> GetLinkedServerAsync(ScriptFactoryAccess.ConnectionInfo info, string linkedServer, string database, CancellationToken token)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString)) return Task.FromResult(MetadataSnapshot.Empty);
            try
            {
                string key = GetKey(info.FullConnectionString) + "|linked|" + linkedServer + "|" + (database ?? string.Empty);
                string message = LocalizationManager.Format("Loading SQL completion metadata from linked server {0}...", linkedServer);
                Entry entry = GetOrCreate(key, info, database, linkedServer,
                    (ct, interactive) => Task.Run(() => LoadLinked(info, linkedServer, database), ct), message);
                return AcquireAsync(entry, token);
            }
            catch (Exception ex)
            {
                CompletionTrace.Failure("metadata cache could not start a linked-server load", ex);
                return Task.FromResult(new MetadataSnapshot { ErrorMessage = ex.Message });
            }
        }

        /// <summary>
        /// Warms the cache for a connection without the status bar animation.  Called when a query
        /// window opens, becomes active or connects, so the first completion request does not pay the
        /// load.  Idempotent and never throws; the catalog is read on a worker thread.
        /// </summary>
        public static Task<MetadataSnapshot> PrefetchAsync(ScriptFactoryAccess.ConnectionInfo info) => EnsureAsync(info);

        /// <summary>
        /// The explicit "connected" hook: loads the snapshot once per connection and then only
        /// validates it.  The shell raises several activation events for one connection (new window,
        /// database dropdown, results tab), and none of them may start a second catalog read or block
        /// the editor while the first one is still running.
        /// </summary>
        public static Task<MetadataSnapshot> WarmConnectionAsync(ScriptFactoryAccess.ConnectionInfo info) => EnsureAsync(info);

        internal static Task<MetadataSnapshot> EnsureAsync(ScriptFactoryAccess.ConnectionInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString)) return Task.FromResult(MetadataSnapshot.Empty);
            try
            {
                string key = GetKey(info.FullConnectionString);
                Entry entry = GetOrCreate(key, info, null, null,
                    (ct, interactive) => LoadCoreAsync(info, null, ct, interactive),
                    LocalizationManager.T("Loading SQL completion metadata..."));

                entry.Touch();
                MetadataSnapshot snapshot = entry.Snapshot;
                if (snapshot == null) return EnsureLoad(entry, false, false);

                MaybeValidate(entry);
                return entry.Dirty ? EnsureLoad(entry, false, false) : Task.FromResult(snapshot);
            }
            catch (Exception ex)
            {
                CompletionTrace.Failure("metadata prefetch failed", ex);
                return Task.FromResult(MetadataSnapshot.Empty);
            }
        }

        private static async Task<MetadataSnapshot> AcquireAsync(Entry entry, CancellationToken token)
        {
            entry.Touch();
            MetadataSnapshot snapshot = entry.Snapshot;
            if (snapshot != null)
            {
                Interlocked.Increment(ref entry.Hits);
                MaybeValidate(entry);
                return snapshot;
            }

            Interlocked.Increment(ref entry.Misses);
            Task<MetadataSnapshot> load = EnsureLoad(entry, true, false);
            return await AwaitWithCancellation(load, token).ConfigureAwait(false);
        }

        public static bool TryGetCached(ScriptFactoryAccess.ConnectionInfo info, out MetadataSnapshot snapshot)
        {
            snapshot = null;
            if (!TryGetConnectionKey(info, out string key)) return false;
            if (!Cache.TryGetValue(key, out Entry entry)) return false;
            snapshot = entry.Snapshot;
            return snapshot != null;
        }

        internal static bool TryGetConnectionKey(ScriptFactoryAccess.ConnectionInfo info, out string key)
        {
            key = null;
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString)) return false;
            try
            {
                key = GetKey(info.FullConnectionString);
                return true;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        // Entry lifecycle
        // ------------------------------------------------------------------

        private static Entry GetOrCreate(string key, ScriptFactoryAccess.ConnectionInfo info, string database, string linkedServer,
            Func<CancellationToken, bool, Task<MetadataSnapshot>> loader, string interactiveMessage)
        {
            if (Cache.TryGetValue(key, out Entry existing))
            {
                existing.Touch();
                return existing;
            }

            var created = new Entry
            {
                Key = key,
                Database = database,
                Info = info,
                Loader = loader,
                InteractiveMessage = interactiveMessage,
                Scope = (info == null ? string.Empty : info.ServerName) + "/" + (database ?? (info == null ? string.Empty : info.Database))
            };

            Entry current = Cache.GetOrAdd(key, created);
            if (!ReferenceEquals(current, created))
            {
                current.Touch();
                return current;
            }

            SeedFromDisk(current);
            EvictIfNeeded(key);

            // A seed older than the trust window starts the live load immediately; a fresh seed
            // waits for the periodic validation instead of opening a connection on window activation.
            MetadataSnapshot seed = current.Snapshot;
            if (seed != null && DateTime.UtcNow - seed.PublishedUtc > DiskSeedMaxAge)
                _ = EnsureLoad(current, false, false);
            return current;
        }

        private static void SeedFromDisk(Entry entry)
        {
            try
            {
                if (!SettingsManager.GetSqlCompletionSettings().persistMetadataCache) return;
                if (!MetadataPersistence.TryLoad(entry.Key, out MetadataSnapshot snapshot, out MetadataSignature signature, out DateTime savedUtc))
                    return;

                snapshot.Source = "disk";
                snapshot.IsStale = true;
                snapshot.PublishedUtc = savedUtc;
                snapshot.Revision = Interlocked.Increment(ref revisionCounter);
                entry.Snapshot = snapshot;
                entry.State = MetadataEntryState.Ready;
                entry.Signature = signature;
                entry.HasSignature = signature.HasValue;
                Interlocked.Increment(ref entry.DiskSeeds);
                CompletionTrace.Write("metadata disk seed scope='" + snapshot.Scope + "' objects=" + snapshot.Objects.Count
                    + " saved=" + savedUtc.ToString("o"));
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "SQL completion metadata disk seed was ignored.");
            }
        }

        private static Task<MetadataSnapshot> EnsureLoad(Entry entry, bool interactive, bool force)
        {
            lock (entry.Gate)
            {
                Task<MetadataSnapshot> running = entry.Loading;
                if (!force && running != null && !running.IsCompleted) return running;

                entry.State = entry.Snapshot == null ? MetadataEntryState.Loading : MetadataEntryState.Refreshing;
                entry.Loading = LoadAndPublishAsync(entry, interactive);
                return entry.Loading;
            }
        }

        private static async Task<MetadataSnapshot> LoadAndPublishAsync(Entry entry, bool interactive)
        {
            bool gateTaken = false;
            var clock = Stopwatch.StartNew();
            try
            {
                await FullLoadGate.WaitAsync(entry.Cancellation.Token).ConfigureAwait(false);
                gateTaken = true;

                // The catalog queries are synchronous ADO.NET calls.  Running the loader on the
                // caller's thread meant an interactive prefetch (window activation, connection
                // change) could open a connection and read the catalog on the UI thread, which is
                // exactly the pause the cache is supposed to remove.  The loader therefore always
                // runs on a worker thread, and only the publication returns to the caller.
                MetadataSnapshot loaded = interactive
                    ? await LoadInteractiveAsync(entry).ConfigureAwait(false)
                    : await Task.Run(() => entry.Loader(entry.Cancellation.Token, false), entry.Cancellation.Token).ConfigureAwait(false);

                entry.Cancellation.Token.ThrowIfCancellationRequested();
                clock.Stop();

                bool publishable = loaded != null
                    && (loaded.Objects.Count > 0 || loaded.Parameters.Count > 0 || string.IsNullOrWhiteSpace(loaded.ErrorMessage));
                if (publishable)
                {
                    loaded.Source = "server";
                    loaded.IsStale = false;
                    loaded.Revision = Interlocked.Increment(ref revisionCounter);
                    loaded.PublishedUtc = DateTime.UtcNow;
                    entry.LastLoadMilliseconds = clock.Elapsed.TotalMilliseconds;
                    Interlocked.Increment(ref entry.Refreshes);
                    if (!string.IsNullOrEmpty(entry.Database)) UnavailableDatabases.TryRemove(entry.Key, out _);

                    // A replacement refreshes the objects it actually read and keeps every other
                    // object instance as it was, instead of swapping the whole snapshot at once.
                    // Readers therefore never see the catalog disappear and re-appear.
                    MetadataSnapshot previous = entry.Snapshot;
                    if (previous != null && previous.Objects.Count > 0 && loaded.Objects.Count > 0)
                        await MergeIntoPublishedAsync(entry, loaded).ConfigureAwait(false);
                    else
                        Publish(entry, loaded, interactive ? "load" : "background-load");

                    entry.MarkValidated();
                    ScheduleSignature(entry);
                    SchedulePersist(entry);
                }
                else
                {
                    entry.State = entry.Snapshot != null ? MetadataEntryState.Ready : MetadataEntryState.Failed;
                }

                return entry.Snapshot ?? loaded ?? MetadataSnapshot.Empty;
            }
            catch (OperationCanceledException)
            {
                return entry.Snapshot ?? MetadataSnapshot.Empty;
            }
            catch (Exception ex)
            {
                entry.State = entry.Snapshot != null ? MetadataEntryState.Ready : MetadataEntryState.Failed;
                MSSQLToolPackage._logger?.Error(ex, "SQL completion metadata load failed");
                CompletionTrace.Failure("metadata load failed", ex);
                return entry.Snapshot ?? new MetadataSnapshot { ErrorMessage = ex.Message };
            }
            finally
            {
                if (gateTaken) FullLoadGate.Release();
            }
        }

        private static async Task<MetadataSnapshot> LoadInteractiveAsync(Entry entry)
        {
            string message = entry.InteractiveMessage ?? LocalizationManager.T("Loading SQL completion metadata...");
            using (StatusFeedback.Begin(message))
            {
                LoadProgress.Reset(message);
                return await Task.Run(() => entry.Loader(entry.Cancellation.Token, true), entry.Cancellation.Token).ConfigureAwait(false);
            }
        }

        /// <summary>How many objects one incremental publication carries.</summary>
        private const int MergeBatchSize = 500;

        /// <summary>
        /// Applies a freshly loaded snapshot to the published one object by object.  Unchanged
        /// objects keep their instance (and therefore their enrichment); only added, removed and
        /// changed objects are swapped in, and every batch is published on its own so an open popup
        /// keeps answering from the previous data while the refresh runs.
        /// </summary>
        private static async Task MergeIntoPublishedAsync(Entry entry, MetadataSnapshot loaded)
        {
            MetadataSnapshot published = entry.Snapshot;
            if (published == null)
            {
                Publish(entry, loaded, "load");
                return;
            }

            var publishedByIdentity = new Dictionary<string, DatabaseObjectMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (DatabaseObjectMetadata item in published.Objects)
            {
                string identity = IdentityOf(item);
                if (!publishedByIdentity.ContainsKey(identity)) publishedByIdentity[identity] = item;
            }

            var loadedIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changed = new List<DatabaseObjectMetadata>();
            foreach (DatabaseObjectMetadata item in loaded.Objects)
            {
                string identity = IdentityOf(item);
                loadedIdentities.Add(identity);
                if (!publishedByIdentity.TryGetValue(identity, out DatabaseObjectMetadata existing) || !SameObjectShape(existing, item))
                    changed.Add(item);
            }

            var removed = new List<string>();
            foreach (DatabaseObjectMetadata item in published.Objects)
            {
                string identity = IdentityOf(item);
                if (!loadedIdentities.Contains(identity)) removed.Add(identity);
            }

            CompletionTrace.Write("metadata merge scope='" + loaded.Scope + "' changed=" + changed.Count
                + " removed=" + removed.Count + " objects=" + loaded.Objects.Count);

            bool removalsPending = removed.Count > 0;
            int index = 0;
            while (index < changed.Count || removalsPending)
            {
                MetadataSnapshot next = CloneSnapshot(entry.Snapshot ?? published);

                if (removalsPending)
                {
                    // One pass over the object list instead of one pass per removed object.
                    var removalSet = new HashSet<string>(removed, StringComparer.OrdinalIgnoreCase);
                    next.Objects.RemoveAll(item => removalSet.Contains(IdentityOf(item)));
                    removalsPending = false;
                }

                var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int position = 0; position < next.Objects.Count; position++)
                {
                    string identity = IdentityOf(next.Objects[position]);
                    if (!positions.ContainsKey(identity)) positions[identity] = position;
                }

                int take = Math.Min(MergeBatchSize, changed.Count - index);
                for (int offset = 0; offset < take; offset++)
                {
                    DatabaseObjectMetadata item = changed[index + offset];
                    string identity = IdentityOf(item);
                    if (positions.TryGetValue(identity, out int position))
                    {
                        DatabaseObjectMetadata existing = next.Objects[position];
                        // The loader returns before its enrichment pass, so a fresh instance often
                        // has no details yet.  Reuse the previous ones until the pass fills them in
                        // (it writes to the very instance that is published here).
                        bool loadedHasDetails = !string.IsNullOrEmpty(item.Description)
                            || (item.Indexes != null && item.Indexes.Count > 0);
                        PreserveDetails(existing, item, preserveDefinition: false, preserveDetails: !loadedHasDetails);
                        next.Objects[position] = item;
                    }
                    else
                    {
                        positions[identity] = next.Objects.Count;
                        next.Objects.Add(item);
                    }
                }

                index += take;

                next.Source = "merge";
                next.IsStale = false;
                next.LoadedUtc = loaded.LoadedUtc;
                next.PublishedUtc = DateTime.UtcNow;
                next.Revision = Interlocked.Increment(ref revisionCounter);
                next.ErrorMessage = loaded.ErrorMessage;
                next.IsPartial = loaded.IsPartial;
                Publish(entry, next, "merge");

                await Task.Yield();
            }

            // Catalog-wide data is only consistent once every object batch has been applied.
            MetadataSnapshot final = CloneSnapshot(entry.Snapshot ?? published);
            final.CompatibilityLevel = loaded.CompatibilityLevel;
            final.ErrorMessage = loaded.ErrorMessage;
            final.IsPartial = loaded.IsPartial;
            final.Source = "merge";
            final.IsStale = false;
            final.LoadedUtc = loaded.LoadedUtc;
            final.PublishedUtc = DateTime.UtcNow;
            final.Revision = Interlocked.Increment(ref revisionCounter);

            final.Parameters.Clear();
            final.Parameters.AddRange(loaded.Parameters);
            final.Schemas.Clear();
            final.Schemas.AddRange(loaded.Schemas);
            final.Databases.Clear();
            final.Databases.AddRange(loaded.Databases);
            final.LinkedServers.Clear();
            final.LinkedServers.AddRange(loaded.LinkedServers);
            final.LinkedServerDatabases.Clear();
            foreach (var pair in loaded.LinkedServerDatabases)
                final.LinkedServerDatabases[pair.Key] = new List<string>(pair.Value);
            final.SetForeignKeys(loaded.ForeignKeys);

            Publish(entry, final, "merge-complete");
        }

        /// <summary>Copies the snapshot envelope but shares the object instances with the source.</summary>
        private static MetadataSnapshot CloneSnapshot(MetadataSnapshot source)
        {
            var clone = new MetadataSnapshot
            {
                LoadedUtc = source.LoadedUtc,
                Scope = source.Scope,
                ErrorMessage = source.ErrorMessage,
                IsPartial = source.IsPartial,
                CompatibilityLevel = source.CompatibilityLevel,
                Source = source.Source,
                IsStale = source.IsStale,
                PublishedUtc = source.PublishedUtc,
                Revision = source.Revision
            };

            clone.Schemas.AddRange(source.Schemas);
            clone.Databases.AddRange(source.Databases);
            clone.LinkedServers.AddRange(source.LinkedServers);
            foreach (var pair in source.LinkedServerDatabases)
                clone.LinkedServerDatabases[pair.Key] = new List<string>(pair.Value);
            clone.Objects.AddRange(source.Objects);
            clone.Parameters.AddRange(source.Parameters);
            clone.SetForeignKeys(source.ForeignKeys);
            return clone;
        }

        /// <summary>
        /// True when the two objects describe the same catalog shape, so the published instance can
        /// be kept and no completion result can change.  Optional enrichment is not compared: it is
        /// not part of the shape.
        /// </summary>
        private static bool SameObjectShape(DatabaseObjectMetadata existing, DatabaseObjectMetadata incoming)
        {
            if (existing == null || incoming == null) return false;
            if (existing.Kind != incoming.Kind) return false;
            if (existing.IsTableValuedFunction != incoming.IsTableValuedFunction) return false;
            if (!string.Equals(existing.SynonymBaseObjectName ?? string.Empty, incoming.SynonymBaseObjectName ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)) return false;
            if (existing.Columns.Count != incoming.Columns.Count) return false;

            var byName = new Dictionary<string, ColumnMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (ColumnMetadata column in existing.Columns)
            {
                if (!byName.ContainsKey(column.Name)) byName[column.Name] = column;
            }

            foreach (ColumnMetadata column in incoming.Columns)
            {
                if (!byName.TryGetValue(column.Name, out ColumnMetadata previous)) return false;
                if (!string.Equals(previous.DataType ?? string.Empty, column.DataType ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
                if (previous.IsNullable != column.IsNullable) return false;
                if (previous.IsIdentity != column.IsIdentity || previous.IsComputed != column.IsComputed) return false;
            }

            return true;
        }

        private static void Publish(Entry entry, MetadataSnapshot snapshot, string reason)
        {
            if (snapshot == null) return;
            entry.Snapshot = snapshot;
            entry.State = MetadataEntryState.Ready;
            entry.Dirty = false;
            entry.Touch();

            CompletionTrace.Write("metadata published scope='" + snapshot.Scope + "' source=" + snapshot.Source
                + " reason=" + reason + " objects=" + snapshot.Objects.Count + " revision=" + snapshot.Revision);

            RaiseInvalidated();
            Action<MetadataSnapshotChange> handlers = SnapshotPublished;
            if (handlers == null) return;
            var change = new MetadataSnapshotChange { Key = entry.Key, Scope = snapshot.Scope, Reason = reason, Snapshot = snapshot };
            try { handlers(change); }
            catch (Exception ex) { MSSQLToolPackage._logger?.Debug(ex, "A completion popup could not refresh after a metadata update."); }
        }

        private static void RaiseInvalidated()
        {
            Action handlers = Invalidated;
            if (handlers == null) return;
            try { handlers(); }
            catch (Exception ex) { MSSQLToolPackage._logger?.Debug(ex, "A dependent metadata cache could not be cleared."); }
        }

        // ------------------------------------------------------------------
        // Invalidation and refresh
        // ------------------------------------------------------------------

        /// <summary>
        /// Marks every snapshot of this server dirty and starts a background replacement.  The old
        /// snapshot keeps answering completions in the meantime: this is the difference between a
        /// popup that pauses after DDL and one that does not.
        /// </summary>
        public static void Invalidate(ScriptFactoryAccess.ConnectionInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString)) return;
            try
            {
                string prefix = new SqlConnectionStringBuilder(info.FullConnectionString).DataSource + "|";
                foreach (var pair in Cache)
                {
                    if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    MarkDirty(pair.Value, "invalidate");
                }
                RaiseInvalidated();
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "SQL completion metadata could not be invalidated.");
            }
        }

        private static void MarkDirty(Entry entry, string reason)
        {
            entry.Dirty = true;
            MetadataSnapshot snapshot = entry.Snapshot;
            if (snapshot != null) snapshot.IsStale = true;
            CompletionTrace.Write("metadata dirty scope='" + entry.Scope + "' reason=" + reason);
            _ = EnsureLoad(entry, false, false);
        }

        public static void InvalidateAll()
        {
            foreach (var pair in Cache)
            {
                try { pair.Value.Cancellation.Cancel(); } catch { }
            }
            Cache.Clear();
            UnavailableDatabases.Clear();
            RaiseInvalidated();
            CompletionTrace.Write("metadata cache cleared");
        }

        /// <summary>Explicit user refresh: reloads and returns the newest snapshot, keeping the
        /// previous one usable if the reload fails.</summary>
        public static async Task<MetadataSnapshot> RefreshAsync(ScriptFactoryAccess.ConnectionInfo info, CancellationToken token)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.FullConnectionString)) return MetadataSnapshot.Empty;
            try
            {
                string key = GetKey(info.FullConnectionString);
                Entry entry = GetOrCreate(key, info, null, null,
                    (ct, interactive) => LoadCoreAsync(info, null, ct, interactive),
                    LocalizationManager.T("Refreshing SQL completion metadata..."));
                entry.Dirty = true;
                Task<MetadataSnapshot> load = EnsureLoad(entry, true, true);
                return await AwaitWithCancellation(load, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "Refreshing SQL completion metadata failed");
                TryGetCached(info, out MetadataSnapshot existing);
                return existing ?? new MetadataSnapshot { ErrorMessage = ex.Message };
            }
        }

        /// <summary>Kept for callers that only need the yes/no answer. The package now uses
        /// <see cref="SchemaChangeAnalyzer.Analyze"/> so it can refresh the affected objects instead
        /// of the whole database.</summary>
        public static bool ShouldInvalidateAfterExecution(string sql)
            => SchemaChangeAnalyzer.Analyze(sql).HasChanges;

        /// <summary>Periodic freshness check of the active connection, called from a lightweight timer.</summary>
        internal static void ValidateIfDue(ScriptFactoryAccess.ConnectionInfo info)
        {
            if (!TryGetConnectionKey(info, out string key)) return;
            if (Cache.TryGetValue(key, out Entry entry)) MaybeValidate(entry);
        }

        private static void MaybeValidate(Entry entry)
        {
            if (entry.Snapshot == null) return;

            int minutes = SettingsManager.GetSqlCompletionSettings().validateMetadataMinutes;
            if (minutes <= 0) return;
            if (DateTime.UtcNow - entry.LastValidatedUtc < TimeSpan.FromMinutes(minutes)) return;
            if (Interlocked.CompareExchange(ref entry.ValidationRunning, 1, 0) != 0) return;

            _ = Task.Run(async () =>
            {
                try { await ValidateAsync(entry).ConfigureAwait(false); }
                catch (Exception ex) { CompletionTrace.Failure("metadata validation failed", ex); }
                finally { Volatile.Write(ref entry.ValidationRunning, 0); }
            });
        }

        /// <summary>
        /// Compares the catalog signature with the one stored when the snapshot was published.  On a
        /// difference the replacement loads in the background; readers keep the current snapshot.
        /// </summary>
        private static async Task<bool> ValidateAsync(Entry entry)
        {
            MetadataSnapshot snapshot = entry.Snapshot;
            if (snapshot == null) return false;
            if (DateTime.UtcNow - entry.LastValidatedUtc < MinimumValidationGap) return false;

            MetadataSignature current = await Task.Run(() => ReadSignature(entry.Info, entry.Database, entry.Cancellation.Token),
                entry.Cancellation.Token).ConfigureAwait(false);
            entry.MarkValidated();
            if (!current.HasValue) return false;

            bool changed = !entry.HasSignature || !entry.Signature.Equals(current);
            entry.Signature = current;
            entry.HasSignature = true;

            if (!changed)
            {
                if (snapshot.IsStale && !entry.Dirty) snapshot.IsStale = false;
                return false;
            }

            CompletionTrace.Write("metadata signature changed scope='" + entry.Scope + "' objects=" + current.Objects
                + " columns=" + current.Columns + " indexes=" + current.Indexes);
            await EnsureLoad(entry, false, false).ConfigureAwait(false);
            return true;
        }

        private static void ScheduleSignature(Entry entry)
        {
            string database = entry.Database;
            ScriptFactoryAccess.ConnectionInfo info = entry.Info;
            if (info == null) return;
            _ = Task.Run(() =>
            {
                try
                {
                    MetadataSignature signature = ReadSignature(info, database, entry.Cancellation.Token);
                    if (!signature.HasValue) return;
                    entry.Signature = signature;
                    entry.HasSignature = true;
                    entry.MarkValidated();
                }
                catch (Exception ex) { CompletionTrace.Failure("metadata signature could not be stored", ex); }
            });
        }

        private static void SchedulePersist(Entry entry)
        {
            try
            {
                var settings = SettingsManager.GetSqlCompletionSettings();
                if (!settings.persistMetadataCache) return;
                MetadataSnapshot snapshot = entry.Snapshot;
                if (snapshot == null || snapshot.Objects.Count == 0) return;
                if (DateTime.UtcNow - entry.LastPersistUtc < PersistThrottle) return;
                entry.MarkPersisted();
                if (Interlocked.CompareExchange(ref entry.PersistRunning, 1, 0) != 0) return;

                MetadataSignature signature = entry.Signature;
                int maxFiles = Math.Max(1, settings.metadataCacheMaxDatabases);
                _ = Task.Run(() =>
                {
                    try { MetadataPersistence.Save(entry.Key, snapshot, signature, maxFiles); }
                    catch (Exception ex) { MSSQLToolPackage._logger?.Debug(ex, "SQL completion metadata could not be persisted."); }
                    finally { Volatile.Write(ref entry.PersistRunning, 0); }
                });
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Debug(ex, "SQL completion metadata persistence was skipped.");
            }
        }

        // ------------------------------------------------------------------
        // Catalog signature
        // ------------------------------------------------------------------

        /// <summary>
        /// Four aggregate queries: a few milliseconds that prove whether the snapshot is still
        /// accurate.  A default signature means the catalog could not be read; callers then keep the
        /// current snapshot and retry on the next tick.
        /// </summary>
        private static MetadataSignature ReadSignature(ScriptFactoryAccess.ConnectionInfo info, string database, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                using (var connection = CreateMetadataConnection(info, database))
                using (token.Register(() => { try { connection.Close(); } catch { } }))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandTimeout = 20;
                        command.CommandText = @"
SELECT COUNT_BIG(*), ISNULL(SUM(CONVERT(bigint,o.object_id)),0), ISNULL(MAX(o.modify_date), CONVERT(datetime2,'19000101'))
FROM sys.all_objects o
WHERE o.type IN ('U','V','P','PC','X','FN','IF','TF','FS','FT') AND (o.is_ms_shipped=0 OR SCHEMA_NAME(o.schema_id)='sys');
SELECT COUNT_BIG(*) FROM sys.all_columns c JOIN sys.all_objects o ON o.object_id=c.object_id WHERE o.is_ms_shipped=0;
SELECT COUNT_BIG(*) FROM sys.indexes i JOIN sys.objects o ON o.object_id=i.object_id WHERE i.index_id>0 AND i.is_hypothetical=0;
SELECT COUNT_BIG(*) FROM sys.check_constraints cc JOIN sys.objects o ON o.object_id=cc.parent_object_id;";

                        using (var reader = command.ExecuteReader())
                        {
                            long objects = 0, objectIds = 0, columns = 0, indexes = 0, constraints = 0;
                            DateTime changed = DateTime.MinValue;
                            if (reader.Read())
                            {
                                objects = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                                objectIds = reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetValue(1));
                                changed = reader.IsDBNull(2) ? DateTime.MinValue : Convert.ToDateTime(reader.GetValue(2));
                            }
                            if (reader.NextResult() && reader.Read()) columns = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                            if (reader.NextResult() && reader.Read()) indexes = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                            if (reader.NextResult() && reader.Read()) constraints = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                            return new MetadataSignature(objects, objectIds, changed, columns, indexes, constraints);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return default(MetadataSignature);
            }
            catch (Exception ex)
            {
                CompletionTrace.Failure("metadata signature unavailable", ex);
                return default(MetadataSignature);
            }
        }

        // ------------------------------------------------------------------
        // Incremental refresh (single object, plus one dependency level)
        // ------------------------------------------------------------------

        /// <summary>Last targeted refresh per connection + object, so a long word does not queue one
        /// catalog read per keystroke.</summary>
        private static readonly ConcurrentDictionary<string, DateTime> MissRefreshStamps =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan MissRefreshGap = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Applies the object set of an executed DDL batch.  Concretely changed objects are reloaded
        /// one by one; anything ambiguous falls back to a full background reload.
        /// </summary>
        internal static void ApplySchemaChange(ScriptFactoryAccess.ConnectionInfo info, SchemaChangeSet change)
        {
            if (info == null || change == null || !change.HasChanges) return;
            if (!TryGetConnectionKey(info, out string key)) return;
            if (!Cache.TryGetValue(key, out Entry entry))
            {
                EnsureAsync(info);
                return;
            }

            if (change.RequiresFullReload || change.Objects.Count == 0 || entry.Snapshot == null)
            {
                MarkDirty(entry, change.RequiresFullReload ? "schema change (full)" : "schema change");
                CompletionTrace.Write("metadata schema change -> full reload scope='" + entry.Scope + "' objects=" + change.Objects.Count
                    + " full=" + change.RequiresFullReload);
                return;
            }

            List<SchemaObjectReference> references = change.Objects.ToList();
            CompletionTrace.Write("metadata schema change -> incremental scope='" + entry.Scope + "' " + string.Join(",", references));
            _ = Task.Run(async () =>
            {
                try
                {
                    // More objects than one patch call handles are applied as several small
                    // patches.  Falling back to a whole-database reload here made a multi-table
                    // migration pause completion for seconds.
                    const int chunkSize = 20;
                    for (int offset = 0; offset < references.Count; offset += chunkSize)
                    {
                        var chunk = references.GetRange(offset, Math.Min(chunkSize, references.Count - offset));
                        await PatchObjectsAsync(entry, chunk).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    CompletionTrace.Failure("incremental metadata refresh failed", ex);
                    MarkDirty(entry, "incremental failure");
                }
            });
        }

        /// <summary>
        /// A completion request that finds nothing is often a stale snapshot: a column added from
        /// another window or another client is not in the cache until the next signature check,
        /// which can be minutes away.  Reload only the objects the statement names, so the next
        /// keystroke sees them without waiting for a whole-database reload.
        /// </summary>
        internal static void RefreshObjectsOnMiss(ScriptFactoryAccess.ConnectionInfo info, IEnumerable<string> qualifiedNames)
        {
            try
            {
                if (info == null || qualifiedNames == null) return;
                if (!TryGetConnectionKey(info, out string key)) return;
                if (!Cache.TryGetValue(key, out Entry entry) || entry.Snapshot == null) return;

                var references = new List<SchemaObjectReference>();
                foreach (string name in qualifiedNames)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!DatabaseIdentifier.TrySplitSqlServer(name, out List<string> parts) || parts.Count == 0) continue;
                    // A cross-database name belongs to another snapshot; the reference loader owns it.
                    if (parts.Count > 2 && !string.Equals(parts[0], info.Database, StringComparison.OrdinalIgnoreCase)) continue;
                    string objectName = parts[parts.Count - 1];
                    string schema = parts.Count >= 2 ? parts[parts.Count - 2] : string.Empty;
                    if (string.IsNullOrWhiteSpace(objectName)) continue;

                    bool duplicate = false;
                    string referenceKey = ((schema ?? string.Empty) + "|" + objectName).ToLowerInvariant();
                    foreach (SchemaObjectReference existing in references)
                        if (string.Equals(existing.Key, referenceKey, StringComparison.OrdinalIgnoreCase)) { duplicate = true; break; }
                    if (duplicate) continue;

                    references.Add(new SchemaObjectReference { Schema = schema, Name = objectName });
                    if (references.Count >= 8) break;
                }
                if (references.Count == 0) return;

                // One refresh per object per few seconds: typing a long word must not queue one
                // catalog read per keystroke.
                DateTime now = DateTime.UtcNow;
                var due = new List<SchemaObjectReference>();
                foreach (SchemaObjectReference reference in references)
                {
                    string stampKey = key + "#" + reference.Key;
                    if (MissRefreshStamps.TryGetValue(stampKey, out DateTime last) && now - last < MissRefreshGap) continue;
                    MissRefreshStamps[stampKey] = now;
                    due.Add(reference);
                }
                if (due.Count == 0) return;
                if (MissRefreshStamps.Count > 512) MissRefreshStamps.Clear();

                CompletionTrace.Write("completion miss -> targeted metadata refresh scope='" + entry.Scope + "' " + string.Join(",", due));
                _ = Task.Run(async () =>
                {
                    try { await PatchObjectsAsync(entry, due).ConfigureAwait(false); }
                    catch (Exception ex) { CompletionTrace.Failure("targeted metadata refresh failed", ex); }
                });
            }
            catch (Exception ex)
            {
                CompletionTrace.Failure("targeted metadata refresh could not start", ex);
            }
        }

        private static async Task PatchObjectsAsync(Entry entry, IReadOnlyList<SchemaObjectReference> references)
        {
            await PatchGate.WaitAsync(entry.Cancellation.Token).ConfigureAwait(false);
            try
            {
                MetadataSnapshot existing = entry.Snapshot;
                if (existing == null)
                {
                    await EnsureLoad(entry, false, false).ConfigureAwait(false);
                    return;
                }

                var replacements = new Dictionary<string, DatabaseObjectMetadata>(StringComparer.OrdinalIgnoreCase);
                var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var newParameters = new List<RoutineParameterMetadata>();
                var dependents = new List<SchemaObjectReference>();
                bool ambiguous = false;

                using (var connection = CreateMetadataConnection(entry.Info, entry.Database))
                {
                    connection.Open();
                    foreach (SchemaObjectReference reference in references)
                    {
                        LoadedObject loaded = LoadSingleObject(connection, reference, dependents, out bool nameAmbiguous);
                        if (nameAmbiguous) { ambiguous = true; break; }
                        if (loaded == null) { removed.Add(reference.Key); continue; }

                        DatabaseObjectMetadata previous = FindObject(existing, reference);
                        if (previous != null) PreserveDetails(previous, loaded.Object, preserveDefinition: false, preserveDetails: false);
                        replacements[reference.Key] = loaded.Object;
                        newParameters.AddRange(loaded.Parameters);
                    }

                    if (!ambiguous)
                    {
                        // One dependency level: an ALTER VIEW must also refresh the procedures that
                        // select from it, otherwise their column metadata stays stale.
                        foreach (SchemaObjectReference dependent in dependents.Take(20))
                        {
                            if (replacements.ContainsKey(dependent.Key) || removed.Contains(dependent.Key)) continue;
                            LoadedObject loaded = LoadSingleObject(connection, dependent, null, out bool dependentAmbiguous);
                            if (dependentAmbiguous) { ambiguous = true; break; }
                            if (loaded == null) { removed.Add(dependent.Key); continue; }
                            DatabaseObjectMetadata previous = FindObject(existing, dependent);
                            if (previous != null) PreserveDetails(previous, loaded.Object, preserveDefinition: false, preserveDetails: false);
                            replacements[dependent.Key] = loaded.Object;
                            newParameters.AddRange(loaded.Parameters);
                        }
                    }
                }

                if (ambiguous)
                {
                    CompletionTrace.Write("metadata patch ambiguous -> full reload scope='" + entry.Scope + "'");
                    MarkDirty(entry, "ambiguous object name");
                    return;
                }

                var updated = new MetadataSnapshot
                {
                    LoadedUtc = DateTime.UtcNow,
                    PublishedUtc = DateTime.UtcNow,
                    Scope = existing.Scope,
                    CompatibilityLevel = existing.CompatibilityLevel,
                    ErrorMessage = existing.ErrorMessage,
                    IsPartial = existing.IsPartial,
                    Source = "patch",
                    Revision = Interlocked.Increment(ref revisionCounter)
                };
                updated.Schemas.AddRange(existing.Schemas);
                updated.Databases.AddRange(existing.Databases);
                updated.LinkedServers.AddRange(existing.LinkedServers);
                foreach (var pair in existing.LinkedServerDatabases) updated.LinkedServerDatabases[pair.Key] = new List<string>(pair.Value);
                updated.SetForeignKeys(existing.ForeignKeys);

                foreach (DatabaseObjectMetadata item in existing.Objects)
                {
                    string identity = IdentityOf(item);
                    if (removed.Contains(identity)) continue;
                    DatabaseObjectMetadata replacement;
                    updated.Objects.Add(replacements.TryGetValue(identity, out replacement) ? replacement : item);
                }

                foreach (var pair in replacements)
                {
                    bool present = false;
                    foreach (DatabaseObjectMetadata item in updated.Objects)
                        if (string.Equals(IdentityOf(item), pair.Key, StringComparison.OrdinalIgnoreCase)) { present = true; break; }
                    if (!present) updated.Objects.Add(pair.Value);
                }

                foreach (RoutineParameterMetadata parameter in existing.Parameters)
                {
                    if (IsParameterOfAny(parameter, references)) continue;
                    updated.Parameters.Add(parameter);
                }
                foreach (RoutineParameterMetadata parameter in newParameters) updated.Parameters.Add(parameter);

                foreach (DatabaseObjectMetadata item in replacements.Values)
                    if (!string.IsNullOrWhiteSpace(item.Schema) && !updated.Schemas.Contains(item.Schema)) updated.Schemas.Add(item.Schema);

                Publish(entry, updated, "incremental");
                entry.MarkValidated();
                ScheduleSignature(entry);
                SchedulePersist(entry);
            }
            finally
            {
                PatchGate.Release();
            }
        }

        private sealed class LoadedObject
        {
            public DatabaseObjectMetadata Object;
            public List<RoutineParameterMetadata> Parameters = new List<RoutineParameterMetadata>();
        }

        private static LoadedObject LoadSingleObject(SqlConnection connection, SchemaObjectReference reference,
            List<SchemaObjectReference> dependents, out bool ambiguous)
        {
            ambiguous = false;
            int objectId;
            string type;
            bool isSystem;

            using (var command = connection.CreateCommand())
            {
                command.CommandTimeout = 15;
                command.CommandText = reference.Schema == null
                    ? @"SELECT TOP (2) o.object_id,o.type,o.is_ms_shipped FROM sys.all_objects o WHERE o.name=@name;"
                    : @"SELECT TOP (2) o.object_id,o.type,o.is_ms_shipped FROM sys.all_objects o JOIN sys.schemas s ON s.schema_id=o.schema_id WHERE o.name=@name AND s.name=@schema;";
                command.Parameters.AddWithValue("@name", reference.Name);
                if (reference.Schema != null) command.Parameters.AddWithValue("@schema", reference.Schema);

                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return null;
                    objectId = reader.GetInt32(0);
                    type = reader.GetString(1);
                    isSystem = !reader.IsDBNull(2) && reader.GetBoolean(2);
                    if (reader.Read()) { ambiguous = true; return null; }
                }
            }

            var item = new DatabaseObjectMetadata
            {
                Database = connection.Database,
                Schema = reference.Schema,
                Name = reference.Name,
                Kind = ToKind(type),
                IsTableValuedFunction = type == "IF" || type == "TF" || type == "FT",
                IsSystem = isSystem
            };

            using (var command = connection.CreateCommand())
            {
                command.CommandTimeout = 15;
                command.CommandText = @"
SELECT s.name,c.name,ty.name,c.max_length,c.precision,c.scale,c.is_nullable,c.is_identity,c.is_computed,c.column_id
FROM sys.all_columns c
JOIN sys.all_objects o ON o.object_id=c.object_id
JOIN sys.schemas s ON s.schema_id=o.schema_id
JOIN sys.types ty ON ty.user_type_id=c.user_type_id
WHERE c.object_id=@id ORDER BY c.column_id;";
                command.Parameters.AddWithValue("@id", objectId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        item.Schema = string.IsNullOrWhiteSpace(item.Schema) ? reader.GetString(0) : item.Schema;
                        item.Columns.Add(new ColumnMetadata
                        {
                            Name = reader.GetString(1),
                            DataType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                            MaxLength = reader.IsDBNull(3) ? (short)0 : reader.GetInt16(3),
                            Precision = reader.IsDBNull(4) ? (byte)0 : reader.GetByte(4),
                            Scale = reader.IsDBNull(5) ? (byte)0 : reader.GetByte(5),
                            IsNullable = !reader.IsDBNull(6) && reader.GetBoolean(6),
                            IsIdentity = !reader.IsDBNull(7) && reader.GetBoolean(7),
                            IsComputed = !reader.IsDBNull(8) && reader.GetBoolean(8),
                            Ordinal = reader.IsDBNull(9) ? 0 : reader.GetInt32(9)
                        });
                    }
                }
            }

            var result = new LoadedObject { Object = item };
            if (item.Kind == CompletionItemKind.Procedure || item.Kind == CompletionItemKind.Function || item.IsTableValuedFunction)
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 15;
                    command.CommandText = @"
SELECT p.name,ty.name,p.is_output,p.max_length,p.precision,p.scale,p.parameter_id,p.has_default_value
FROM sys.all_parameters p JOIN sys.types ty ON ty.user_type_id=p.user_type_id
WHERE p.object_id=@id AND p.parameter_id>0 ORDER BY p.parameter_id;";
                    command.Parameters.AddWithValue("@id", objectId);
                    string owner = (item.Schema ?? string.Empty) + "." + item.Name;
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            result.Parameters.Add(new RoutineParameterMetadata
                            {
                                ObjectName = connection.Database + "." + owner,
                                Name = reader.GetString(0),
                                DataType = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                                IsOutput = !reader.IsDBNull(2) && reader.GetBoolean(2),
                                MaxLength = reader.IsDBNull(3) ? (short)0 : reader.GetInt16(3),
                                Precision = reader.IsDBNull(4) ? (byte)0 : reader.GetByte(4),
                                Scale = reader.IsDBNull(5) ? (byte)0 : reader.GetByte(5),
                                Ordinal = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                                HasDefaultValue = !reader.IsDBNull(7) && reader.GetBoolean(7)
                            });
                }
            }

            if (dependents != null && (item.Kind == CompletionItemKind.View || item.Kind == CompletionItemKind.Procedure || item.Kind == CompletionItemKind.Function))
            {
                try
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandTimeout = 15;
                        command.CommandText = @"
SELECT DISTINCT s.name,ro.name
FROM sys.sql_expression_dependencies d
JOIN sys.objects ro ON ro.object_id=d.referencing_id
JOIN sys.schemas s ON s.schema_id=ro.schema_id
WHERE d.referenced_id=@id AND d.referencing_id<>d.referenced_id;";
                        command.Parameters.AddWithValue("@id", objectId);
                        using (var reader = command.ExecuteReader())
                            while (reader.Read())
                                dependents.Add(new SchemaObjectReference { Schema = reader.GetString(0), Name = reader.GetString(1) });
                    }
                }
                catch (Exception ex) { RecordOptionalFailure("object dependencies", ex); }
            }

            // The enrichment pass belongs to a full load, so a patched object reads its own
            // description, indexes, constraints and row count here.  Without this an ALTER TABLE
            // kept the index list that no longer exists.
            TryLoadObjectDetails(connection, item, objectId);

            return result;
        }

        /// <summary>
        /// Re-reads the optional details of one object after a DDL patch, in one round trip with
        /// several result sets.
        /// </summary>
        private static void TryLoadObjectDetails(SqlConnection connection, DatabaseObjectMetadata item, int objectId)
        {
            if (connection == null || item == null) return;

            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 10;
                    command.CommandText = @"
SELECT CONVERT(nvarchar(4000), ep.value)
FROM sys.extended_properties ep
WHERE ep.major_id=@id AND ep.minor_id=0 AND ep.name=N'MS_Description';
SELECT c.name, dc.definition, CONVERT(nvarchar(4000), cep.value)
FROM sys.all_columns c
LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
LEFT JOIN sys.extended_properties cep ON cep.major_id=c.object_id AND cep.minor_id=c.column_id AND cep.name=N'MS_Description'
WHERE c.object_id=@id;
SELECT i.name, i.type_desc, i.is_unique, i.is_primary_key, ic.is_included_column, c.name
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
WHERE i.object_id=@id AND i.index_id>0 AND i.is_hypothetical=0
ORDER BY i.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id;
SELECT cc.name, cc.definition FROM sys.check_constraints cc WHERE cc.parent_object_id=@id ORDER BY cc.name;
SELECT SUM(ps.row_count) FROM sys.dm_db_partition_stats ps WHERE ps.object_id=@id AND ps.index_id IN (0,1);";
                    command.Parameters.AddWithValue("@id", objectId);

                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read() && !reader.IsDBNull(0)) item.Description = reader.GetString(0);

                        if (reader.NextResult())
                        {
                            var columns = new Dictionary<string, ColumnMetadata>(StringComparer.OrdinalIgnoreCase);
                            foreach (ColumnMetadata column in item.Columns)
                                if (!columns.ContainsKey(column.Name)) columns[column.Name] = column;

                            while (reader.Read())
                            {
                                if (reader.IsDBNull(0)) continue;
                                if (!columns.TryGetValue(reader.GetString(0), out ColumnMetadata column)) continue;
                                if (!reader.IsDBNull(1)) column.DefaultDefinition = reader.GetString(1);
                                if (!reader.IsDBNull(2)) column.Description = reader.GetString(2);
                            }
                        }

                        if (reader.NextResult())
                        {
                            var indexes = new List<IndexMetadata>();
                            var byName = new Dictionary<string, IndexMetadata>(StringComparer.OrdinalIgnoreCase);
                            while (reader.Read())
                            {
                                string indexName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                                if (indexName.Length == 0) continue;
                                if (!byName.TryGetValue(indexName, out IndexMetadata index))
                                {
                                    index = new IndexMetadata
                                    {
                                        Name = indexName,
                                        TypeDescription = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                                        IsUnique = !reader.IsDBNull(2) && reader.GetBoolean(2),
                                        IsPrimaryKey = !reader.IsDBNull(3) && reader.GetBoolean(3)
                                    };
                                    byName[indexName] = index;
                                    indexes.Add(index);
                                }

                                string columnName = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                                if (columnName.Length == 0) continue;
                                bool included = !reader.IsDBNull(4) && reader.GetBoolean(4);
                                if (included) index.IncludedColumns.Add(columnName);
                                else
                                {
                                    index.KeyColumns.Add(columnName);
                                    ColumnMetadata column = item.Columns.FirstOrDefault(c =>
                                        string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
                                    if (column != null)
                                    {
                                        column.IsPrimaryKey |= index.IsPrimaryKey;
                                        column.IsUnique |= index.IsUnique;
                                    }
                                }
                            }

                            item.SetIndexes(indexes);
                        }

                        if (reader.NextResult())
                        {
                            var constraints = new List<CheckConstraintMetadata>();
                            while (reader.Read())
                            {
                                constraints.Add(new CheckConstraintMetadata
                                {
                                    Name = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                                    Definition = reader.IsDBNull(1) ? string.Empty : reader.GetString(1)
                                });
                            }
                            item.SetCheckConstraints(constraints);
                        }

                        if (reader.NextResult() && reader.Read() && !reader.IsDBNull(0))
                            item.EstimatedRowCount = Convert.ToInt64(reader.GetValue(0));
                    }
                }

                item.ResetDetailCache();
            }
            catch (Exception ex)
            {
                RecordOptionalFailure("object details after a schema change", ex);
            }
        }

        private static void PreserveDetails(DatabaseObjectMetadata previous, DatabaseObjectMetadata replacement,
            bool preserveDefinition, bool preserveDetails)
        {
            if (previous == null || replacement == null) return;

            if (string.IsNullOrWhiteSpace(replacement.SynonymBaseObjectName))
                replacement.SynonymBaseObjectName = previous.SynonymBaseObjectName;
            if (preserveDefinition) replacement.Definition = previous.Definition;

            if (preserveDetails)
            {
                replacement.Description = previous.Description;
                replacement.EstimatedRowCount = previous.EstimatedRowCount;
                if (previous.Indexes != null && previous.Indexes.Count > 0) replacement.SetIndexes(previous.Indexes);
                if (previous.CheckConstraints != null && previous.CheckConstraints.Count > 0)
                    replacement.SetCheckConstraints(previous.CheckConstraints);

                foreach (ColumnMetadata column in replacement.Columns)
                {
                    ColumnMetadata old = previous.Columns.FirstOrDefault(c =>
                        string.Equals(c.Name, column.Name, StringComparison.OrdinalIgnoreCase));
                    if (old == null) continue;
                    column.Description = old.Description;
                    column.DefaultDefinition = old.DefaultDefinition;
                }
            }

            replacement.ResetDetailCache();
        }

        private static string IdentityOf(DatabaseObjectMetadata item)
            => ((item.Schema ?? string.Empty) + "|" + (item.Name ?? string.Empty)).ToLowerInvariant();

        private static DatabaseObjectMetadata FindObject(MetadataSnapshot snapshot, SchemaObjectReference reference)
        {
            if (snapshot == null || reference == null) return null;
            foreach (DatabaseObjectMetadata item in snapshot.Objects)
                if (string.Equals(item.Name, reference.Name, StringComparison.OrdinalIgnoreCase)
                    && (reference.Schema == null || string.Equals(item.Schema, reference.Schema, StringComparison.OrdinalIgnoreCase)))
                    return item;
            return null;
        }

        private static bool IsParameterOfAny(RoutineParameterMetadata parameter, IReadOnlyList<SchemaObjectReference> references)
        {
            if (parameter == null || string.IsNullOrWhiteSpace(parameter.ObjectName)) return false;
            string full = DatabaseIdentifier.NormalizeSqlServer(parameter.ObjectName);
            foreach (SchemaObjectReference reference in references)
            {
                if (reference == null || string.IsNullOrWhiteSpace(reference.Name)) continue;
                if (!full.EndsWith("." + reference.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (reference.Schema == null || full.EndsWith("." + reference.Schema + "." + reference.Name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // On-demand object details (definitions)
        // ------------------------------------------------------------------

        /// <summary>
        /// Fetches view/procedure/function definitions for the objects the current statement actually
        /// references, instead of loading every definition in the database during enrichment.  Bounded
        /// and skipped while a previous fetch is still running.
        /// </summary>
        internal static void EnsureDefinitionsAsync(ScriptFactoryAccess.ConnectionInfo info, MetadataSnapshot snapshot,
            IEnumerable<DatabaseObjectMetadata> objects)
        {
            if (info == null || snapshot == null || objects == null) return;
            if (!SettingsManager.GetSqlCompletionSettings().showObjectDetails) return;

            var pending = new List<DatabaseObjectMetadata>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DatabaseObjectMetadata item in objects)
            {
                if (item == null || item.IsSystem || !string.IsNullOrEmpty(item.Definition)) continue;
                if (item.Kind != CompletionItemKind.View && item.Kind != CompletionItemKind.Procedure && item.Kind != CompletionItemKind.Function) continue;
                if (!seen.Add(IdentityOf(item))) continue;
                pending.Add(item);
                if (pending.Count >= 20) break;
            }
            if (pending.Count == 0) return;

            _ = Task.Run(async () =>
            {
                if (!await DetailGate.WaitAsync(0).ConfigureAwait(false)) return;
                try
                {
                    using (var connection = CreateMetadataConnection(info))
                    {
                        connection.Open();
                        foreach (DatabaseObjectMetadata item in pending)
                        {
                            try
                            {
                                using (var command = connection.CreateCommand())
                                {
                                    command.CommandTimeout = 10;
                                    command.CommandText = "SELECT OBJECT_DEFINITION(OBJECT_ID(@name));";
                                    command.Parameters.AddWithValue("@name", string.IsNullOrWhiteSpace(item.Schema)
                                        ? Quote(item.Name)
                                        : Quote(item.Schema) + "." + Quote(item.Name));
                                    object value = command.ExecuteScalar();
                                    if (value == null || value == DBNull.Value) continue;
                                    item.Definition = Convert.ToString(value);
                                    item.ResetDetailCache();
                                }
                            }
                            catch (Exception ex) { RecordOptionalFailure("object definition", ex); }
                        }
                    }
                }
                catch (Exception ex) { RecordOptionalFailure("definitions on demand", ex); }
                finally { DetailGate.Release(); }
            });
        }

        private static string Quote(string identifier)
            => string.IsNullOrWhiteSpace(identifier) ? "[]" : "[" + identifier.Replace("]", "]]") + "]";

        // ------------------------------------------------------------------
        // Diagnostics and lifetime
        // ------------------------------------------------------------------

        internal static IReadOnlyList<MetadataCacheDiagnostic> GetDiagnostics()
        {
            var result = new List<MetadataCacheDiagnostic>();
            foreach (var pair in Cache)
            {
                Entry entry = pair.Value;
                MetadataSnapshot snapshot = entry.Snapshot;
                var diagnostic = new MetadataCacheDiagnostic
                {
                    Key = entry.Key,
                    Scope = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.Scope) ? snapshot.Scope : entry.Scope,
                    State = entry.State.ToString(),
                    Source = snapshot == null ? "-" : snapshot.Source,
                    Error = snapshot == null ? null : snapshot.ErrorMessage,
                    Objects = snapshot == null ? 0 : snapshot.Objects.Count,
                    Columns = snapshot == null ? 0 : snapshot.Objects.Sum(o => o.Columns.Count),
                    Parameters = snapshot == null ? 0 : snapshot.Parameters.Count,
                    LoadedUtc = snapshot == null ? DateTime.MinValue : snapshot.LoadedUtc,
                    LastAccessUtc = entry.LastAccessUtc,
                    LastValidatedUtc = entry.LastValidatedUtc == DateTime.MinValue ? (DateTime?)null : entry.LastValidatedUtc,
                    Hits = Interlocked.Read(ref entry.Hits),
                    Misses = Interlocked.Read(ref entry.Misses),
                    Refreshes = Interlocked.Read(ref entry.Refreshes),
                    DiskSeeds = Interlocked.Read(ref entry.DiskSeeds),
                    LastLoadMilliseconds = entry.LastLoadMilliseconds
                };
                diagnostic.EstimatedBytes = MetadataDiagnosticsFormatter.EstimateBytes(diagnostic.Objects, diagnostic.Columns, diagnostic.Parameters);
                result.Add(diagnostic);
            }
            return result;
        }

        internal static void Shutdown()
        {
            foreach (var pair in Cache)
            {
                try { pair.Value.Cancellation.Cancel(); } catch { }
            }
            Cache.Clear();
        }

        private static void EvictIfNeeded(string activeKey)
        {
            int maximum = Math.Max(1, SettingsManager.GetSqlCompletionSettings().metadataCacheMaxDatabases);
            if (Cache.Count <= maximum) return;

            lock (EvictionGate)
            {
                int excess = Cache.Count - maximum;
                var candidates = Cache
                    .Where(pair => !string.Equals(pair.Key, activeKey, StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Value)
                    .Where(entry => entry.Snapshot != null && (entry.Loading == null || entry.Loading.IsCompleted))
                    .OrderBy(entry => entry.LastAccessUtc)
                    .ToList();

                foreach (Entry entry in candidates)
                {
                    if (excess-- <= 0) break;
                    Cache.TryRemove(entry.Key, out Entry _);
                    CompletionTrace.Write("metadata entry evicted scope='" + entry.Scope + "'");
                }
            }
        }
    }
}
