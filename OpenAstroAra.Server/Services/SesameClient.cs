#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace OpenAstroAra.Server.Services;

/// <summary>A Sesame answer: the object's main identifier, its other names, J2000 position, Simbad type and
/// V magnitude when given.</summary>
public sealed record SesameResult(string MainId, IReadOnlyList<string> Aliases, double RaDeg, double DecDeg,
    string? ObjectType, double? Magnitude);

/// <summary>Sesame could not be asked (no route, DNS, timeout, a non-2xx answer or an unparsable body). The
/// resolver reports <c>offline</c> for it; a clean "nothing found" is a null result, not this.</summary>
public sealed class SesameUnavailableException : Exception {
    public SesameUnavailableException() { }
    public SesameUnavailableException(string message) : base(message) { }
    public SesameUnavailableException(string message, Exception inner) : base(message, inner) { }
}

public interface ISesameClient {
    /// <summary>Resolve one name through CDS Sesame (Simbad, then NED and VizieR). Null when every resolver
    /// drew a blank; throws <see cref="SesameUnavailableException"/> when the service could not be reached.</summary>
    Task<SesameResult?> ResolveAsync(string name, CancellationToken ct);
}

/// <summary>
/// #1267 — the CDS Sesame name resolver (<c>https://cds.unistra.fr/cgi-bin/nph-sesame</c>), the one
/// internet call behind target search. Asked only after the offline index missed, from the daemon (the
/// client has no internet dependency; over the SBC's hotspot it works whenever the SBC has an uplink),
/// bounded at 5 s and 1 MB, HTTPS only, no redirects. The XML (<c>-ox</c>) output is parsed with DTDs
/// off. This research makes use of the SIMBAD database, operated at CDS, Strasbourg, France.
/// </summary>
public sealed partial class SesameClient : ISesameClient, IDisposable {

    /// <summary><c>-oxpIF</c>: XML output, the position as degrees, every identifier, the fluxes; <c>SNV</c>:
    /// Simbad, then NED, then VizieR.</summary>
    internal static readonly Uri Endpoint = new("https://cds.unistra.fr/cgi-bin/nph-sesame/-oxpIF/SNV");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private const int MaxBodyBytes = 1 << 20;
    /// <summary>Sesame's own limit is generous; a name longer than this is not a name.</summary>
    public const int MaxNameLength = 128;

    private readonly HttpClient _http;
    private readonly ILogger<SesameClient> _logger;

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The handler's ownership transfers to the HttpClient (disposeHandler: true), which Dispose() releases.")]
    public SesameClient(ILogger<SesameClient>? logger = null, HttpMessageHandler? handler = null) {
        _logger = logger ?? NullLogger<SesameClient>.Instance;
        _http = new HttpClient(handler ?? new SocketsHttpHandler {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }, disposeHandler: true) {
            Timeout = RequestTimeout,
            MaxResponseContentBufferSize = MaxBodyBytes,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenAstroAra/1.0 (+https://github.com/open-astro/openastro-ara)");
    }

    /// <summary>The request URL for a name (internal for the test).</summary>
    internal static Uri UrlFor(string name) => new(Endpoint + "?" + Uri.EscapeDataString(name.Trim()));

    public async Task<SesameResult?> ResolveAsync(string name, CancellationToken ct) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var url = UrlFor(name);
        string body;
        try {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) {
                LogHttpStatus((int)response.StatusCode);
                throw new SesameUnavailableException($"Sesame answered HTTP {(int)response.StatusCode}");
            }
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
            // The caller gave up: say so as a cancellation, whatever the transport threw meanwhile.
            ct.ThrowIfCancellationRequested();
            // A timeout surfaces as TaskCanceledException while the caller's token is still live.
            LogUnreachable(ex);
            throw new SesameUnavailableException("Sesame could not be reached", ex);
        }
        try {
            return Parse(body);
        } catch (XmlException ex) {
            LogUnparsable(ex);
            throw new SesameUnavailableException("Sesame answered something that is not its XML", ex);
        }
    }

    /// <summary>Parse a <c>-ox</c> body: the first <c>&lt;Resolver&gt;</c> that carries <c>jradeg</c> and
    /// <c>jdedeg</c> wins (Simbad first by the <c>SNV</c> order); none → null. Internal for the test.</summary>
    internal static SesameResult? Parse(string xml) {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var doc = XDocument.Load(reader);
        var resolvers = doc.Descendants("Resolver").ToList();
        foreach (var resolver in resolvers) {
            var ra = Number(Text(resolver, "jradeg"));
            var dec = Number(Text(resolver, "jdedeg"));
            if (ra is not { } r || dec is not { } d || d is < -90 or > 90) {
                continue;
            }
            var aliases = resolver.Elements("alias").Select(a => (a.Value ?? string.Empty).Trim()).Where(a => a.Length > 0).ToList();
            var main = Text(resolver, "oname");
            if (main.Length == 0) {
                main = aliases.Count > 0 ? aliases[0] : string.Empty;
            }
            if (main.Length == 0) {
                continue;
            }
            // <mag band="V"><v>10.32</v>…</mag> (the -F fluxes); V first, else the first band given.
            var mags = resolver.Elements("mag").ToList();
            var vBand = mags.FirstOrDefault(m => string.Equals((string?)m.Attribute("band"), "V", StringComparison.Ordinal)) ?? mags.FirstOrDefault();
            var mag = vBand is null ? null : Number(Text(vBand, "v"));
            var otype = Text(resolver, "otype");
            return new SesameResult(main,
                aliases.Where(a => !string.Equals(a, main, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray(),
                ((r % 360.0) + 360.0) % 360.0, d, otype.Length == 0 ? null : otype, mag);
        }
        // No resolver answered. "Nothing found" from at least one is a clean miss; anything else in <INFO>
        // (a Simbad timeout, a backend error) means the service did not do its job: unavailable, so the user
        // hears "needs internet / try again", not "no such object".
        if (resolvers.Count > 0 && !resolvers.Any(x => x.Elements("INFO").Any(i => (i.Value ?? string.Empty).Contains("othing found", StringComparison.Ordinal)))) {
            var info = resolvers.SelectMany(x => x.Elements("INFO")).Select(i => (i.Value ?? string.Empty).Trim()).FirstOrDefault(v => v.Length > 0);
            if (info is not null) {
                throw new SesameUnavailableException("Sesame reported a problem: " + info);
            }
        }
        return null;
    }

    private static string Text(XElement parent, string name) => (parent.Element(name)?.Value ?? string.Empty).Trim();

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    public void Dispose() => _http.Dispose();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sesame answered HTTP {Status} (#1267)")]
    private partial void LogHttpStatus(int status);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sesame unreachable (#1267)")]
    private partial void LogUnreachable(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sesame answered something that is not its XML (#1267)")]
    private partial void LogUnparsable(Exception ex);
}
