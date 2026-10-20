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
/// §63.4 — guider-twin deletes that could not run when the ARA profile was deleted (no guider
/// connected, or the guider was guiding / calibrating and must not lose its equipment). Keyed by the
/// deleted ARA profile's id: a second delete of the same profile replaces the entry. Drained by
/// <see cref="GuiderService"/> on the next guider connect (before the twin ensure) and whenever the
/// connected guider is seen idle.
/// </summary>
public interface IGuiderTwinDeleteStore {
    IReadOnlyList<PendingGuiderTwinDelete> List();
    void Add(PendingGuiderTwinDelete entry);
    void Remove(Guid araProfileId);
}

/// <summary>JSON file next to the profiles (<c>{profileDir}/guider-twin-deletes.json</c>), written whole
/// on every change; a missing or unreadable file is an empty queue.</summary>
public sealed partial class FileGuiderTwinDeleteStore : IGuiderTwinDeleteStore {
    public const string FileName = "guider-twin-deletes.json";

    private readonly string _path;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    // Loaded once, written through: the ping tick asks every few seconds and must not touch the disk.
    private List<PendingGuiderTwinDelete>? _cache;

    public FileGuiderTwinDeleteStore(string profileDir, ILogger<FileGuiderTwinDeleteStore>? logger = null) {
        ArgumentException.ThrowIfNullOrEmpty(profileDir);
        _path = Path.Combine(profileDir, FileName);
        _logger = logger;
    }

    public IReadOnlyList<PendingGuiderTwinDelete> List() {
        lock (_gate) {
            return Load().ToArray();
        }
    }

    public void Add(PendingGuiderTwinDelete entry) {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate) {
            var all = Load().Where(e => e.AraProfileId != entry.AraProfileId).Append(entry).ToList();
            Save(all);
        }
    }

    public void Remove(Guid araProfileId) {
        lock (_gate) {
            var all = Load();
            var kept = all.Where(e => e.AraProfileId != araProfileId).ToList();
            if (kept.Count != all.Count) {
                Save(kept);
            }
        }
    }

    // Caller holds _gate.
    private List<PendingGuiderTwinDelete> Load() {
        if (_cache is not null) {
            return _cache;
        }
        if (!File.Exists(_path)) {
            return _cache = new List<PendingGuiderTwinDelete>();
        }
        try {
            var json = File.ReadAllText(_path);
            var file = JsonSerializer.Deserialize(json, AraJsonSerializerContext.Default.PendingGuiderTwinDeleteFile);
            return _cache = file?.Pending?.ToList() ?? new List<PendingGuiderTwinDelete>();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            if (_logger is not null) LogUnreadable(_logger, ex, _path);
            return _cache = new List<PendingGuiderTwinDelete>();
        }
    }

    // Caller holds _gate.
    private void Save(List<PendingGuiderTwinDelete> all) {
        _cache = all;
        if (all.Count == 0) {
            try {
                File.Delete(_path);
            } catch (IOException ex) {
                if (_logger is not null) LogRemoveFailed(_logger, ex, _path);
            }
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(new PendingGuiderTwinDeleteFile(all), AraJsonSerializerContext.Default.PendingGuiderTwinDeleteFile);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    [LoggerMessage(EventId = 6372, Level = LogLevel.Warning, Message = "§63.4 pending guider-twin delete queue at {Path} is unreadable; treating it as empty")]
    private static partial void LogUnreadable(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 6373, Level = LogLevel.Warning, Message = "§63.4 could not remove the empty guider-twin delete queue at {Path}")]
    private static partial void LogRemoveFailed(ILogger logger, Exception ex, string path);
}
