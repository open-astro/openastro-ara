#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>§45 capture-fetch — retrieve a daemon-saved solver frame into a local path. The daemon
/// sandboxes <c>capture_single_frame</c> saves to its own directory (on ITS machine), so the polar-align
/// engine downloads each frame over the daemon's HTTP capture endpoint (guider#77,
/// <c>GET /api/capture/&lt;filename&gt;</c>) instead of assuming a shared filesystem. A seam so the
/// bench can serve frames without a real HTTP server.</summary>
public interface IPolarAlignFrameFetcher {
    /// <summary>Download the daemon-saved frame <paramref name="filename"/> from the guider at
    /// <paramref name="host"/> (RPC port <paramref name="rpcPort"/> — the HTTP endpoint derives
    /// from it) to <paramref name="destinationPath"/> (overwrite). Throws on any transport/HTTP
    /// failure — the caller counts it as one failed solve. The fetcher owns the URI derivation
    /// so benches with ephemeral fake-guider ports never build (possibly out-of-range) URIs.</summary>
    Task FetchAsync(string host, int rpcPort, string filename, string destinationPath, CancellationToken ct);
}

/// <summary>HttpClient-backed fetcher. One shared client (socket reuse across the adjust loop's
/// once-a-second frames); the timeout bounds a wedged daemon well under the capture timeout.</summary>
public sealed class HttpPolarAlignFrameFetcher : IPolarAlignFrameFetcher, IDisposable {

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task FetchAsync(string host, int rpcPort, string filename, string destinationPath, CancellationToken ct) {
        var source = OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PHD2Guider.CaptureFrameUri(host, rpcPort, filename);
        using var response = await _http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        // Write via a temp name + move so a torn download can never be handed to the solver as a
        // complete FITS (the solver treats a corrupt file as one failed solve, but a clean rename
        // makes the failure mode deterministic).
        var tmp = destinationPath + ".part";
        var moved = false;
        try {
            await using (var file = File.Create(tmp)) {
                await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
            }
            File.Move(tmp, destinationPath, overwrite: true);
            moved = true;
        } finally {
            // A cancelled/failed download must not litter the work dir with .part files
            // across the adjust loop's many retries.
            if (!moved) {
                try { File.Delete(tmp); } catch (IOException) { }
            }
        }
    }

    public void Dispose() => _http.Dispose();
}
