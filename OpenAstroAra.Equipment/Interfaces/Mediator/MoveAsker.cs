#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Equipment.Interfaces.Mediator {

    /// <summary>
    /// #2139 (record 0026) — who asks to move the mount. Move Admission checks each asker for its own set of rules, so
    /// every slew, park and home names its asker explicitly: there is no default that would hide a caller that was missed.
    /// </summary>
    public enum MoveAsker {
        /// <summary>A person at the app: the REST GoTo, park and home.</summary>
        Person,
        /// <summary>A Session's instruction: a sequence slew, park or home.</summary>
        Session,
        /// <summary>Center (plate solve and re-slew), whoever started it.</summary>
        Center,
        /// <summary>The meridian flip: its GoTo, and the park after a failed flip.</summary>
        MeridianFlip,
        /// <summary>End Night's and the unattended shutdown's ladder.</summary>
        Shutdown,
        /// <summary>The safety and equipment-fault parks.</summary>
        SafetyPark,
        /// <summary>Target recenter: asks up front, before it starts a Center.</summary>
        TargetRecenter,
        /// <summary>A Session's resume refinement: asks up front, before it re-centres.</summary>
        ResumeRefinement,
    }
}
