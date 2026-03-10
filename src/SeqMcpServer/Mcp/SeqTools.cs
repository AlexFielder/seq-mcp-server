using ModelContextProtocol.Server;
using Seq.Api.Model.Events;
using Seq.Api.Model.Signals;
using SeqMcpServer.Services;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace SeqMcpServer.Mcp;

/// <summary>
/// MCP tools for interacting with Seq structured logging server.
/// </summary>
[McpServerToolType]
public static class SeqTools
{
    [McpServerTool, Description("Search Seq events with filters, returning up to the specified count")]
    public static async Task<object> SeqSearch(
        SeqConnectionFactory fac,
        [Required] string filter,
        [Range(1, 1000)] int count = 100,
        string? workspace = null,
        CancellationToken ct = default)
    {
        try
        {
            var conn = fac.Create(workspace);
            var events = new List<EventEntity>();
            await foreach (var evt in conn.Events.EnumerateAsync(
                filter: filter,
                count: count,
                render: true,
                cancellationToken: ct).WithCancellation(ct))
            {
                events.Add(evt);
            }
            return events;
        }
        catch (OperationCanceledException)
        {
            return new List<EventEntity>();
        }
        catch (Exception ex)
        {
            return new { error = ex.GetType().Name, message = ex.Message, innerMessage = ex.InnerException?.Message };
        }
    }

    [McpServerTool, Description("Wait for and capture live events from Seq (times out after 5 seconds, returns captured events as a snapshot)")]
    public static async Task<object> SeqWaitForEvents(
        SeqConnectionFactory fac,
        string? filter = null,
        [Range(1, 100)] int count = 10,
        string? workspace = null,
        CancellationToken ct = default)
    {
        try
        {
            var conn = fac.Create(workspace);
            var events = new List<EventEntity>();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            await foreach (var evt in conn.Events.StreamAsync(
                unsavedSignal: null,
                signal: null,
                filter: filter ?? string.Empty,
                cancellationToken: ct).WithCancellation(combinedCts.Token))
            {
                events.Add(evt);
                if (events.Count >= count) break;
            }

            return events;
        }
        catch (OperationCanceledException)
        {
            return new List<EventEntity>();
        }
        catch (Exception ex)
        {
            return new { error = ex.GetType().Name, message = ex.Message, innerMessage = ex.InnerException?.Message };
        }
    }

    [McpServerTool, Description("List available signals in Seq (read-only access to shared signals)")]
    public static async Task<object> SignalList(
        SeqConnectionFactory fac,
        string? workspace = null,
        CancellationToken ct = default)
    {
        try
        {
            var conn = fac.Create(workspace);
            return await conn.Signals.ListAsync(shared: true, cancellationToken: ct);
        }
        catch (OperationCanceledException)
        {
            return new List<SignalEntity>();
        }
        catch (Exception ex)
        {
            return new { error = ex.GetType().Name, message = ex.Message, innerMessage = ex.InnerException?.Message };
        }
    }
}
