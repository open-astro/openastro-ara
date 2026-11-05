#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Platesolving {

    /// <summary>
    /// NINA's "Center and Rotate" — slew to the target, plate-solve, sync + re-slew to centre, then
    /// rotate the camera to <see cref="PositionAngle"/>. Common in real plans (one per target).
    ///
    /// Ported as a first-class type so NINA exports round-trip (Coordinates / PositionAngle /
    /// Inherited survive import) instead of degrading to an <see cref="UnknownSequenceItem"/>.
    ///
    /// EXECUTION: the centre half runs for real through <see cref="ICenteringExecutor"/> (the §28
    /// capture → plate-solve → sync → re-slew loop). With a rotator connected the ROTATE half
    /// runs first (§38 rotation fidelity, NINA's order — solve → sync rotator → folded relative
    /// move until within the profile's rotation tolerance) and then the centre; with no rotator
    /// — the common case — the position angle is preserved in the plan but deliberately not
    /// applied, matching NINA's no-rotator behaviour. Originally tracked in PORT_TODO
    /// alongside the framing position-angle item.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class CenterAndRotate : SequenceItem {

        public CenterAndRotate(ICenteringExecutor? centeringExecutor = null, IRotatorMediator? rotatorMediator = null) {
            this.centeringExecutor = centeringExecutor;
            this.rotatorMediator = rotatorMediator;
            Coordinates = new InputCoordinates();
        }

        private readonly ICenteringExecutor? centeringExecutor;
        private readonly IRotatorMediator? rotatorMediator;

        private bool inherited;

        [JsonProperty]
        public bool Inherited {
            get => inherited;
            set {
                inherited = value;
                RaisePropertyChanged();
            }
        }

        private InputCoordinates coordinates = null!;

        [JsonProperty]
        public InputCoordinates Coordinates {
            get => coordinates;
            set {
                coordinates = value;
                RaisePropertyChanged();
            }
        }

        private double positionAngle;

        [JsonProperty]
        public double PositionAngle {
            get => positionAngle;
            set {
                positionAngle = value;
                RaisePropertyChanged();
            }
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (centeringExecutor is null) {
                // Prototype-only construction (no executor wired into the run engine) —
                // fail loudly rather than silently skipping the centre.
                throw new SequenceEntityFailedException("Center-and-rotate is not wired for sequence execution on this daemon.");
            }
            var target = Coordinates?.Coordinates
                ?? throw new SequenceEntityFailedException("Center-and-rotate has no target coordinates.");
            if (rotatorMediator?.GetInfo()?.Connected == true) {
                // §38 rotation fidelity — the executor rotates to the plan's position angle
                // (solve → sync → folded relative move, rotate-first like NINA) then centres.
                Logger.Info($"Center-and-rotate: rotating to position angle {PositionAngle}° then centering on {target}");
                var result = await centeringExecutor.CenterAndRotateAsync(target, PositionAngle, progress, token);
                if (!result.Success) {
                    // A mis-rotated or un-centred target would quietly ruin every subsequent frame.
                    throw new SequenceEntityFailedException(FailureText(result.FailedStep));
                }
                return;
            }
            // No rotator — the common case, and the only case NINA itself skips rotation in:
            // the position angle stays in the plan but is deliberately not applied.
            Logger.Info($"Center-and-rotate: centering on {target} (position angle {PositionAngle}° preserved; no rotator connected, rotation skipped — matches NINA without a rotator)");
            var centred = await centeringExecutor.CenterAsync(target, progress, token);
            if (!centred.Success) {
                // An un-centred target would quietly ruin every subsequent frame.
                throw new SequenceEntityFailedException(FailureText(centred.FailedStep));
            }
        }

        private const string SlewCheck = "Check the mount (connected, unparked, not at a limit) and the target altitude (above the horizon).";
        private const string CentreCheck = "Check the plate solve (solver set up, focus, exposure) and the centring threshold and attempts.";

        // The text is the run's failure text: name the step that failed and what to check.
        private static string FailureText(CenteringStep step) => step switch {
            CenteringStep.Slew => "Center-and-rotate failed at the slew to the target: the mount refused it. " + SlewCheck,
            CenteringStep.Rotate => "Center-and-rotate failed at the rotation step: the plate solve failed or the rotator did not reach the position angle within the rotation tolerance. "
                + "Check the rotator (connected, moving, rotation tolerance) and the plate solve (solver set up, focus, exposure).",
            _ => "Center-and-rotate failed at the centring step: the target did not centre. " + CentreCheck,
        };

        public override object Clone() {
            return new CenterAndRotate(centeringExecutor, rotatorMediator) {
                Icon = Icon,
                Name = Name,
                Category = Category,
                Description = Description,
                Inherited = Inherited,
                // Null-safe: a malformed/partial import could deserialize Coordinates as null
                // (the [JsonProperty] setter would overwrite the ctor's instance), so don't NPE.
                Coordinates = Coordinates?.Clone() ?? new InputCoordinates(),
                PositionAngle = PositionAngle,
            };
        }
    }
}