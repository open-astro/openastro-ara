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
    public Task<MountReading?> ReadNowAsync(CancellationToken ct) => ReadAsync(positionOnly: false, ct);

    /// <inheritdoc />
    public Task<MountReading?> ReadPositionNowAsync(CancellationToken ct) => ReadAsync(positionOnly: true, ct);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A failed read is 'no fresh reading' (null) by contract; the routine fails or reads again its own way.")]
    private async Task<MountReading?> ReadAsync(bool positionOnly, CancellationToken ct) {
        ITelescopeDevice? client;
        long session;
        EquatorialCoordinateType? knownSystem;
        bool readPierSide;
        lock (_gate) {
            client = _lifecycle.Live;
            session = SessionEpoch;
            knownSystem = _equatorialSystemKnown ? _equatorialSystemRaw : null;
            readPierSide = !positionOnly && !_sideOfPierUnsupported;
        }
        if (client is null) {
            return null;
        }
        var readAt = PoseClock.GetUtcNow();
        try {
            var (ra, dec, slewing, side, system) = await Task.Run(() => (
                client.RightAscension,
                client.Declination,
                positionOnly ? null : ReadOrNull(() => client.Slewing),
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
