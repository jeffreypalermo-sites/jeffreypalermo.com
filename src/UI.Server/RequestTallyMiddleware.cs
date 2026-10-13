using JeffreyPalermo.Core.Traffic;
using Microsoft.Extensions.Options;

namespace JeffreyPalermo.UI.Server;

/// <summary>
/// Counts every request this instance answers, once, when it has been answered (ADR-0025): what kind it was
/// (<see cref="RequestKinds"/>), the status, and how long the app took, from here until the pipeline was done with
/// it. It stands around the exception handler, so a request that failed counts as the 500 it was answered with.
/// Nothing of the request is kept: <see cref="RequestTally"/> holds numbers only.
/// </summary>
public sealed class RequestTallyMiddleware(RequestDelegate next, RequestTally tally, TimeProvider time, IOptions<SiteOptions> options)
{
    private readonly string _frontDoorId = options.Value.FrontDoorId;

    public Task InvokeAsync(HttpContext context)
    {
        // Before the URL rules rewrite the path: the kind is of the address that was asked for.
        var kind = RequestKinds.Of(
            context.Request.Path.Value ?? "/",
            FrontDoorRequest.IsFromThisSitesFrontDoor(context.Request, _frontDoorId),
            FrontDoorRequest.IsAHealthProbe(context.Request));
        var started = time.GetTimestamp();

        Task answering;
        try
        {
            answering = next(context);
        }
        catch
        {
            Failed(kind, started);
            throw;
        }

        if (answering.IsCompletedSuccessfully)
        {
            tally.Record(kind, context.Response.StatusCode, time.GetElapsedTime(started));
            return Task.CompletedTask;
        }

        return Awaited(answering, context, kind, started);
    }

    private async Task Awaited(Task answering, HttpContext context, RequestKind kind, long started)
    {
        try
        {
            await answering.ConfigureAwait(false);
        }
        catch
        {
            Failed(kind, started);
            throw;
        }

        tally.Record(kind, context.Response.StatusCode, time.GetElapsedTime(started));
    }

    /// <summary>Nothing below answered the failure (a developer's machine has no exception handler here): what answers it, outside, answers 500.</summary>
    private void Failed(RequestKind kind, long started) =>
        tally.Record(kind, StatusCodes.Status500InternalServerError, time.GetElapsedTime(started));
}
