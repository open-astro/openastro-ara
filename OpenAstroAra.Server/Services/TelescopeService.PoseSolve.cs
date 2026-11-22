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
using OpenAstroAra.Astrometry;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

// #2173 — the mount half of the plate-solve check (PoseSolveCheckService): the device the trust belongs to, the count
// of Ara mount commands a check compares before and after its exposure, the trust a matching solve gives, and the
// Sync the user may tap after a mismatch (the existing mediator Sync, so its guards apply). Nothing here moves the mount.
public sealed partial class TelescopeService {

    /// <inheritdoc />
    public long PoseCommandCount {
        get {
            lock (_gate) {
                return PoseTrust.Commands;
            }
        }
    }

    /// <inheritdoc />
    public string? PoseDeviceId {
        get {
            lock (_gate) {
                return _lifecycle.Live is null ? null : PoseTrust.DeviceId;
            }
        }
    }

    /// <inheritdoc />
    public string? TrustPositionBySolve(long sessionEpoch, long commandCount, double decDeg) {
        string? cleared;
        string? device;
        lock (_gate) {
            // A reconnect or an Ara command since the check's first read: the solve may not show the pose it compared.
            if (_lifecycle.Live is null || SessionEpoch != sessionEpoch || PoseTrust.Commands != commandCount) {
                return null;
            }
            cleared = PoseTrust.Reason;
            PoseTrust.TrustBySolve(_runtime.AtHome, decDeg);
            device = _lifecycle.Device?.Name;
        }
        LogPoseTrustedBySolve(device ?? "?", cleared ?? "none");
        PersistPose();
        RefreshCacheOnce();
        return cleared ?? string.Empty;
    }

    /// <inheritdoc />
    public Task<bool> SyncForPoseCheckAsync(Coordinates sky, CancellationToken ct) => Sync(sky, ct);

    [LoggerMessage(Level = LogLevel.Information, Message = "Telescope '{Device}': a plate solve agreed with the mount's position and pier side (cleared an unverified position: {Reason}); its position is trusted again (#2173)")]
    private partial void LogPoseTrustedBySolve(string device, string reason);
}
