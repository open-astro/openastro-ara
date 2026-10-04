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

// #1265 — fixtures run in parallel, tests inside a fixture stay sequential. The worker count
// defaults to the core count (NUnit's own default), so a 4-core CI runner gets 4 workers and
// a laptop gets more; override with `dotnet test -- NUnit.NumberOfTestWorkers=N`.
//
// A fixture that touches process-wide state — environment variables, the global Serilog
// logger, a fixed port, the daemon's composition root — carries [NonParallelizable], which
// runs it alone (NUnit drains the parallel workers first). Everything else is expected to own
// its resources: ephemeral loopback ports, per-test temp directories, per-test profile stores.
[assembly: Parallelizable(ParallelScope.Fixtures)]
