using JeffreyPalermo.Core.Traffic;
using JeffreyPalermo.UI.Server;
using JeffreyPalermo.UnitTests.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace JeffreyPalermo.UnitTests.UI;

/// <summary>
/// How a request reaches the count (ADR-0025): once, when it has been answered, with the status it was answered
/// with, whether the rest of the pipeline answered at once, later, or failed.
/// </summary>
public class RequestTallyMiddlewareTests
{
    private const string FrontDoorId = "11111111-2222-3333-4444-555555555555";

    private readonly ManualTime _time = new();

    [Theory]
    [InlineData(200, 0)]
    [InlineData(404, 0)]
    [InlineData(503, 1)]
    public async Task ARequestAnsweredAtOnceIsCountedWithItsStatus(int statusCode, int errors)
    {
        var tally = new RequestTally(_time);
        var context = Request("/feed/");

        await Middleware(tally, answering =>
        {
            Assert.Equal(0, tally.Read().Traffic);
            answering.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        }).InvokeAsync(context);

        Assert.Equal(new RequestCounts(0, 1, errors, 1, 0, 0), tally.Read());
    }

    [Theory]
    [InlineData(200, 0)]
    [InlineData(410, 0)]
    [InlineData(500, 1)]
    public async Task ARequestAnsweredLaterIsCountedWhenItHasBeenAnsweredWithItsStatus(int statusCode, int errors)
    {
        var tally = new RequestTally(_time);
        var answer = new TaskCompletionSource();
        var context = Request("/feed/");

        var invoked = Middleware(tally, async answering =>
        {
            await answer.Task;
            answering.Response.StatusCode = statusCode;
        }).InvokeAsync(context);
        var whileAnswering = tally.Read();
        _time.Advance(TimeSpan.FromMilliseconds(90));
        answer.SetResult();
        await invoked;

        Assert.Equal(0, whileAnswering.Traffic);
        var counts = tally.Read();
        Assert.Equal(1, counts.DirectTraffic);
        Assert.Equal(errors, counts.Errors);
        // The 90 ms the answer took: "over 80, up to 100".
        Assert.InRange(counts.P95Milliseconds, 81, 100);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARequestNothingAnsweredForIsAnErrorAndTheFailureGoesOn(bool later)
    {
        var tally = new RequestTally(_time);
        var middleware = Middleware(tally, async _ =>
        {
            if (later)
            {
                await Task.Yield();
            }

            throw new InvalidOperationException("Broken, for the test.");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(Request("/")));

        Assert.Equal(new RequestCounts(0, 1, 1, 1, 0, 0), tally.Read());
    }

    [Fact]
    public async Task AFailureBeforeAnythingIsAwaitedIsCountedToo()
    {
        var tally = new RequestTally(_time);
        var middleware = Middleware(tally, _ => throw new InvalidOperationException("Broken, for the test."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(Request("/")));

        Assert.Equal(1, tally.Read().Errors);
        Assert.Equal(1, tally.Read().Traffic);
    }

    /// <summary>The URL rules rewrite the path further in (<c>/?s=</c> becomes <c>/search/</c>): the kind is of what was asked.</summary>
    [Fact]
    public async Task TheKindIsOfTheAddressThatWasAskedFor()
    {
        var tally = new RequestTally(_time);

        await Middleware(tally, answering =>
        {
            answering.Request.Path = "/_version";
            return Task.CompletedTask;
        }).InvokeAsync(Request("/"));
        await Middleware(tally, answering =>
        {
            answering.Request.Path = "/search/";
            return Task.CompletedTask;
        }).InvokeAsync(Request("/_telemetry"));

        Assert.Equal(1, tally.Read().Traffic);
        Assert.Equal(1, tally.Read().Checks);
    }

    [Fact]
    public async Task TheFrontDoorsRequestsItsProbesAndTheDirectOnesAreToldApart()
    {
        var tally = new RequestTally(_time);
        var middleware = Middleware(tally, _ => Task.CompletedTask, FrontDoorId);

        await middleware.InvokeAsync(Request("/", ("X-Azure-FDID", FrontDoorId)));
        await middleware.InvokeAsync(Request("/feed/", ("X-Azure-FDID", FrontDoorId)));
        await middleware.InvokeAsync(Request("/", ("X-Azure-FDID", "99999999-0000-0000-0000-000000000000")));
        await middleware.InvokeAsync(Request("/"));
        await middleware.InvokeAsync(Request("/", ("X-Azure-FDID", FrontDoorId), ("X-FD-HealthProbe", "1")));
        await middleware.InvokeAsync(Request("/_health/ready", ("X-Azure-FDID", FrontDoorId)));

        Assert.Equal(new RequestCounts(FrontDoorTraffic: 2, DirectTraffic: 2, Errors: 0, P95Milliseconds: 1, Checks: 1, FrontDoorProbes: 1), tally.Read());
    }

    private RequestTallyMiddleware Middleware(RequestTally tally, RequestDelegate next, string frontDoorId = "") =>
        new(next, tally, _time, Options.Create(new SiteOptions { FrontDoorId = frontDoorId }));

    private static DefaultHttpContext Request(string path, params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        return context;
    }
}
