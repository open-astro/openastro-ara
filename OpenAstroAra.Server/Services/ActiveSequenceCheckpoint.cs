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
using System.Text.Json;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Writes <c>{profileDir}/sequences/active/current.json</c> per §28.1
/// sequence-checkpoint convention + §38.2 storage layout. The file is
/// the canonical "is a sequence running" signal — UI reads it without
/// polling the in-memory run-state dict, and the §28.2 recovery routine
/// on daemon startup uses it to decide whether the previous run needs
/// to be reconciled.
///
/// Writes are atomic (temp + rename) so a crash mid-write leaves either
/// the old file or the new one intact, never a torn write. Per playbook
/// §28.1 corruption-quarantine: a parse-fail on startup renames the
/// existing file to <c>current.json.corrupt.&lt;unix-ts&gt;</c> rather than
/// blocking startup. That handling lands when the daemon-startup
/// reconciliation lands; this helper covers the write path only.
/// </summary>
public sealed class ActiveSequenceCheckpoint {

    public const string FileName = "current.json";

    private readonly string _activeDir;
    private readonly string _path;
    private readonly ILogger? _logger;
    private readonly object _lock = new();

    public ActiveSequenceCheckpoint(string profileDir, ILogger? logger = null) {
        _activeDir = System.IO.Path.Combine(profileDir, "sequences", FileSequenceService.ActiveDirName);
        _path = System.IO.Path.Combine(_activeDir, FileName);
        _logger = logger;
        try { Directory.CreateDirectory(_activeDir); } catch (Exception ex) {
            _logger?.LogWarning(ex, "Failed to create active sequences dir {Path}", _activeDir);
        }
    }

    /// <summary>Path of the canonical checkpoint file; exposed for tests + the §28.2 reader.</summary>
    public string FilePath => _path;

    /// <summary>
    /// Write the supplied run state to <c>active/current.json</c>. Atomic
    /// via temp + rename. Failures log but don't throw — the active-file
    /// is auxiliary; loss of it only affects recovery, not the live run.
    /// </summary>
    public void Write(SequenceRunStateDto state) {
        lock (_lock) {
            try {
                Directory.CreateDirectory(_activeDir);
                var tempPath = _path + ".tmp";
                var json = JsonSerializer.Serialize(state, AraJsonSerializerContext.Default.SequenceRunStateDto);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _path, overwrite: true);
            } catch (Exception ex) {
                _logger?.LogWarning(ex, "Failed to write active sequence checkpoint to {Path}", _path);
            }
        }
    }

    /// <summary>
    /// Remove the checkpoint file. Called on terminal lifecycle events
    /// (sequence.complete, sequence.stopped, sequence.aborted). Idempotent;
    /// missing-file is not an error.
    /// </summary>
    public void Clear() {
        lock (_lock) {
            try {
                if (File.Exists(_path)) File.Delete(_path);
            } catch (Exception ex) {
                _logger?.LogWarning(ex, "Failed to clear active sequence checkpoint at {Path}", _path);
            }
        }
    }

    /// <summary>Returns true if a checkpoint file exists (sequence was running).</summary>
    public bool Exists() => File.Exists(_path);

    /// <summary>
    /// Read + deserialize the checkpoint file. Returns null when the file
    /// is missing or the JSON is malformed. The §28.1 corruption quarantine
    /// (rename to .corrupt.&lt;ts&gt;) lives in the startup reconciler that
    /// calls this method; this getter just signals "couldn't read it" via
    /// the null return.
    /// </summary>
    public SequenceRunStateDto? TryRead() {
        if (!File.Exists(_path)) return null;
        try {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize(json, AraJsonSerializerContext.Default.SequenceRunStateDto);
        } catch (Exception ex) {
            _logger?.LogWarning(ex, "Failed to read active sequence checkpoint from {Path}", _path);
            return null;
        }
    }
}