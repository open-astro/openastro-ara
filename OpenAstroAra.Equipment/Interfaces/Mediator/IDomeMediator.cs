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
using OpenAstroAra.Core.Enum;
using OpenAstroAra.Equipment.Equipment.MyDome;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Interfaces.Mediator {

    public interface IDomeMediator : IDeviceMediator<object, IDomeConsumer, DomeInfo> {
        bool IsFollowingScope { get; }

        Task WaitForDomeSynchronization(CancellationToken cancellationToken);

        Task<bool> SyncToScopeCoordinates(Coordinates coordinates, PierSide sideOfPier, CancellationToken cancellationToken);

        Task<bool> OpenShutter(CancellationToken cancellationToken);

        Task<bool> CloseShutter(CancellationToken cancellationToken);

        Task<bool> EnableFollowing(CancellationToken cancellationToken);

        Task<bool> DisableFollowing(CancellationToken cancellationToken);

        Task<bool> Park(CancellationToken cancellationToken);

        Task<bool> FindHome(CancellationToken cancellationToken);

        Task<bool> SlewToAzimuth(double degrees, CancellationToken cancellationToken);
        event EventHandler<EventArgs> Synced;
        event Func<object, EventArgs, Task> Opened;
        event Func<object, EventArgs, Task> Closed;
        event Func<object, EventArgs, Task> Parked;
        event Func<object, EventArgs, Task> Homed;
        event Func<object, DomeEventArgs, Task> Slewed;
    }


    public class DomeEventArgs : EventArgs {
        public DomeEventArgs(double from, double to) {
            From = from;
            To = to;
        }

        public double From { get; }
        public double To { get; }
    }
}