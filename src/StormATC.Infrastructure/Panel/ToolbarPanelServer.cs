using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Options;
using StormATC.Application.Panel;

namespace StormATC.Infrastructure.Panel;

/// <summary>
/// A small web server on the loopback interface that feeds the in-simulator panel.
/// </summary>
/// <remarks>
/// <para>
/// The MSFS toolbar panel is a web page running inside the simulator; it cannot reach StormATC's
/// session directly, so it asks this server for the state a few times a second:
/// <c>GET /api/state</c> returns the conversation and the phraseology for the current stage.
/// </para>
/// <para>
/// The same page is served at <c>/panel</c>, so it can also be opened in a browser or in a VR
/// overlay application. Only the loopback interface is bound, requests whose host is not
/// loopback are refused, and nothing here changes the session: it is read-only.
/// </para>
/// </remarks>
public sealed class ToolbarPanelServer : IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".svg"] = "image/svg+xml",
    };

    private readonly IToolbarPanelStateSource _state;
    private readonly ToolbarPanelOptions _options;
    private readonly ILogger<ToolbarPanelServer> _logger;
    private readonly string _panelDirectory;
    private HttpListener? _listener;
    private CancellationTokenSource? _stopping;
    private Task? _loop;

    /// <summary>Creates the server.</summary>
    /// <param name="state">Where the panel's state comes from.</param>
    /// <param name="options">The port and whether the server runs.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="panelDirectory">The folder holding the panel's page, script and style.</param>
    public ToolbarPanelServer(
        IToolbarPanelStateSource state,
        IOptions<ToolbarPanelOptions> options,
        ILogger<ToolbarPanelServer> logger,
        string panelDirectory)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _panelDirectory = panelDirectory ?? throw new ArgumentNullException(nameof(panelDirectory));
    }

    /// <summary>The address the panel is served at once the server has started.</summary>
    public Uri? Address { get; private set; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("The toolbar panel server is disabled.");
            return Task.CompletedTask;
        }

        var prefix = new Uri(
            string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{_options.Port}/"));

        var listener = new HttpListener();
        listener.Prefixes.Add(prefix.AbsoluteUri);

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            // A busy port or a refused registration must not stop StormATC: the panel is optional.
            _logger.LogWarning(
                ex,
                "The toolbar panel server could not listen on {Address}; the in-simulator panel will not be available.",
                prefix);
            listener.Close();
            return Task.CompletedTask;
        }

        _listener = listener;
        _stopping = new CancellationTokenSource();
        Address = prefix;
        _loop = Task.Run(() => ServeAsync(listener, _stopping.Token), CancellationToken.None);
        _logger.LogInformation("The toolbar panel is served at {Address}panel.", prefix);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            return;
        }

        await _stopping!.CancelAsync().ConfigureAwait(false);
        _listener.Close();

        if (_loop is not null)
        {
            await Task.WhenAny(_loop, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)).ConfigureAwait(false);
        }

        _listener = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping?.Dispose();
        ((IDisposable?)_listener)?.Dispose();
    }

    private async Task ServeAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            try
            {
                await HandleAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or InvalidOperationException or ObjectDisposedException)
            {
                // The panel closed the connection mid-reply; it will ask again.
                _logger.LogDebug(ex, "A toolbar panel request failed.");
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        HttpListenerResponse response = context.Response;
        try
        {
            // Rebinding protection: a page on another site that resolves its own name to
            // 127.0.0.1 would carry that name in the Host header.
            if (!IsLoopbackHost(context.Request.Url?.Host))
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }

            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers["Cache-Control"] = "no-store";

            string method = context.Request.HttpMethod;
            if (method == "OPTIONS")
            {
                response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
                response.Headers["Access-Control-Allow-Headers"] = "*";
                response.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }

            if (method is not ("GET" or "HEAD"))
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            string path = context.Request.Url!.AbsolutePath.TrimEnd('/');
            switch (path)
            {
                case "/api/state":
                    await WriteAsync(
                        response,
                        "application/json; charset=utf-8",
                        JsonSerializer.SerializeToUtf8Bytes(_state.Current(), Json),
                        cancellationToken).ConfigureAwait(false);
                    return;

                case "" or "/panel":
                    await WriteFileAsync(response, "standalone.html", cancellationToken).ConfigureAwait(false);
                    return;
            }

            const string filePrefix = "/panel/";
            if (path.StartsWith(filePrefix, StringComparison.Ordinal))
            {
                await WriteFileAsync(response, path[filePrefix.Length..], cancellationToken).ConfigureAwait(false);
                return;
            }

            response.StatusCode = (int)HttpStatusCode.NotFound;
        }
        finally
        {
            response.Close();
        }
    }

    private async Task WriteFileAsync(HttpListenerResponse response, string name, CancellationToken cancellationToken)
    {
        // Only a plain file name inside the panel folder: no separators, no parent references.
        string safe = Path.GetFileName(name);
        string file = Path.Combine(_panelDirectory, safe);
        if (safe != name
            || !ContentTypes.TryGetValue(Path.GetExtension(safe), out string? contentType)
            || !File.Exists(file))
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        byte[] bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        await WriteAsync(response, contentType, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAsync(
        HttpListenerResponse response,
        string contentType,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsLoopbackHost(string? host) =>
        host is "127.0.0.1" or "localhost" or "[::1]";
}
