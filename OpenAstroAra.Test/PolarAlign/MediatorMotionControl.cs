#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enums;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test.PolarAlign {

    /// <summary>
    /// #2066 — the <see cref="IMountMotionControl"/> seam over a bare mediator mock, for the Polar Align tests whose mount
    /// models its motors on the mediator (<c>MoveAxis</c>, <c>GetCurrentPosition</c>) rather than the telescope service.
    /// Its reading is the mediator's position, stamped now, in mount session 0; it keeps no stop count. The daemon never
    /// takes this path: Polar Align gets the telescope service itself.
    /// </summary>
    internal sealed class MediatorMotionControl(ITelescopeMediator mount) : IMountMotionControl {

        /// <summary>The seam a test's mount offers: its own (the fake GEM mount, a mock set up as the telescope service),
        /// else this one over its mediator.</summary>
        public static IMountMotionControl For(ITelescopeMediator mount) => mount as IMountMotionControl ?? new MediatorMotionControl(mount);

        public Task AbortSlewAsync(string reason, CancellationToken ct) => Task.Run(mount.StopSlew, CancellationToken.None);

        public Task MoveAxisAsync(int axis, double rate, int? leaseMs, long? unlessAbortedSince, CancellationToken ct) =>
            Task.Run(() => mount.MoveAxis((TelescopeAxes)axis, rate), CancellationToken.None);

        public Task<MountReading?> ReadNowAsync(CancellationToken ct) {
            var pos = mount.GetCurrentPosition();
            if (pos is null) {
                return Task.FromResult<MountReading?>(null);
            }
            var info = mount.GetInfo();
            return Task.FromResult<MountReading?>(new MountReading(pos.RA, pos.Dec, info?.Slewing, info?.SideOfPier ?? PierSide.pierUnknown,
                DateTimeOffset.UtcNow, SessionEpoch: 0, pos.Epoch));
        }
    }
}
