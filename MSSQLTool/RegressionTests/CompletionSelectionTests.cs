using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MSSQLTool;
using MSSQLTool.Completion;

namespace MSSQLTool.RegressionTests
{
    /// <summary>
    /// Behaviour of the completion list's highlight: it always starts on the first row, and the
    /// order of the list never depends on what was inserted before (the usage-based ranking and the
    /// "remembered highlight" were removed on request).
    /// </summary>
    internal static partial class Program
    {
        internal static void RunCompletionSelectionTests()
        {
            Run("Completion: highlight starts on the first row", TestCompletionFirstRow);
            Run("Completion: usage ranking is bounded", TestCompletionLearningIsBounded);
            Run("Completion: list order is deterministic", TestCompletionOrderIsStable);
        Run("Completion: fully typed name ranks first", TestCompletionPrefersTheFullyTypedName);
            Run("Completion: typing keeps the best match selected", TestCompletionTypingKeepsBestMatch);
            Run("Completion: a click inside the popup keeps it open", TestCompletionStaysOpenWhileClicked);
        }

        private static void TestCompletionStaysOpenWhileClicked()
        {
            // Another application took the foreground: the popup always closes, it must never
            // float above a foreign window.
            True(CompletionPresenter.ShouldDismissFromPoll(false, false, true));
            True(CompletionPresenter.ShouldDismissFromPoll(false, true, true));

            // A click inside the popup (list row, details pane, resize border) neither moves the
            // caret nor leaves SSMS.  The poll used to dismiss it there, which closed the window
            // under the user's pointer; it has to stay open now.
            Equal(false, CompletionPresenter.ShouldDismissFromPoll(true, true, true));
            Equal(false, CompletionPresenter.ShouldDismissFromPoll(true, true, false));

            // Idle popup with an unchanged caret: keep it.
            Equal(false, CompletionPresenter.ShouldDismissFromPoll(true, false, true));

            // The caret really moved (arrow keys, a click in the editor, another document): close.
            True(CompletionPresenter.ShouldDismissFromPoll(true, false, false));
        }

        private static void TestCompletionTypingKeepsBestMatch()
        {
            var items = new List<CompletionItem>
            {
                new CompletionItem { DisplayText = "dbo.ZDSKDH", InsertText = "dbo.ZDSKDH", Kind = CompletionItemKind.Table },
                new CompletionItem { DisplayText = "dbo.ZDSKDB", InsertText = "dbo.ZDSKDB", Kind = CompletionItemKind.Table }
            };

            // Typing only (no arrow keys, no mouse): the refresh returns to the first row,
            // which is the best match for the text that was just typed.
            Equal(0, CompletionPresenter.ChooseSelectionIndex(true, false, items, "Table|dbo.ZDSKDB", 1));
            Equal(0, CompletionPresenter.ChooseSelectionIndex(false, false, items, null, 0));

            // A row the user actually moved to survives the refresh while it still matches.
            Equal(1, CompletionPresenter.ChooseSelectionIndex(true, true, items, "Table|dbo.ZDSKDB", 0));
            Equal(0, CompletionPresenter.ChooseSelectionIndex(true, true, items, "Table|dbo.ZDSKDH", 1));
            Equal(0, CompletionPresenter.ChooseSelectionIndex(true, true, items, "Table|dbo.GONE", 9));

            // The manual choice only lives until the typed prefix changes.  It used to survive the
            // whole session, so one arrow press kept the highlight on a stale row and made every
            // later inline preview get force-cleared as "navigated" (typing D then showed nothing).
            True(CompletionPresenter.KeepsManualSelection(true, false));
            Equal(false, CompletionPresenter.KeepsManualSelection(true, true));
            Equal(false, CompletionPresenter.KeepsManualSelection(false, false));
            Equal(false, CompletionPresenter.KeepsManualSelection(false, true));
        }

        private static void TestCompletionFirstRow()
        {
            // Empty list: nothing to select.
            Equal(-1, CompletionPresenter.InitialSelectionIndex(0));
            // Any non-empty list selects its first row, whatever the popup did before.
            Equal(0, CompletionPresenter.InitialSelectionIndex(1));
            Equal(0, CompletionPresenter.InitialSelectionIndex(25));
        }

        private static void TestCompletionLearningIsBounded()
        {
            // Usage history is back, but strictly bounded and switchable: it reorders near-ties
            // and never grows into an unbounded learner.
            Type store = typeof(CompletionPresenter).Assembly.GetType("MSSQLTool.Completion.CompletionUsageStore", false);
            True(store != null);
            True(typeof(SettingsManager.SqlCompletionSettings).GetField("preferRecentlyUsed") != null);
            True(typeof(SettingsManager.SqlCompletionSettings).GetField("learnFromUsage") == null);

            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Equal(12, CompletionUsageStore.ComputeBoost(int.MaxValue, now, now));
            Equal(3, CompletionUsageStore.ComputeBoost(1, now.AddYears(-1), now));
            Equal(4, CompletionUsageStore.ComputeBoost(0, now, now));
        }

        private static void TestCompletionOrderIsStable()
        {
            // The same statement always produces the same candidate order, so nothing is re-ranked
            // behind the user's back while typing.
            CompletionContext context = SqlCompletionAnalyzer.Analyze("SELECT Na", 9);
            List<string> first = SqlCompletionAnalyzer.BuildItems(context, Metadata()).Select(i => i.DisplayText).ToList();
            List<string> second = SqlCompletionAnalyzer.BuildItems(SqlCompletionAnalyzer.Analyze("SELECT Na", 9), Metadata())
                .Select(i => i.DisplayText).ToList();

            True(first.Count > 0);
            Equal(string.Join("|", first), string.Join("|", second));
        }

        private static void TestCompletionPrefersTheFullyTypedName()
        {
            // A longer name that only starts with the typed text must not outrank the exact name,
            // not even when the longer one carries the full history boost (bounded at 12).
            var items = new List<CompletionItem>
            {
                new CompletionItem { DisplayText = "WDDDH_BAK", InsertText = "WDDDH_BAK", Kind = CompletionItemKind.Table, Score = 100 },
                new CompletionItem { DisplayText = "WDDDH", InsertText = "WDDDH", Kind = CompletionItemKind.Table, Score = 100 }
            };

            List<CompletionItem> filtered = SqlCompletionAnalyzer.FilterCandidates(items, "WDDDH");
            Equal("WDDDH", filtered[0].DisplayText);
        }
    }
}
