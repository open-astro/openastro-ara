#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MySwitch;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Interfaces.Mediator {

    /// <summary>
    /// ARA multi-instance extension (§10.6, Switch pilot PR3): per-device targeting for the
    /// <c>SetSwitchValue</c> instruction when more than one switch hub is connected. Lives in its
    /// own ARA-owned interface — not on the NINA-inherited <see cref="ISwitchMediator"/> — so the
    /// inherited surface (and its ISO-8859-1 source file) stays untouched; the instruction
    /// type-checks for this capability and falls back to the single-target mediator methods (the
    /// lowest-numbered "primary" device) when it's absent or no device is named.
    /// </summary>
    public interface ISwitchDeviceTargeting {

        /// <summary>The <see cref="SwitchInfo"/> of the connected switch with this Alpaca device
        /// number, or a disconnected snapshot when none matches. <c>-1</c> = the primary
        /// (lowest-numbered) device, identical to <c>ISwitchMediator.GetInfo()</c>.</summary>
        SwitchInfo GetInfo(int alpacaDeviceNumber);

        /// <summary>Writes <paramref name="value"/> to the <paramref name="switchIndex"/>-th
        /// writable switch of the device with <paramref name="alpacaDeviceNumber"/> (<c>-1</c> =
        /// primary). Same degrade contract as the single-target overload: not-connected /
        /// out-of-range resolve to a logged no-op; genuine cancellation propagates.</summary>
        Task SetSwitchValue(int alpacaDeviceNumber, short switchIndex, double value,
            IProgress<ApplicationStatus> progress, CancellationToken ct);
    }
}
