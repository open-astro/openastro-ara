#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Sequencer.Interfaces {

    /// <summary>
    /// §59.9 — the condition gate every autofocus trigger consults before firing: an autofocus
    /// during passing clouds / a blocked aperture / dew formation just fails and wastes sky
    /// time, so the trigger defers and retries on its next check (the conditions are
    /// level-based — they re-fire naturally once the sky recovers). Implemented server-side
    /// over the §51 diagnostics state; a missing seam or a broken diagnostics read means
    /// "don't defer" — diagnostics must never be able to freeze focusing.
    /// </summary>
    public interface IAutofocusConditionGate {

        /// <summary>
        /// Non-null human-readable reason while autofocus should wait (e.g. "clouds passing");
        /// null when conditions are fine to focus in.
        /// </summary>
        string? DeferralReason();
    }
}
