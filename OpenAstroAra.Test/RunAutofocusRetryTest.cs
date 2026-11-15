#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyFocuser;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.Autofocus;
using OpenAstroAra.Sequencer.Trigger.Autofocus;
using OpenAstroAra.Sequencer.Utility;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1892: a failed autofocus sweep is retried, then parks the run awaiting the user
    /// (the Start Guiding pattern) instead of letting the plan image on out of focus.</summary>
    [TestFixture]
    public class RunAutofocusRetryTest {
        private static readonly IProgress<ApplicationStatus> NoProgress = new Progress<ApplicationStatus>();

        private sealed class SyncProgress(Action<ApplicationStatus> report) : IProgress<ApplicationStatus> {
            public void Report(ApplicationStatus value) => report(value);
        }

        /// <summary>Answers the scripted results in order, then succeeds; counts the sweeps.</summary>
        private sealed class ScriptedAutofocus(AutofocusFailure? failure, params bool[] results) : IAutofocusExecutor {
            private readonly Queue<bool> _results = new(results);
            public int Calls;
            public Action? OnCall;
            public AutofocusFailure? LastFailure { get; private set; }

            public Task<bool> RunAutofocusAsync(IProgress<ApplicationStatus> progress, CancellationToken token) {
                Calls++;
                OnCall?.Invoke();
                var ok = _results.Count == 0 || _results.Dequeue();
                LastFailure = ok ? null : failure;
                return Task.FromResult(ok);
            }
        }

        private static IFocuserMediator Focuser() {
            var focuser = new Mock<IFocuserMediator>();
            focuser.Setup(f => f.GetInfo()).Returns(new FocuserInfo { Connected = true });
            return focuser.Object;
        }

        private static (RunAutofocus Item, PauseGate Gate) Rig(IAutofocusExecutor executor) {
            var gate = new PauseGate();
            var root = new SequenceRootContainer { PauseGate = gate };
            var item = new RunAutofocus(executor);
            item.AttachNewParent(root);
            return (item, gate);
        }

        private static async Task<bool> PauseRequested(PauseGate gate) {
            for (var i = 0; i < 100 && !gate.IsPauseRequested; i++) {
                await Task.Delay(20);
            }
            return gate.IsPauseRequested;
        }

        [Test]
        public async Task A_sweep_that_fails_once_is_retried_and_the_run_carries_on_focused() {
            var af = new ScriptedAutofocus(new AutofocusFailure("curve fit unusable"), false, true);
            var (item, gate) = Rig(af);

            await item.Execute(NoProgress, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(af.Calls, Is.EqualTo(2));
            Assert.That(gate.IsPauseRequested, Is.False);
        }

        [Test]
        public async Task Three_failed_sweeps_park_the_run_for_the_user_instead_of_imaging_out_of_focus() {
            var af = new ScriptedAutofocus(new AutofocusFailure("curve fit unusable"), false, false, false, true);
            var (item, gate) = Rig(af);
            string? status = null;

            var execute = item.Execute(new SyncProgress(s => status = s.Status), CancellationToken.None);
            Assert.That(await PauseRequested(gate), Is.True);
            Assert.That(execute.IsCompleted, Is.False, "parked, not failed and not carrying on");
            Assert.That(gate.PendingKind, Is.EqualTo(PauseKind.AwaitingUser));
            Assert.That(af.Calls, Is.EqualTo(3));
            Assert.That(status, Does.Contain("curve fit unusable"));

            gate.Resume();
            await execute.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(af.Calls, Is.EqualTo(4), "Resume tried again");
        }

        [Test]
        public async Task Outside_a_run_three_failed_sweeps_still_throw() {
            var af = new ScriptedAutofocus(new AutofocusFailure("curve fit unusable"), false, false, false);
            var item = new RunAutofocus(af);

            await Assert.ThatAsync(() => item.Execute(NoProgress, CancellationToken.None), Throws.TypeOf<SequenceEntityFailedException>());
            Assert.That(af.Calls, Is.EqualTo(3));
        }

        [Test]
        public async Task A_curve_minimum_at_the_focuser_limit_says_to_rezero_or_move_the_focuser_out() {
            var af = new ScriptedAutofocus(new AutofocusFailure("curve fit unusable (inRange=False)", AtFocuserLimit: true), false, false, false, true);
            var (item, gate) = Rig(af);
            string? status = null;

            var execute = item.Execute(new SyncProgress(s => status = s.Status), CancellationToken.None);
            Assert.That(await PauseRequested(gate), Is.True);
            Assert.That(status, Does.Contain("re-zero the focuser or move it out"));

            gate.Resume();
            await execute.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Test]
        public async Task A_failure_away_from_the_limit_does_not_blame_the_focuser_travel() {
            var af = new ScriptedAutofocus(new AutofocusFailure("only 2 of 9 probes had stars"), false, false, false, true);
            var (item, gate) = Rig(af);
            string? status = null;

            var execute = item.Execute(new SyncProgress(s => status = s.Status), CancellationToken.None);
            Assert.That(await PauseRequested(gate), Is.True);
            Assert.That(status, Does.Not.Contain("re-zero"));

            gate.Resume();
            await execute.WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Test]
        public async Task A_cancelled_token_is_not_retried_and_does_not_pause() {
            using var cts = new CancellationTokenSource();
            var af = new ScriptedAutofocus(null, false) { OnCall = cts.Cancel };
            var (item, gate) = Rig(af);

            await Assert.ThatAsync(() => item.Execute(NoProgress, cts.Token), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(af.Calls, Is.EqualTo(1));
            Assert.That(gate.IsPauseRequested, Is.False);
        }

        [Test]
        public async Task A_sweep_the_user_cancelled_is_not_retried_and_does_not_pause() {
            var af = new ScriptedAutofocus(new AutofocusFailure("cancelled", Cancelled: true), false);
            var (item, gate) = Rig(af);

            await Assert.ThatAsync(() => item.Execute(NoProgress, CancellationToken.None), Throws.TypeOf<SequenceEntityFailedException>());
            Assert.That(af.Calls, Is.EqualTo(1));
            Assert.That(gate.IsPauseRequested, Is.False);
        }

        [Test]
        public async Task An_autofocus_fired_by_the_after_exposures_trigger_parks_the_run_too() {
            var af = new ScriptedAutofocus(new AutofocusFailure("curve fit unusable"), false, false, false, true);
            var gate = new PauseGate();
            var root = new SequenceRootContainer { PauseGate = gate };
            var trigger = new AutofocusAfterExposures { AfterExposures = 1 };
            trigger.TriggerRunner.Add(new RunAutofocus(af));
            trigger.AttachNewParent(root);

            var run = trigger.Run(root, NoProgress, CancellationToken.None);
            Assert.That(await PauseRequested(gate), Is.True, "the RunAutofocus inside the trigger must reach the root's pause gate");
            Assert.That(run.IsCompleted, Is.False);

            gate.Resume();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(af.Calls, Is.EqualTo(4));
        }

        [Test]
        public async Task An_autofocus_fired_by_the_temperature_trigger_parks_the_run_too() {
            var af = new ScriptedAutofocus(new AutofocusFailure("curve fit unusable"), false, false, false, true);
            var gate = new PauseGate();
            var root = new SequenceRootContainer { PauseGate = gate };
            var trigger = new AutofocusAfterTemperatureChangeTrigger(Focuser());
            trigger.TriggerRunner.Add(new RunAutofocus(af));
            trigger.AttachNewParent(root);

            var run = trigger.Run(root, NoProgress, CancellationToken.None);
            Assert.That(await PauseRequested(gate), Is.True);

            gate.Resume();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(af.Calls, Is.EqualTo(4));
        }
    }
}