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
using PierSide = OpenAstroAra.Core.Enums.PierSide;

namespace OpenAstroAra.Server.Services;

/// <summary>#2173 — the last trusted plate solve's position angle per mount, camera and rotator angle, with the pier side
/// the mount was trusted on when it was taken. An equatorial mount keeps a camera's position angle as RA turns, and a pier
/// flip turns it by 180°, so the plate-solve check reads the other pier side from it.</summary>
public interface IPoseSolvePaStore {
    /// <summary>The stored angle and side for <paramref name="key"/>, or null when none.</summary>
    (double PositionAngleDeg, PierSide SideOfPier)? Lookup(string key);

    /// <summary>Store one (best-effort).</summary>
    void Store(string key, double positionAngleDeg, PierSide sideOfPier);
}

/// <summary>In memory only (tests, and a daemon with no profile directory).</summary>
public sealed class MemoryPoseSolvePaStore : IPoseSolvePaStore {
    private readonly Dictionary<string, (double, PierSide)> _entries = new(StringComparer.Ordinal);

    public (double PositionAngleDeg, PierSide SideOfPier)? Lookup(string key) {
        lock (_entries) {
            return _entries.TryGetValue(key, out var e) ? e : null;
        }
    }

    public void Store(string key, double positionAngleDeg, PierSide sideOfPier) {
        lock (_entries) {
            _entries[key] = (positionAngleDeg, sideOfPier);
        }
    }
}

/// <summary>JSON file next to the profiles (<c>{profileDir}/pose-solve-pa.json</c>), loaded once and written whole on
/// each change: a power loss that restarts the daemon keeps the angle the next check compares with.</summary>
public sealed partial class FilePoseSolvePaStore : IPoseSolvePaStore {
    public const string FileName = "pose-solve-pa.json";

    private readonly string _path;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, (double PositionAngleDeg, PierSide SideOfPier)> _entries;

    public FilePoseSolvePaStore(string profileDir, ILogger<FilePoseSolvePaStore>? logger = null) {
        ArgumentException.ThrowIfNullOrEmpty(profileDir);
        _path = Path.Combine(profileDir, FileName);
        _logger = logger;
        _entries = Load();
    }

    public (double PositionAngleDeg, PierSide SideOfPier)? Lookup(string key) {
        lock (_gate) {
            return _entries.TryGetValue(key, out var e) ? e : null;
        }
    }

    public void Store(string key, double positionAngleDeg, PierSide sideOfPier) {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate) {
            _entries[key] = (positionAngleDeg, sideOfPier);
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var file = new PoseSolvePaFile(_entries
                    .Select(kv => new PoseSolvePaEntry(kv.Key, kv.Value.PositionAngleDeg, SideName(kv.Value.SideOfPier))).ToList());
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(file, AraJsonSerializerContext.Default.PoseSolvePaFile));
                File.Move(tmp, _path, overwrite: true);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                if (_logger is not null) LogWriteFailed(_logger, ex, _path);
            }
        }
    }

    private Dictionary<string, (double, PierSide)> Load() {
        var entries = new Dictionary<string, (double, PierSide)>(StringComparer.Ordinal);
        if (!File.Exists(_path)) {
            return entries;
        }
        try {
            var file = JsonSerializer.Deserialize(File.ReadAllText(_path), AraJsonSerializerContext.Default.PoseSolvePaFile);
            foreach (var e in file?.Entries ?? []) {
                if (!string.IsNullOrEmpty(e.Key) && double.IsFinite(e.PositionAngleDeg) && ParseSide(e.SideOfPier) is PierSide side) {
                    entries[e.Key] = (e.PositionAngleDeg, side);
                }
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            if (_logger is not null) LogUnreadable(_logger, ex, _path);
        }
        return entries;
    }

    private static string SideName(PierSide side) => side == PierSide.pierEast ? "east" : "west";

    private static PierSide? ParseSide(string? side) => side switch {
        "east" => PierSide.pierEast,
        "west" => PierSide.pierWest,
        _ => null,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "#2173 the plate-solve position-angle file at {Path} is unreadable; treating it as empty")]
    private static partial void LogUnreadable(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "#2173 could not write the plate-solve position-angle file at {Path}; the angle is kept until the daemon restarts")]
    private static partial void LogWriteFailed(ILogger logger, Exception ex, string path);
}
