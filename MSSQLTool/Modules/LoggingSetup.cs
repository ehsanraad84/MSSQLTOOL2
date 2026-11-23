using NLog;
using NLog.Config;
using NLog.Targets;
using System;
using System.IO;

namespace MSSQLTool
{
    /// <summary>
    /// Builds the file logger.  Extracted from the package so the log folder can be re-applied at
    /// runtime after the data folder changes, instead of only at SSMS start-up.
    /// </summary>
    internal static class LoggingSetup
    {
        private static readonly object Gate = new object();
        private static bool redactionRenderersRegistered;

        /// <summary>Current log folder, or null before the first call.</summary>
        public static string CurrentLogFolder { get; private set; }

        public static void Apply(string logDirectory)
        {
            if (string.IsNullOrWhiteSpace(logDirectory)) return;

            lock (Gate)
            {
                Directory.CreateDirectory(logDirectory);
                CurrentLogFolder = logDirectory;

                try
                {
                    LogManager.Setup()
                        .SetupExtensions(extensions =>
                        {
                            if (redactionRenderersRegistered) return;
                            extensions.RegisterLayoutRenderer<RedactedMessageRenderer>("redactedMessage");
                            extensions.RegisterLayoutRenderer<RedactedExceptionRenderer>("redactedException");
                            redactionRenderersRegistered = true;
                        })
                        .LoadConfiguration(builder =>
                        {
                            var fileTarget = new FileTarget("fileLog")
                            {
                                FileName = Path.Combine(logDirectory, "log_${shortdate}.log"),
                                // Never write ${message}/${exception} directly: the redacting
                                // renderers keep connection details out of the log files.
                                Layout = "${longdate}|${level}|${logger}|${redactedMessage}${redactedException}",
                                ArchiveFileName = Path.Combine(logDirectory, "archive/log.{###}.txt"),
                                ArchiveAboveSize = 1024 * 1024 * 5,
                                MaxArchiveFiles = 5
                            };

                            builder.ForLogger()
                                   .FilterMinLevel(LogLevel.Info)
                                   .WriteTo(fileTarget);
                        });
                }
                catch (Exception ex)
                {
                    // Logging must never take the extension down with it.
                    System.Diagnostics.Trace.WriteLine("MSSQL Tool: the file logger could not be configured: " + ex.Message);
                }
            }
        }
    }
}
