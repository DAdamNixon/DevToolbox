using DevToolbox.Mcp.Core;
using ModelContextProtocol;

namespace DevToolbox.Mcp.Tools;

/// <summary>
/// The single site where an exception becomes something a caller can see. Every tool body is
/// wrapped in <see cref="GuardAsync{T}"/>; nothing else in this project catches.
/// <para>
/// The shape follows the SDK's own documented contract, which draws exactly the line needed:
/// an <see cref="McpException"/>'s message IS propagated to the caller, so it is the sanctioned
/// channel for text we authored deliberately — while any other exception type's message is NOT,
/// because the SDK replaces it with a generic failure. So anything escaping this wrapper fails
/// <b>closed</b>: the caller learns the call failed and nothing else.
/// </para>
/// <para>
/// The split below is the whole design. Our own refusals — an unknown handle, an unknown template,
/// a bad argument, a clamped page size, a query that outran its budget — were each written to name
/// only the caller's own argument and what to do instead. They are already safe and are passed
/// through verbatim, because prefixing them with a .NET type name makes them harder to act on and
/// no safer. Everything else, a filesystem or SQLite failure being the realistic case, goes through
/// <see cref="SafeError.Describe"/>, which strips paths and reads nothing but the message and the
/// type name.
/// </para>
/// </summary>
internal static class ToolErrors
{
    internal static async Task<T> GuardAsync<T>(Func<Task<T>> body)
    {
        try
        {
            return await body();
        }
        catch (UnknownHandleException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (UnknownTemplateException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (QueryTimeoutException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // RowCap.Clamp on a page size at or below zero. Its message states the requested value
            // and the ceiling, both of which the caller supplied or is entitled to.
            throw new McpException(ex.Message);
        }
        catch (ArgumentException ex)
        {
            // Bad dates, an empty log file name, a missing or unknown location, both query shapes at
            // once. Authored messages — see the Tasks note on giving these their own type, because
            // this arm still assumes every ArgumentException in the call tree is one of ours.
            throw new McpException(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // Two sources, and both are safe to show. Ours: SavedQueryService's duplicate-name
            // refusal. The library's: a wrapped query failure,
            // whose message for a raw-SQL call is the SQLite parse error — which is the single most
            // useful thing a caller can be told, since the SQL was theirs. Scrubbed for paths
            // regardless, because the other InvalidOperationException in that layer wraps file I/O.
            throw new McpException(SafeError.Scrub(ex.Message));
        }
        catch (NotSupportedException ex)
        {
            // A write attempted through a read-only path. Reaching here is a bug in this server
            // rather than caller error, so it is reported plainly rather than dressed up.
            throw new McpException(SafeError.Scrub(ex.Message));
        }
        catch (McpException)
        {
            // Already mapped — never double-wrap, never re-describe.
            throw;
        }
        catch (Exception ex)
        {
            throw new McpException(SafeError.Describe(ex));
        }
    }
}
