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
using OpenAstroAra.Core.Interfaces;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyGuider;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Interfaces.Mediator {

    public interface IGuiderMediator : IDeviceMediator<object, IGuiderConsumer, GuiderInfo> {

        Task<bool> Dither(CancellationToken token);

        /// <summary>The verdict of the last <see cref="Dither"/>: null when the guider settled, otherwise why
        /// it did not ("refused" when it was not guiding or not connected; "the guider is calibrating" or
        /// "the guider is paused" when it was busy with one of those, which the Dither instruction parks on
        /// instead of restarting guiding; the guider's own message when it rejected the dither RPC; its
        /// settle error; or "cancelled"). The Dither
        /// instruction reads it so a frame never starts on a star still swinging from a failed settle
        /// (2026-10-16: two settle timeouts, two trailed frames).</summary>
        string? LastDitherError { get; }

        Guid StartRMSRecording();

        RMS GetRMSRecording(Guid handle);

        RMS StopRMSRecording(Guid handle);

        Task<bool> StartGuiding(bool forceCalibration, IProgress<ApplicationStatus> progress, CancellationToken token);

        Task<bool> StopGuiding(CancellationToken token);

        Task<bool> AutoSelectGuideStar(CancellationToken token);

        Task<bool> ClearCalibration(CancellationToken token);

        Task<bool> SetShiftRate(SiderealShiftTrackingRate shiftTrackingRate, CancellationToken ct);

        Task<bool> StopShifting(CancellationToken ct);

        LockPosition GetLockPosition();

        event Func<object, EventArgs, Task> Dithered;

        event EventHandler<IGuideStep> GuideEvent;
        event Func<object, EventArgs, Task> GuidingStarted;
        event Func<object, EventArgs, Task> GuidingStopped;
    }
}