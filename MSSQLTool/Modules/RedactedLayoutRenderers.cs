using System.Text;
using NLog;
using NLog.LayoutRenderers;

namespace MSSQLTool
{
    /// <summary>
    /// Writes the formatted message after <see cref="LogRedaction"/> has removed server, database,
    /// login and secret values.  Registered by <see cref="LoggingSetup"/>; the file log uses it
    /// instead of <c>${message}</c> so a report copied from the log folder is as safe as the
    /// diagnostics page.
    /// </summary>
    [LayoutRenderer("redactedMessage")]
    internal sealed class RedactedMessageRenderer : LayoutRenderer
    {
        protected override void Append(StringBuilder builder, LogEventInfo logEvent)
            => builder.Append(LogRedaction.Redact(logEvent.FormattedMessage));
    }

    /// <summary>Same masking for the exception block (type, message, inner exceptions and stack).</summary>
    [LayoutRenderer("redactedException")]
    internal sealed class RedactedExceptionRenderer : LayoutRenderer
    {
        protected override void Append(StringBuilder builder, LogEventInfo logEvent)
        {
            if (logEvent.Exception == null) return;
            builder.Append(' ');
            builder.Append(LogRedaction.Redact(logEvent.Exception.ToString()));
        }
    }
}
