using Microsoft.VisualStudio;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Linq;
using MSSQLTool.Editor;
using MSSQLTool.Editor.Vs2022;

namespace MSSQLTool.Completion
{
    internal sealed class CompletionController : IDisposable
    {
        private readonly IVsTextView textView;
        private readonly CompletionPresenter presenter = new CompletionPresenter();
        private readonly SynchronizationContext uiContext;
        private readonly System.Windows.Forms.Timer sessionMonitor;
        private readonly SqlPromptHighlightOverlay highlightOverlay;
        private readonly IEditorHost editorHost;
        private readonly MSSQLTool.Rendering.DiagnosticOverlay diagnosticOverlay;

        // Candidates funded by the last analysis, reused for keystrokes that only extend the
        // current word: filtering those from memory keeps typing inside a single frame.
        private volatile List<CompletionItem> cachedCandidates;
        private volatile CompletionContext cachedContext;
        private MetadataSnapshot cachedMetadata;
        private int cachedCaretOffset;
        private Microsoft.VisualStudio.Text.ITextSnapshot cachedSnapshot;
        private string sessionLocalKey;
        private List<DatabaseObjectMetadata> sessionLocalObjects;
        private int currentVersionNumber = -1;
        private readonly Dictionary<string, List<CompletionItem>> prefixCache =
            new Dictionary<string, List<CompletionItem>>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim analysisGate = new SemaphoreSlim(1, 1);
        private readonly object sessionLocalGate = new object();
        private readonly object analysisCacheGate = new object();
        private readonly Dictionary<string, CompletionContext> analysisCache =
            new Dictionary<string, CompletionContext>(StringComparer.Ordinal);
        private readonly Queue<string> analysisCacheOrder = new Queue<string>();
        private const int AnalysisCacheCapacity = 8;

        /// <summary>How long a keystroke may wait for a cold metadata snapshot before the loading
        /// frame is left in place and the publication refreshes it.  A whole catalog read must never
        /// hold the popup back.</summary>
        private static readonly TimeSpan MetadataWaitTimeout = TimeSpan.FromMilliseconds(900);
        private readonly Microsoft.VisualStudio.Text.Editor.IWpfTextView layoutView;
        private readonly EventHandler<Microsoft.VisualStudio.Text.Editor.TextViewLayoutChangedEventArgs> layoutChanged;
        private CancellationTokenSource requestCancellation;
        private CompletionContext currentContext;
        private int currentLine;
        private int currentColumn;
        private int currentDocLastLine = -1;
        private int currentDocLastColumn = -1;
        private string currentDocumentText;
        private readonly List<TextSpan> snippetStops = new List<TextSpan>();
        private int snippetStopIndex = -1;

        public CompletionController(IVsTextView textView)
        {
            this.textView = textView;
            uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            if (SqlEditorServices.TryGetSnapshot(textView, out Microsoft.VisualStudio.Text.ITextSnapshot initialSnapshot))
            {
                trackedBuffer = initialSnapshot.TextBuffer;
                if (trackedBuffer != null) trackedBuffer.Changed += OnBufferChanged;
            }
            presenter.CommitRequested += () => CommitCurrent();
            presenter.DismissRequested += () => Dismiss("popup request");
            sessionMonitor = new System.Windows.Forms.Timer { Interval = 200 };
            sessionMonitor.Tick += (_, __) =>
            {
                // Scrolling does not move the caret or change the text, so a visible popup has
                // to be re-anchored explicitly; the same tick keeps the caret/popup contract.
                if (presenter.IsVisible)
                {
                    bool caretCurrent = IsCaretCurrent();
                    bool pointerInteracting = presenter.IsPointerInteracting;
                    bool foregroundInProcess = presenter.ForegroundIsInProcess;
                    // Clicking the popup itself (list row, details pane, resize border) does not
                    // move the caret and does not leave SSMS: the poll has to ignore that gesture
                    // instead of closing the window the user is working in.
                    if (CompletionPresenter.ShouldDismissFromPoll(foregroundInProcess, pointerInteracting, caretCurrent))
                    {
                        CompletionTrace.Write("poll dismiss foregroundInProcess=" + foregroundInProcess
                            + " pointerInteracting=" + pointerInteracting + " caretCurrent=" + caretCurrent
                            + " line=" + currentLine + " column=" + currentColumn);
                        Dismiss("poll");
                    }
                }
            };
            sessionMonitor.Start();
            editorHost = EditorHostFactory.Create(textView);
            // Related-token highlight and squiggles are editor tags now (Editor/Vs2022), so the
            // window painters are not created at all: nothing is drawn twice and nothing drifts
            // while the view scrolls.
            highlightOverlay = null;
            diagnosticOverlay = null;

            // Scroll, zoom and wrapping move the popup by event instead of the 200 ms poll, which
            // only remains as the fallback when the editor adapter is unavailable.
            if (SqlEditorServices.TryGetTextView(textView, out Microsoft.VisualStudio.Text.Editor.IWpfTextView wpfView))
            {
                layoutView = wpfView;
                layoutChanged = (_, __) => { if (presenter.IsVisible) presenter.Reposition(textView); };
                layoutView.LayoutChanged += layoutChanged;
            }

            // A background refresh (schema change, signature check, prefetch) must reach an open
            // popup: without this the list kept the pre-DDL columns until the next keystroke.
            onSnapshotPublished = OnSnapshotPublished;
            SqlMetadataCache.SnapshotPublished += onSnapshotPublished;
        }

        public event Action BeforePopupShown;

        private Action<MetadataSnapshotChange> onSnapshotPublished;

        private static IEnumerable<DatabaseObjectMetadata> ReferencedObjects(IEnumerable<CompletionItem> items)
        {
            if (items == null) yield break;
            foreach (CompletionItem item in items)
            {
                if (item == null || item.ObjectSource == null) continue;
                if (item.ObjectSource.Kind == CompletionItemKind.Table) continue;
                yield return item.ObjectSource;
            }
        }

        /// <summary>
        /// Rebuilds the visible candidate list from a newly published snapshot. This is what makes
        /// stale-while-revalidate observable: the popup shows the old columns immediately, then swaps
        /// in the new ones when the background load finishes, without a keystroke.
        /// </summary>
        private void OnSnapshotPublished(MetadataSnapshotChange change)
        {
            try
            {
                if (change == null || change.Snapshot == null) return;
                CompletionContext context = cachedContext;
                if (context == null || ReferenceEquals(cachedMetadata, change.Snapshot)) return;
                if (!SqlMetadataCache.TryGetConnectionKey(ScriptFactoryAccess.GetCurrentConnectionInfo(), out string key)) return;
                if (!string.Equals(key, change.Key, StringComparison.OrdinalIgnoreCase)) return;

                // A popup that found nothing (a column added from another window) is closed by the
                // time the refresh lands.  Dropping the cached list makes the next keystroke analyze
                // against the new snapshot instead of answering from the stale one.
                if (!presenter.IsVisible)
                {
                    cachedCandidates = null;
                    cachedMetadata = null;
                    prefixCache.Clear();
                    return;
                }

                List<CompletionItem> candidates = SqlCompletionAnalyzer.BuildCandidates(context, change.Snapshot);
                if (candidates == null) return;

                cachedCandidates = candidates;
                cachedMetadata = change.Snapshot;
                prefixCache.Clear();
                List<CompletionItem> items = SqlCompletionAnalyzer.FilterCandidates(candidates, context.Prefix);
                if (items.Count == 0) return;

                uiContext.Post(_ =>
                {
                    try
                    {
                        if (!presenter.IsVisible || !ReferenceEquals(cachedContext, context) || !IsCaretCurrent()) return;
                        presenter.Show(textView, items, change.Snapshot, context);
                    }
                    catch (Exception ex)
                    {
                        FeatureDiagnostics.Report("SQL Completion", "The refreshed completion list could not be shown", ex);
                    }
                }, null);
            }
            catch (Exception ex)
            {
                CompletionTrace.Failure("metadata refresh could not be applied to the popup", ex);
            }
        }

        /// <summary>True while a click inside the completion popup may still be settling.</summary>
        internal bool IsSettlingPopupClick => presenter.IsPointerClickSettling;

        public bool IsVisible => presenter.IsVisible;

        public void Request(bool explicitRequest)
        {
            try
            {
                RequestCore(explicitRequest);
            }
            finally
            {
            }
        }

        private void RequestCore(bool explicitRequest)
        {
            var settings = SettingsManager.GetSqlCompletionSettings();
            requestCancellation?.Cancel();
            requestCancellation?.Dispose();
            if (!settings.enabled || (!explicitRequest && !settings.automaticPopup))
            {
                requestCancellation = null;
                currentContext = null;
                presenter.Hide("settings disabled");
                return;
            }
            requestCancellation = new CancellationTokenSource();
            CancellationToken token = requestCancellation.Token;

            // Fast path first: a keystroke that only extends the current word is served from the
            // cached candidates without copying the document.  Anything else (a dot, a comma, a
            // new statement, a new connection) falls through to the full analysis.
            if (!explicitRequest && TryServeFromSnapshotCache(false))
            {
                if (RuntimeLog.Enabled) RuntimeLog.Write("REQUEST", "served from the cached candidate list (one keystroke)");
                return;
            }

            // One full-document read per keystroke; only the current statement is analyzed.  The
            // window scanner is a single pass, so a 600 KB script no longer re-masks and re-parses
            // the whole document for every space, comma or dot.
            string text;
            int line;
            int column;
            if (!TryReadDocument(out text, out line, out column, out int docLastLine, out int docLastColumn)) return;
            currentDocLastLine = docLastLine;
            currentDocLastColumn = docLastColumn;
            int caretOffset = GetOffset(text, line, column);
            SqlStatementWindow window = SqlStatementWindow.Find(text, caretOffset);
            string beforeCaret = window.BeforeCaret;
            string afterCaret = window.AfterCaret;
            currentLine = line;
            currentColumn = column;
            currentDocumentText = text;
            if (SqlEditorServices.TryGetSnapshot(textView, out Microsoft.VisualStudio.Text.ITextSnapshot requestedSnapshot))
                currentVersionNumber = requestedSnapshot.Version.VersionNumber;
            ScriptFactoryAccess.ConnectionInfo connection = ScriptFactoryAccess.GetCurrentConnectionInfo();
            string metadataScope = connection == null ? string.Empty : (connection.ServerName + "|" + connection.Database);
            int compatibilityLevel = 170;
            if (connection != null && SqlMetadataCache.TryGetCached(connection, out MetadataSnapshot cachedMeta)
                && cachedMeta.CompatibilityLevel > 0)
                compatibilityLevel = cachedMeta.CompatibilityLevel;
            var requestMarker = new CompletionContext { IsExplicitRequest = explicitRequest, MetadataScope = metadataScope };
            if (RuntimeLog.Enabled)
                RuntimeLog.Write("REQUEST", "trigger=" + (explicitRequest ? "explicit" : "typing")
                    + " caret=" + line + ":" + column + " docLength=" + text.Length
                    + " statement=" + window.Start + ".." + (window.Start + beforeCaret.Length)
                    + " charBefore='" + (beforeCaret.Length == 0 ? string.Empty : beforeCaret[beforeCaret.Length - 1].ToString()) + "'"
                    + " connection='" + (connection == null ? "<none>" : connection.DisplayName) + "'"
                    + " scope='" + metadataScope + "' compatibility=" + compatibilityLevel
                    + " settings{mode=" + settings.completionCommitKey + " delay=" + settings.delayMilliseconds
                    + " maxItems=" + settings.maximumItems + " details=" + settings.showObjectDetails + "}");
            currentContext = requestMarker;
            if (CompletionTrace.DetailEnabled)
            {
                // Detailed autocomplete: the raw context the keystroke arrived with — the statement
                // window, both sides of the caret and the switches in force.
                CompletionTrace.Detail("context trigger=" + (explicitRequest ? "explicit" : "typing")
                    + " caret=" + line + ":" + column + " offset=" + caretOffset
                    + " documentLength=" + text.Length + " documentEnd=" + docLastLine + ":" + docLastColumn
                    + " version=" + currentVersionNumber
                    + " statementStart=" + window.Start + " statementCaretColumn=" + window.CaretColumn
                    + " charBefore=" + CompletionTrace.Quote(beforeCaret.Length == 0 ? string.Empty : beforeCaret[beforeCaret.Length - 1].ToString())
                    + " connection=" + CompletionTrace.Quote(connection?.DisplayName ?? string.Empty)
                    + " scope=" + CompletionTrace.Quote((metadataScope ?? string.Empty).Replace('|', '/'))
                    + " compatibility=" + compatibilityLevel
                    + " settings{mode=" + settings.completionCommitKey + " delay=" + settings.delayMilliseconds
                    + " maxItems=" + settings.maximumItems + " details=" + settings.showObjectDetails
                    + " columnPicker=" + settings.enableColumnPicker + "}");
                CompletionTrace.Detail("context statement=" + CompletionTrace.Quote(
                    CompletionTrace.Snippet(beforeCaret + "|" + afterCaret, 600)));
            }
            _ = LoadAndShowAsync(requestMarker, text, window.Start, beforeCaret, line, column, window.CaretColumn,
                afterCaret, caretOffset, connection, compatibilityLevel, token, explicitRequest, settings.delayMilliseconds);
        }

        /// <summary>
        /// The trailing word at the caret.  Only plain word characters count, so punctuation (a
        /// dot, a parenthesis, a comma) always falls back to a real analysis.
        /// </summary>
        internal static string TailWord(string text, int caretOffset)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            int end = Math.Max(0, Math.Min(caretOffset, text.Length));
            int start = end;
            while (start > 0 && IsWordChar(text[start - 1])) start--;
            return text.Substring(start, end - start);
        }

        private static bool IsWordChar(char value)
            => char.IsLetterOrDigit(value) || value == '_' || value == '@' || value == '#' || value == '$';

        /// <summary>
        /// Serves a keystroke that only extends (or shortens) the current word from the cached
        /// candidate list.  Validity is decided from the snapshot version and the edit ranges, so no
        /// document text has to be copied on the fast path.
        /// </summary>
        private bool TryServeFromSnapshotCache(bool explicitRequest)
        {
            List<CompletionItem> candidates = cachedCandidates;
            CompletionContext context = cachedContext;
            MetadataSnapshot metadata = cachedMetadata;
            if (candidates == null || context == null || metadata == null) return false;
            if (!SqlEditorServices.TryGetSnapshot(textView, out Microsoft.VisualStudio.Text.ITextSnapshot snapshot)) return false;
            if (textView.GetCaretPos(out int line, out int column) != VSConstants.S_OK) return false;

            int caretOffset = OffsetOf(snapshot, line, column);
            if (!CacheEditIsWordOnly(snapshot, caretOffset)) return false;

            int start = caretOffset;
            while (start > 0 && IsWordChar(snapshot[start - 1])) start--;
            if (start == caretOffset) return false;

            string prefix = snapshot.GetText(start, caretOffset - start);
            if (prefix.Length == 0 || prefix.Length > 64) return false;

            ScriptFactoryAccess.ConnectionInfo connection = ScriptFactoryAccess.GetCurrentConnectionInfo();
            string scope = connection == null ? string.Empty : connection.ServerName + "|" + connection.Database;
            if (!string.Equals(context.MetadataScope ?? string.Empty, scope ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;

            List<CompletionItem> items = GetFiltered(candidates, prefix);
            // The list predates the last letter, and a snippet is only known once its whole prefix
            // is typed: completing "SSF" here must re-analyze, or the row Ctrl+Space shows stays
            // absent while the stale list is filtered down.
            if (SqlCompletionAnalyzer.NeedsSnippetReanalysis(prefix, context.SnippetsEnabled, items,
                    SnippetService.GetAllSnippets()))
                return false;
            if (items.Count == 0) return false;

            context.Prefix = prefix;
            context.ReplacementStartColumn = Math.Max(0, column - prefix.Length);
            context.CaretLine = line;
            context.CaretColumn = column;
            context.IsExplicitRequest = explicitRequest;
            currentContext = context;
            currentLine = line;
            currentColumn = column;
            currentVersionNumber = snapshot.Version.VersionNumber;
            cachedSnapshot = snapshot;
            cachedCaretOffset = caretOffset;

            try
            {
                if (!presenter.IsVisible) BeforePopupShown?.Invoke();
                if (CompletionTrace.DetailEnabled)
                    CompletionTrace.Detail("structured source=cache caret=" + line + ":" + column
                        + " offset=" + caretOffset + " " + CompletionTrace.Describe(context));
                presenter.Show(textView, items, metadata, context);
                return true;
            }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("SQL Completion", "The cached completion list could not be shown", ex);
                return false;
            }
        }

        private static int OffsetOf(Microsoft.VisualStudio.Text.ITextSnapshot snapshot, int line, int column)
        {
            if (snapshot == null || line < 0 || line >= snapshot.LineCount) return 0;
            Microsoft.VisualStudio.Text.ITextSnapshotLine snapshotLine = snapshot.GetLineFromLineNumber(line);
            return snapshotLine.Start.Position + Math.Min(Math.Max(column, 0), snapshotLine.Length);
        }

        private Microsoft.VisualStudio.Text.ITextBuffer trackedBuffer;

        /// <summary>
        /// Invalidates the fast-path cache as soon as an edit does anything other than typing or
        /// deleting one word character at the caret (space, punctuation, paste, undo, multi-caret).
        /// Keeping this check in the buffer event means the fast path never has to diff text.
        /// </summary>
        private void OnBufferChanged(object sender, Microsoft.VisualStudio.Text.TextContentChangedEventArgs e)
        {
            try
            {
                if (e.Changes == null || e.Changes.Count == 0) return;
                foreach (Microsoft.VisualStudio.Text.ITextChange change in e.Changes)
                {
                    string inserted = change.NewText ?? string.Empty;
                    if (inserted.Length > 1 || change.OldLength > 1) { cachedSnapshot = null; return; }
                    if (inserted.Length == 1 && !IsWordChar(inserted[0])) { cachedSnapshot = null; return; }
                    if (Math.Abs(change.OldPosition - cachedCaretOffset) > 1) { cachedSnapshot = null; return; }
                }
            }
            catch (Exception)
            {
                cachedSnapshot = null;
            }
        }

        private bool CacheEditIsWordOnly(Microsoft.VisualStudio.Text.ITextSnapshot snapshot, int caretOffset)
        {
            if (cachedSnapshot == null) return false;
            if (ReferenceEquals(cachedSnapshot, snapshot)) return true;
            // The buffer event already rejected every non-word edit; only the caret has to still be
            // one character away from where the cached candidates were built.
            return Math.Abs(caretOffset - cachedCaretOffset) <= 1;
        }

        private List<CompletionItem> GetFiltered(List<CompletionItem> candidates, string prefix)
        {
            List<CompletionItem> cached;
            if (prefixCache.TryGetValue(prefix, out cached)) return cached;

            List<CompletionItem> items = SqlCompletionAnalyzer.FilterCandidates(candidates, prefix);
            if (prefixCache.Count > 64) prefixCache.Clear();
            prefixCache[prefix] = items;
            return items;
        }

        /// <summary>
        /// Temp tables declared before the current statement.  The prefix hash changes only when
        /// an edit touches the prefix, so typing inside the current statement reuses the list
        /// instead of re-running the temp-table regexes on every keystroke.
        /// </summary>
        private List<DatabaseObjectMetadata> GetSessionLocalObjects(string text, int statementStart, string scope)
        {
            if (string.IsNullOrEmpty(text) || statementStart <= 0)
            {
                lock (sessionLocalGate) { sessionLocalKey = null; sessionLocalObjects = null; }
                return null;
            }
            int hash = SqlStatementWindow.HashRange(text, 0, statementStart);
            string key = (scope ?? string.Empty) + "|" + statementStart + "|" + hash;
            lock (sessionLocalGate)
            {
                if (string.Equals(key, sessionLocalKey, StringComparison.Ordinal)) return sessionLocalObjects;
                List<DatabaseObjectMetadata> locals = SqlCompletionAnalyzer.CollectSessionLocalObjects(
                    text.Substring(0, Math.Min(statementStart, text.Length)));
                sessionLocalObjects = locals;
                sessionLocalKey = key;
                return locals;
            }
        }

        /// <summary>
        /// Analysis is keyed by the exact statement slice and caret.  A popup refresh, an explicit
        /// Ctrl+Space on the same text and an undo/redo therefore reuse the parse instead of paying
        /// for it twice; the key includes the session-local prefix and the compatibility level so a
        /// changed temp table or connection can never hit a stale context.
        /// </summary>
        private CompletionContext AnalyzeWithStatementCache(CompletionContext requestMarker, string beforeCaret,
            int analysisColumn, string afterCaret, string documentText, int statementStart, int compatibilityLevel)
        {
            List<DatabaseObjectMetadata> sessionLocals = GetSessionLocalObjects(documentText, statementStart, requestMarker.MetadataScope);
            string key = (requestMarker.MetadataScope ?? string.Empty) + "|" + (sessionLocalKey ?? (statementStart + "|0"))
                + "|" + compatibilityLevel + "|" + analysisColumn
                + "|" + (beforeCaret ?? string.Empty).Length + ":" + SqlStatementWindow.HashRange(beforeCaret, 0, (beforeCaret ?? string.Empty).Length)
                + "|" + (afterCaret ?? string.Empty).Length + ":" + SqlStatementWindow.HashRange(afterCaret, 0, (afterCaret ?? string.Empty).Length);
            lock (analysisCacheGate)
            {
                CompletionContext cached;
                if (analysisCache.TryGetValue(key, out cached)) return cached;
            }

            CompletionContext context = SqlCompletionAnalyzer.Analyze(beforeCaret, analysisColumn, afterCaret, sessionLocals, compatibilityLevel);
            if (context == null) return null;
            lock (analysisCacheGate)
            {
                if (!analysisCache.ContainsKey(key))
                {
                    analysisCache[key] = context;
                    analysisCacheOrder.Enqueue(key);
                    while (analysisCacheOrder.Count > AnalysisCacheCapacity)
                        analysisCache.Remove(analysisCacheOrder.Dequeue());
                }
            }
            return context;
        }


        private async Task LoadAndShowAsync(CompletionContext requestMarker, string documentText, int statementStart,
            string beforeCaret, int caretLine, int caretColumn, int analysisColumn, string afterCaret,
            int documentCaretOffset, ScriptFactoryAccess.ConnectionInfo connection, int compatibilityLevel,
            CancellationToken token, bool explicitRequest, int delayMilliseconds)
        {
            CompletionContext context = requestMarker;
            var requestWatch = Stopwatch.StartNew();
            long analysisMilliseconds = 0;
            try
            {
                if (!explicitRequest)
                    await Task.Delay(delayMilliseconds, token).ConfigureAwait(false);

                await analysisGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    // Latest wins: if a newer keystroke already replaced this request, skip the
                    // expensive analysis instead of queueing it behind the previous one.
                    if (!ReferenceEquals(currentContext, requestMarker)) return;

                    var analysisWatch = Stopwatch.StartNew();
                    context = await Task.Run(() => AnalyzeWithStatementCache(requestMarker, beforeCaret, analysisColumn,
                        afterCaret, documentText, statementStart, compatibilityLevel), token).ConfigureAwait(false);
                    analysisMilliseconds = analysisWatch.ElapsedMilliseconds;
                }
                finally
                {
                    analysisGate.Release();
                }
                context.IsExplicitRequest = explicitRequest;
                context.MetadataScope = requestMarker.MetadataScope;
                context.CaretLine = caretLine;
                context.CaretColumn = caretColumn;
                if (CompletionTrace.DetailEnabled)
                {
                    // Detailed autocomplete: caret and the structured data the analyzer produced.
                    CompletionTrace.Detail("caret line=" + caretLine + " column=" + caretColumn
                        + " offset=" + documentCaretOffset + " statementStart=" + statementStart
                        + " analysisColumn=" + analysisColumn
                        + " replacementStartColumn=" + context.ReplacementStartColumn
                        + " documentEnd=" + currentDocLastLine + ":" + currentDocLastColumn
                        + " version=" + currentVersionNumber);
                    CompletionTrace.Detail("structured source=analyze ms=" + analysisMilliseconds + " "
                        + CompletionTrace.Describe(context));
                }
                if (RuntimeLog.Enabled)
                    RuntimeLog.Perf("ANALYZE", "kind=" + context.Kind + " prefix='" + context.Prefix + "' qualifier='" + context.Qualifier
                        + "' target='" + (context.TargetObject ?? string.Empty) + "' aliases=" + context.Aliases.Count
                        + " suppress=" + context.Suppress + "", analysisMilliseconds);
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(Interlocked.CompareExchange(ref currentContext, context, requestMarker), requestMarker)) return;

                if (context.Suppress || (!explicitRequest && context.Kind == CompletionContextKind.General && string.IsNullOrEmpty(context.Prefix)))
                {
                    PostDismiss(context, token);
                    return;
                }

                // Once metadata is cached, avoid showing a metadata-free list first.
                // That preliminary frame was immediately replaced by the full list
                // and made the popup visibly disappear/repaint on every keystroke.
                bool hasCachedMetadata = connection != null
                    && SqlMetadataCache.TryGetCached(connection, out MetadataSnapshot _);
                if (!hasCachedMetadata)
                {
                    // A cold cache used to leave the popup blank for the whole catalog read (more
                    // than six seconds on a large database).  The frame keeps the keystroke
                    // responsive and the publication replaces it without another key.
                    _ = ShowLoadingFrameAsync(context, connection, token);
                    cachedContext = context;
                    cachedCandidates = null;
                    cachedMetadata = null;
                    prefixCache.Clear();
                }

                MetadataSnapshot metadata = await SqlMetadataCache.GetAsync(connection, token, MetadataWaitTimeout).ConfigureAwait(false);
                if (metadata == null)
                {
                    // Still loading: the frame stays up and OnSnapshotPublished fills it in.
                    return;
                }
                var referencedWatch = Stopwatch.StartNew();
                metadata = await LoadReferencedDatabasesAsync(context, connection, metadata, token).ConfigureAwait(false);
                await SqlMetadataCache.ResolveSynonymColumnsAsync(connection, metadata, context, token).ConfigureAwait(false);
                if (RuntimeLog.Enabled)
                    RuntimeLog.Perf("SCOPE", "referenced databases + synonym columns: databases=" + metadata.Databases.Count
                        + " objects=" + metadata.Objects.Count, referencedWatch.ElapsedMilliseconds);
                token.ThrowIfCancellationRequested();
                List<CompletionItem> candidates = SqlCompletionAnalyzer.BuildCandidates(context, metadata);
                cachedCandidates = candidates;
                cachedContext = context;
                cachedMetadata = metadata;
                prefixCache.Clear();
                cachedCaretOffset = documentCaretOffset;
                List<CompletionItem> visible = SqlCompletionAnalyzer.FilterCandidates(candidates, context.Prefix);
                if (RuntimeLog.Enabled)
                    RuntimeLog.Perf("RESULT", "candidates=" + candidates.Count + " visible=" + visible.Count
                        + " objects=" + metadata.Objects.Count + " databases=" + metadata.Databases.Count
                        + " errors='" + (metadata.ErrorMessage ?? string.Empty) + "'", requestWatch.ElapsedMilliseconds);

                // Nothing matched: most often the snapshot predates a column that was added from
                // another window.  Refresh just the objects this statement names, so the next
                // keystroke can offer them without a whole-database reload.
                if (visible.Count == 0 && !context.Suppress && connection != null)
                    SqlMetadataCache.RefreshObjectsOnMiss(connection, MissRefreshTargets(context));

                PostItems(context, visible, metadata, token);

                // Definitions are no longer part of the bulk load: fetch the ones this statement
                // references, bounded, so the first tooltip is complete without paying for the whole
                // database's procedure bodies.
                if (SettingsManager.GetSqlCompletionSettings().showObjectDetails)
                    SqlMetadataCache.EnsureDefinitionsAsync(connection, metadata, ReferencedObjects(candidates));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                FeatureDiagnostics.Report("SQL Completion", "Completion request failed", ex);
                var failed = new MetadataSnapshot { ErrorMessage = ex.Message };
                PostItems(context, SqlCompletionAnalyzer.BuildItems(context, MetadataSnapshot.Empty), failed, token);
            }
        }

        private void PostDismiss(CompletionContext context, CancellationToken token)
        {
            uiContext.Post(_ =>
            {
                try
                {
                    if (!token.IsCancellationRequested && ReferenceEquals(currentContext, context) && IsCurrentSnapshot())
                        presenter.Hide("empty context");
                }
                catch (Exception ex)
                {
                    FeatureDiagnostics.Report("SQL Completion", "The completion list could not be dismissed", ex);
                }
            }, null);
        }

        // A member-list request (e.g. "dbo.") before the metadata cache is warm
        // used to show nothing at all. After a short grace period — so fast
        // loads never flash an empty popup — park a loading frame in the popup.
        private async Task ShowLoadingFrameAsync(CompletionContext context, ScriptFactoryAccess.ConnectionInfo connection, CancellationToken token)
        {
            try
            {
                await Task.Delay(150, token).ConfigureAwait(false);
                if (connection != null && SqlMetadataCache.TryGetCached(connection, out MetadataSnapshot _)) return;
                uiContext.Post(_ =>
                {
                    if (token.IsCancellationRequested || !ReferenceEquals(currentContext, context) || !IsCurrentSnapshot()) return;
                    if (!presenter.IsVisible)
                    {
                        BeforePopupShown?.Invoke();
                        presenter.ShowLoading(textView);
                    }
                }, null);
            }
            catch (OperationCanceledException) { }
        }

        // Merged cross-database snapshots, keyed by connection + referenced
        // databases/linked servers. Deep-cloning a whole database snapshot per
        // keystroke dominated CPU time in multi-database scripts; the merge
        // only changes when the referenced set changes. Capped in size and
        // cleared whenever the metadata cache is invalidated.
        private static readonly ConcurrentDictionary<string, MetadataSnapshot> MergedSnapshotCache =
            new ConcurrentDictionary<string, MetadataSnapshot>(StringComparer.Ordinal);

        // The same names the analyzer uses for function completion.  A function call is never a
        // database reference: "ISNULL(Comment, '')" reported the function as TargetObject, the
        // fallback below turned it into Initial Catalog=ISNULL, the login answered 4060 and the
        // keystroke paid the full login timeout.
        private static readonly HashSet<string> BuiltInFunctionNameSet =
            new HashSet<string>(SqlCompletionAnalyzer.BuiltInFunctionNames, StringComparer.OrdinalIgnoreCase);

        static CompletionController()
        {
            SqlMetadataCache.Invalidated += MergedSnapshotCache.Clear;
        }

        private static async Task<MetadataSnapshot> LoadReferencedDatabasesAsync(CompletionContext context,
            ScriptFactoryAccess.ConnectionInfo connection, MetadataSnapshot current, CancellationToken token)
        {
            if (connection == null || current == null) return current ?? MetadataSnapshot.Empty;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var linked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Names only the fallback rule below accepted: they are absent from the current snapshot,
            // so they may not be databases at all.  A failed guess must not export its connection
            // error into the merged snapshot — the message would ride along with every later RESULT
            // and the popup would report a database the script never names.
            var speculated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> inspect = value =>
            {
                if (!DatabaseIdentifier.TrySplitSqlServer(value, out List<string> parts)) return;
                string first = parts.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(first)) return;
                if (current.LinkedServers.Any(s => string.Equals(s, first, StringComparison.OrdinalIgnoreCase)))
                {
                    linked[first] = parts.Count > 1 ? parts[1] : string.Empty;
                    return;
                }
                if (current.Databases.Any(d => string.Equals(d, first, StringComparison.OrdinalIgnoreCase))
                    && !string.Equals(first, connection.Database, StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(first);
                    return;
                }
                // A database name can be missing from the snapshot (a partial load, or a login whose
                // sys.databases is filtered).  A plain qualifier that is neither a schema, an object
                // nor an alias can only be a database, so its objects are still worth loading —
                // otherwise "SELECT * FROM other_db." answers with an empty list.  The metadata cache
                // remembers the attempt, so a name that turns out not to exist costs one lookup.
                if (parts.Count != 1) return;
                // "a." and "c." are aliases or columns that the alias map had not picked up yet; a
                // one-letter database is legal but rare enough that guessing it is never worth a
                // login round trip.  Real single-letter databases are still accepted above, where the
                // name is matched against sys.databases.
                if (first.Length < 2) return;
                if (BuiltInFunctionNameSet.Contains(first)) return;
                if (current.Schemas.Any(s => string.Equals(s, first, StringComparison.OrdinalIgnoreCase))) return;
                if (current.Objects.Any(o => string.Equals(o.Name, first, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(o.Schema, first, StringComparison.OrdinalIgnoreCase))) return;
                if (context.Aliases != null && context.Aliases.Keys.Any(a => string.Equals(a, first, StringComparison.OrdinalIgnoreCase))) return;
                if (string.Equals(first, connection.Database, StringComparison.OrdinalIgnoreCase)) return;
                names.Add(first);
                speculated.Add(first);
            };
            inspect(context.Qualifier);
            // The target is inspected for every context: "otherdb.dbo.MyFunc(" still needs its database.
            // What must not happen is a function *name* being read as a database, which is what the
            // built-in/one-letter guards above reject ("ISNULL(" reported target='ISNULL').
            inspect(context.TargetObject);
            foreach (string source in context.Aliases.Values) inspect(source);
            if (names.Count == 0 && linked.Count == 0) return current;
            CompletionTrace.Write("referenced load request databases='" + string.Join(",", names) + "' linked='"
                + string.Join(",", linked.Select(p => p.Key + ">" + p.Value)) + "' knownDatabases=" + current.Databases.Count
                + " qualifier='" + (context.Qualifier ?? string.Empty) + "' target='" + (context.TargetObject ?? string.Empty) + "'");

            string cacheKey = connection.FullConnectionString
                + "#" + string.Join("|", names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                + "#" + string.Join("|", linked.Select(p => p.Key + ">" + p.Value).OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            if (MergedSnapshotCache.TryGetValue(cacheKey, out MetadataSnapshot cachedMerged)) return cachedMerged;

            MetadataSnapshot merged = CloneSnapshot(current);
            foreach (string name in names)
            {
                try
                {
                    MetadataSnapshot additional = await SqlMetadataCache.GetDatabaseAsync(connection, name, token).ConfigureAwait(false);
                    int added = 0;
                    foreach (DatabaseObjectMetadata item in additional.Objects)
                    {
                        DatabaseObjectMetadata copy = CloneObject(item, true);
                        // The object list of another database is identified by this name; a snapshot
                        // that lost it would make every "db.schema." list come back empty.
                        if (string.IsNullOrWhiteSpace(copy.Database)) copy.Database = name;
                        merged.Objects.Add(copy);
                        added++;
                    }
                    foreach (RoutineParameterMetadata item in additional.Parameters) merged.Parameters.Add(item);
                    foreach (ForeignKeyMetadata item in additional.ForeignKeys) merged.ForeignKeys.Add(item);
                    bool isSpeculative = speculated.Contains(name);
                    if (!string.IsNullOrWhiteSpace(additional.ErrorMessage) && !isSpeculative)
                        merged.ErrorMessage = additional.ErrorMessage;
                    CompletionTrace.Write("referenced database '" + name + "' objects=" + added
                        + (isSpeculative ? " speculative=True" : string.Empty)
                        + " error='" + (additional.ErrorMessage ?? string.Empty) + "'");
                }
                catch (Exception ex)
                {
                    // One unavailable database must not take the completion list of the current one
                    // down with it (that used to surface as "no suggestions at all").
                    CompletionTrace.Failure("referenced database '" + name + "' could not be loaded", ex);
                }
            }
            foreach (var pair in linked)
            {
                try
                {
                    MetadataSnapshot additional = await SqlMetadataCache.GetLinkedServerAsync(connection, pair.Key, pair.Value, token).ConfigureAwait(false);
                    foreach (DatabaseObjectMetadata item in additional.Objects) merged.Objects.Add(CloneObject(item, true));
                    foreach (RoutineParameterMetadata item in additional.Parameters) merged.Parameters.Add(item);
                    foreach (var databases in additional.LinkedServerDatabases) merged.LinkedServerDatabases[databases.Key] = databases.Value;
                    if (!string.IsNullOrWhiteSpace(additional.ErrorMessage)) merged.ErrorMessage = additional.ErrorMessage;
                    CompletionTrace.Write("linked server '" + pair.Key + "' objects=" + additional.Objects.Count
                        + " error='" + (additional.ErrorMessage ?? string.Empty) + "'");
                }
                catch (Exception ex)
                {
                    CompletionTrace.Failure("linked server '" + pair.Key + "' could not be loaded", ex);
                }
            }
            if (MergedSnapshotCache.Count >= 6) MergedSnapshotCache.Clear();
            MergedSnapshotCache[cacheKey] = merged;
            return merged;
        }

        /// <summary>Objects whose columns the current statement reads, used to refresh a snapshot that
        /// answered a completion request with nothing.</summary>
        private static IEnumerable<string> MissRefreshTargets(CompletionContext context)
        {
            if (context == null) yield break;
            if (!string.IsNullOrWhiteSpace(context.TargetObject)) yield return context.TargetObject;
            if (context.Aliases == null) yield break;
            foreach (string source in context.Aliases.Values)
                if (!string.IsNullOrWhiteSpace(source)) yield return source;
        }

        internal static DatabaseObjectMetadata CloneObject(DatabaseObjectMetadata source, bool external)
        {
            var result = new DatabaseObjectMetadata
            {
                Server = source.Server, Database = source.Database, Schema = source.Schema, Name = source.Name,
                Kind = source.Kind, IsSystem = source.IsSystem, IsTableValuedFunction = source.IsTableValuedFunction,
                IsExternal = external || source.IsExternal, SynonymBaseObjectName = source.SynonymBaseObjectName,
                Definition = source.Definition, Description = source.Description, EstimatedRowCount = source.EstimatedRowCount
            };
            foreach (ColumnMetadata column in source.Columns)
                result.Columns.Add(new ColumnMetadata
                {
                    Name = column.Name, DataType = column.DataType, IsNullable = column.IsNullable,
                    IsIdentity = column.IsIdentity, IsComputed = column.IsComputed, IsPrimaryKey = column.IsPrimaryKey,
                    IsForeignKey = column.IsForeignKey, IsUnique = column.IsUnique, Ordinal = column.Ordinal,
                    MaxLength = column.MaxLength, Precision = column.Precision, Scale = column.Scale,
                    DefaultDefinition = column.DefaultDefinition, Description = column.Description
                });
            foreach (IndexMetadata index in source.Indexes)
            {
                var copy = new IndexMetadata { Name = index.Name, TypeDescription = index.TypeDescription, IsUnique = index.IsUnique, IsPrimaryKey = index.IsPrimaryKey };
                copy.KeyColumns.AddRange(index.KeyColumns);
                copy.IncludedColumns.AddRange(index.IncludedColumns);
                result.Indexes.Add(copy);
            }
            foreach (CheckConstraintMetadata constraint in source.CheckConstraints)
                result.CheckConstraints.Add(new CheckConstraintMetadata { Name = constraint.Name, Definition = constraint.Definition });
            result.ProjectionSources.AddRange(source.ProjectionSources);
            return result;
        }

        private static MetadataSnapshot CloneSnapshot(MetadataSnapshot source)
        {
            var result = new MetadataSnapshot
            {
                LoadedUtc = source.LoadedUtc, Scope = source.Scope, ErrorMessage = source.ErrorMessage,
                IsPartial = source.IsPartial, CompatibilityLevel = source.CompatibilityLevel
            };
            result.Objects.AddRange(source.Objects);
            result.Parameters.AddRange(source.Parameters);
            result.ForeignKeys.AddRange(source.ForeignKeys);
            result.Schemas.AddRange(source.Schemas);
            result.Databases.AddRange(source.Databases);
            result.LinkedServers.AddRange(source.LinkedServers);
            foreach (var pair in source.LinkedServerDatabases) result.LinkedServerDatabases[pair.Key] = new List<string>(pair.Value);
            return result;
        }

        private void PostItems(CompletionContext context, List<CompletionItem> items, MetadataSnapshot metadata, CancellationToken token)
        {
            try
            {
                int objectCount = metadata?.Objects?.Count ?? 0;
                string aliases = context?.Aliases == null
                    ? string.Empty
                    : string.Join(",", context.Aliases.Select(a => a.Key + "=" + a.Value));
                MSSQLToolPackage._logger?.Debug(
                    $"COMPLETION-CONTEXT kind={context?.Kind} prefix='{context?.Prefix}' line={context?.CaretLine} column={context?.CaretColumn} " +
                    $"aliases=[{aliases}] objects={objectCount} items={items?.Count ?? 0}");
            }
            catch (Exception)
            {
            }

            uiContext.Post(_ =>
            {
                // This callback runs on the UI thread from the message loop: anything that escapes
                // here surfaces as an SSMS error dialog with a raw .NET message and is never logged.
                try
                {
                    if (token.IsCancellationRequested || !ReferenceEquals(currentContext, context) || !IsCurrentSnapshot()) return;
                    if (SqlEditorServices.TryGetSnapshot(textView, out Microsoft.VisualStudio.Text.ITextSnapshot shownSnapshot))
                        cachedSnapshot = shownSnapshot;

                    if (items.Count == 0 && !CompletionPresenter.HasParameterInfo(metadata, context))
                    {
                        presenter.Hide("no candidates");
                    }
                    else
                    {
                        // Dismiss SSMS's native completion only when opening our popup.
                        // Sending CANCEL for every in-place refresh can disturb the
                        // editor and causes a visible close/reopen cycle.
                        if (!presenter.IsVisible)
                            BeforePopupShown?.Invoke();
                        presenter.Show(textView, items, metadata, context);
                    }
                }
                catch (Exception ex)
                {
                    FeatureDiagnostics.Report("SQL Completion", "The completion list could not be shown", ex);
                    try { presenter.Hide("error"); } catch { }
                }
            }, null);
        }

        public void RefreshMetadata()
        {
            ScriptFactoryAccess.ConnectionInfo connection = ScriptFactoryAccess.GetCurrentConnectionInfo();
            SqlMetadataCache.Invalidate(connection);
            Request(true);
        }

        public bool HandleNavigation(uint commandId) => presenter.HandleNavigation(commandId);

        public bool TryCommit()
        {
            if (!presenter.IsVisible || presenter.SelectedItem == null) return false;
            return CommitCurrent();
        }

        public bool TryCommitOnCharacter(char value)
        {
            CompletionItem item = presenter.SelectedItem;
            if (!presenter.IsVisible || item == null) return false;
            bool shouldCommit = ShouldCommitOnCharacter(item, value);
            if (!shouldCommit) return false;
            return CommitCurrent();
        }

        internal static bool ShouldCommitOnCharacter(CompletionItem item, char value)
        {
            if (item == null) return false;
            return value == '.' && (item.Kind == CompletionItemKind.Server || item.Kind == CompletionItemKind.Database || item.Kind == CompletionItemKind.Schema)
                || value == '(' && item.Kind == CompletionItemKind.Function
                || value == ' ' && item.Kind == CompletionItemKind.Procedure
                || value == ',' && item.Kind == CompletionItemKind.Column;
        }

        public bool TryAdvanceSnippet(bool backwards)
        {
            if (snippetStops.Count == 0) return false;
            snippetStopIndex += backwards ? -1 : 1;
            if (snippetStopIndex < 0) snippetStopIndex = 0;
            if (snippetStopIndex >= snippetStops.Count) { snippetStops.Clear(); snippetStopIndex = -1; return false; }
            TextSpan span = snippetStops[snippetStopIndex];
            textView.SetSelection(span.iStartLine, span.iStartIndex, span.iEndLine, span.iEndIndex);
            return true;
        }

        public void Dismiss(string reason = null)
        {
            requestCancellation?.Cancel();
            presenter.Hide(reason);
        }

        private bool CommitCurrent()
        {
            CompletionItem item = presenter.SelectedItem;
            if (item == null || currentContext == null || !IsCurrentSnapshot()) { Dismiss("commit: stale context"); return false; }
            List<CompletionItem> selectedItems = presenter.SelectedItems;
            bool insertMultipleColumns = selectedItems.Count > 1 && selectedItems.All(i => i.Kind == CompletionItemKind.Column);
            if (textView.GetBuffer(out IVsTextLines lines) != VSConstants.S_OK) return false;
            textView.GetCaretPos(out int line, out int column);
            int start = Math.Min(column, currentContext.ReplacementStartColumn);
            string insertText = GetCommitInsertText(item, selectedItems);
            bool addFunctionParens = item.Kind == CompletionItemKind.Function && !insertMultipleColumns
                && !insertText.EndsWith("(", StringComparison.Ordinal);
            if (addFunctionParens) insertText += "()";
            SnippetVariableResult snippetResult = null;
            if (item.Kind == CompletionItemKind.Snippet)
            {
                snippetResult = SnippetVariableProcessor.ProcessVariables(insertText, SettingsManager.GetSnippetSettings().cursorMarker);
                insertText = snippetResult.ProcessedText;
            }
            IntPtr value = Marshal.StringToHGlobalUni(insertText);
            try
            {
                using (var edit = EditorEditTransaction.Begin(lines, "SQL completion"))
                {
                    lines.ReplaceLines(line, start, line, column, value, insertText.Length, new TextSpan[1]);
                    edit.Complete();
                }
                snippetStops.Clear();
                snippetStopIndex = -1;
                if (snippetResult != null && snippetResult.TabStops.Count > 0)
                {
                    foreach (SnippetTabStop stop in snippetResult.TabStops)
                        snippetStops.Add(ToTextSpan(line, start, insertText, stop.Offset, stop.Length));
                    TryAdvanceSnippet(false);
                }
                else
                {
                    int caretOffset = snippetResult != null && snippetResult.CursorOffset >= 0 ? snippetResult.CursorOffset : insertText.Length;
                    if (addFunctionParens) caretOffset = insertText.Length - 1;
                    TextSpan caret = ToTextSpan(line, start, insertText, caretOffset, 0);
                    textView.SetCaretPos(caret.iStartLine, caret.iStartIndex);
                }
            }
            finally { Marshal.FreeHGlobal(value); }
            try { CompletionUsageStore.Record(currentContext?.MetadataScope, item); }
            catch (Exception) { }
            if (CompletionTrace.DetailEnabled)
                CompletionTrace.Detail("commit kind=" + item.Kind + " display=" + CompletionTrace.Quote(item.DisplayText)
                    + " inserted=" + CompletionTrace.Quote(CompletionTrace.Snippet(insertText, 200))
                    + " line=" + line + " range=" + start + ".." + column
                    + " selected=" + selectedItems.Count + " multiColumn=" + insertMultipleColumns);
            bool requestAfterSnippet = ShouldRequestCompletionAfterCommit(item);
            // The join continuation is the second half of one gesture (Tab, then pick the ON key), so
            // it must open even when the fuzzy-popup setting is off.
            bool explicitSnippetContinuation = currentContext.IsExplicitRequest || item.Kind == CompletionItemKind.Join;
            Dismiss("commit: " + item.DisplayText);
            if (requestAfterSnippet) Request(explicitSnippetContinuation);
            return true;
        }

        internal static string GetCommitInsertText(CompletionItem current, IReadOnlyCollection<CompletionItem> selected)
        {
            if (current == null) return string.Empty;
            if (selected != null && selected.Count > 1 && selected.All(i => i != null && i.Kind == CompletionItemKind.Column))
                return string.Join(", ", selected.Select(i => i.InsertText));
            return current.InsertText ?? string.Empty;
        }

        internal static bool ShouldRequestCompletionAfterCommit(CompletionItem item)
        {
            if (item == null) return false;
            if (item.Kind == CompletionItemKind.Snippet) return true;

            // A plain JOIN table leaves the caret right after the name; reopening the popup there is
            // what turns "insert the table" into a second step offering the ON keys, instead of
            // inserting a condition the user never asked for.
            return item.Kind == CompletionItemKind.Join
                && (item.InsertText ?? string.Empty).IndexOf(" ON ", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private bool IsCurrentSnapshot()
        {
            if (currentContext == null || textView == null) return false;
            // Cheap staleness checks only: caret plus the buffer version.  Reading the whole document
            // here used to run once per posted callback and dominated large scripts.
            if (textView.GetCaretPos(out int line, out int column) != VSConstants.S_OK) return false;
            if (line != currentLine || column != currentColumn) return false;
            if (!SqlEditorServices.TryGetSnapshot(textView, out Microsoft.VisualStudio.Text.ITextSnapshot snapshot)) return false;
            if (snapshot.Version.VersionNumber != currentVersionNumber) return false;

            ScriptFactoryAccess.ConnectionInfo connection = ScriptFactoryAccess.GetCurrentConnectionInfo();
            string scope = connection == null ? string.Empty : connection.ServerName + "|" + connection.Database;
            return string.Equals(currentContext.MetadataScope ?? string.Empty, scope ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsCompletionSnapshotCurrent(string originalText, string currentText, int originalLine,
            int originalColumn, string originalScope, int currentLine, int currentColumn, string currentScope)
            => originalLine == currentLine && originalColumn == currentColumn
                && string.Equals(originalText, currentText, StringComparison.Ordinal)
                && string.Equals(originalScope ?? string.Empty, currentScope ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        private bool IsCaretCurrent()
        {
            if (currentContext == null || textView == null) return false;
            return textView.GetCaretPos(out int line, out int column) == VSConstants.S_OK && line == currentLine && column == currentColumn;
        }

        private static TextSpan ToTextSpan(int startLine, int startColumn, string text, int offset, int length)
        {
            int line = startLine;
            int column = startColumn;
            int endOffset = Math.Min(text.Length, offset + length);
            int endLine = line;
            int endColumn = column;
            for (int i = 0; i < endOffset; i++)
            {
                bool newline = text[i] == '\n';
                if (newline) { if (i < offset) { line++; column = 0; } endLine++; endColumn = 0; }
                else { if (i < offset) column++; endColumn++; }
            }
            return new TextSpan { iStartLine = line, iStartIndex = column, iEndLine = endLine, iEndIndex = endColumn };
        }

        private bool TryReadDocument(out string text, out int line, out int column, out int docLastLine, out int docLastColumn)
        {
            text = null; line = column = 0; docLastLine = docLastColumn = 0;
            if (SqlEditorServices.TryGetSnapshot(textView, out Microsoft.VisualStudio.Text.ITextSnapshot snapshot))
            {
                if (textView.GetCaretPos(out line, out column) != VSConstants.S_OK) return false;
                text = snapshot.GetText();
                docLastLine = Math.Max(0, snapshot.LineCount - 1);
                docLastColumn = snapshot.GetLineFromLineNumber(docLastLine).Length;
                return true;
            }

            if (textView.GetBuffer(out IVsTextLines lines) != VSConstants.S_OK) return false;
            textView.GetCaretPos(out line, out column);
            if (lines.GetLastLineIndex(out docLastLine, out docLastColumn) != VSConstants.S_OK) return false;
            return lines.GetLineText(0, 0, docLastLine, docLastColumn, out text) == VSConstants.S_OK;
        }

        private static int GetOffset(string text, int line, int column)
        {
            int offset = 0;
            for (int i = 0; i < line && offset < text.Length; i++)
            {
                int next = text.IndexOf('\n', offset);
                if (next < 0) return text.Length;
                offset = next + 1;
            }
            return Math.Min(text.Length, offset + column);
        }

        public void Dispose()
        {
            requestCancellation?.Cancel();
            requestCancellation?.Dispose();
            sessionMonitor.Stop();
            sessionMonitor.Dispose();
            highlightOverlay?.Dispose();
            analysisGate.Dispose();
            if (trackedBuffer != null) { trackedBuffer.Changed -= OnBufferChanged; trackedBuffer = null; }
            if (layoutView != null && layoutChanged != null) layoutView.LayoutChanged -= layoutChanged;
            diagnosticOverlay?.Dispose();
            editorHost.Dispose();
            presenter.Dispose();
            if (onSnapshotPublished != null)
            {
                SqlMetadataCache.SnapshotPublished -= onSnapshotPublished;
                onSnapshotPublished = null;
            }
        }
    }
}
