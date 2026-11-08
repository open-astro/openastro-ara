#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.PlateSolving.Interfaces;
using OpenAstroAra.PlateSolving.Solvers;
using OpenAstroAra.Profile.Interfaces;
using System.Threading;

namespace OpenAstroAra.PlateSolving {

    public class PlateSolverFactoryProxy : IPlateSolverFactory {

        public IPlateSolver GetBlindSolver(IPlateSolveSettings plateSolveSettings) {
            return PlateSolverFactory.GetBlindSolver(plateSolveSettings);
        }

        public IPlateSolver GetPlateSolver(IPlateSolveSettings plateSolveSettings) {
            return PlateSolverFactory.GetPlateSolver(plateSolveSettings);
        }

        public IImageSolver GetImageSolver(IPlateSolver plateSolver, IPlateSolver blindSolver) {
            return new ImageSolver(plateSolver, blindSolver);
        }

        public ICaptureSolver GetCaptureSolver(IPlateSolver plateSolver, IPlateSolver blindSolver, IImagingMediator imagingMediator, IFilterWheelMediator filterWheelMediator) {
            return new CaptureSolver(plateSolver, blindSolver, imagingMediator, filterWheelMediator);
        }

        public ICenteringSolver GetCenteringSolver(IPlateSolver plateSolver, IPlateSolver blindSolver, IImagingMediator imagingMediator, ITelescopeMediator telescopeMediator, IFilterWheelMediator filterWheelMediator, IDomeMediator domeMediator, IDomeFollower domeFollower) {
            return new CenteringSolver(plateSolver, blindSolver, imagingMediator, telescopeMediator, filterWheelMediator, domeMediator, domeFollower);
        }
    }

    public static class PlateSolverFactory {

        // Set once the first removed solver has been substituted, so a long automated run logs it once
        // rather than every solve.
        private static int removedSolverWarned;

        // ADR 0007: ASTAP is the only solver Ara ships. Every other value (the removed AstrometryNet and the
        // NINA solvers) still deserializes from a persisted profile and resolves to ASTAP, with a warning.
        private static ASTAPSolver GetPlateSolver(IPlateSolveSettings plateSolveSettings, string configured, bool removed) {
            if (removed && Interlocked.CompareExchange(ref removedSolverWarned, 1, 0) == 0) {
                Logger.Warning($"Plate solve - solver is configured as {configured}, which was removed (ADR 0007: ASTAP only). Using ASTAP instead.");
            }
            return new ASTAPSolver(plateSolveSettings.ASTAPLocation, plateSolveSettings.ASTAPDatabaseLocation);
        }

        public static IPlateSolver GetPlateSolver(IPlateSolveSettings plateSolveSettings) {
            var type = plateSolveSettings.PlateSolverType;
            return GetPlateSolver(plateSolveSettings, type.ToString(), type != PlateSolver.ASTAP);
        }

        public static IPlateSolver GetBlindSolver(IPlateSolveSettings plateSolveSettings) {
            var type = plateSolveSettings.BlindSolverType;
            return GetPlateSolver(plateSolveSettings, type.ToString(), type != BlindSolver.ASTAP);
        }
    }
}
