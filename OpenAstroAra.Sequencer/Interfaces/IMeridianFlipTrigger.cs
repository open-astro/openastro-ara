#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Sequencer.Trigger;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Interfaces {

    /// <summary>#1597 — the wire values of <see cref="MeridianFlipForecast.State"/>.</summary>
    public static class MeridianFlipStates {
        public const string DueAt = "due_at";
        public const string WaitingForExposure = "waiting_for_exposure";
        public const string Flipping = "flipping";
        public const string AutoFlipOff = "auto_flip_off";
        public const string NoneTonight = "none_tonight";
    }

    /// <summary>#1597 — when the trigger expects to fire. <c>ExpectedUtc</c> is null when no time applies.</summary>
    public sealed record MeridianFlipForecast(string State, DateTimeOffset? ExpectedUtc);

    public interface IMeridianFlipTrigger : ISequenceTrigger {
        DateTime LatestFlipTime { get; }
        DateTime EarliestFlipTime { get; }

        /// <summary>#1597 — the trigger's own decision, read ahead of time for the run status.</summary>
        MeridianFlipForecast Forecast(DateTimeOffset now);
    }
}