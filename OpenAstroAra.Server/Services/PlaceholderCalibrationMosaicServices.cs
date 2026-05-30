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

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Phase 13.14 — placeholder <see cref="ICalibrationService"/>. One
/// fixture calibration session matching the §13.2 sample frames so the
/// §39 Calibration UI has a wire shape to render. Matching-flats
/// generation returns a synthetic <see cref="GeneratedFlatSequenceDto"/>.
/// </summary>
public sealed class PlaceholderCalibrationService : ICalibrationService {
    private static readonly Guid SampleSessionId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly CalibrationSessionDto SampleSession = new(
        Id: SampleSessionId,
        TargetName: "M31",
        SessionStartUtc: new DateTimeOffset(2026, 5, 30, 3, 0, 0, TimeSpan.Zero),
        SessionEndUtc: new DateTimeOffset(2026, 5, 30, 4, 30, 0, TimeSpan.Zero),
        LightFrameCount: 2,
        FiltersUsed: new[] {
            new CalibrationFilterSummaryDto("L", 1, 180.0, RecommendedFlatFrames: 30),
            new CalibrationFilterSummaryDto("R", 1, 180.0, RecommendedFlatFrames: 30),
        },
        MatchingFlatsAvailable: false,
        MatchingDarksAvailable: true,
        ProfileId: null);

    public Task<CursorPage<CalibrationSessionDto>> ListSessionsAsync(int limit, string? cursor, CancellationToken ct) =>
        Task.FromResult(new CursorPage<CalibrationSessionDto>(
            new[] { SampleSession }, NextCursor: null, HasMore: false));

    public Task<CalibrationSessionDto?> GetSessionAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<CalibrationSessionDto?>(id == SampleSessionId ? SampleSession : null);

    public Task<GeneratedFlatSequenceDto> GenerateMatchingFlatsAsync(Guid sessionId, MatchingFlatsRequestDto request, CancellationToken ct) =>
        Task.FromResult(new GeneratedFlatSequenceDto(
            SourceSessionId: sessionId,
            GeneratedSequenceId: Guid.NewGuid(),
            GeneratedSequenceName: $"Flats for {sessionId:N}",
            TotalFlatFrames: 60,
            Steps: new[] {
                new GeneratedFlatStepDto("L", request.OverrideFrameCount ?? 30, request.OverrideTargetAdu ?? 32000, PanelBrightness: 128),
                new GeneratedFlatStepDto("R", request.OverrideFrameCount ?? 30, request.OverrideTargetAdu ?? 32000, PanelBrightness: 128),
            }));
}

/// <summary>
/// Phase 13.14 — placeholder <see cref="IDarkLibraryService"/>. Status
/// reports a 2-entry library; build accepts and returns 202; entries
/// list reflects the same 2 fixture darks.
/// </summary>
public sealed class PlaceholderDarkLibraryService : IDarkLibraryService {
    private static readonly DarkLibraryEntryDto[] SampleEntries = new[] {
        new DarkLibraryEntryDto(
            Id: Guid.Parse("88888888-8888-8888-8888-888888888881"),
            ExposureSeconds: 180,
            Gain: 100,
            TemperatureC: -10.0,
            FrameCount: 30,
            CapturedUtc: new DateTimeOffset(2026, 5, 15, 22, 0, 0, TimeSpan.Zero),
            FilePath: "/var/lib/openastroara/darks/180s_g100_-10c.fits",
            FileSizeBytes: 503_316_480L),
        new DarkLibraryEntryDto(
            Id: Guid.Parse("88888888-8888-8888-8888-888888888882"),
            ExposureSeconds: 300,
            Gain: 100,
            TemperatureC: -10.0,
            FrameCount: 30,
            CapturedUtc: new DateTimeOffset(2026, 5, 15, 23, 0, 0, TimeSpan.Zero),
            FilePath: "/var/lib/openastroara/darks/300s_g100_-10c.fits",
            FileSizeBytes: 503_316_480L),
    };

    public Task<OperationAcceptedDto> StartBuildAsync(DarkLibraryBuildRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("dark-library.build", idempotencyKey));

    public Task<DarkLibraryStateDto> GetStatusAsync(CancellationToken ct) =>
        Task.FromResult(new DarkLibraryStateDto(
            Status: "idle",
            TotalCombinations: 2,
            CompletedCombinations: 2,
            BuildStartedUtc: new DateTimeOffset(2026, 5, 15, 22, 0, 0, TimeSpan.Zero),
            BuildCompletedUtc: new DateTimeOffset(2026, 5, 15, 23, 30, 0, TimeSpan.Zero),
            FailureReason: null,
            Entries: SampleEntries));

    public Task<IReadOnlyList<DarkLibraryEntryDto>> ListEntriesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DarkLibraryEntryDto>>(SampleEntries);
}

/// <summary>
/// Phase 13.14 — placeholder <see cref="IMosaicService"/>. In-memory
/// dictionary backs CRUD round-trip; one fixture starter mosaic is
/// pre-seeded so list/get aren't empty on first load.
/// </summary>
public sealed class PlaceholderMosaicService : IMosaicService {
    private readonly object _lock = new();
    private readonly Dictionary<Guid, MosaicDto> _mosaics;

    public PlaceholderMosaicService() {
        var seedId = Guid.Parse("99999999-9999-9999-9999-999999999991");
        _mosaics = new Dictionary<Guid, MosaicDto> {
            [seedId] = new MosaicDto(
                Id: seedId,
                Name: "Veil nebula 3×2",
                CenterRaDegrees: 312.75,        // ~Veil
                CenterDecDegrees: 30.5,
                PanelCountX: 3,
                PanelCountY: 2,
                OverlapPercent: 15.0,
                PositionAngleDegrees: 0.0,
                TotalPanels: 6,
                CreatedUtc: new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero),
                GeneratedSequenceId: null),
        };
    }

    public Task<CursorPage<MosaicDto>> ListAsync(int limit, string? cursor, CancellationToken ct) {
        lock (_lock) {
            return Task.FromResult(new CursorPage<MosaicDto>(
                _mosaics.Values.Take(Math.Max(1, limit)).ToList(),
                NextCursor: null, HasMore: false));
        }
    }

    public Task<MosaicDto?> GetAsync(Guid id, CancellationToken ct) {
        lock (_lock) {
            return Task.FromResult<MosaicDto?>(_mosaics.TryGetValue(id, out var m) ? m : null);
        }
    }

    public Task<MosaicDto> CreateAsync(MosaicCreateRequestDto request, string? idempotencyKey, CancellationToken ct) {
        var now = DateTimeOffset.UtcNow;
        var dto = new MosaicDto(
            Id: Guid.NewGuid(),
            Name: request.Name,
            CenterRaDegrees: request.CenterRaDegrees,
            CenterDecDegrees: request.CenterDecDegrees,
            PanelCountX: request.PanelCountX,
            PanelCountY: request.PanelCountY,
            OverlapPercent: request.OverlapPercent,
            PositionAngleDegrees: request.PositionAngleDegrees ?? 0.0,
            TotalPanels: request.PanelCountX * request.PanelCountY,
            CreatedUtc: now,
            GeneratedSequenceId: null);
        lock (_lock) { _mosaics[dto.Id] = dto; }
        return Task.FromResult(dto);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct) {
        lock (_lock) { return Task.FromResult(_mosaics.Remove(id)); }
    }

    public Task<IReadOnlyList<MosaicPanelDto>> GetPanelsAsync(Guid mosaicId, CancellationToken ct) {
        lock (_lock) {
            if (!_mosaics.TryGetValue(mosaicId, out var m))
                return Task.FromResult<IReadOnlyList<MosaicPanelDto>>(Array.Empty<MosaicPanelDto>());
            // Synthesize panels in a grid; centers approximated as the
            // mosaic center (real impl spreads them by FOV * (1-overlap)).
            // §47.3 crosses_ra_wrap flag is false for the fixture.
            var panels = new List<MosaicPanelDto>(m.TotalPanels);
            int idx = 0;
            for (int y = 0; y < m.PanelCountY; y++) {
                for (int x = 0; x < m.PanelCountX; x++) {
                    panels.Add(new MosaicPanelDto(
                        MosaicId: m.Id,
                        PanelIndex: idx++,
                        PanelX: x, PanelY: y,
                        CenterRaDegrees: m.CenterRaDegrees,
                        CenterDecDegrees: m.CenterDecDegrees,
                        CrossesRaWrap: false,
                        Status: "pending",
                        TargetFrameId: null));
                }
            }
            return Task.FromResult<IReadOnlyList<MosaicPanelDto>>(panels);
        }
    }

    public Task<MosaicProgressDto?> GetProgressAsync(Guid mosaicId, CancellationToken ct) {
        lock (_lock) {
            if (!_mosaics.TryGetValue(mosaicId, out var m))
                return Task.FromResult<MosaicProgressDto?>(null);
            return Task.FromResult<MosaicProgressDto?>(new MosaicProgressDto(
                MosaicId: m.Id,
                CompletedPanels: 0,
                TotalPanels: m.TotalPanels,
                CurrentPanelIndex: null,
                CurrentPanelStartedUtc: null,
                FramesPerPanelTarget: 20,
                FramesCapturedTotal: 0));
        }
    }
}
