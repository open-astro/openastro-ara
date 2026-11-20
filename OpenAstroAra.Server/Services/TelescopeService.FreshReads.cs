#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Common.DeviceInterfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using PierSide = OpenAstroAra.Core.Enums.PierSide;

namespace OpenAstroAra.Server.Services;

/// <summary>#2066 (ARA-5) — the fresh, timestamped reading of <see cref="IMountMotionControl"/>.</summary>
public sealed partial class TelescopeService {

    /// <inheritdoc />
    /// <remarks>Stamped when the read begins (<see cref="PoseClock"/>), so a slow round trip ages the reading by its whole
    /// length. The session is the one the driver client belongs to, read with it under the gate: a reconnect during the
    /// read leaves the old session's number on a reading the old client answered. The frame is the session's equatorial
    /// system once the refresh read it; until then the driver is asked with the position (its answer is not kept: the
    /// refresh owns that state). Pier side is not asked of a mount that does not implement it (<c>pierUnknown</c>).</remarks>
    public Task<MountReading?> ReadNowAsync(CancellationToken ct) => ReadAsync(readSlewing: true, readSide: true, ct);

    /// <inheritdoc />
    public Task<MountReading?> ReadPositionNowAsync(CancellationToken ct) => ReadAsync(readSlewing: false, readSide: false, ct);

    /// <inheritdoc />
    public Task<MountReading?> ReadPoseNowAsync(CancellationToken ct) => ReadAsync(readSlewing: false, readSide: true, ct);

    /// <inheritdoc />
    public double? MatchMoveAxisRate(int axis, double requested) {
        IReadOnlyList<(double Min, double Max)>? bands;
        lock (_gate) {
            bands = BandsForAxis(_axisBands, axis, out _);
        }
        return MatchMoveAxisRateAtOrBelow(requested, bands);
    }

    /// <summary>#2067, #2083 — the fastest rate within <paramref name="bands"/> at or below |<paramref name="requested"/>|,
    /// sign kept: a rate inside a band is kept, one between two bands takes the top of the band below, and one under every
    /// band has none (null). Never rounds up, unlike the pad's <see cref="SnapMoveAxisRate"/> (which raises a slow press
    /// to the nearest band): a routine's time bound is planned on the rate it sends. 0 (and NaN) is a stop: 0. Null when no
    /// bands are known. Internal static so the rule is unit-testable.</summary>
    internal static double? MatchMoveAxisRateAtOrBelow(double requested, IReadOnlyList<(double Min, double Max)>? bands) {
        if (requested == 0 || double.IsNaN(requested)) {
            return 0;
        }
        if (bands is not { Count: > 0 }) {
            return null;
        }
        var magnitude = Math.Abs(requested);
        double? best = null;
        foreach (var (min, max) in bands) {
            if (min <= magnitude && max > 0) {
                var candidate = Math.Min(max, magnitude);
                if (best is not double b || candidate > b) {
                    best = candidate;
                }
            }
        }
        return best is double r && r > 0 ? Math.CopySign(r, requested) : null;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A failed read is 'no fresh reading' (null) by contract; the routine fails or reads again its own way.")]
    private async Task<MountReading?> ReadAsync(bool readSlewing, bool readSide, CancellationToken ct) {
        ITelescopeDevice? client;
        long session;
        EquatorialCoordinateType? knownSystem;
        bool readPierSide;
        lock (_gate) {
            client = _lifecycle.Live;
            session = SessionEpoch;
            knownSystem = _equatorialSystemKnown ? _equatorialSystemRaw : null;
            readPierSide = readSide && !_sideOfPierUnsupported;
        }
        if (client is null) {
            return null;
        }
        var readAt = PoseClock.GetUtcNow();
        try {
            var (ra, dec, slewing, side, system) = await Task.Run(() => (
                client.RightAscension,
                client.Declination,
                readSlewing ? ReadOrNull(() => client.Slewing) : null,
                readPierSide ? ReadSideOfPier(client).SideOfPier : PierSide.pierUnknown,
                knownSystem ?? ReadEquatorialSystem(client) ?? EquatorialCoordinateType.Other), ct).ConfigureAwait(false);
            if (!double.IsFinite(ra) || !double.IsFinite(dec)) {
                return null;
            }
            return new MountReading(ra, dec, slewing, side, readAt, session, MapEpoch(system));
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception) {
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: a failed Slewing read is 'unknown' (null), never a failed reading.")]
    private static bool? ReadOrNull(Func<bool> read) {
        try {
            return read();
        } catch (Exception) {
            return null;
        }
    }
}
