#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Runtime.CompilerServices;

// The solver classes are internal; the test project exercises ASTAPSolver's argument builder
// directly. Declared in source because this project sets GenerateAssemblyInfo=false, which
// silently drops the csproj <InternalsVisibleTo> item.
[assembly: InternalsVisibleTo("OpenAstroAra.Test")]
