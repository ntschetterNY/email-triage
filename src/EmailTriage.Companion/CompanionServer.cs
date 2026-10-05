using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EmailTriage.Companion;

/// <summary>
/// The HTTPS endpoint the iPhone app talks to. Off unless the user turns it
/// on in Settings. Every request must come from a private network address
/// (the same Wi-Fi, in practice) and carry the pairing token; the phone in
/// turn only talks to a server presenting the certificate it pinned.
/// </summary>
public sealed class CompanionServer : IAsyncDisposable
{
    public const int DefaultPort = 47821;

    private readonly CompanionService _service;
    private readonly CompanionIdentity _identity;
    private WebApplication? _app;

    public CompanionServer(CompanionService service, CompanionIdentity identity, int port = DefaultPort)
    {
        _service = service;
        _identity = identity;
        Port = port;
    }

    public int Port { get; }

    public bool IsRunning => _app is not null;

    /// <summary>Called for each request answered, for an "in use" hint on the PC.</summary>
    public event EventHandler? Served;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_app is not null) return;

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = "EmailTriage.Companion" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 1024 * 1024;
            k.ListenAnyIP(Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(_identity.Certificate);
            });
        });
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
        });

        var app = builder.Build();
        app.Use(GuardAsync);
        Map(app, _service);

        await app.StartAsync(ct).ConfigureAwait(false);
        _app = app;
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        if (app is null) return;

        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    /// <summary>Same network, right token, or nothing; and every refusal or failure answered as JSON.</summary>
    private async Task GuardAsync(HttpContext context, Func<Task> next)
    {
        if (!LocalNetwork.IsPrivate(context.Connection.RemoteIpAddress))
        {
            await Refuse(context, StatusCodes.Status403Forbidden, "Only devices on the same network can connect.");
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        var token = header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase) ? header[bearer.Length..].Trim() : null;
        if (!_identity.Accepts(token))
        {
            await Refuse(context, StatusCodes.Status401Unauthorized, "This device isn't paired with the PC. Pair it again from Settings on the PC.");
            return;
        }

        context.Response.Headers.CacheControl = "no-store";

        try
        {
            await next();
            Served?.Invoke(this, EventArgs.Empty);
        }
        catch (CompanionException ex)
        {
            await Refuse(context, ex.Status, ex.Message);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The phone went away; nobody to answer.
        }
        catch (Exception ex)
        {
            await Refuse(context, StatusCodes.Status500InternalServerError, ex.Message);
        }
    }

    private static Task Refuse(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted) return Task.CompletedTask;
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ErrorDto(message));
    }

    private static void Map(WebApplication app, CompanionService s)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/hello", () => s.Hello());
        api.MapGet("/inbox", (CancellationToken ct) => s.GetInboxAsync(ct));
        api.MapPost("/thread", (ThreadRequest r, CancellationToken ct) => s.GetThreadAsync(r, ct));
        api.MapPost("/attachment", (AttachmentRequest r, CancellationToken ct) => s.GetAttachmentAsync(r, ct));
        api.MapPost("/archive", async (RefsRequest r, CancellationToken ct) => new { done = await s.ArchiveAsync(r, ct) });
        api.MapPost("/move", async (MoveRequest r, CancellationToken ct) => new { done = await s.MoveAsync(r, ct) });
        api.MapGet("/folders", (string? q, CancellationToken ct) => s.SearchFoldersAsync(q, ct));
        api.MapGet("/snooze/options", () => s.SnoozeOptions());
        api.MapGet("/snooze/parse", (string? text) =>
            s.ParseSnooze(text) is { } parsed
                ? Results.Ok(parsed)
                : Results.Json(new ErrorDto("Not a time I understand - try \"tomorrow 9am\", \"fri\" or \"3d\"."),
                    statusCode: StatusCodes.Status422UnprocessableEntity));
        api.MapPost("/snooze", async (SnoozeRequest r, CancellationToken ct) => new { done = await s.SnoozeAsync(r, ct) });
        api.MapPost("/read", async (ReadRequest r, CancellationToken ct) => { await s.SetReadAsync(r, ct); return Results.NoContent(); });
        api.MapPost("/flag", async (FlagRequest r, CancellationToken ct) => { await s.SetFlagAsync(r, ct); return Results.NoContent(); });
        api.MapPost("/reply", async (ReplyRequest r, CancellationToken ct) => { await s.ReplyAsync(r, ct); return Results.NoContent(); });
        api.MapPost("/feedback", (FeedbackRequest r, CancellationToken ct) => s.SendFeedbackAsync(r, ct));
        api.MapGet("/feedback", (CancellationToken ct) => s.ListFeedbackAsync(ct));
    }

    /// <summary>The addresses to put in the pairing link.</summary>
    public static IReadOnlyList<IPAddress> PairingHosts() => Pairing.LocalAddresses();
}
