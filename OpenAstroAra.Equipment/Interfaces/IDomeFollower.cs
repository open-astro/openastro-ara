#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Interfaces {

    public interface IDomeFollower : INotifyPropertyChanged {
        bool IsSynchronized { get; }
        bool IsFollowing { get; }

        Task Stop();

        Task Start();

        Task<bool> TriggerTelescopeSync();

        Task WaitForDomeSynchronization(CancellationToken cancellationToken);

        TopocentricCoordinates GetSynchronizedDomeCoordinates(TelescopeInfo telescopeInfo);

        bool IsDomeWithinTolerance(Angle currentDomeAzimuth, TopocentricCoordinates targetDomeCoordinates);

        Task<bool> SyncToScopeCoordinates(Coordinates coordinates, PierSide sideOfPier, CancellationToken cancellationToken);
    }
}