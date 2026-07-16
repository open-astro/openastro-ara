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
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §37 file-backed multi-profile repository. Each saved profile is a
/// <see cref="StoredProfileDto"/> at <c>{profileDir}/profiles/{id}.json</c>; the active
/// profile id is a plain-text pointer at <c>{profileDir}/profiles/active.id</c>.
///
/// The active profile's live settings still flow through <see cref="IProfileStore"/>
/// (the existing section GET/PUT surface + <c>profile.json</c> working copy). This
/// repository keeps the active profile's <c>{id}.json</c> in sync by mirroring the live
/// store on every change, and on <see cref="Select"/> it loads a saved profile's settings
/// into the live store.
///
/// Migration: on first construction, if no profiles exist yet, the current live store
/// snapshot (which the <see cref="FileProfileStore"/> already loaded from the legacy
/// single <c>profile.json</c>, or defaults) is seeded as the initial "Default" profile.
/// </summary>
public sealed partial class FileProfileRepository : IProfileRepository, IDisposable {
    private readonly object _lock = new();
    private readonly string _dir;
    private readonly string _activePtrPath;
    private readonly IProfileStore _liveStore;
    private readonly ILogger<FileProfileRepository> _logger;

    private readonly Dictionary<Guid, ProfileMetaDto> _metas = new();
    private Guid? _activeId;

    // Set while loading a saved profile into the live store (Select / seed), so the
    // per-section Changed notifications don't each rewrite the active file; the final
    // state is written once at the end. Reentrant-safe: the live store raises Changed
    // synchronously on the same thread that holds _lock here.
    // volatile: read in OnLiveChanged, which can run on a different thread than the writer if a
    // store ever violates the sync-dispatch contract — volatile guarantees the handler sees the
    // current values rather than a stale cached copy (so the gate + assert are trustworthy).
    private volatile bool _suppressMirror;
    // Managed thread id of an in-progress ApplyToLive (0 = none); lets OnLiveChanged assert the
    // IProfileStore.Changed sync-dispatch contract that _suppressMirror depends on.
    private volatile int _applyingThreadId;
    private int _disposed; // 0 = live, 1 = disposed; flipped via Interlocked so Dispose is single-shot

    private static readonly AraJsonSerializerContext _indented =
        new(new JsonSerializerOptions(AraJsonSerializerContext.Default.Options) {
            WriteIndented = true,
        });

    public FileProfileRepository(string profileDir, IProfileStore liveStore,
        ILogger<FileProfileRepository>? logger = null) {
        ArgumentNullException.ThrowIfNull(profileDir);
        ArgumentNullException.ThrowIfNull(liveStore);
        _dir = Path.Combine(profileDir, "profiles");
        _activePtrPath = Path.Combine(_dir, "active.id");
        _liveStore = liveStore;
        _logger = logger ?? NullLogger<FileProfileRepository>.Instance;

        Directory.CreateDirectory(_dir);
        Load();
        // Subscribe AFTER load+seed so seeding doesn't recurse through the mirror.
        _liveStore.Changed += OnLiveChanged;
    }

    public Guid? ActiveId { get { lock (_lock) { return _activeId; } } }

    public ProfileListDto List() {
        lock (_lock) {
            // Newest-first is the friendlier default for a "known profiles" list.
            var ordered = _metas.Values
                .OrderByDescending(m => m.CreatedUtc)
                .ToList();
            return new ProfileListDto(_activeId, ordered);
        }
    }

    public StoredProfileDto? GetProfile(Guid id) {
        // Only the membership check needs the lock; the disk read is done outside it so a
        // GET doesn't block other repository operations for the duration of the I/O. Reads
        // are race-safe: files are written atomically (tmp + Move), and a concurrent delete
        // just yields a null read, which ReadFile already handles.
        bool known;
        lock (_lock) { known = _metas.ContainsKey(id); }
        return known ? ReadFile(id) : null;
    }

    public ProfileMetaDto Create(string name, ProfileSnapshotDto? settings, bool makeActive) {
        lock (_lock) {
            var now = DateTimeOffset.UtcNow;
            var meta = new ProfileMetaDto(Guid.NewGuid(), NormalizeName(name), now, now);
            var snap = settings ?? ProfileStoreSnapshot.Capture(_liveStore);
            if (!WriteFile(new StoredProfileDto(meta, snap))) {
                // Don't register a profile we couldn't persist — it would vanish on restart.
                throw new IOException($"Failed to write profile file for '{meta.Name}'.");
            }
            _metas[meta.Id] = meta;

            // The very first profile must be active; otherwise honor the caller.
            if (makeActive || _activeId is null) {
                ApplyToLive(meta.Id, snap);
                _activeId = meta.Id;
                PersistActivePointer();
            }
            return meta;
        }
    }

    public bool Rename(Guid id, string name) {
        lock (_lock) {
            if (!_metas.TryGetValue(id, out var meta)) return false;
            var stored = ReadFile(id);
            if (stored is null) return false;
            var updated = meta with { Name = NormalizeName(name), UpdatedUtc = DateTimeOffset.UtcNow };
            if (!WriteFile(stored with { Meta = updated })) return false;
            _metas[id] = updated;
            return true;
        }
    }

    public ProfileDeleteResult Delete(Guid id) {
        lock (_lock) {
            if (!_metas.ContainsKey(id)) return ProfileDeleteResult.NotFound;
            TryDeleteFile(id);
            _metas.Remove(id);
            if (_activeId != id) return ProfileDeleteResult.Deleted;

            // The active profile was deleted. Mirror the boot-time recovery
            // order (LoadAll): fall back to the newest remaining profile, or —
            // when this was the last one — seed a factory-defaults "Default",
            // exactly what a fresh install gets. The daemon never runs without
            // an active profile, so deleting the last one means "start over",
            // not "run headless on ghost settings".
            var fallback = _metas.Values.OrderByDescending(m => m.CreatedUtc).FirstOrDefault();
            if (fallback is not null) {
                var stored = ReadFile(fallback.Id);
                if (stored is not null) ApplyToLive(fallback.Id, stored.Settings);
                _activeId = fallback.Id;
                PersistActivePointer();
            } else {
                _activeId = null;
                try {
                    Create("Default", ProfileSnapshotNormalizer.Defaults, makeActive: true);
                } catch (IOException ex) {
                    // The delete itself succeeded; only the reseed write failed
                    // (disk full / permissions). Don't fail the DELETE for it —
                    // the repository stays at zero profiles until the next boot,
                    // which runs the same seed logic again (LoadAll).
                    LogPersistFailed(ex, _dir);
                }
            }
            return ProfileDeleteResult.Deleted;
        }
    }

    public bool SelectProfile(Guid id) {
        lock (_lock) {
            if (!_metas.ContainsKey(id)) return false;
            if (_activeId == id) return true;        // already active — no-op
            var stored = ReadFile(id);
            if (stored is null) return false;
            ApplyToLive(id, stored.Settings);
            _activeId = id;
            PersistActivePointer();
            return true;
        }
    }

    // ── internals ────────────────────────────────────────────────────────────

    // Push a snapshot into the live store without each section's Changed event
    // rewriting the (about-to-be-)active file; mirror the final state once.
    private void ApplyToLive(Guid id, ProfileSnapshotDto snap) {
        _suppressMirror = true;
        _applyingThreadId = Environment.CurrentManagedThreadId;
        try {
            ProfileStoreSnapshot.Apply(_liveStore, snap);
        } finally {
            _applyingThreadId = 0;
            _suppressMirror = false;
        }
        // Persist the active file from the just-applied snapshot (bump UpdatedUtc).
        PersistActiveFile(id, snap);
    }

    private void OnLiveChanged(object? sender, EventArgs e) {
        // The _suppressMirror gate only works if IProfileStore raises Changed synchronously
        // on the thread doing the Put (the documented contract). If an apply is in progress
        // but this fired on a different thread, the store dispatched asynchronously and the
        // gate can't be trusted — catch that in debug rather than silently writing 13 extra
        // mirror files per select. (Not data-corrupting, hence assert not throw.)
        Debug.Assert(
            _applyingThreadId == 0 || _applyingThreadId == Environment.CurrentManagedThreadId,
            "IProfileStore.Changed must be raised synchronously on the Put thread; see its doc.");

        // A live section PUT happened — mirror it into the active profile's file so the
        // saved set never drifts from what the user is editing.
        lock (_lock) {
            if (_suppressMirror) return;
            if (_activeId is not Guid id) return;
            PersistActiveFile(id, ProfileStoreSnapshot.Capture(_liveStore));
        }
    }

    private void PersistActiveFile(Guid id, ProfileSnapshotDto snap) {
        if (!_metas.TryGetValue(id, out var meta)) return;
        var updated = meta with { UpdatedUtc = DateTimeOffset.UtcNow };
        // Only advance the in-memory meta if the file actually persisted, so the cache can't
        // claim an UpdatedUtc the disk doesn't have.
        if (WriteFile(new StoredProfileDto(updated, snap))) _metas[id] = updated;
    }

    private void Load() {
        // Reclaim any *.tmp orphaned by a write hard-killed between WriteAllText and Move
        // (the {id}.json.tmp / active.id.tmp siblings). They don't match the *.json glob so
        // they're harmless, but they'd accumulate — same boot sweep as §43-2 / §55.1.
        SweepTempFiles();
        foreach (var path in SafeEnumerateProfileFiles()) {
            // Only our own "{id:N}.json" files — ignore any other *.json a future feature
            // might drop in profiles/, and ignore a file whose contents disagree with its name.
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var fileId)) {
                continue;
            }
            try {
                var json = File.ReadAllText(path);
                var stored = JsonSerializer.Deserialize(json, AraJsonSerializerContext.Default.StoredProfileDto);
                if (stored is not null && stored.Meta.Id == fileId) _metas[stored.Meta.Id] = stored.Meta;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                LogLoadFailed(ex, path);
            }
        }

        _activeId = ReadActivePointer();

        if (_metas.Count == 0) {
            // Fresh install (or legacy single-profile migration): seed the live store's
            // current snapshot — which FileProfileStore already loaded from the legacy
            // profile.json or defaults — as the initial active profile.
            Create("Default", settings: null, makeActive: true);
            return;
        }

        if (_activeId is null || !_metas.ContainsKey(_activeId.Value)) {
            // No/stale active pointer: adopt the newest profile and load it.
            var fallback = _metas.Values.OrderByDescending(m => m.CreatedUtc).First();
            var stored = ReadFile(fallback.Id);
            if (stored is not null) ApplyToLive(fallback.Id, stored.Settings);
            _activeId = fallback.Id;
            PersistActivePointer();
        } else {
            // Load the active profile into the live store so the working copy matches
            // the named active profile on boot (the two could differ if profile.json was
            // edited out-of-band).
            var stored = ReadFile(_activeId.Value);
            if (stored is not null) ApplyToLive(_activeId.Value, stored.Settings);
        }
    }

    private void SweepTempFiles() {
        try {
            foreach (var tmp in Directory.EnumerateFiles(_dir, "*.tmp")) {
                try {
                    File.Delete(tmp);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    LogPersistFailed(ex, tmp);
                }
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogLoadFailed(ex, _dir);
        }
    }

    private IEnumerable<string> SafeEnumerateProfileFiles() {
        try {
            return Directory.EnumerateFiles(_dir, "*.json").ToList();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogLoadFailed(ex, _dir);
            return Array.Empty<string>();
        }
    }

    private string FilePath(Guid id) => Path.Combine(_dir, id.ToString("N") + ".json");

    private StoredProfileDto? ReadFile(Guid id) {
        try {
            var json = File.ReadAllText(FilePath(id));
            var stored = JsonSerializer.Deserialize(json, AraJsonSerializerContext.Default.StoredProfileDto);
            if (stored is null) {
                return null;
            }
            // Normalize where the nulls are born: a saved snapshot written before a
            // section existed deserializes that section to null, and this read feeds
            // GET /api/v1/profiles/{id}, profile-share export, AND profile-select's
            // Apply → Put* push into the active store. The store re-normalizes on its
            // own write path, but the first two would otherwise ship
            // `"camera_electronics": null` (etc.) on the wire. (#689 follow-up.)
            return stored with {
                Settings = ProfileSnapshotNormalizer.Normalize(
                    (ProfileSnapshotDto?)stored.Settings ?? ProfileSnapshotNormalizer.Defaults),
            };
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            LogLoadFailed(ex, FilePath(id));
            return null;
        }
    }

    // True on success. Callers must not mutate the in-memory _metas unless this returns
    // true, or the cache would diverge from disk (and silently lose the profile on restart).
    private bool WriteFile(StoredProfileDto stored) {
        var path = FilePath(stored.Meta.Id);
        var tmp = path + ".tmp";
        try {
            var json = JsonSerializer.Serialize(stored, _indented.StoredProfileDto);
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogPersistFailed(ex, path);
            return false;
        }
    }

    private void TryDeleteFile(Guid id) {
        try {
            var path = FilePath(id);
            if (File.Exists(path)) File.Delete(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogPersistFailed(ex, FilePath(id));
        }
    }

    private Guid? ReadActivePointer() {
        try {
            if (!File.Exists(_activePtrPath)) return null;
            var text = File.ReadAllText(_activePtrPath).Trim();
            return Guid.TryParse(text, out var id) ? id : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogLoadFailed(ex, _activePtrPath);
            return null;
        }
    }

    private void PersistActivePointer() {
        if (_activeId is not Guid id) return;
        var tmp = _activePtrPath + ".tmp";
        try {
            File.WriteAllText(tmp, id.ToString());
            File.Move(tmp, _activePtrPath, overwrite: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogPersistFailed(ex, _activePtrPath);
        }
    }

    // Profile names are user-facing labels, not free-form blobs — trim, default when empty,
    // and cap the length so a pathological caller can't store a multi-MB name to disk and
    // echo it in every List() response.
    private const int MaxNameLength = 120;

    private static string NormalizeName(string name) {
        var trimmed = (name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed)) return "Untitled profile";
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }

    public void Dispose() {
        // Single-shot even if the DI container (which makes no formal single-caller guarantee)
        // disposes twice — otherwise we'd double-unsubscribe.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _liveStore.Changed -= OnLiveChanged;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read profile file {Path}")]
    partial void LogLoadFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to persist profile file {Path}")]
    partial void LogPersistFailed(Exception ex, string path);
}
