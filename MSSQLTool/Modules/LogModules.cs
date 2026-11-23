using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace MSSQLTool
{
    /// <summary>The part of the extension a log line belongs to.</summary>
    internal enum LogModule
    {
        /// <summary>Everything that is not one of the modules below (settings, updates, commands).</summary>
        General = 0,
        /// <summary>Completion popup, IntelliSense and the analyzer behind it.</summary>
        Completion = 1,
        /// <summary>Reserved: the query tab restore feature was removed; the value keeps stored masks stable.</summary>
        QueryTabs = 2,
        /// <summary>Captured query texts (the query history window).</summary>
        QueryTextHistory = 3,
        /// <summary>Execution history (the "Execution History" window and its database).</summary>
        ExecutionHistory = 4,
        /// <summary>Completion metadata cache: loading, merging, schema change refresh.</summary>
        MetadataCache = 5,
        /// <summary>Result grid and its analytics panel.</summary>
        ResultGrid = 6,
        /// <summary>Query templates and snippet insertion.</summary>
        QueryTemplates = 7
    }

    /// <summary>
    /// Decides which <see cref="LogModule"/> a log line comes from.  The class that writes the line
    /// is the module, so no call site has to pass a category; helpers that only forward another
    /// module's line (<see cref="RuntimeLog"/>, <c>CompletionTrace</c>) are skipped and the class
    /// below them is used instead.
    /// </summary>
    internal static class LogModuleCatalog
    {
        private static readonly HashSet<string> Forwarders = new HashSet<string>(StringComparer.Ordinal)
        {
            "MSSQLTool.RuntimeLog",
            "MSSQLTool.Completion.CompletionTrace"
        };

        internal static bool IsForwarder(Type type)
            => type != null && type.FullName != null && Forwarders.Contains(type.FullName);

        /// <summary>Maps a class to its module.  Unknown classes belong to <see cref="LogModule.General"/>.</summary>
        internal static LogModule Classify(Type type)
        {
            if (type == null) return LogModule.General;

            string ns = type.Namespace ?? string.Empty;
            string name = type.Name ?? string.Empty;

            if (ns.StartsWith("MSSQLTool.QueryTabs", StringComparison.Ordinal)) return LogModule.QueryTabs;
            if (ns.StartsWith("MSSQLTool.QueryTemplates", StringComparison.Ordinal)) return LogModule.QueryTemplates;
            if (ns.StartsWith("MSSQLTool.Results", StringComparison.Ordinal)) return LogModule.ResultGrid;
            if (ns.StartsWith("MSSQLTool.QueryHistory", StringComparison.Ordinal)) return LogModule.ExecutionHistory;

            // The cache types are what "loading the cache" means; the completion pipeline above them
            // has its own switch.
            if (name.StartsWith("SqlMetadataCache", StringComparison.Ordinal)
                || name.StartsWith("MetadataPersistence", StringComparison.Ordinal)
                || name.StartsWith("MetadataSignature", StringComparison.Ordinal)
                || name.StartsWith("SnapshotObjectIndex", StringComparison.Ordinal)
                || name.StartsWith("SchemaChangeAnalyzer", StringComparison.Ordinal)
                || name.StartsWith("MetadataSnapshot", StringComparison.Ordinal))
                return LogModule.MetadataCache;

            if (ns.StartsWith("MSSQLTool.Completion", StringComparison.Ordinal)) return LogModule.Completion;

            if (name.StartsWith("QueryText", StringComparison.Ordinal)
                || name.StartsWith("RecoveredTempFile", StringComparison.Ordinal))
                return LogModule.QueryTextHistory;

            if (name.StartsWith("Completion", StringComparison.Ordinal)
                || name.StartsWith("KeypressCommandFilter", StringComparison.Ordinal)
                || name.StartsWith("AsteriskExpansion", StringComparison.Ordinal))
                return LogModule.Completion;

            if (name.StartsWith("QueryHistory", StringComparison.Ordinal)
                || name.StartsWith("HistoryDatabaseMigration", StringComparison.Ordinal))
                return LogModule.ExecutionHistory;

            if (name.StartsWith("GridAccess", StringComparison.Ordinal)
                || name.StartsWith("ResultSet", StringComparison.Ordinal)
                || name.StartsWith("GridAnalytics", StringComparison.Ordinal))
                return LogModule.ResultGrid;

            if (name.StartsWith("QueryTemplate", StringComparison.Ordinal)) return LogModule.QueryTemplates;

            return LogModule.General;
        }

        /// <summary>
        /// Walks up from the logger until the class that actually produced the line is found.
        /// Called only when the level is enabled, so a filtered-out line costs nothing.
        /// </summary>
        internal static LogModule ClassifyCaller()
        {
            try
            {
                // Start above ClassifyCaller itself; the forwarding helpers are skipped below.
                var trace = new StackTrace(1, false);
                for (int index = 0; index < trace.FrameCount && index < 16; index++)
                {
                    Type type = trace.GetFrame(index)?.GetMethod()?.DeclaringType;
                    if (type == null) continue;
                    if (type == typeof(ModuleLogger) || type == typeof(LogModuleCatalog)) continue;
                    if (IsForwarder(type)) continue;
                    return Classify(type);
                }
            }
            catch (Exception)
            {
            }

            return LogModule.General;
        }
    }

    /// <summary>
    /// The runtime mask behind the Diagnostics checkboxes.  Default is "everything on", so an
    /// upgrade changes nothing; unchecking a module stops its lines from reaching any target (file
    /// and the live test-mode log) without touching the other modules.
    /// </summary>
    internal static class ModuleLogSettings
    {
        public const int ModuleCount = 8;
        public const int AllModulesMask = (1 << ModuleCount) - 1;

        private static int enabledMask = AllModulesMask;

        internal static int CurrentMask => Volatile.Read(ref enabledMask);

        public static bool IsEnabled(LogModule module) => (CurrentMask & MaskOf(module)) != 0;

        internal static int MaskOf(LogModule module)
        {
            int index = (int)module;
            return index >= 0 && index < ModuleCount ? 1 << index : 1;
        }

        internal static void ApplyMask(int mask) => Volatile.Write(ref enabledMask, mask & AllModulesMask);

        internal static void Apply(SettingsManager.LoggingSettings settings)
        {
            settings = settings ?? new SettingsManager.LoggingSettings();

            int mask = 0;
            if (settings.logGeneral) mask |= MaskOf(LogModule.General);
            if (settings.logCompletion) mask |= MaskOf(LogModule.Completion);
            if (settings.logQueryTabs) mask |= MaskOf(LogModule.QueryTabs);
            if (settings.logQueryTextHistory) mask |= MaskOf(LogModule.QueryTextHistory);
            if (settings.logExecutionHistory) mask |= MaskOf(LogModule.ExecutionHistory);
            if (settings.logMetadataCache) mask |= MaskOf(LogModule.MetadataCache);
            if (settings.logResultGrid) mask |= MaskOf(LogModule.ResultGrid);
            if (settings.logQueryTemplates) mask |= MaskOf(LogModule.QueryTemplates);

            Volatile.Write(ref enabledMask, mask);
        }
    }

    /// <summary>
    /// Drop-in replacement for <see cref="Logger"/> that drops a line when its module is switched
    /// off in Diagnostics.  It exposes the four levels the extension uses, with the same overloads,
    /// so the existing <c>_logger</c> call sites need no change.
    /// </summary>
    internal sealed class ModuleLogger
    {
        private static readonly object[] NoArguments = new object[0];

        private readonly Logger inner;
        private readonly LogModule? fixedModule;

        public ModuleLogger(Logger inner)
        {
            this.inner = inner;
        }

        private ModuleLogger(Logger inner, LogModule module)
        {
            this.inner = inner;
            fixedModule = module;
        }

        public static implicit operator ModuleLogger(Logger logger)
            => logger == null ? null : new ModuleLogger(logger);

        public static implicit operator Logger(ModuleLogger logger) => logger?.inner;

        /// <summary>A logger whose lines always belong to <paramref name="module"/>.</summary>
        public ModuleLogger For(LogModule module) => new ModuleLogger(inner, module);

        public bool IsEnabled(LogLevel level)
        {
            try { return inner != null && inner.IsEnabled(level); }
            catch (Exception) { return false; }
        }

        public void Debug(string message) => Write(LogLevel.Debug, null, message, NoArguments);

        public void Debug(string message, params object[] args) => Write(LogLevel.Debug, null, message, args);

        public void Debug(Exception exception, string message) => Write(LogLevel.Debug, exception, message, NoArguments);

        public void Debug(Exception exception, string message, params object[] args) => Write(LogLevel.Debug, exception, message, args);

        public void Info(string message) => Write(LogLevel.Info, null, message, NoArguments);

        public void Info(string message, params object[] args) => Write(LogLevel.Info, null, message, args);

        public void Info(Exception exception, string message) => Write(LogLevel.Info, exception, message, NoArguments);

        public void Info(Exception exception, string message, params object[] args) => Write(LogLevel.Info, exception, message, args);

        public void Warn(string message) => Write(LogLevel.Warn, null, message, NoArguments);

        public void Warn(string message, params object[] args) => Write(LogLevel.Warn, null, message, args);

        public void Warn(Exception exception, string message) => Write(LogLevel.Warn, exception, message, NoArguments);

        public void Warn(Exception exception, string message, params object[] args) => Write(LogLevel.Warn, exception, message, args);

        public void Error(string message) => Write(LogLevel.Error, null, message, NoArguments);

        public void Error(string message, params object[] args) => Write(LogLevel.Error, null, message, args);

        public void Error(Exception exception, string message) => Write(LogLevel.Error, exception, message, NoArguments);

        public void Error(Exception exception, string message, params object[] args) => Write(LogLevel.Error, exception, message, args);

        private void Write(LogLevel level, Exception exception, string message, object[] args)
        {
            Logger target = inner;
            if (target == null) return;

            bool levelEnabled;
            try { levelEnabled = target.IsEnabled(level); }
            catch (Exception) { return; }
            if (!levelEnabled) return;

            LogModule module = fixedModule ?? LogModuleCatalog.ClassifyCaller();
            if (!ModuleLogSettings.IsEnabled(module)) return;

            object[] arguments = args ?? NoArguments;
            if (exception == null && arguments.Length == 0) target.Log(level, message);
            else target.Log(level, exception, message, arguments);
        }
    }
}
