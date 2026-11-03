#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>What <see cref="RigVersions.CollectAsync"/> found.</summary>
public sealed record RigVersionsResult(string? BoardModel, string? GuiderVersion,
    IReadOnlyList<AlpacaServerVersionDto> AlpacaServers, string? AstapVersion);

/// <summary>The versions on the rig beyond the daemon's own, for <c>GET /server/versions</c>: the board,
/// the guider, each Alpaca server the devices are on (AlpacaBridge) and ASTAP. Every part is best-effort:
/// one that can't be read is null (or left out), never an error.</summary>
public sealed partial class RigVersions {
    private static readonly TimeSpan AstapTimeout = TimeSpan.FromSeconds(3);
    // The solver binary doesn't change while the daemon runs; once a path answers, keep its version.
    // A miss (not installed yet, or slow on a cold start) is asked again next time.
    private static readonly ConcurrentDictionary<string, string> AstapCache = new(StringComparer.Ordinal);

    private readonly IGuiderService? _guider;
    private readonly IEquipmentSelectionStore? _selections;
    private readonly IAlpacaManagementClient? _alpaca;
    private readonly Func<string?>? _astapPath;
    private readonly Func<string?> _boardModel;
    private readonly Func<string, CancellationToken, Task<string?>> _astapVersion;

    public RigVersions(IGuiderService? guider, IEquipmentSelectionStore? selections, IAlpacaManagementClient? alpaca,
            Func<string?>? astapPath,
            Func<string?>? boardModel = null,
            Func<string, CancellationToken, Task<string?>>? astapVersion = null) {
        _guider = guider;
        _selections = selections;
        _alpaca = alpaca;
        _astapPath = astapPath;
        _boardModel = boardModel ?? (() => ReadBoardModel("/proc/device-tree/model"));
        _astapVersion = astapVersion ?? ((path, _) => CachedAstapVersionAsync(path));
    }

    public async Task<RigVersionsResult> CollectAsync(CancellationToken ct) {
        var servers = AlpacaServersAsync(ct);
        var astapPath = _astapPath?.Invoke();
        var astap = string.IsNullOrWhiteSpace(astapPath) ? Task.FromResult<string?>(null) : _astapVersion(astapPath, ct);
        return new RigVersionsResult(_boardModel(), _guider?.GuiderVersion, await servers.ConfigureAwait(false),
            await astap.ConfigureAwait(false));
    }

    /// <summary>Each distinct Alpaca server among the remembered devices, asked in parallel (one slow
    /// server costs at most its 3 s timeout, not one per device).</summary>
    private async Task<IReadOnlyList<AlpacaServerVersionDto>> AlpacaServersAsync(CancellationToken ct) {
        if (_selections is null || _alpaca is null) {
            return Array.Empty<AlpacaServerVersionDto>();
        }
        var devices = await _selections.GetAllAsync(ct).ConfigureAwait(false);
        // The same host rule the identity reconciler uses: the IP when known, else the name.
        var endpoints = devices
            .Select(d => (Host: string.IsNullOrWhiteSpace(d.IpAddress) ? d.HostName : d.IpAddress, Port: d.IpPort))
            .Where(e => !string.IsNullOrWhiteSpace(e.Host) && e.Port is > 0 and <= 65535)
            .Distinct()
            .ToList();
        var answers = await Task.WhenAll(endpoints.Select(async e => {
            var d = await DescribeAsync(e.Host, e.Port, ct).ConfigureAwait(false);
            return new AlpacaServerVersionDto(e.Host, e.Port, d?.ServerName, d?.Manufacturer, d?.Version);
        })).ConfigureAwait(false);
        return answers;
    }

    private async Task<AlpacaServerDescription?> DescribeAsync(string host, int port, CancellationToken ct) {
        try {
            return await _alpaca!.GetDescriptionAsync(host, port, ct).ConfigureAwait(false);
        } catch (ArgumentException) {
            return null; // a remembered host that isn't a valid name: listed, without a version
        }
    }

    /// <summary>The board's name from the device tree ("Raspberry Pi 5 Model B Rev 1.1"), or null when the
    /// file isn't there (macOS, a container).</summary>
    internal static string? ReadBoardModel(string path) {
        try {
            var model = File.ReadAllText(path).TrimEnd('\0', '\n', ' ');
            return model.Length == 0 ? null : model;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>The solver path to probe for an ASTAP version: null unless the profile's engine is ASTAP,
    /// so another solver (solve-field…) is never started with -h.</summary>
    internal static string? AstapPathOf(PlateSolveSettingsDto settings) =>
        string.Equals(settings.Engine, "astap", StringComparison.OrdinalIgnoreCase) ? settings.PathOrEndpoint?.Trim() : null;

    internal static async Task<string?> CachedAstapVersionAsync(string path) {
        if (AstapCache.TryGetValue(path, out var known)) {
            return known;
        }
        var version = await RunAstapVersionAsync(path).ConfigureAwait(false);
        if (version is not null) {
            AstapCache[path] = version;
        }
        return version;
    }

    /// <summary>The version from ASTAP's banner ("ASTAP astrometric solver version CLI-2026.09.01").</summary>
    internal static string? ParseAstapVersion(string output) {
        var m = AstapBanner().Match(output);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"ASTAP[^\n]*?version\s+(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex AstapBanner();

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort probe: a missing, broken or hanging solver just has no version to show.")]
    private static async Task<string?> RunAstapVersionAsync(string path) {
        if (!File.Exists(path)) {
            return null;
        }
        try {
            using var process = new Process {
                StartInfo = new ProcessStartInfo(path, "-h") {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            process.Start();
            using var timeout = new CancellationTokenSource(AstapTimeout);
            try {
                // Drain stderr too: a full pipe would stall the solver into the timeout.
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return ParseAstapVersion(output);
            } finally {
                if (!process.HasExited) {
                    process.Kill(entireProcessTree: true);
                }
            }
        } catch (Exception) {
            return null;
        }
    }
}
