#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts.WsEvents;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §29 — notices a storage drive being plugged or pulled and tells live
/// clients. Polls two cheap kernel views every 5 s (no exec, no privilege):
/// the block-device names under <c>/sys/block</c> and whether the ARA store
/// mount is present in <c>/proc/self/mounts</c>. On any change it broadcasts
/// <c>storage.devices_changed</c> — clients re-fetch their device/space
/// state, so an unplugged drive shows as gone within seconds instead of
/// whenever something happened to refresh.
/// </summary>
public sealed partial class StorageDeviceWatcher : BackgroundService {

    private const string MountPoint = "/media/openastroara";
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly IWsBroadcaster? _ws;
    private readonly ILogger<StorageDeviceWatcher> _logger;
    private readonly TimeSpan _interval;
    private string? _lastSnapshot;

    public StorageDeviceWatcher(
        IWsBroadcaster? ws,
        ILogger<StorageDeviceWatcher>? logger = null,
        TimeSpan? interval = null) {
        _ws = ws;
        _logger = logger ?? NullLogger<StorageDeviceWatcher>.Instance;
        _interval = interval ?? DefaultInterval;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Watch-loop boundary: any escaping exception ends the BackgroundService for the process lifetime, silently killing live device watch. Every tick failure (incl. WS publish) is logged and the next tick retries. CA1031's log-and-recover boundary applies.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (!OperatingSystem.IsLinux() || _ws is null) {
            return; // nothing to watch on dev machines; nothing to tell without a bus
        }
        // Seed silently: startup is not a change. Inside the same safety
        // net as the loop — .NET's default BackgroundServiceExceptionBehavior
        // is StopHost, so an unguarded throw here could take the daemon down.
        try {
            _lastSnapshot = TrySnapshot();
        } catch (Exception ex) {
            LogSnapshotFailed(ex);
        }
        using var timer = new PeriodicTimer(_interval);
        while (!stoppingToken.IsCancellationRequested) {
            try {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
                    break;
                }
                var snapshot = TrySnapshot();
                if (snapshot is not null && _lastSnapshot is not null && snapshot != _lastSnapshot) {
                    LogDevicesChanged(_lastSnapshot, snapshot);
                    using var doc = JsonDocument.Parse("{}");
                    await _ws.PublishAsync(WsEventCatalog.StorageDevicesChanged, doc.RootElement.Clone(), stoppingToken)
                        .ConfigureAwait(false);
                }
                if (snapshot is not null) {
                    _lastSnapshot = snapshot;
                }
            } catch (OperationCanceledException) {
                break;
            } catch (Exception ex) {
                // Log-and-continue boundary: ANY escape here would end the
                // BackgroundService for the rest of the process — silently
                // killing live device watch. A failed tick (WS publish
                // included) is worth a log line, never the watcher.
                LogSnapshotFailed(ex);
            }
        }
    }

    /// <summary>Block-device names + store-mount presence as one comparable
    /// string; null when the kernel views are momentarily unreadable.</summary>
    private static string? TrySnapshot() {
        try {
            var blocks = Directory.GetDirectories("/sys/block")
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .OrderBy(n => n, StringComparer.Ordinal);
            var mounted = File.ReadLines("/proc/self/mounts")
                .Any(l => l.Split(' ') is { Length: > 1 } parts && parts[1] == MountPoint);
            return string.Join(',', blocks) + "|store=" + (mounted ? "1" : "0");
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Storage devices changed: {Before} -> {After}.")]
    private partial void LogDevicesChanged(string before, string after);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Storage device watch tick failed; will retry next tick.")]
    private partial void LogSnapshotFailed(Exception ex);
}
