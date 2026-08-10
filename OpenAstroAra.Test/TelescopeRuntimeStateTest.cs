#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §42.9 runtime-state precedence — including the parked-over-slewing
    /// contradiction guard. A driver/bridge that reports Slewing=true while
    /// AtPark=true is lying or stale (a parked mount cannot be moving; the
    /// AlpacaBridge ZWO vendor did exactly this with no mount attached), so
    /// "parked" must win and the mount must never read as "slewing".
    /// </summary>
    [TestFixture]
    public class TelescopeRuntimeStateTest {

        [TestCase(false, false, false, ExpectedResult = "idle")]
        [TestCase(true, false, false, ExpectedResult = "slewing")]
        [TestCase(false, true, false, ExpectedResult = "parked")]
        [TestCase(false, false, true, ExpectedResult = "tracking")]
        [TestCase(true, true, false, ExpectedResult = "parked")] // guard: parked beats slewing
        [TestCase(true, true, true, ExpectedResult = "parked")] // phantom case: all flags true
        [TestCase(true, false, true, ExpectedResult = "slewing")] // slewing beats tracking
        public string ResolveRuntimeState(bool slewing, bool parked, bool tracking) =>
            TelescopeService.ResolveRuntimeState(slewing, parked, tracking);
    }
}
