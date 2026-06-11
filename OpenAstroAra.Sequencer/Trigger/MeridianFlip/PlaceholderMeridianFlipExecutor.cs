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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Trigger.MeridianFlip {

    /// <summary>
    /// Placeholder <see cref="IMeridianFlipExecutor"/> so MEF composition of <see cref="MeridianFlipTrigger"/>
    /// succeeds before the real §58.4 orchestration lands (its own sub-PR). It deliberately THROWS rather than
    /// no-ops: a meridian flip that silently doesn't happen risks the OTA swinging into the mount/tripod, so a
    /// sequence that actually reaches a flip with no real executor wired must fail loudly, not continue as if
    /// the flip succeeded.
    /// </summary>
    [Export(typeof(IMeridianFlipExecutor))]
    public class PlaceholderMeridianFlipExecutor : IMeridianFlipExecutor {

        public Task<bool> MeridianFlip(Coordinates targetCoordinates, TimeSpan timeToFlip, IProgress<ApplicationStatus> progress, CancellationToken token) {
            Logger.Error("Meridian Flip - no flip executor is wired (the §58.4 orchestration is not implemented yet). Refusing to continue rather than skip a required flip.");
            throw new NotImplementedException("Meridian flip orchestration (§58.4) is not implemented yet.");
        }
    }
}
