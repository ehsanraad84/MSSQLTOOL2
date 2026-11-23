using System;
using System.ComponentModel.Design;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.Windows;
using Task = System.Threading.Tasks.Task;

namespace MSSQLTool
{
    /// <summary>
    /// Drops the SQL completion metadata cache for the active connection and
    /// reloads it eagerly, so schema changes made outside of this plugin
    /// (other developers, other tools) are picked up without waiting for the
    /// next completion request.
    /// </summary>
    internal sealed class RefreshCompletionMetadataCommand
    {
        public const int CommandId = 4146;

        public static readonly Guid CommandSet = new Guid("45457e02-6dec-4a4d-ab22-c9ee126d23c5");

        private readonly MSSQLToolPackage package;

        private RefreshCompletionMetadataCommand(MSSQLToolPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static RefreshCompletionMetadataCommand Instance
        {
            get;
            private set;
        }

        public static async Task InitializeAsync(MSSQLToolPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync((typeof(IMenuCommandService))) as OleMenuCommandService;
            Instance = new RefreshCompletionMetadataCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                ScriptFactoryAccess.ConnectionInfo connection = ScriptFactoryAccess.GetCurrentConnectionInfo();
                if (connection == null || string.IsNullOrWhiteSpace(connection.FullConnectionString))
                {
                    // Without a connection nothing can be invalidated; reporting
                    // success here would leave the user with stale metadata.
                    LocalizedMessageBox.Show(
                        LocalizationManager.T("Connect to a server first — open a connected query window, then run refresh again."),
                        "Refresh Completion Metadata", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                Completion.MetadataSnapshot previous = null;
                Completion.SqlMetadataCache.TryGetCached(connection, out previous);

                // RefreshAsync reloads and returns the newest snapshot; when the reload fails the
                // previous snapshot stays in use instead of leaving the popup empty.
                Task reload = Task.Run(async () =>
                {
                    try
                    {
                        Completion.MetadataSnapshot snapshot =
                            await Completion.SqlMetadataCache.RefreshAsync(connection, CancellationToken.None).ConfigureAwait(false);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                        ReportResult(snapshot, previous);
                    }
                    catch (Exception ex)
                    {
                        MSSQLToolPackage._logger?.Error(ex, "Manual SQL completion metadata refresh failed");
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        ShowStatus(LocalizationManager.T("SQL completion metadata refresh failed.") + " " + ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "Manual SQL completion metadata refresh failed");
                LocalizedMessageBox.Show(
                    LocalizationManager.T("Could not refresh SQL completion metadata:") + " " + ex.Message,
                    "Refresh Completion Metadata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ReportResult(Completion.MetadataSnapshot snapshot, Completion.MetadataSnapshot previous)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (snapshot == null)
            {
                ShowStatus(LocalizationManager.T("SQL completion metadata refreshed."));
                return;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.ErrorMessage))
            {
                ShowStatus(LocalizationManager.Format("SQL completion metadata refresh failed: {0}", snapshot.ErrorMessage));
                return;
            }

            // A failure that kept the old snapshot must not look like a successful refresh.
            if (previous != null && ReferenceEquals(previous, snapshot) && snapshot.IsStale)
            {
                ShowStatus(LocalizationManager.T("SQL completion metadata refresh did not complete; the previous snapshot is still in use."));
                return;
            }

            ShowStatus(LocalizationManager.Format("SQL completion metadata refreshed ({0} objects, {1} columns).",
                snapshot.Objects.Count, snapshot.Objects.Sum(o => o.Columns.Count)));
        }

        private static void ShowStatus(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var statusBar = Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar;
            statusBar?.SetText(text);
        }
    }
}
