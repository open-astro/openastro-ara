// Headless server stub: the INotifyPropertyChanged event in this file satisfies
// the IDomeFollower contract but is never raised server-side (the Flutter client
// drives state over REST/WS), so CS0067 "event is never used" is expected here
// and intentionally suppressed for the whole file.
#pragma warning disable CS0067

#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces;
using System.ComponentModel;

namespace OpenAstroAra.Server.Services.Equipment;

/// <summary>
/// §38k-21 — headless no-op stub for <see cref="IDomeFollower"/>. Same pattern
/// as the §38k-9 … §38k-20 mediator stubs, for the one non-mediator dependency
/// the <c>SynchronizeDome</c> instruction needs. Reports "not synchronized /
/// not following" and no-ops the start/stop/sync operations.
///
/// <see cref="GetSynchronizedDomeCoordinates"/> throws
/// <see cref="NotSupportedException"/> — there is no honest headless sentinel
/// for a computed dome azimuth (same reasoning as the camera stub's exposure
/// members). No registered prototype executes, so this is never reached; the
/// real dome-following math lands with real dome support.
/// </summary>
public sealed class HeadlessDomeFollower : IDomeFollower {

    public bool IsSynchronized => false;
    public bool IsFollowing => false;

    public Task StopAsync() => Task.CompletedTask;
    public Task Start() => Task.CompletedTask;
    public Task<bool> TriggerTelescopeSync(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task WaitForDomeSynchronization(CancellationToken cancellationToken) => Task.CompletedTask;

    public TopocentricCoordinates GetSynchronizedDomeCoordinates(TelescopeInfo telescopeInfo) =>
        throw new NotSupportedException(
            "Headless dome-follower stub does not compute dome coordinates; real dome-following lands with real dome support.");

    public bool IsDomeWithinTolerance(Angle currentDomeAzimuth, TopocentricCoordinates targetDomeCoordinates) => false;

    public Task<bool> SyncToScopeCoordinates(Coordinates coordinates, PierSide sideOfPier, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public event PropertyChangedEventHandler? PropertyChanged;
}
