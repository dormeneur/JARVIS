using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Contracts.Browser;
using Jarvis.Core.Companion;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Windows.Win32;

namespace Jarvis.Platform.Browser;

/// <summary>
/// Where the Jarvis Browser Link extension connects: a WebSocket on 127.0.0.1 only. A connection
/// is accepted only when its Origin is chrome-extension://&lt;an ID the user approved&gt;. Browsers
/// set Origin themselves, so web pages can't pretend to be the extension. An unknown extension is
/// remembered as "waiting for approval" for Settings → Browser and turned away.
/// </summary>
public sealed partial class BrowserLinkServer : IBrowserLink, IHostedService, IDisposable
{
    public const int Port = 47613;
    private const string ExtensionScheme = "chrome-extension://";
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Filling waits for the page: scrolling, and a dropdown opening before its option is clicked.</summary>
    private static readonly TimeSpan FillTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Reading every frame of a big page can take a few seconds.</summary>
    private static readonly TimeSpan FormTimeout = TimeSpan.FromSeconds(6);
    private static readonly string[] Browsers = ["chrome", "msedge", "brave", "arc", "vivaldi", "opera", "chromium"];

    private readonly IOptionsMonitor<BrowserOptions> _options;
    private readonly ILogger<BrowserLinkServer> _logger;
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _waiting = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private WebSocket? _socket;
    private int _nextId;

    public BrowserLinkServer(IOptionsMonitor<BrowserOptions> options, ILogger<BrowserLinkServer> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>Raised when an extension that isn't approved yet tries to connect (its ID).</summary>
    public event Action<string>? ApprovalWanted;

    /// <summary>The last unapproved extension that tried to connect, for Settings.</summary>
    public string? WaitingForApproval { get; private set; }

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public unsafe bool IsBrowserInFront
    {
        get
        {
            uint process;
            PInvoke.GetWindowThreadProcessId(PInvoke.GetForegroundWindow(), &process);
            try
            {
                using var owner = Process.GetProcessById((int)process);
                return Browsers.Contains(owner.ProcessName, StringComparer.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/jarvis/");
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync, CancellationToken.None);
        }
        catch (HttpListenerException exception)
        {
            LogCouldNotListen(exception); // the browser link is optional; Jarvis runs without it
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _stopping.Dispose();
        _sending.Dispose();
        ((IDisposable)_listener).Dispose();
    }

    public async Task<BrowserPage?> ReadPageAsync(CancellationToken cancellationToken)
    {
        if (await RequestAsync(new JsonObject { ["type"] = "page" }, cancellationToken).ConfigureAwait(false) is not JsonObject page)
        {
            return null;
        }

        string Read(string key) => page[key]?.GetValue<string>() ?? string.Empty;
        var elements = (page["elements"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(element => new PageElement(
                element["id"]?.GetValue<string>() ?? string.Empty,
                element["kind"]?.GetValue<string>() ?? "button",
                element["label"]?.GetValue<string>() ?? string.Empty,
                element["onScreen"]?.GetValue<bool>() == true))
            .Where(element => element.Id.Length > 0)
            .ToList();
        var tabs = (page["tabs"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(tab => new BrowserTab(tab["id"]?.GetValue<string>() ?? string.Empty, tab["title"]?.GetValue<string>() ?? string.Empty, tab["active"]?.GetValue<bool>() == true))
            .Where(tab => tab.Id.Length > 0)
            .ToList();
        return new BrowserPage(Read("url"), Read("title"), Read("selection"), Read("text"), elements, tabs);
    }

    public async Task<BrowserActResult?> ActAsync(BrowserAct act, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(act);
        var request = new JsonObject { ["type"] = "act", ["action"] = act.Action, ["target"] = act.Target, ["text"] = act.Text };
        if (await RequestAsync(request, cancellationToken, FillTimeout).ConfigureAwait(false) is not JsonObject result)
        {
            return null;
        }

        int? Number(string key) => result[key] is JsonValue number && number.TryGetValue<double>(out var n) ? (int)n : null;
        return new BrowserActResult(
            result["done"]?.GetValue<bool>() == true,
            Number("x"),
            Number("y"),
            result["focused"]?.GetValue<bool>() == true,
            result["reason"]?.GetValue<string>(),
            result["text"]?.GetValue<string>());
    }

    public async Task<BrowserForm?> ReadFormAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            return null;
        }

        if (await RequestAsync(new JsonObject { ["type"] = "form" }, cancellationToken, FormTimeout).ConfigureAwait(false) is not JsonObject form)
        {
            return new BrowserForm(string.Empty, string.Empty, [], Problem: "The browser extension didn't answer. Reload Jarvis Browser Link on your browser's extensions page, then try again.");
        }

        string Read(JsonNode? node, string key) => node?[key]?.GetValue<string>() ?? string.Empty;
        var questions = (form["questions"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(question => new FormQuestion(
                Read(question, "id"),
                Read(question, "text"),
                Read(question, "kind"),
                [.. (question["options"] as JsonArray ?? []).Select(option => option?.GetValue<string>() ?? string.Empty).Where(option => option.Length > 0)],
                question["required"]?.GetValue<bool>() == true))
            .Where(question => question.Id.Length > 0 && question.Text.Length > 0)
            .ToList();
        // The extension only sees windows it's allowed in: a private window it can't see makes it read
        // the last normal window instead. Never fill a page other than the one in front.
        var tabTitle = Read(form, "tabTitle");
        if (IsBrowserInFront && tabTitle.Length > 0 && FrontWindowTitle() is { Length: > 0 } front && !front.Contains(tabTitle, StringComparison.Ordinal))
        {
            var allowed = form["allowedInPrivate"]?.GetValue<bool>() == true;
            return new BrowserForm(Read(form, "url"), Read(form, "title"), [], Problem: allowed
                ? "The page I can read isn't the one in front. Click into the form's tab, then try again."
                : "I can't see the window in front. If it's a private (incognito) window, turn on \"Allow in Private\" for Jarvis Browser Link: open your browser's extensions page, then Details.");
        }

        var problem = form["reason"]?.GetValue<string>() is { Length: > 0 } reason ? $"I couldn't read a form here: {reason}." : null;
        if (form["version"] is null)
        {
            problem = "The browser extension is out of date. Reload Jarvis Browser Link on your browser's extensions page, then try again.";
        }

        return new BrowserForm(Read(form, "url"), Read(form, "title"), questions, Read(form, "description"), questions.Count == 0 ? problem ?? "I couldn't find any questions on this page." : null);
    }

    /// <summary>The title bar text of the window in front (browsers put the tab's title there).</summary>
    private static unsafe string FrontWindowTitle()
    {
        Span<char> buffer = stackalloc char[512];
        fixed (char* text = buffer)
        {
            var length = PInvoke.GetWindowText(PInvoke.GetForegroundWindow(), text, buffer.Length);
            return new string(buffer[..Math.Max(0, length)]);
        }
    }

    public async Task<FormFill?> FillFormAsync(string questionId, string value, CancellationToken cancellationToken)
    {
        var request = new JsonObject { ["type"] = "fill", ["question"] = questionId, ["value"] = value };
        if (await RequestAsync(request, cancellationToken, FillTimeout).ConfigureAwait(false) is not JsonObject fill)
        {
            return null;
        }

        int? Number(string key) => fill[key] is JsonValue number && number.TryGetValue<double>(out var n) ? (int)n : null;
        return new FormFill(fill["filled"]?.GetValue<bool>() == true, Number("x"), Number("y"), fill["reason"]?.GetValue<string>());
    }

    public async Task<bool> CheckFormAsync(string questionId, string value, CancellationToken cancellationToken)
        => await RequestAsync(new JsonObject { ["type"] = "check", ["question"] = questionId, ["value"] = value }, cancellationToken).ConfigureAwait(false) is JsonValue shown
           && shown.TryGetValue<bool>(out var yes) && yes;

    public async Task<bool> OpenAsync(string url, CancellationToken cancellationToken)
        => await RequestAsync(new JsonObject { ["type"] = "open", ["url"] = url }, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>An extension ID from a WebSocket Origin, if it is one.</summary>
    internal static string? ExtensionId(string? origin)
        => origin is not null && origin.StartsWith(ExtensionScheme, StringComparison.OrdinalIgnoreCase)
            ? origin[ExtensionScheme.Length..].TrimEnd('/')
            : null;

    private async Task<JsonNode?> RequestAsync(JsonObject request, CancellationToken cancellationToken, TimeSpan? timeLimit = null)
    {
        if (_socket is not { State: WebSocketState.Open } socket)
        {
            return null;
        }

        var id = Interlocked.Increment(ref _nextId);
        request["id"] = id;
        var reply = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = reply;
        try
        {
            await SendAsync(socket, request.ToJsonString(), cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeLimit ?? ReplyTimeout);
            return await reply.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
            return null;
        }
        finally
        {
            _waiting.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(WebSocket socket, string message, CancellationToken cancellationToken)
    {
        await _sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sending.Release();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => AcceptAsync(context));
        }
    }

    private async Task AcceptAsync(HttpListenerContext context)
    {
        var extension = ExtensionId(context.Request.Headers["Origin"]);
        if (!context.Request.IsWebSocketRequest || extension is null)
        {
            Refuse(context, HttpStatusCode.Forbidden); // web pages and anything else
            return;
        }

        if (!_options.CurrentValue.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            WaitingForApproval = extension;
            ApprovalWanted?.Invoke(extension);
            LogUnapproved(extension);
            Refuse(context, HttpStatusCode.Forbidden);
            return;
        }

        WebSocket socket;
        try
        {
            socket = (await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false)).WebSocket;
        }
        catch (WebSocketException)
        {
            return;
        }

        var previous = Interlocked.Exchange(ref _socket, socket);
        previous?.Abort(); // the newest browser wins
        LogConnected(extension);
        await ReadLoopAsync(socket).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open && !_stopping.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(buffer, _stopping.Token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                message.Write(buffer, 0, received.Count);
                if (!received.EndOfMessage)
                {
                    continue;
                }

                Deliver(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                message.SetLength(0);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
            // The browser closed or Jarvis is quitting.
        }
        finally
        {
            Interlocked.CompareExchange(ref _socket, null, socket);
            socket.Dispose();
        }
    }

    private void Deliver(string text)
    {
        JsonNode? reply;
        try
        {
            reply = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return;
        }

        if (reply?["id"]?.GetValue<int>() is { } id && _waiting.TryRemove(id, out var waiter))
        {
            waiter.TrySetResult(reply["ok"]?.GetValue<bool>() == true ? reply["result"]?.DeepClone() ?? JsonValue.Create(true) : null);
        }
    }

    private static void Refuse(HttpListenerContext context, HttpStatusCode status)
    {
        context.Response.StatusCode = (int)status;
        context.Response.Close();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The browser link couldn't start; the extension won't connect")]
    private partial void LogCouldNotListen(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Browser extension {Extension} tried to connect; it needs approval in Settings → Browser")]
    private partial void LogUnapproved(string extension);

    [LoggerMessage(Level = LogLevel.Information, Message = "Browser extension {Extension} connected")]
    private partial void LogConnected(string extension);
}
