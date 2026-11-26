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
using OpenAstroAra.Server.Contracts.Refusals;
using System;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// One motion owner. Polar Align holds the mount's motion owner (<see cref="IMountMotionControl.AcquireMotion"/>) for
    /// its whole run, the adjust loop included, and gives it back once the run's exit has stopped the mount: meanwhile the
    /// REST GoTo, Center, Park and Home and every mediator GoTo (the sequencer's) are refused. Nothing that stops the mount
    /// is (Emergency Stop, Stop Mount, AbortSlew, a pad stop, tracking off, the safety, fault and shutdown parks). The run's
    /// own Find Home after Done goes out as the holder's move (<see cref="IMountMotionControl.ActAsHolder"/>), so the
    /// hold never lapses (<see cref="GoHomeAsync"/>). A start while another owner holds the mount, or while the guider guides or
    /// calibrates, is refused with <c>polar-align-mount-busy</c>.
    /// </summary>
    public sealed partial class PolarAlignService {

        /// <summary>The start's guider check: a guider that guides or calibrates (<see cref="GuideCameraCustody.IsBusy"/>)
        /// would move the mount beside Polar Align's turns, so the start is refused before the camera is taken or anything
        /// moves.</summary>
        private void ThrowIfGuiderBusy() {
            if (_guider.AppState is { } state && GuideCameraCustody.IsBusy(state)) {
                var why = $"the guider is {state.ToLowerInvariant()}";
                LogMountBusy(why);
                throw new RefusalException(RefusalCatalog.PolarAlignMountBusy, MountBusyMessage(why));
            }
        }

        internal static string MountBusyMessage(string why) =>
            $"Polar Align moves the mount only while nothing else moves it ({why}) — let the slew finish or stop guiding, then start again";

        /// <summary>Take the mount's motion owner for a run. Another owner holding it refuses the start with
        /// <c>polar-align-mount-busy</c>.</summary>
        private IDisposable AcquireMountForRun() {
            try {
                return _motionControl.AcquireMotion(MotionOwners.PolarAlign);
            } catch (InvalidOperationException ex) when (ex is not RefusalException) {
                LogMountBusy(ex.Message);
                throw new RefusalException(RefusalCatalog.PolarAlignMountBusy, MountBusyMessage(ex.Message));
            }
        }

        [LoggerMessage(Level = LogLevel.Warning, Message = "§45 polar-align: the mount is busy ({Why}) — the start is refused (polar-align-mount-busy)")]
        private partial void LogMountBusy(string why);
    }
}
