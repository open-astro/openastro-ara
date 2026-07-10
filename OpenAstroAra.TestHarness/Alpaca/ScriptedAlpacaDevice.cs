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
using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.TestHarness.Alpaca;

/// <summary>
/// A minimal loopback Alpaca device whose per-property GET answers are scripted: the responder
/// receives the lower-cased request path (e.g. <c>/api/v1/telescope/0/tracking</c>) and returns
/// the JSON literal to put in the Alpaca envelope's <c>Value</c> (e.g. <c>"true"</c>,
/// <c>"false"</c>, <c>"3.5"</c>), or null for the default <c>true</c>. Every PUT answers success.
/// Swap the responder at runtime (<see cref="Respond"/>) to script a state change mid-test —
/// e.g. a mount silently dropping <c>Tracking</c>. Type-mismatched reads (a bool where the
/// client expects an int) throw client-side and fall back to that field's default, exactly like
/// the fixed-value stub the §42.3 disconnect E2E uses.
/// </summary>
public sealed class ScriptedAlpacaDevice : IAsyncDisposable {

    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private volatile Func<string, string?>? _responder;

    private ScriptedAlpacaDevice(HttpListener listener, int port, Func<string, string?>? responder) {
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener = listener;
        _responder = responder;
        _loop = Task.Run(LoopAsync);
    }

    public Uri BaseUri { get; }

    public static ScriptedAlpacaDevice Start(Func<string, string?>? responder = null) {
        var (listener, port) = LoopbackListener.Bind();
        return new ScriptedAlpacaDevice(listener, port, responder);
    }

    /// <summary>Replace the responder (thread-safe) — subsequent GETs answer with the new script.</summary>
    public void Respond(Func<string, string?> responder) => _responder = responder;

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "Not a round-trip normalization: Alpaca URL paths are lower-case on the wire, and the responder contract documents receiving the lower-cased path — upper-casing would fight the ecosystem's own convention.")]
    private async Task LoopAsync() {
        while (!_cts.IsCancellationRequested) {
            HttpListenerContext ctx;
            try {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            } catch (HttpListenerException) {
                return;
            } catch (ObjectDisposedException) {
                return;
            }
            var value = "true";
            if (ctx.Request.HttpMethod == "GET") {
                var scripted = _responder?.Invoke(ctx.Request.Url?.AbsolutePath.ToLowerInvariant() ?? "");
                if (scripted is not null) {
                    value = scripted;
                }
            }
            var body = Encoding.UTF8.GetBytes(
                $$"""{"Value":{{value}},"ClientTransactionID":0,"ServerTransactionID":0,"ErrorNumber":0,"ErrorMessage":""}""");
            try {
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
                ctx.Response.Close();
            } catch (HttpListenerException) {
                // client went away mid-write — irrelevant to the test
            } catch (ObjectDisposedException) {
                // listener torn down mid-write — irrelevant to the test
            }
        }
    }

    public async ValueTask DisposeAsync() {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        _listener.Close();
        try {
            await _loop.ConfigureAwait(false);
        } catch (HttpListenerException) {
        } catch (ObjectDisposedException) {
        }
        _cts.Dispose();
    }
}
