#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #2070 — the mounts whose position is unverified, by device id, kept across daemon restarts: a
/// restart must not turn a mount at a false zero back into a trusted one (see <see cref="MountPoseTrust"/>).
/// </summary>
public interface IMountPoseStore {
    /// <summary>The stored reason for <paramref name="deviceId"/>, or null when it is trusted. Reads memory only.</summary>
    string? UnverifiedReason(string deviceId);

    /// <summary>Store <paramref name="reason"/> for <paramref name="deviceId"/> (null removes it). Best-effort.</summary>
    void Store(string deviceId, string? reason);
}

/// <summary>JSON file next to the profiles (<c>{profileDir}/mount-pose.json</c>), written whole on every
/// change and loaded once at boot; a missing or unreadable file means every mount starts trusted on its
/// stored state (the first read of each session still decides).</summary>
public sealed partial class FileMountPoseStore : IMountPoseStore {
    public const string FileName = "mount-pose.json";

    private readonly string _path;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _unverified;

    public FileMountPoseStore(string profileDir, ILogger<FileMountPoseStore>? logger = null) {
        ArgumentException.ThrowIfNullOrEmpty(profileDir);
        _path = Path.Combine(profileDir, FileName);
        _logger = logger;
        _unverified = Load();
    }

    public string? UnverifiedReason(string deviceId) {
        lock (_gate) {
            return _unverified.TryGetValue(deviceId, out var reason) ? reason : null;
        }
    }

    public void Store(string deviceId, string? reason) {
        ArgumentNullException.ThrowIfNull(deviceId);
        lock (_gate) {
            var known = _unverified.TryGetValue(deviceId, out var current);
            if (reason is null ? !known : known && current == reason) {
                return;
            }
            if (reason is null) {
                _unverified.Remove(deviceId);
            } else {
                _unverified[deviceId] = reason;
            }
            try {
                Save();
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                if (_logger is not null) LogWriteFailed(_logger, ex, _path);
            }
        }
    }

    private Dictionary<string, string> Load() {
        if (!File.Exists(_path)) {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        try {
            var file = JsonSerializer.Deserialize(File.ReadAllText(_path), AraJsonSerializerContext.Default.MountPoseFile);
            return (file?.Unverified ?? [])
                .Where(e => !string.IsNullOrEmpty(e.DeviceId) && !string.IsNullOrEmpty(e.Reason))
                .GroupBy(e => e.DeviceId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Last().Reason, StringComparer.Ordinal);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            if (_logger is not null) LogUnreadable(_logger, ex, _path);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    // Caller holds _gate.
    private void Save() {
        if (_unverified.Count == 0) {
            File.Delete(_path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var file = new MountPoseFile(_unverified.Select(kv => new MountPoseEntry(kv.Key, kv.Value)).ToList());
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, AraJsonSerializerContext.Default.MountPoseFile));
        File.Move(tmp, _path, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "#2070 the unverified-mount file at {Path} is unreadable; treating it as empty")]
    private static partial void LogUnreadable(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "#2070 could not write the unverified-mount file at {Path}; the flag holds until the daemon restarts")]
    private static partial void LogWriteFailed(ILogger logger, Exception ex, string path);
}
