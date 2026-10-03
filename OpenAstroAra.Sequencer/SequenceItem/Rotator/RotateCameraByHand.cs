#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

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
using OpenAstroAra.Sequencer.Utility;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Rotator {

    /// <summary>
    /// Rotate the camera BY HAND to a position angle, with the daemon as the protractor. On a rig
    /// without a rotator the plan's framing angle used to be silently dropped (Center and Rotate
    /// centres only — NINA's no-rotator behaviour). This step, emitted before Center and Rotate when
    /// the rig has no rotator, starts the plate-solve readout (<see cref="IRotationAssistExecutor"/>),
    /// parks the run awaiting the user — the same pause-gate dress as Wait for User: run state
    /// <c>paused_awaiting_user</c>, the client's "the rig needs you" banner — and sits INSIDE the
    /// pause (it awaits the gate itself) so the readout stops the moment the user presses Resume and
    /// the following Center and Rotate can take the camera. With a rotator connected the step is a
    /// no-op: Center and Rotate rotates for real.
    ///
    /// Why a readout and not a slew: there is no motor. Like the guide-camera focus card, the daemon
    /// measures and the user turns; the advice (which way, how far) is the client's, relative to the
    /// user's last move, because the daemon cannot know the optical train's mirror flips.
    /// </summary>
    [ExportMetadata("Name", "Lbl_SequenceItem_Rotator_RotateCameraByHand_Name")]
    [ExportMetadata("Description", "Lbl_SequenceItem_Rotator_RotateCameraByHand_Description")]
    [ExportMetadata("Icon", "RotatorSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Rotator")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class RotateCameraByHand : SequenceItem {

        private readonly IRotationAssistExecutor? assist;
        private readonly IRotatorMediator? rotatorMediator;

        [ImportingConstructor]
        public RotateCameraByHand() : this(null, null) {
        }

        public RotateCameraByHand(IRotationAssistExecutor? assist, IRotatorMediator? rotatorMediator) {
            this.assist = assist;
            this.rotatorMediator = rotatorMediator;
        }

        private RotateCameraByHand(RotateCameraByHand cloneMe) : this(cloneMe.assist, cloneMe.rotatorMediator) {
            CopyMetaData(cloneMe);
        }

        private double positionAngle;

        /// <summary>The sky position angle (degrees, east of north) the camera should end up at.</summary>
        [JsonProperty]
        public double PositionAngle {
            get => positionAngle;
            set {
                positionAngle = AstroUtil.EuclidianModulus(value, 360);
                RaisePropertyChanged();
            }
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (rotatorMediator?.GetInfo()?.Connected == true) {
                Logger.Info($"Rotate camera by hand: a rotator is connected — skipped, Center and Rotate rotates to {PositionAngle:0.#}° itself");
                return;
            }
            if (assist is null) {
                throw new SequenceEntityFailedException("Rotate camera by hand: no rotation assist is wired into the sequencer");
            }
            var gate = (ItemUtility.GetRootContainer(this.Parent) as IPauseGateHost)?.PauseGate;
            if (gate is null) {
                // Standalone execution (validation, tests without a run): nothing can wait for a human.
                Logger.Info("Rotate camera by hand: no pause gate on this run — skipped");
                return;
            }

            await assist.StartAsync(PositionAngle, token);
            try {
                Logger.Info($"Rotate camera by hand: readout running toward {PositionAngle:0.#}° — pausing the run awaiting the user");
                progress?.Report(new ApplicationStatus() {
                    Status = $"Rotate the camera to {PositionAngle:0.#}° — follow the readout, then press Resume"
                });
                gate.RequestPause(PauseKind.AwaitingUser);
                // Sit inside the pause: Resume (or an abort) is what ends the readout, so the next
                // instruction never finds the camera still busy solving.
                await gate.WaitWhilePausedAsync(token);
                Logger.Info("Rotate camera by hand: resumed — readout stopped");
            } finally {
                await assist.StopAsync();
            }
        }

        public override object Clone() {
            return new RotateCameraByHand(this) {
                PositionAngle = PositionAngle
            };
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(RotateCameraByHand)}, PositionAngle: {PositionAngle}";
        }
    }
}
