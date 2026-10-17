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
    /// A capture refused because the frames have nowhere to go: the store drive is not mounted. The
    /// sequencer parks the run awaiting the user on this rather than failing frame after frame — the
    /// night of 2026-10-16 the drive fell off mid-run and every exposure failed in an instant, each one
    /// still firing the dither, so the guider sat "settling" for half an hour. An
    /// <see cref="InvalidOperationException"/> so callers that already handle a failed capture still do.
    /// The message is written for the user.
    /// </summary>
    public sealed class StoreUnavailableException : InvalidOperationException {

        public StoreUnavailableException() : base("The storage disk is not mounted.") {
        }

        public StoreUnavailableException(string message) : base(message) {
        }

        public StoreUnavailableException(string message, Exception inner) : base(message, inner) {
        }
    }
}
