#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.TestHarness.Net;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenAstroAra.TestHarness.Alpaca;

/// <summary>
/// A localhost HTTP reverse proxy that sits between the OpenAstro Ara daemon and a
/// real Alpaca device (the ASCOM OmniSim) and can inject the §42.2 fault modes on
/// demand. The daemon is pointed at the proxy via a <c>DiscoveredDeviceDto</c>
/// carrying the proxy's host/port, so every Alpaca call (<c>/api/v1/{type}/{n}/{method}</c>)
/// flows through here; unmatched requests pass straight through unchanged.
///
/// This is the high-leverage component of the virtual-observatory bench: one proxy
/// makes EVERY device fault-injectable, mapping the transport faults a real driver
/// can exhibit (error, HTTP failure, comms drop, hang, never-settles) onto the
/// recovery flows under test — without any physical hardware.
/// </summary>
public sealed class AlpacaFaultProxy : IAsyncDisposable {
    // The trigger counter lives here, not on the public AlpacaFaultRule, so external
    // code can't corrupt it: each armed rule is wrapped in a private mutable RuleState.
    private sealed class RuleState {
        public required AlpacaFaultRule Rule { get; init; }
        public int Fired { get; set; }
    }

    private readonly HttpListener _listener;
    private readonly Uri _upstream;
    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _client;
    private readonly List<RuleState> _rules = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    // Spawned per-request handler tasks, removed as each completes. Drained before
    // _client is disposed so a handler mid-ReadAsByteArrayAsync can't race disposal.
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    private volatile Exception? _lastHandlerFault;
    // The single byte emitted before aborting a Drop — cached to avoid a per-call alloc.
    private static readonly byte[] DropProbeByte = [(byte)'{'];

    private AlpacaFaultProxy(Uri upstream, HttpListener listener, int port) {
        _upstream = upstream;
        Port = port;
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener = listener; // already bound + started by BindLoopbackListener
        // No auto-redirect/proxy: we forward verbatim. A generous timeout lets the
        // Delay fault model a slow device without the proxy's own client giving up.
        // The proxy owns the handler explicitly (disposeHandler: false) so disposal
        // order is unambiguous — client then handler in DisposeAsync.
        _handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
        _client = new HttpClient(_handler, disposeHandler: false) {
            Timeout = TimeSpan.FromMinutes(5),
        };
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The base address the daemon should be pointed at (e.g. <c>http://127.0.0.1:49xxx/</c>).</summary>
    public Uri BaseUri { get; }

    /// <summary>The loopback TCP port the proxy is listening on.</summary>
    public int Port { get; }

    /// <summary>
    /// The last unexpected fault thrown out of a request handler (past its own
    /// internal try/catch), or <c>null</c>. Surfaced so a test can assert the proxy
    /// itself didn't crash rather than diagnosing a silent hang.
    /// </summary>
    public Exception? LastHandlerFault => _lastHandlerFault;

    private void RecordHandlerFault(AggregateException fault, HttpListenerContext context) {
        _lastHandlerFault = fault.InnerException ?? fault; // volatile write — visible to test readers
        System.Diagnostics.Trace.TraceError("AlpacaFaultProxy request handler faulted: " + _lastHandlerFault);
        SafeAbort(context); // don't leave the client hanging on a handler crash
    }

    /// <summary>
    /// Starts a proxy forwarding to <paramref name="upstreamBaseUri"/> (the OmniSim,
    /// e.g. <c>http://127.0.0.1:32323/</c>) on an ephemeral loopback port.
    /// </summary>
    public static AlpacaFaultProxy Start(Uri upstreamBaseUri) {
        ArgumentNullException.ThrowIfNull(upstreamBaseUri);
        var (listener, port) = LoopbackListener.Bind();
        return new AlpacaFaultProxy(upstreamBaseUri, listener, port);
    }

    /// <summary>Arms a fault rule. Rules are evaluated newest-first; the first match wins.</summary>
    public void InjectFault(AlpacaFaultRule rule) {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.MaxTriggers is int max && max <= 0) {
            // A non-positive cap would make the rule fire 0 times (silently dormant) —
            // catch the mistake here rather than as a baffling no-op at request time.
            throw new ArgumentOutOfRangeException(
                nameof(rule), max, "AlpacaFaultRule.MaxTriggers must be positive (or null for unlimited).");
        }
        lock (_gate) {
            _rules.Add(new RuleState { Rule = rule });
        }
    }

    /// <summary>Removes all armed fault rules — subsequent requests pass through cleanly.</summary>
    public void ClearFaults() {
        lock (_gate) {
            _rules.Clear();
        }
    }

    private async Task AcceptLoopAsync() {
        while (!_cts.IsCancellationRequested) {
            HttpListenerContext context;
            try {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            } catch (HttpListenerException) {
                return; // listener stopped during disposal
            } catch (ObjectDisposedException) {
                return; // listener disposed during disposal
            }
            // Handle each request independently so a slow/hung one (Delay fault) never
            // blocks the accept loop or other in-flight requests. Track the task so
            // DisposeAsync can drain it before tearing down _client; the continuation
            // removes it on completion and surfaces any unexpected fault (which would
            // otherwise be swallowed and show as a client-side hang).
            var task = Task.Run(() => HandleAsync(context));
            _inFlight[task] = 0;
            _ = task.ContinueWith(
                t => {
                    _inFlight.TryRemove(t, out _);
                    if (t.Exception is { } fault) {
                        RecordHandlerFault(fault, context);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(HttpListenerContext context) {
        var (deviceType, deviceNumber, method) = ParsePath(context.Request.Url);
        var fault = MatchFault(context.Request.HttpMethod, deviceType, deviceNumber, method);

        // Loop (not a single if) so nested delays — Delay(a, Delay(b, Drop())) — each
        // take effect rather than the inner one being silently discarded.
        while (fault is DelayFault delay) {
            try {
                await Task.Delay(delay.Duration, _cts.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                SafeAbort(context);
                return;
            }
            fault = delay.Then;
        }

        switch (fault) {
            case DropFault:
                await DropConnectionAsync(context, _cts.Token).ConfigureAwait(false);
                return;
            case HttpStatusFault http:
                await WriteRawAsync(context, http.StatusCode, body: "", contentType: "text/plain", _cts.Token).ConfigureAwait(false);
                return;
            case AlpacaErrorFault err:
                await WriteAlpacaErrorAsync(context, err, _cts.Token).ConfigureAwait(false);
                return;
            case RewriteValueFault rewrite:
                await ForwardAsync(context, rewrite.JsonValueLiteral).ConfigureAwait(false);
                return;
            default:
                await ForwardAsync(context, rewriteValueLiteral: null).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>Finds the first matching un-expired rule, incrementing its trigger count.</summary>
    private AlpacaFault? MatchFault(string httpVerb, string? deviceType, int? deviceNumber, string? method) {
        lock (_gate) {
            // Newest-first so a test can layer a later, more specific rule over an
            // earlier blanket one.
            for (var i = _rules.Count - 1; i >= 0; i--) {
                var state = _rules[i];
                var rule = state.Rule;
                if (rule.MaxTriggers is int max && state.Fired >= max) {
                    // Expired one-shot rule: drop it so a long scenario injecting many
                    // one-shots doesn't grow _rules unboundedly. Safe under reverse
                    // iteration — only indices <= i shift, none already visited.
                    _rules.RemoveAt(i);
                    continue;
                }
                if (rule.HttpVerb is { } v && !string.Equals(v, httpVerb, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
                if (rule.DeviceType is { } dt && !string.Equals(dt, deviceType, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
                if (rule.DeviceNumber is int dn && dn != deviceNumber) {
                    continue;
                }
                if (rule.Method is { } m && !string.Equals(m, method, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
                state.Fired++;
                return rule.Fault;
            }
        }
        return null;
    }

    /// <summary>Forwards the request to the upstream device, optionally rewriting the response envelope's Value.</summary>
    private async Task ForwardAsync(HttpListenerContext context, string? rewriteValueLiteral) {
        var req = context.Request;
        var targetUri = new Uri(_upstream, req.Url!.PathAndQuery);
        using var forward = new HttpRequestMessage(new HttpMethod(req.HttpMethod), targetUri);

        if (req.HasEntityBody) {
            using var bodyStream = new MemoryStream();
            await req.InputStream.CopyToAsync(bodyStream, _cts.Token).ConfigureAwait(false);
            var content = new ByteArrayContent(bodyStream.ToArray());
            if (req.ContentType is { } ct) {
                content.Headers.TryAddWithoutValidation("Content-Type", ct);
            }
            forward.Content = content;
        }

        // Forward the inbound headers verbatim (minus hop-by-hop + the ones the
        // transport sets itself) so the proxy is a faithful pass-through — once the
        // bench drives the real daemon, an Accept/auth/custom Alpaca header must reach
        // the device, not be silently dropped. Request vs content headers are routed by
        // trying the request collection first and falling back to the content's.
        foreach (string? name in req.Headers) {
            if (name is null || IsSkippedForwardHeader(name)) {
                continue;
            }
            var values = req.Headers.GetValues(name);
            if (values is null) {
                continue;
            }
            if (!forward.Headers.TryAddWithoutValidation(name, values)) {
                forward.Content?.Headers.TryAddWithoutValidation(name, values);
            }
        }

        try {
            using var upstream = await _client.SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, _cts.Token).ConfigureAwait(false);
            var bytes = await upstream.Content.ReadAsByteArrayAsync(_cts.Token).ConfigureAwait(false);
            if (rewriteValueLiteral is not null) {
                bytes = RewriteEnvelopeValue(bytes, rewriteValueLiteral);
            }
            var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
            await WriteRawBytesAsync(context, (int)upstream.StatusCode, bytes, contentType, _cts.Token).ConfigureAwait(false);
        } catch (HttpRequestException) {
            // Upstream device unreachable — surface a 502 rather than crashing the proxy.
            await WriteRawAsync(context, 502, body: "upstream unreachable", contentType: "text/plain", _cts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            SafeAbort(context);
        } catch (IOException) {
            SafeAbort(context);
        } catch (ObjectDisposedException) {
            // _client disposed out from under a forward that began just before
            // teardown — drop it. (DisposeAsync drains in-flight handlers first, so
            // this is only reachable on a hard race during shutdown.)
            SafeAbort(context);
        }
    }

    // Hop-by-hop headers (RFC 7230 §6.1) plus the ones the transport/content layer
    // owns — never forwarded end-to-end through a proxy.
    private static readonly HashSet<string> SkippedForwardHeaders = new(StringComparer.OrdinalIgnoreCase) {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade",
        "Host", "Content-Length", "Content-Type",
    };

    private static bool IsSkippedForwardHeader(string name) => SkippedForwardHeaders.Contains(name);

    /// <summary>Replaces the <c>Value</c> field of an Alpaca JSON envelope; passes the body through unchanged if it isn't parseable JSON.</summary>
    private static byte[] RewriteEnvelopeValue(byte[] body, string jsonValueLiteral) {
        try {
            var node = JsonNode.Parse(body);
            if (node is JsonObject obj) {
                obj["Value"] = JsonNode.Parse(jsonValueLiteral);
                return Encoding.UTF8.GetBytes(obj.ToJsonString());
            }
        } catch (JsonException) {
            // Not a JSON envelope (e.g. a management endpoint) — leave it untouched.
        }
        return body;
    }

    private static async Task WriteAlpacaErrorAsync(HttpListenerContext context, AlpacaErrorFault err, CancellationToken cancellationToken) {
        // Echo the client's transaction id when present so the envelope is well-formed
        // for the real ASCOM.Alpaca client (which correlates responses by it).
        var clientTxn = await ReadClientTransactionIdAsync(context.Request, cancellationToken).ConfigureAwait(false);
        var envelope = new JsonObject {
            ["ClientTransactionID"] = clientTxn,
            // Fixed sentinel: the ASCOM client doesn't require a monotonic
            // ServerTransactionID, and the proxy issues one synthetic error at a time.
            ["ServerTransactionID"] = 1,
            ["ErrorNumber"] = err.ErrorNumber,
            ["ErrorMessage"] = err.Message,
            // An error response carries no value — this null is correct here, distinct
            // from RewriteValue rejecting a "null" literal (which would erase a *success*
            // envelope's Value).
            ["Value"] = null,
        };
        await WriteRawAsync(context, 200, envelope.ToJsonString(), "application/json", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ReadClientTransactionIdAsync(HttpListenerRequest req, CancellationToken cancellationToken) {
        // GET carries it in the query; PUT carries it in the x-www-form-urlencoded
        // body. Best-effort: default 0. (Reads the body only for non-GET — an Alpaca
        // error short-circuits forwarding, so the body is otherwise unconsumed.) The
        // token is essential here: without it a large/malformed PUT body could block
        // the read, hanging the DisposeAsync handler drain.
        if (long.TryParse(req.QueryString["ClientTransactionID"], out var fromQuery)) {
            return fromQuery;
        }
        if (req.HasEntityBody) {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries)) {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0) {
                    continue;
                }
                var key = Uri.UnescapeDataString(pair[..eq]);
                if (string.Equals(key, "ClientTransactionID", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(Uri.UnescapeDataString(pair[(eq + 1)..]), out var fromBody)) {
                    return fromBody;
                }
            }
        }
        return 0;
    }

    private static async Task WriteRawAsync(HttpListenerContext context, int statusCode, string body, string contentType, CancellationToken cancellationToken) =>
        await WriteRawBytesAsync(context, statusCode, Encoding.UTF8.GetBytes(body), contentType, cancellationToken).ConfigureAwait(false);

    private static async Task WriteRawBytesAsync(HttpListenerContext context, int statusCode, byte[] body, string contentType, CancellationToken cancellationToken) {
        try {
            var resp = context.Response;
            resp.StatusCode = statusCode;
            resp.ContentType = contentType;
            resp.ContentLength64 = body.Length;
            await resp.OutputStream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            resp.OutputStream.Close();
        } catch (HttpListenerException) {
            // Client went away mid-write; nothing to do.
        } catch (ObjectDisposedException) {
            // Response already torn down (e.g. concurrent disposal).
        } catch (OperationCanceledException) {
            // Proxy disposed mid-write — abandon the response.
        }
    }

    /// <summary>
    /// Models a comms drop: promise a fixed-length body, send one byte, then tear the
    /// connection down before delivering the rest. A bare <see cref="HttpListenerResponse.Abort"/>
    /// (or a truncated chunked stream) is auto-completed cleanly by the managed listener
    /// on some platforms; a Content-Length the response never fulfils makes the client
    /// read past the bytes it gets and surface a premature-end / reset error — which is
    /// what a real device losing comms looks like.
    /// </summary>
    private static async Task DropConnectionAsync(HttpListenerContext context, CancellationToken cancellationToken) {
        try {
            var resp = context.Response;
            resp.StatusCode = 200;
            resp.SendChunked = false;
            resp.ContentLength64 = 64; // promise 64 bytes…
            await resp.OutputStream.WriteAsync(DropProbeByte, cancellationToken).ConfigureAwait(false); // …deliver 1
            await resp.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        } catch (HttpListenerException) {
            // Connection already gone — the drop is the point.
        } catch (ObjectDisposedException) {
            // Response already torn down.
        } catch (OperationCanceledException) {
            // Proxy disposed mid-drop — abandon it.
        } catch (IOException) {
            // Stream faulted mid-write — also a drop from the client's view.
        }
        SafeAbort(context);
    }

    private static void SafeAbort(HttpListenerContext context) {
        try {
            context.Response.Abort();
        } catch (ObjectDisposedException) {
            // Already aborted/disposed — the client sees a reset either way.
        }
    }

    /// <summary>Parses <c>/api/v1/{deviceType}/{deviceNumber}/{method}</c>; any element is null when the path isn't device-scoped.</summary>
    private static (string? DeviceType, int? DeviceNumber, string? Method) ParsePath(Uri? url) {
        if (url is null) {
            return (null, null, null);
        }
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Expect: api / v1 / {type} / {number} / {method}
        if (segments.Length < 5 ||
            !string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[1], "v1", StringComparison.OrdinalIgnoreCase)) {
            return (null, null, null);
        }
        var deviceType = segments[2];
        int? deviceNumber = int.TryParse(segments[3], out var n) ? n : null;
        var method = segments[4];
        return (deviceType, deviceNumber, method);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        await _cts.CancelAsync().ConfigureAwait(false);
        try {
            _listener.Stop();
        } catch (ObjectDisposedException) {
            // Already disposed.
        }
        try {
            await _acceptLoop.ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // Expected on shutdown.
        }
        // Drain in-flight request handlers BEFORE disposing _client, so none can be
        // mid-SendAsync/ReadAsByteArrayAsync when the client goes away. Await *completion*
        // without observing the aggregate fault — each handler's own continuation already
        // observed + recorded its exception (LastHandlerFault), so a faulted handler must
        // not propagate out of DisposeAsync and mask the original test failure. The
        // ContinueWith swallow is the CA-clean way to "await all, ignore faults".
        await Task.WhenAll(_inFlight.Keys)
            .ContinueWith(static _ => { }, TaskScheduler.Default)
            .ConfigureAwait(false);
        _listener.Close();
        _client.Dispose();
        _handler.Dispose();
        _cts.Dispose();
    }
}
