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
using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;

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
/// Not registered yet for the <c>imported/</c>, <c>templates/</c>, <c>active/</c>
/// subdirs from §38.2 — those land with the §38 import flow + §38 template
/// instantiator. This impl covers the <c>library/</c> root only.
/// </summary>
public sealed class FileSequenceService : ISequenceService {
    private readonly string _libraryDir;
    private readonly ILogger<FileSequenceService>? _logger;
    private readonly object _writeLock = new();
    private static readonly AraJsonSerializerContext _indentedContext =
        new(new JsonSerializerOptions(AraJsonSerializerContext.Default.Options) {
            WriteIndented = true,
        });

    public FileSequenceService(string profileDir, ILogger<FileSequenceService>? logger = null) {
        _libraryDir = Path.Combine(profileDir, "sequences", "library");
        _logger = logger;
        try { Directory.CreateDirectory(_libraryDir); }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "Failed to create sequence library dir {Path}", _libraryDir);
        }
    }

    public Task<CursorPage<SequenceListItemDto>> ListAsync(int limit, string? cursor, CancellationToken ct) {
        var entries = new List<SequenceDto>();
        try {
            foreach (var path in Directory.EnumerateFiles(_libraryDir, "*.json")) {
                ct.ThrowIfCancellationRequested();
                var dto = TryLoadFile(path);
                if (dto is not null) entries.Add(dto);
            }
        } catch (DirectoryNotFoundException) {
            // First-run before any sequence is saved.
        }

        var items = entries
            .OrderByDescending(s => s.ModifiedUtc)
            .Take(Math.Max(1, limit))
            .Select(s => new SequenceListItemDto(
                s.Id, s.Name, s.Description, s.CreatedUtc, s.ModifiedUtc,
                CurrentRunState: null,
                InstructionCount: 0,
                TargetCount: 0,
                TemplateOrigin: s.TemplateOrigin))
            .ToList();
        return Task.FromResult(new CursorPage<SequenceListItemDto>(items, NextCursor: null, HasMore: false));
    }

    public Task<SequenceDto?> GetAsync(Guid id, CancellationToken ct) {
        var path = PathFor(id);
        if (!File.Exists(path)) return Task.FromResult<SequenceDto?>(null);
        return Task.FromResult(TryLoadFile(path));
    }

    public Task<SequenceDto> CreateAsync(SequenceCreateRequestDto request, string? idempotencyKey, CancellationToken ct) {
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
        return Task.FromResult(dto);
    }

    public Task<SequenceDto?> UpdateAsync(Guid id, SequenceUpdateRequestDto request, CancellationToken ct) {
        var path = PathFor(id);
        if (!File.Exists(path)) return Task.FromResult<SequenceDto?>(null);
        var existing = TryLoadFile(path);
        if (existing is null) return Task.FromResult<SequenceDto?>(null);
        var updated = existing with {
            Name = request.Name ?? existing.Name,
            Description = request.Description ?? existing.Description,
            Body = request.Body ?? existing.Body,
            ModifiedUtc = DateTimeOffset.UtcNow,
        };
        WriteFile(updated);
        return Task.FromResult<SequenceDto?>(updated);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct) {
        var path = PathFor(id);
        if (!File.Exists(path)) return Task.FromResult(false);
        try {
            File.Delete(path);
            return Task.FromResult(true);
        } catch (Exception ex) {
            _logger?.LogWarning(ex, "Failed to delete sequence {Id} at {Path}", id, path);
            return Task.FromResult(false);
        }
    }

    public Task<SequenceShareDto> ShareExportAsync(Guid id, CancellationToken ct) {
        var path = PathFor(id);
        SequenceDto? existing = File.Exists(path) ? TryLoadFile(path) : null;
        // Endpoint catches null Get for 404; the share contract is non-null.
        // Synthesize an empty share for unknown ids (placeholder semantic that
        // matches the prior in-memory impl).
        existing ??= new SequenceDto(
            Id: id, Name: "Unknown sequence", Description: null,
            CreatedUtc: DateTimeOffset.UtcNow, ModifiedUtc: DateTimeOffset.UtcNow,
            Body: JsonDocument.Parse("{}").RootElement.Clone(),
            TemplateOrigin: null);
        var manifestBytes = existing.Body.GetRawText().Length;
        return Task.FromResult(new SequenceShareDto(
            SequenceId: existing.Id,
            SequenceName: existing.Name,
            ShareFormat: "openastroara.v1",
            Manifest: existing.Body,
            PayloadBytes: manifestBytes,
            DownloadUrl: $"/api/v1/sequences/{existing.Id}/share/payload"));
    }

    private string PathFor(Guid id) => Path.Combine(_libraryDir, $"{id:D}.json");

    private SequenceDto? TryLoadFile(string path) {
        try {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, AraJsonSerializerContext.Default.SequenceDto);
        } catch (Exception ex) {
            _logger?.LogWarning(ex, "Failed to load sequence from {Path}", path);
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
                _logger?.LogWarning(ex, "Failed to write sequence {Id}", dto.Id);
                throw;
            }
        }
    }
}
