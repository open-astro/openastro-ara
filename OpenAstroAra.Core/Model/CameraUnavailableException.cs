#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;

namespace OpenAstroAra.Core.Model {

    /// <summary>
    /// A capture refused because the camera is gone: not connected, or it failed twice running
    /// (a stalled exposure, a driver error). The sequencer parks the run awaiting the user on this
    /// rather than failing frame after frame — the night of 2026-10-21 the camera dropped off USB
    /// during the flats and the run carried on into the bias and darks with no camera. An
    /// <see cref="InvalidOperationException"/> so callers that already handle a failed capture
    /// still do. The message is written for the user.
    /// </summary>
    public sealed class CameraUnavailableException : InvalidOperationException {

        public CameraUnavailableException() : this("The camera stopped responding.") {
        }

        public CameraUnavailableException(string message) : base(message) {
            Cause = message;
        }

        public CameraUnavailableException(string message, Exception inner) : base(message, inner) {
            Cause = message;
        }

        /// <summary>
        /// A camera failure a caller outside a run may see, where nothing waits for Resume: the
        /// message is the <paramref name="cause"/> followed by <paramref name="nextStep"/> (#1436).
        /// A run's pause prompt uses <see cref="Cause"/> alone and adds its own Resume step, so the
        /// next step given here never reaches a paused run.
        /// </summary>
        public CameraUnavailableException(string cause, string? nextStep, Exception? inner)
            : base(nextStep is null ? cause : $"{cause} {nextStep}", inner) {
            Cause = cause;
        }

        /// <summary>What went wrong, without a next step; the run's pause prompt adds its own.</summary>
        public string Cause { get; }
    }
}
