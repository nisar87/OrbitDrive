using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OrbitDrive.Gateway;

/// <summary>
/// One access-log line per request and a correlation id the owner can quote when asking for help (R46). The line names the ROUTE TEMPLATE, never the raw path or
/// query: download links and ids travel in the path and must not reach a log. No headers, bodies, accounts, addresses or file names are logged.
/// </summary>
public sealed partial class RequestLogging(RequestDelegate next, ILogger<RequestLogging> logger)
{
    public const string Header = "X-Request-Id";

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex SafeId();

    public async Task InvokeAsync(HttpContext context)
    {
        // A caller-supplied id is accepted only if it is plainly an id (so it cannot smuggle text into the log); otherwise we make one.
        var supplied = context.Request.Headers[Header].ToString();
        var id = SafeId().IsMatch(supplied) ? supplied : Secrets.Token(8);
        context.Response.Headers[Header] = id;
        var clock = Stopwatch.StartNew();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = id });
        try { await next(context); }
        finally
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)";
            logger.LogInformation("{Method} {Route} -> {Status} in {Millis} ms [{RequestId}]", context.Request.Method, route, context.Response.StatusCode, clock.ElapsedMilliseconds, id);
        }
    }
}
