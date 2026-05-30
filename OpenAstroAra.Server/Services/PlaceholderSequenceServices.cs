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
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Phase 13.13 — placeholder <see cref="ISequenceService"/> covering the
/// §38 sequence CRUD surface. Backed by an in-memory dictionary so
/// create/update/delete actually round-trip during a single daemon
/// lifetime (resets on restart). Real §28-DB-backed impl + the §38
/// sequence orchestrator land in Phase 13.x.
/// </summary>
public sealed class PlaceholderSequenceService : ISequenceService {
    private readonly object _lock = new();
    private readonly Dictionary<Guid, SequenceDto> _sequences = new();

    public Task<CursorPage<SequenceListItemDto>> ListAsync(int limit, string? cursor, CancellationToken ct) {
        lock (_lock) {
            var items = _sequences.Values
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
    }

    public Task<SequenceDto?> GetAsync(Guid id, CancellationToken ct) {
        lock (_lock) {
            return Task.FromResult<SequenceDto?>(_sequences.TryGetValue(id, out var seq) ? seq : null);
        }
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
        lock (_lock) { _sequences[dto.Id] = dto; }
        return Task.FromResult(dto);
    }

    public Task<SequenceDto?> UpdateAsync(Guid id, SequenceUpdateRequestDto request, CancellationToken ct) {
        lock (_lock) {
            if (!_sequences.TryGetValue(id, out var existing)) return Task.FromResult<SequenceDto?>(null);
            var updated = existing with {
                Name = request.Name ?? existing.Name,
                Description = request.Description ?? existing.Description,
                Body = request.Body ?? existing.Body,
                ModifiedUtc = DateTimeOffset.UtcNow,
            };
            _sequences[id] = updated;
            return Task.FromResult<SequenceDto?>(updated);
        }
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct) {
        lock (_lock) { return Task.FromResult(_sequences.Remove(id)); }
    }

    public Task<SequenceShareDto> ShareExportAsync(Guid id, CancellationToken ct) {
        lock (_lock) {
            if (!_sequences.TryGetValue(id, out var existing)) {
                // Real impl would 404; the endpoint catches null returns,
                // but ShareExportAsync's contract is non-nullable. Return
                // a synthetic share for unknown ids (placeholder semantic).
                existing = new SequenceDto(
                    Id: id, Name: "Unknown sequence", Description: null,
                    CreatedUtc: DateTimeOffset.UtcNow, ModifiedUtc: DateTimeOffset.UtcNow,
                    Body: JsonDocument.Parse("{}").RootElement.Clone(),
                    TemplateOrigin: null);
            }
            return Task.FromResult(new SequenceShareDto(
                SequenceId: existing.Id,
                SequenceName: existing.Name,
                ShareFormat: "openastroara.v1",
                Manifest: existing.Body,
                PayloadBytes: 4_096,
                DownloadUrl: $"/api/v1/sequences/{existing.Id}/share/payload"));
        }
    }
}

/// <summary>
/// Phase 13.13 — placeholder <see cref="ISequencerService"/> for §38
/// runtime control. GetRunState returns null (no run in progress);
/// every action returns 202 OperationAccepted with a sequencer-prefixed
/// operation_type. Real impl wraps the legacy NINA <c>ISequencer</c>
/// with a thread-safe lifecycle worker that emits §60.9 WS events.
/// </summary>
public sealed class PlaceholderSequencerService : ISequencerService {
    public Task<SequenceRunStateDto?> GetRunStateAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<SequenceRunStateDto?>(null);

    public Task<OperationAcceptedDto> StartAsync(Guid id, SequenceStartRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.start", idempotencyKey));

    public Task<OperationAcceptedDto> PauseAsync(Guid id, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.pause", idempotencyKey));

    public Task<OperationAcceptedDto> ResumeAsync(Guid id, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.resume", idempotencyKey));

    public Task<OperationAcceptedDto> AbortAsync(Guid id, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.abort", idempotencyKey));

    public Task<OperationAcceptedDto> StopAsync(Guid id, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("sequencer.stop", idempotencyKey));
}
