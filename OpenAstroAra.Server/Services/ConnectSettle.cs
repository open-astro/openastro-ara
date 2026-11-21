#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Services;

/// <summary>The one settle poll for a connect that runs in the background: <see cref="ProfileDeviceSwitch"/>
/// (many devices) and <see cref="EquipmentReconnector"/> (one role) both wait through it, so they cannot drift.</summary>
internal static class ConnectSettle {
    internal static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Calls <paramref name="settled"/> every <see cref="Poll"/> until it returns true. False when
    /// <paramref name="deadline"/> passed first; throws <see cref="OperationCanceledException"/> when
    /// <paramref name="ct"/> (which <paramref name="deadline"/> must be linked to) was cancelled.</summary>
    internal static async Task<bool> UntilAsync(Func<Task<bool>> settled, CancellationToken deadline, CancellationToken ct) {
        while (!await settled().ConfigureAwait(false)) {
            try {
                await Task.Delay(Poll, deadline).ConfigureAwait(false);
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                return false;
            }
        }
        return true;
    }
}