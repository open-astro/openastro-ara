#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenAstroAra.TestHarness.Guider;

/// <summary>
/// A scriptable fake of the openastro-guider / PHD2 event server for the
/// virtual-observatory bench. It speaks the PHD2 JSON-RPC line protocol over TCP so
/// the real ARA guider client can connect, calibrate, guide, dither, and lose the
/// star — all without the C++ daemon.
///
/// Every accepted connection is symmetric (as in real PHD2): it immediately receives
/// the on-connect event burst, then in parallel (a) answers JSON-RPC requests with
/// canned results and (b) receives any broadcast events. The ARA client uses one
/// persistent connection for the event stream and a fresh connection per RPC, so the
/// fake must serve both shapes on any connection.
/// </summary>
public sealed class FakeGuider : IAsyncDisposable {
    private sealed class Connection {
        public required NetworkStream Stream { get; init; }
        // Serializes writes to this connection's stream — broadcasts and RPC replies
        // can race otherwise, interleaving bytes of two messages.
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentDictionary<Connection, byte> _connections = new();
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    private readonly Lock _gate = new();
    private readonly List<JsonObject> _onConnectEvents = [];
    private readonly Dictionary<string, Func<JsonObject, JsonNode?>> _rpcResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _receivedMethods = [];

    private FakeGuider(int port) {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        // PHD2 greets every new connection with Version then an initial AppState.
        _onConnectEvents.Add(PhdEvents.Version());
        _onConnectEvents.Add(PhdEvents.AppState("Stopped"));
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The loopback TCP port the fake guider is listening on.</summary>
    public int Port { get; }

    /// <summary>The number of currently-open client connections — lets a test wait deterministically for an accept/close.</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>
    /// Starts a fake guider on an ephemeral loopback port (pass 4400 to mimic the real
    /// daemon's well-known port in a non-parallel rig).
    /// </summary>
    public static FakeGuider Start(int port = 0) => new(port);

    /// <summary>
    /// The RPC method names received so far, in order. Returns a fresh array snapshot on
    /// each read (under the lock) so the caller can't observe a torn list mid-mutation.
    /// </summary>
    public IReadOnlyList<string> ReceivedMethods {
        get { lock (_gate) { return _receivedMethods.ToArray(); } }
    }

    /// <summary>
    /// Overrides the result returned for an RPC <paramref name="method"/>. The factory
    /// receives the parsed request and returns the JSON <c>result</c> value (PHD2
    /// returns <c>0</c> for most successful setters; getters return a typed value).
    /// Unset methods default to <c>result: 0</c>.
    /// </summary>
    public void OnRpc(string method, Func<JsonObject, JsonNode?> resultFactory) {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(resultFactory);
        lock (_gate) {
            _rpcResults[method] = resultFactory;
        }
    }

    /// <summary>Convenience overload: a constant result value for <paramref name="method"/>.</summary>
    // DeepClone per call — a JsonNode can only be parented once, so each response needs a fresh copy.
    public void OnRpc(string method, JsonNode? result) => OnRpc(method, _ => result?.DeepClone());

    /// <summary>
    /// Replaces the burst of events sent to each connection on accept. Passing no
    /// arguments clears the greeting entirely (a connection then receives nothing until
    /// the first <see cref="BroadcastAsync"/>) — useful for testing a silent server.
    /// </summary>
    public void SetOnConnectEvents(params JsonObject[] events) {
        ArgumentNullException.ThrowIfNull(events);
        lock (_gate) {
            _onConnectEvents.Clear();
            _onConnectEvents.AddRange(events);
        }
    }

    /// <summary>Pushes an event to every currently-connected client (e.g. StarLost, SettleDone).</summary>
    public async Task BroadcastAsync(JsonObject @event) {
        ArgumentNullException.ThrowIfNull(@event);
        var line = Frame(@event); // serialized once; the same bytes go to every connection
        // Deliver in parallel so a slow/blocked client doesn't delay the others; each
        // connection's WriteLock serializes its own writes. WriteAsync never throws.
        await Task.WhenAll(_connections.Keys.Select(c => WriteAsync(c, line, _cts.Token))).ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync() {
        while (!_cts.IsCancellationRequested) {
            TcpClient client;
            try {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                return;
            } catch (SocketException) {
                return; // listener stopped during disposal
            }
            // Gate the handler on `started` so the body (and thus its completion +
            // the removing continuation) can't run before _inFlight[task] is set —
            // otherwise a fast-completing task could be removed before it was added and
            // linger in _inFlight forever.
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = Task.Run(async () => {
                await started.Task.ConfigureAwait(false);
                await HandleConnectionAsync(client).ConfigureAwait(false);
            });
            _inFlight[task] = 0;
            _ = task.ContinueWith(t => _inFlight.TryRemove(t, out _), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            started.SetResult();
        }
    }

    private async Task HandleConnectionAsync(TcpClient client) {
        client.NoDelay = true;
        using var owned = client;
        var stream = client.GetStream();
        var conn = new Connection { Stream = stream };
        _connections[conn] = 0;
        try {
            // On-connect event burst. Snapshot under lock so a concurrent
            // SetOnConnectEvents can't mutate the list mid-iteration; DeepClone each
            // event so two concurrently-accepted connections never call ToJsonString on
            // the same JsonObject instance (System.Text.Json nodes aren't documented
            // safe for concurrent reads).
            JsonObject[] greeting;
            lock (_gate) {
                greeting = [.. _onConnectEvents.Select(e => (JsonObject)e.DeepClone())];
            }
            foreach (var e in greeting) {
                await WriteAsync(conn, Frame(e), _cts.Token).ConfigureAwait(false);
            }

            using var reader = new StreamReader(stream, Encoding.ASCII);
            string? line;
            while ((line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false)) is not null) {
                if (line.Length == 0) {
                    continue;
                }
                await HandleRequestAsync(conn, line).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            // disposing
        } catch (IOException) {
            // client closed the connection — normal for the per-RPC connections
        } finally {
            // Deliberately do NOT dispose conn.WriteLock: BroadcastAsync enumerates the
            // live _connections, so it can hold a Connection that's being removed here.
            // A SemaphoreSlim used only for Wait/Release (never AvailableWaitHandle) needs
            // no disposal, so leaving it intact removes the WaitAsync→ObjectDisposed race
            // entirely — a write to the now-closed stream just fails inside WriteAsync's
            // own guarded try.
            _connections.TryRemove(conn, out _);
        }
    }

    private async Task HandleRequestAsync(Connection conn, string line) {
        JsonObject request;
        try {
            if (JsonNode.Parse(line) is not JsonObject obj) {
                return;
            }
            request = obj;
        } catch (JsonException) {
            return; // ignore non-JSON noise
        }

        var method = (string?)request["method"];
        if (method is null) {
            return; // not an RPC request (could be an echoed event)
        }
        var id = request["id"];

        Func<JsonObject, JsonNode?>? factory;
        lock (_gate) {
            _receivedMethods.Add(method);
            _rpcResults.TryGetValue(method, out factory);
        }

        // PHD2 returns integer 0 from most successful calls; getters are overridden via OnRpc.
        JsonObject response;
        try {
            JsonNode? result = factory is null ? 0 : factory(request);
            response = new JsonObject {
                ["jsonrpc"] = "2.0",
                ["result"] = result,
                ["id"] = id?.DeepClone(),
            };
        }
#pragma warning disable CA1031 // a user-supplied OnRpc factory may throw anything
        catch (Exception ex) {
#pragma warning restore CA1031
            // Surface a factory failure as a JSON-RPC error rather than letting it fault
            // the connection (which the client would see as a confusing dropped socket).
            response = new JsonObject {
                ["jsonrpc"] = "2.0",
                ["error"] = new JsonObject { ["code"] = -1, ["message"] = ex.Message },
                ["id"] = id?.DeepClone(),
            };
        }
        await WriteAsync(conn, Frame(response), _cts.Token).ConfigureAwait(false);
    }

    private static async Task WriteAsync(Connection conn, byte[] line, CancellationToken cancellationToken) {
        try {
            await conn.WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return; // disposing — don't even attempt the write (and nothing to release)
        }
        try {
            await conn.Stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await conn.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        } catch (IOException) {
            // peer gone — drop
        } catch (ObjectDisposedException) {
            // connection torn down concurrently
        } catch (OperationCanceledException) {
            // disposed mid-write
        } finally {
            conn.WriteLock.Release();
        }
    }

    // ASCII (not UTF-8) on purpose: the real PHD2Guider.RunListener decodes the event
    // stream with Encoding.ASCII, so the fake mirrors that — non-ASCII would corrupt on
    // the real client too, and matching keeps the fake faithful rather than more
    // permissive. The ARA listener splits on Environment.NewLine, so frame with it.
    private static byte[] Frame(JsonObject message) =>
        Encoding.ASCII.GetBytes(message.ToJsonString() + Environment.NewLine);

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try {
            await _acceptLoop.ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // expected
        }
        // Bounded drain: handlers respond to _cts, but a user-supplied OnRpc factory
        // could block uncancellably — cap the wait so a stuck handler makes teardown
        // diagnosable rather than hanging the test forever.
        var drain = Task.WhenAll(_inFlight.Keys).ContinueWith(static _ => { }, TaskScheduler.Default);
        if (await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false) != drain) {
            System.Diagnostics.Trace.TraceWarning("FakeGuider dispose: in-flight handler drain timed out after 10s.");
        }
        _listener.Dispose();
        _cts.Dispose();
    }
}
