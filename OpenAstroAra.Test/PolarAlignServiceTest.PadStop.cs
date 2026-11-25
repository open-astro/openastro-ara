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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2066 — a pad stop (a rate-0 <c>/moveaxis</c> Polar Align did not send) while Polar Align turns the mount is a stop
    /// from outside: it is counted before the axis stops, the run ends with <c>slew_aborted_externally</c>, and the
    /// routine's next lease renewal does not start the motor again. On <see cref="FakeGemMount"/>.
    /// </summary>
    public partial class PolarAlignServiceTest {

        // The pad's MoveAxis 0 stops the axis but cancels no count: the routine's next lease renewal found the axis still and
        // started the motor again. Counted as a stop first, the renewal is refused and the run ends.
        [Test]
        public async Task A_pad_stop_mid_turn_ends_the_run_and_the_motor_is_not_restarted() {
            var mount = GemAt(-4.0, 85.0, lstHours: 20.0);
            var ws = new WsRecorder();
            using var svc = NewGemService(mount, ws, benchMotors: false); // NINA's 10° steps at the daemon's speeds
            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await mount.FirstMove.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
            Assert.That(svc.IsMovingMount, Is.True, "mid-turn");

            var at = mount.Now;
            await EquipmentEndpoints.MoveAxisAsync(new MoveAxisRequestDto(0, 0.0), mount, CancellationToken.None, svc).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("failed"), mount.Describe());
            var error = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.SlewAbortedExternally));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("direction pad"));
            Assert.That(RaTurns(mount).Where(t => t.StartedAt >= at), Is.Empty, "the motor was not started again. " + mount.Describe());
            mount.AssertEndedStopped(tracking: false);
            mount.AssertCommon(LastEventIndex(ws, WsEventCatalog.PolarAlignError), CounterweightLimitDeg);
        }
    }
}
