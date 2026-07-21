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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Sequencer.Utility;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Utility {

    /// <summary>
    /// §58.12-b — pause the run for a HUMAN step (screw-in filter swap on an
    /// OSC rig, rotating a manual rotator, removing a dew shield…): arms the
    /// root's pause gate as <see cref="PauseKind.AwaitingUser"/> and returns,
    /// so the engine suspends at the next instruction boundary in the full
    /// awaiting-user dress (sequence.paused event, client "the rig needs you"
    /// banner, unattended-shutdown clock armed). The user's explicit Resume
    /// continues the sequence — the same command that clears every other
    /// awaiting-user pause.
    ///
    /// With no gate wired (standalone container execution, e.g. validation),
    /// the instruction deliberately no-ops rather than blocking a headless
    /// run forever.
    /// </summary>
    [ExportMetadata("Name", "Lbl_SequenceItem_Utility_WaitForUser_Name")]
    [ExportMetadata("Description", "Lbl_SequenceItem_Utility_WaitForUser_Description")]
    [ExportMetadata("Icon", "HandStop_SVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Utility")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class WaitForUser : SequenceItem {

        [ImportingConstructor]
        public WaitForUser() { }

        private WaitForUser(WaitForUser cloneMe) : base(cloneMe) {
        }

        /// <summary>
        /// What the user is being asked to do — shown by the client while the
        /// run waits (e.g. "Switch to the Ha filter, then press Resume").
        /// </summary>
        [JsonProperty]
        public string Text { get; set; } = string.Empty;

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var root = ItemUtility.GetRootContainer(this.Parent);
            if ((root as IPauseGateHost)?.PauseGate is { } gate) {
                Logger.Info($"Wait For User - pausing the sequence awaiting the user{(string.IsNullOrWhiteSpace(Text) ? "" : $": {Text}")}");
                progress?.Report(new ApplicationStatus() {
                    Status = string.IsNullOrWhiteSpace(Text) ? "Waiting for you — press Resume" : Text
                });
                gate.RequestPause(PauseKind.AwaitingUser);
            } else {
                // Standalone/validation execution has no gate — a blocking wait
                // here would hang a headless run with nobody to resume it.
                Logger.Warning("Wait For User - no pause gate wired (standalone container?); continuing without pausing.");
            }
            return Task.CompletedTask;
        }

        public override object Clone() {
            return new WaitForUser(this) {
                Text = Text
            };
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(WaitForUser)}, Text: {Text}";
        }
    }
}
