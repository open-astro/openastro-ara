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
using System.IO;
using System.Collections.Concurrent;
using System.Text.Json;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §38.2 filesystem-backed <see cref="ISequenceService"/>. Sequences persist
/// as individual JSON files under <c>{profileDir}/sequences/library/{id}.json</c>
/// matching the playbook storage layout. Replaces the in-memory placeholder
/// (lost on daemon restart) with durable storage.
///
/// Writes are atomic (temp + rename + dir fsync via <see cref="File.Move"/>
/// with overwrite). List scans the directory; for v0.0.1 sequence libraries
/// (typically &lt;100 entries) this is fine; a larger library would benefit
/// from an index, but that's outside §38 scope.
///
/// Constructor also scaffolds the §38.2 sibling subdirs (<c>imported/</c>,
/// <c>templates/</c>, <c>active/</c>) so future sub-PRs that need them have
/// a stable layout without each one re-implementing the same mkdir step.
/// </summary>
public sealed partial class FileSequenceService : ISequenceService {
    private readonly string _libraryDir;
    private readonly ILogger<FileSequenceService> _logger;
    private readonly ISequencerService? _sequencer;
    private readonly object _writeLock = new();
    private static readonly AraJsonSerializerContext _indentedContext =
        new(new JsonSerializerOptions(AraJsonSerializerContext.Default.Options) {
            WriteIndented = true,
        });

    // §38.2 subdir names — kept as constants so consumers (import flow,
    // template loader, active checkpointer) reference the same paths.
    public const string LibraryDirName = "library";
    public const string ImportedDirName = "imported";
    public const string TemplatesDirName = "templates";
    public const string ActiveDirName = "active";

    public FileSequenceService(string profileDir, ILogger<FileSequenceService>? logger = null)
        : this(profileDir, sequencer: null, logger) { }

    public FileSequenceService(string profileDir, ISequencerService? sequencer, ILogger<FileSequenceService>? logger = null) {
        var sequencesRoot = Path.Combine(profileDir, "sequences");
        _libraryDir = Path.Combine(sequencesRoot, LibraryDirName);
        _sequencer = sequencer;
        _logger = logger ?? NullLogger<FileSequenceService>.Instance;

        // §38.2 scaffold: create all four subdirs on startup. Library is the
        // only one this service writes; the others are scaffolded here so the
        // import flow / template loader / active checkpointer can rely on
        // their existence without each one duplicating the mkdir + log path.
        foreach (var dirName in new[] { LibraryDirName, ImportedDirName, TemplatesDirName, ActiveDirName }) {
            var path = Path.Combine(sequencesRoot, dirName);
            try { Directory.CreateDirectory(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                LogCreateSubdirFailed(ex, path);
            }
        }
    }

    public async Task<CursorPage<SequenceListItemDto>> ListAsync(int limit, string? cursor, CancellationToken ct) {
        var entries = new List<SequenceDto>();
        try {
            foreach (var path in Directory.EnumerateFiles(_libraryDir, "*.json")) {
                // Dot-files are library metadata (.create-replays.json), not sequences.
                if (Path.GetFileName(path).StartsWith('.')) continue;
                ct.ThrowIfCancellationRequested();
                var dto = TryLoadFile(path);
                if (dto is not null) entries.Add(dto);
            }
        } catch (DirectoryNotFoundException) {
            // First-run before any sequence is saved.
        }

        var ordered = entries
            .OrderByDescending(s => s.ModifiedUtc)
            .Take(Math.Max(1, limit))
            .ToList();

        var items = new List<SequenceListItemDto>(ordered.Count);
        foreach (var s in ordered) {
            var stats = SequenceBodyInspector.Inspect(s.Body);
            // §38.3 — surface the current run state in the list so WILMA
            // can render a "running" badge without a per-id /state probe.
            SequenceRunState? currentState = null;
            if (_sequencer is not null) {
                var runState = await _sequencer.GetRunStateAsync(s.Id, ct);
                currentState = runState?.State;
            }
            items.Add(new SequenceListItemDto(
                s.Id, s.Name, s.Description, s.CreatedUtc, s.ModifiedUtc,
                CurrentRunState: currentState,
                InstructionCount: stats.InstructionCount,
                TargetCount: stats.TargetCount,
                TemplateOrigin: s.TemplateOrigin));
        }
        return new CursorPage<SequenceListItemDto>(items, NextCursor: null, HasMore: false);
    }

    public Task<SequenceDto?> GetAsync(Guid id, CancellationToken ct) {
        var path = PathFor(id);
        if (!File.Exists(path)) return Task.FromResult<SequenceDto?>(null);
        return Task.FromResult(TryLoadFile(path));
    }

    // Create-replay dedup (2026-07-15 audit; hardened per the #853 review):
    // the endpoint always DECLARED Idempotency-Key but silently ignored it — a
    // retried create (lost response on marginal rig Wi-Fi) duplicated the
    // sequence. Two layers close it properly:
    //   1. _createFlights — keyed SINGLE-FLIGHT, so a retry racing the
    //      still-processing original joins that execution (no TOCTOU window).
    //   2. _replayMap — key→id persisted as .create-replays.json NEXT TO the
    //      sequence files, so the replay survives daemon restarts and has NO
    //      time cliff: an offline draft can push its stamped key weeks later
    //      and still dedupe. Validity is existence-based — the mapping only
    //      replays while the sequence file exists (deleting the sequence
    //      naturally retires the key; no ghost-id resurrection).
    private readonly IdempotencyCache<SequenceDto> _createFlights = new();
    private readonly ConcurrentDictionary<string, Guid> _replayMap = new();
    private readonly object _replayFileLock = new();
    private bool _replayMapLoaded;

    private string ReplayMapPath => Path.Combine(_libraryDir, ".create-replays.json");

    private void EnsureReplayMapLoaded() {
        if (_replayMapLoaded) return;
        lock (_replayFileLock) {
            if (_replayMapLoaded) return;
            try {
                if (File.Exists(ReplayMapPath)) {
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, Guid>>(
                        File.ReadAllText(ReplayMapPath));
                    if (loaded is not null) {
                        foreach (var kv in loaded) {
                            // Only keys whose sequence still exists are alive.
                            if (File.Exists(PathFor(kv.Value))) {
                                _replayMap[kv.Key] = kv.Value;
                            }
                        }
                    }
                }
            } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
                // Best-effort: a corrupt/unreadable map degrades to "no replays",
                // never to a failed create.
            }
            _replayMapLoaded = true;
        }
    }

    private void PersistReplayMap() {
        lock (_replayFileLock) {
            try {
                File.WriteAllText(ReplayMapPath,
                    JsonSerializer.Serialize(new Dictionary<string, Guid>(_replayMap)));
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                // Best-effort — losing the map costs dedup breadth, not data.
            }
        }
    }

    public async Task<SequenceDto> CreateAsync(SequenceCreateRequestDto request, string? idempotencyKey, CancellationToken ct) {
        EnsureReplayMapLoaded();
        // Durable replay first: valid only while the created sequence still
        // exists (a user delete retires the key instead of resurrecting it).
        if (!string.IsNullOrWhiteSpace(idempotencyKey)
            && _replayMap.TryGetValue(idempotencyKey, out var replayId)) {
            if (File.Exists(PathFor(replayId)) && TryLoadFile(PathFor(replayId)) is { } existing) {
                return existing;
            }
            _replayMap.TryRemove(idempotencyKey, out _);
            _createFlights.Evict(idempotencyKey);
        }
        // Single-flight guards the concurrent-retry TOCTOU: two in-flight
        // requests with the same key share ONE create.
        return await _createFlights.GetOrRunAsync(idempotencyKey, () => {
            var now = DateTimeOffset.UtcNow;
            var dto = new SequenceDto(
                Id: Guid.NewGuid(),
                Name: request.Name,
                Description: request.Description,
                CreatedUtc: now,
                ModifiedUtc: now,
                Body: request.Body,
                TemplateOrigin: request.TemplateOrigin);
            WriteFile(dto);
            if (!string.IsNullOrWhiteSpace(idempotencyKey)) {
                _replayMap[idempotencyKey] = dto.Id;
                PersistReplayMap();
            }
            return Task.FromResult(dto);
        }).ConfigureAwait(false);
    }

    public async Task<SequenceUpdateResult> UpdateAsync(Guid id, SequenceUpdateRequestDto request, CancellationToken ct) {
        var path = PathFor(id);
        if (!File.Exists(path)) return SequenceUpdateResult.NotFound;
        if (await HasActiveRunAsync(id, ct)) return SequenceUpdateResult.Refused;
        var existing = TryLoadFile(path);
        if (existing is null) return SequenceUpdateResult.NotFound;
        var updated = existing with {
            Name = request.Name ?? existing.Name,
            Description = request.Description ?? existing.Description,
            Body = request.Body ?? existing.Body,
            ModifiedUtc = DateTimeOffset.UtcNow,
        };
        WriteFile(updated);
        return new SequenceUpdateResult(updated, RunActive: false);
    }

    public async Task<SequenceDeleteResult> DeleteAsync(Guid id, CancellationToken ct) {
        var path = PathFor(id);
        if (!File.Exists(path)) return SequenceDeleteResult.NotFound;
        if (await HasActiveRunAsync(id, ct)) return SequenceDeleteResult.RunActive;
        try {
            File.Delete(path);
            return SequenceDeleteResult.Deleted;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogDeleteFailed(ex, id, path);
            return SequenceDeleteResult.NotFound;
        }
    }

    /// <summary>§38 — whether a live run currently owns this sequence's file. The
    /// guard-then-mutate window that remains is microseconds (vs. the client's old
    /// probe→request round-trip); closing it entirely would need a cross-service
    /// mutation lock shared with <see cref="ISequencerService"/> run reservation,
    /// which isn't warranted for a single-writer daemon.</summary>
    private async Task<bool> HasActiveRunAsync(Guid id, CancellationToken ct) {
        if (_sequencer is null) return false;
        var runState = await _sequencer.GetRunStateAsync(id, ct);
        return runState is not null && IsActiveRun(runState.State);
    }

    /// <summary>Active = the executor may still read/write against this sequence.
    /// <c>Aborting</c> counts: the worker is winding down but not done with the file.
    /// Both paused flavors count: the suspended worker resumes into this file
    /// (§58.12's awaiting-user pause included — a rig failure must not make the
    /// sequence deletable mid-run). Terminal states (Completed/Failed/Stopped)
    /// and Idle do not block.</summary>
    internal static bool IsActiveRun(SequenceRunState state) =>
        state is SequenceRunState.Starting or SequenceRunState.Running
            or SequenceRunState.Paused or SequenceRunState.PausedAwaitingUser
            or SequenceRunState.Aborting;

    public Task<SequenceShareDto?> ShareExportAsync(Guid id, CancellationToken ct) {
        var path = PathFor(id);
        var existing = File.Exists(path) ? TryLoadFile(path) : null;
        // Unknown (or unreadable) id → null so the endpoint returns 404, mirroring
        // the profile-share contract — exporting a deleted sequence is a miss, not
        // an empty placeholder share.
        if (existing is null) return Task.FromResult<SequenceShareDto?>(null);
        // PayloadBytes is the inline manifest's UTF-8 size (what the client will
        // write to the .araseq.json file), not the on-disk SequenceDto wrapper.
        var manifestBytes = System.Text.Encoding.UTF8.GetByteCount(existing.Body.GetRawText());
        return Task.FromResult<SequenceShareDto?>(new SequenceShareDto(
            SequenceId: existing.Id,
            SequenceName: existing.Name,
            ShareFormat: "openastroara.v1",
            Manifest: existing.Body,
            PayloadBytes: manifestBytes,
            // Manifest carries the share inline; no payload route (mirrors profiles).
            DownloadUrl: null));
    }

    private string PathFor(Guid id) => Path.Combine(_libraryDir, $"{id:D}.json");

    private SequenceDto? TryLoadFile(string path) {
        try {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, AraJsonSerializerContext.Default.SequenceDto);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            LogLoadFailed(ex, path);
            return null;
        }
    }

    private void WriteFile(SequenceDto dto) {
        lock (_writeLock) {
            try {
                Directory.CreateDirectory(_libraryDir);
                var path = PathFor(dto.Id);
                var tempPath = path + ".tmp";
                var json = JsonSerializer.Serialize(dto, _indentedContext.SequenceDto);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, path, overwrite: true);
            } catch (Exception ex) {
                LogWriteFailed(ex, dto.Id);
                throw;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to create sequences subdir {Path}")]
    private partial void LogCreateSubdirFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete sequence {Id} at {Path}")]
    private partial void LogDeleteFailed(Exception ex, Guid id, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load sequence from {Path}")]
    private partial void LogLoadFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to write sequence {Id}")]
    private partial void LogWriteFailed(Exception ex, Guid id);
}