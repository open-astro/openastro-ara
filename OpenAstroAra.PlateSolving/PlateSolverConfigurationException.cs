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

namespace OpenAstroAra.PlateSolving {

    /// <summary>
    /// A user-fixable plate-solver SETUP error (solver executable missing/wrong/too-old, etc.) — distinct from
    /// "the solver ran but found no solution" (which returns <c>PlateSolveResult.Success = false</c>, not an
    /// exception). The API layer catches this public base to return a 422 with the message rather than a 500,
    /// so the specific (internal) solver exception types don't have to be reachable outside the assembly.
    /// </summary>
    public class PlateSolverConfigurationException : Exception {
        public PlateSolverConfigurationException() {
        }

        public PlateSolverConfigurationException(string message) : base(message) {
        }

        public PlateSolverConfigurationException(string message, Exception innerException) : base(message, innerException) {
        }
    }
}
