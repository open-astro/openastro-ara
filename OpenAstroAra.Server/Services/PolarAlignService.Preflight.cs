#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    public sealed partial class PolarAlignService {

        /// <summary>#2068 — one start's pre-flight: the list, the settings it read, and the confirm step's plan with the
        /// run's band and sweep (null when the plan could not be made; the list then fails its start-pointing check).</summary>
        private sealed record StartPreflight(
            PolarAlignPreflightResult Result,
            PolarAlignSettingsDto Settings,
            (PolarAlignStartPlanDto Plan, RunBand Band, SweepPlan Sweep)? Start);

        /// <inheritdoc />
        public async Task<PolarAlignPreflightDto> GetPreflightAsync(string? stage, CancellationToken ct) {
            if (stage is not null && !PolarAlignStages.IsValid(stage)) {
                throw new ArgumentException($"unknown polar-align stage '{stage}' — use coarse, fine or check");
            }
            var preflight = await EvaluatePreflightAsync(stage ?? PolarAlignStages.Fine, ct).ConfigureAwait(false);
            return preflight.Result.Dto;
        }

        /// <summary>#2068 (ARA-18) — the pre-flight list for a start at <paramref name="stage"/>
        /// (<see cref="PolarAlignPreflightEvaluator"/>): the same checks the app's Start card shows. Never throws.</summary>
        private async Task<StartPreflight> EvaluatePreflightAsync(string stage, CancellationToken ct) {
            PolarAlignSettingsDto? settings = null;
            (PolarAlignStartPlanDto Plan, RunBand Band, SweepPlan Sweep)? start = null;
            var inputs = new PolarAlignPreflightInputs(
                stage,
                MountInfo: _mount.GetInfo,
                Site: _profileStore.GetSiteSettings,
                Settings: () => settings = PolarAlignSettingsClamp.Apply(_profileStore.GetPolarAlignSettings()),
                ClockRefusal: _mountClock is { } clock ? clock.MountClockMotionRefusal : null,
                PoseUnverifiedReason: _motionControl.PositionUnverifiedReasonAsync,
                Plan: async token => {
                    var planned = await PlanStartAsync(stage, settings ?? throw new InvalidOperationException("the polar-align settings could not be read"), token).ConfigureAwait(false);
                    start = planned;
                    return planned.Plan;
                },
                AlignerPresent: _alignerSource is { } aligners ? () => aligners.Current() is not null : null);
            var result = await PolarAlignPreflightEvaluator.EvaluateAsync(inputs, ct).ConfigureAwait(false);
            return new StartPreflight(result, settings ?? PolarAlignSettingsClamp.Apply(new PolarAlignSettingsDto()), start);
        }

        /// <summary>#2068 — a start refuses on the first check that fails, with that check's own cause (the mount not
        /// connected or parked, no site latitude, #2087's mount clock, #2070's position, the clearance not confirmed, the
        /// start pointing's refusals). A <c>warn</c> or <c>unknown</c> check never refuses.</summary>
        private static void ThrowOnFirstFailure(StartPreflight preflight) {
            if (preflight.Result.FirstFailure is { } failure) {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        /// <summary>The run's plan, band and sweep, once <see cref="ThrowOnFirstFailure"/> let the start through (the
        /// start-pointing check passed, so the plan was made).</summary>
        private static (PolarAlignStartPlanDto Plan, RunBand Band, SweepPlan Sweep) StartPlanOf(StartPreflight preflight) =>
            preflight.Start ?? throw new InvalidOperationException("polar alignment could not plan the start — try again");
    }
}
