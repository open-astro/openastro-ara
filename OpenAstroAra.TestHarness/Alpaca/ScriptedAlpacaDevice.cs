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
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.TestHarness.Alpaca;

/// <summary>
/// A minimal loopback Alpaca device whose per-property GET answers are scripted: the responder
/// receives the lower-cased request path (e.g. <c>/api/v1/telescope/0/tracking</c>) and returns
/// the JSON literal to put in the Alpaca envelope's <c>Value</c> (e.g. <c>"true"</c>,
/// <c>"false"</c>, <c>"3.5"</c>), or null for the default <c>true</c>. A value of the form
/// <c>"!error:&lt;message&gt;"</c> answers an Alpaca DRIVER error envelope instead (ErrorNumber
/// 0x500, the message verbatim), which the client raises as a DriverException — how a bridge
/// reports a latched mount fault (#1193). Every PUT answers success unless a <c>putResponder</c>
/// scripts that path (same contract: a JSON literal for <c>Value</c>, or the error forms above),
/// which is how a test makes a command — not just a read — fail at the device.
/// Swap the responder at runtime (<see cref="Respond"/>) to script a state change mid-test —
/// e.g. a mount silently dropping <c>Tracking</c>. Type-mismatched reads (a bool where the
/// client expects an int) throw client-side and fall back to that field's default, exactly like
/// the fixed-value stub the §42.3 disconnect E2E uses.
/// </summary>
public sealed class ScriptedAlpacaDevice : IAsyncDisposable {

    /// <summary>Script-value prefix that turns the answer into a driver error envelope.</summary>
    public const string ErrorPrefix = "!error:";
    /// <summary>Script-value prefix for ASCOM's not-implemented error (0x400): how a driver
    /// declines an optional property for good (the client raises NotImplementedException).</summary>
    public const string NotImplementedPrefix = "!notimpl:";

    /// <summary>The script value for a driver error carrying <paramref name="message"/>.</summary>
    public static string Error(string message) => ErrorPrefix + message;

    /// <summary>The script value for a not-implemented answer carrying <paramref name="message"/>.</summary>
    public static string NotImplemented(string message) => NotImplementedPrefix + message;

    /// <summary>Every GET path answered, in order (lower-cased, like the responder sees it).</summary>
    public ConcurrentQueue<string> Gets { get; } = new();

    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private volatile Func<string, string?>? _responder;
    private readonly Func<string, string?>? _putResponder;

    /// <summary>Every PUT the device received, as (lower-cased path, form body) — for tests that
    /// assert a write reached the device (the scripted GETs never reflect a write).</summary>
    public ConcurrentQueue<(string Path, string Body)> Puts { get; } = new();

    private ScriptedAlpacaDevice(HttpListener listener, int port, Func<string, string?>? responder, Func<string, string?>? putResponder) {
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener = listener;
        _responder = responder;
        _putResponder = putResponder;
        _loop = Task.Run(LoopAsync);
    }

    public Uri BaseUri { get; }

    public static ScriptedAlpacaDevice Start(Func<string, string?>? responder = null, Func<string, string?>? putResponder = null) {
        var (listener, port) = LoopbackListener.Bind();
        return new ScriptedAlpacaDevice(listener, port, responder, putResponder);
    }

    /// <summary>Replace the responder (thread-safe) — subsequent GETs answer with the new script.</summary>
    public void Respond(Func<string, string?> responder) => _responder = responder;

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "Not a round-trip normalization: Alpaca URL paths are lower-case on the wire, and the responder contract documents receiving the lower-cased path — upper-casing would fight the ecosystem's own convention.")]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Test-harness serve loop: a client aborting mid-PUT must never fault the loop and silently stop the scripted device for the rest of the test.")]
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
                Gets.Enqueue(ctx.Request.Url?.AbsolutePath.ToLowerInvariant() ?? "");
                var scripted = _responder?.Invoke(ctx.Request.Url?.AbsolutePath.ToLowerInvariant() ?? "");
                if (scripted is not null) {
                    value = scripted;
                }
            } else if (ctx.Request.HttpMethod == "PUT") {
                var putPath = ctx.Request.Url?.AbsolutePath.ToLowerInvariant() ?? "";
                var scripted = _putResponder?.Invoke(putPath);
                if (scripted is not null) {
                    value = scripted;
                }
                try {
                    using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                    Puts.Enqueue((putPath, await reader.ReadToEndAsync().ConfigureAwait(false)));
                } catch (Exception) {
                    // client aborted mid-PUT (HttpListenerException, an IOException from a half-read
                    // body, ObjectDisposedException) — irrelevant to the test; the loop must keep
                    // serving, or the device silently stops answering for the rest of the test.
                }
            }
            var body = value.StartsWith(ErrorPrefix, StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes(
                    $$"""{"Value":null,"ClientTransactionID":0,"ServerTransactionID":0,"ErrorNumber":1280,"ErrorMessage":{{JsonSerializer.Serialize(value[ErrorPrefix.Length..])}}}""")
                : value.StartsWith(NotImplementedPrefix, StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes(
                    $$"""{"Value":null,"ClientTransactionID":0,"ServerTransactionID":0,"ErrorNumber":1024,"ErrorMessage":{{JsonSerializer.Serialize(value[NotImplementedPrefix.Length..])}}}""")
                : Encoding.UTF8.GetBytes(
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
