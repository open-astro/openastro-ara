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
using System;
using System.IO;
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>
    /// ADR 0025 rule 5 (#2143): a stop ladder ends routines through <see cref="RigOccupancy"/> and names no routine. The
    /// routines are the ones a <c>RigOccupancy.Register…</c> helper takes (read by reflection, so a new routine is checked
    /// once it registers); the Session's helper is left out, since each ladder keeps its own run step.
    /// </summary>
    [TestFixture]
    public class StopLadderScanTest {

        private static readonly string[] Ladders = {
            "OpenAstroAra.Server/Services/EmergencyStopService.cs",
            "OpenAstroAra.Server/Services/ReactionMountStops.cs",
            "OpenAstroAra.Server/Services/ShutdownLadder.cs",
        };

        private static string FindRepoRoot() {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenAstroAra.sln"))) {
                dir = dir.Parent;
            }
            Assert.That(dir, Is.Not.Null, "the repository root (OpenAstroAra.sln) was not found above the test binaries");
            return dir!.FullName;
        }

        /// <summary>What names a routine in source: the service type each register helper reads. The camera service also
        /// carries a ladder's own steps (abort the exposure, the cooler), so for it the routine is its Live View members.</summary>
        private static string[] RoutineNames() =>
            typeof(RigOccupancy).GetMethods()
                .Where(m => m.Name.StartsWith("Register", StringComparison.Ordinal) && m.Name != nameof(RigOccupancy.RegisterSession))
                .SelectMany(m => m.GetParameters())
                .Select(p => p.ParameterType)
                .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Func<>))
                .Select(t => t.GetGenericArguments()[0])
                .Select(t => t == typeof(ICameraService) ? "LiveView" : t.Name)
                .Distinct()
                .ToArray();

        [Test]
        public void No_stop_ladder_names_a_registered_routine() {
            var names = RoutineNames();
            Assert.That(names, Does.Contain(nameof(IPolarAlignService)).And.Contain(nameof(ICenteringService)), "the helpers were read");
            var root = FindRepoRoot();

            var offenders = Ladders
                .SelectMany(ladder => names
                    .Where(name => File.ReadAllText(Path.Combine(root, ladder)).Contains(name, StringComparison.Ordinal))
                    .Select(name => $"{Path.GetFileName(ladder)} names {name}"))
                .ToList();

            Assert.That(offenders, Is.Empty, "a ladder stops a routine through RigOccupancy.StopAsync, not by its service");
        }
    }
}
