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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.Interfaces;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.FlatDevice;
using OpenAstroAra.Sequencer.Trigger;
using OpenAstroAra.Sequencer.Utility;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>A user's pause stops what the plan started (user, 2026-11-11): an instruction or a
    /// trigger in progress (autofocus, a wait, centring) is interrupted and runs again on Resume.
    /// The frame in hand and a meridian flip still finish first.</summary>
    [TestFixture]
    public class PauseInterruptsPlanWorkTest {

        private static readonly IProgress<ApplicationStatus> NoProgress = new Progress<ApplicationStatus>();

        /// <summary>Waits until its token is cancelled, the first time; finishes at once after.</summary>
        private class SlowItem : SequenceItem {
            public int Starts;
            public bool Finished;
            public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
                Starts++;
                Started.TrySetResult();
                if (Starts == 1) {
                    await Release.Task.WaitAsync(token);
                }
                Finished = true;
            }

            public override object Clone() => new SlowItem();
        }

        /// <summary>A frame: finishes only when released, whatever the pause.</summary>
        private sealed class SlowExposure : SlowItem, IExposureItem {
            public double ExposureTime { get; set; } = 1;
            public int Gain { get; set; }
            public int Offset { get; set; }
            public string ImageType { get; set; } = "LIGHT";
            public BinningMode Binning { get; set; } = new(1, 1);
            public override object Clone() => new SlowExposure();
        }

        private sealed class AfterItem : SequenceItem {
            public bool Ran;
            public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
                Ran = true;
                return Task.CompletedTask;
            }
            public override object Clone() => new AfterItem();
        }

        /// <summary>Fires before the first item until it has finished once (an autofocus).</summary>
        private class SlowTrigger : SequenceTrigger {
            public int Starts;
            public bool Finished;
            public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public override bool ShouldTrigger(ISequenceItem? previousItem, ISequenceItem? nextItem) => !Finished;

            public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
                Starts++;
                Started.TrySetResult();
                if (Starts == 1) {
                    await Release.Task.WaitAsync(token);
                }
                Finished = true;
            }

            public override object Clone() => new SlowTrigger();
        }

        /// <summary>A trigger that must never be cut short (a meridian flip).</summary>
        private sealed class FlipLikeTrigger : SlowTrigger {
            protected override bool RunsThroughPause => true;
            public override object Clone() => new FlipLikeTrigger();
        }

        private static (SequentialContainer Block, PauseGate Gate, TaskCompletionSource Entered) Plan(params ISequenceItem[] items) {
            var gate = new PauseGate();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.PauseEntered += (_, _) => entered.TrySetResult();
            var root = new SequenceRootContainer { PauseGate = gate };
            var block = new SequentialContainer();
            block.AttachNewParent(root);
            foreach (var item in items) {
                block.Add(item);
            }
            return (block, gate, entered);
        }

        private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

        [Test]
        public async Task A_pause_interrupts_the_instruction_in_progress_and_resume_runs_it_again() {
            var slow = new SlowItem();
            var after = new AfterItem();
            var (block, gate, entered) = Plan(slow, after);

            var run = block.Execute(NoProgress, CancellationToken.None);
            await slow.Started.Task.WaitAsync(Soon);
            gate.RequestPause();

            await entered.Task.WaitAsync(Soon);
            Assert.That(slow.Finished, Is.False, "the pause stopped it, it did not wait for it");
            Assert.That(after.Ran, Is.False);

            gate.Resume();
            await run.WaitAsync(Soon);
            Assert.That((slow.Starts, slow.Finished, after.Ran), Is.EqualTo((2, true, true)), "it ran again, then the plan went on");
        }

        [Test]
        public async Task A_pause_interrupts_a_trigger_in_progress_and_resume_runs_it_again() {
            var after = new AfterItem();
            var (block, gate, entered) = Plan(after);
            var trigger = new SlowTrigger();
            block.Add(trigger);

            var run = block.Execute(NoProgress, CancellationToken.None);
            await trigger.Started.Task.WaitAsync(Soon);
            gate.RequestPause();

            await entered.Task.WaitAsync(Soon);
            Assert.That(trigger.Finished, Is.False, "an autofocus in progress stops with the pause");
            Assert.That(after.Ran, Is.False);

            gate.Resume();
            await run.WaitAsync(Soon);
            Assert.That((trigger.Starts, trigger.Finished, after.Ran), Is.EqualTo((2, true, true)));
        }

        [Test]
        public async Task A_pause_lets_the_frame_in_progress_finish() {
            var frame = new SlowExposure();
            var after = new AfterItem();
            var (block, gate, entered) = Plan(frame, after);

            var run = block.Execute(NoProgress, CancellationToken.None);
            await frame.Started.Task.WaitAsync(Soon);
            gate.RequestPause();
            await Task.Delay(200);
            Assert.That(entered.Task.IsCompleted, Is.False, "the frame in hand is still exposing");

            frame.Release.SetResult();
            await entered.Task.WaitAsync(Soon);
            Assert.That((frame.Starts, frame.Finished, after.Ran), Is.EqualTo((1, true, false)), "the frame filed once; the pause lands after it");

            gate.Resume();
            await run.WaitAsync(Soon);
            Assert.That(after.Ran, Is.True);
        }

        [Test]
        public async Task A_pause_lets_a_meridian_flip_finish() {
            var after = new AfterItem();
            var (block, gate, entered) = Plan(after);
            var flip = new FlipLikeTrigger();
            block.Add(flip);

            var run = block.Execute(NoProgress, CancellationToken.None);
            await flip.Started.Task.WaitAsync(Soon);
            gate.RequestPause();
            await Task.Delay(200);
            Assert.That(entered.Task.IsCompleted, Is.False, "a flip is never cut short");

            flip.Release.SetResult();
            await entered.Task.WaitAsync(Soon);
            Assert.That((flip.Starts, flip.Finished), Is.EqualTo((1, true)));

            gate.Resume();
            await run.WaitAsync(Soon);
        }

        [Test]
        public async Task An_instructions_own_wait_for_the_user_does_not_interrupt_it() {
            var slow = new SlowItem();
            var (block, gate, _) = Plan(slow, new AfterItem());

            var run = block.Execute(NoProgress, CancellationToken.None);
            await slow.Started.Task.WaitAsync(Soon);
            gate.RequestPause(PauseKind.AwaitingUser);
            await Task.Delay(200);
            Assert.That(slow.Release.Task.IsCanceled || slow.Finished, Is.False, "still running: only a user's pause interrupts");

            slow.Release.SetResult();
            await Task.Delay(100);
            gate.Resume();
            await run.WaitAsync(Soon);
            Assert.That(slow.Starts, Is.EqualTo(1));
        }

        /// <summary>Takes its whole flat set in one call that honours its token, as the real executor does.</summary>
        private sealed class SlowFlatExecutor : IFlatCaptureExecutor {
            public int Calls;
            public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public async Task<bool> CaptureFlatSetAsync(FlatSetRequest request, IProgress<ApplicationStatus> progress, CancellationToken token) {
                Calls++;
                Started.TrySetResult();
                await Release.Task.WaitAsync(token);
                return true;
            }
            public Task<bool> CaptureSkyFlatSetAsync(SkyFlatSetRequest request, IProgress<ApplicationStatus> progress, CancellationToken token) =>
                CaptureFlatSetAsync(null!, progress, token);
            public Task<bool> OpenCoverAsync(CancellationToken token) => Task.FromResult(true);
            public Task<bool> CloseCoverAsync(CancellationToken token) => Task.FromResult(true);
        }

        [Test]
        public async Task A_pause_lets_the_flat_set_in_progress_finish() {
            // A flat set takes its frames inside one instruction; stopping it mid-set meant Resume shot the whole set again.
            var flats = new SlowFlatExecutor();
            var after = new AfterItem();
            var (block, gate, entered) = Plan(new FlatPanelFlats(flats), after);

            var run = block.Execute(NoProgress, CancellationToken.None);
            await flats.Started.Task.WaitAsync(Soon);
            gate.RequestPause();
            await Task.Delay(200);
            Assert.That(entered.Task.IsCompleted, Is.False, "the flat set is still taking its frames");

            flats.Release.SetResult();
            await entered.Task.WaitAsync(Soon);
            Assert.That((flats.Calls, after.Ran), Is.EqualTo((1, false)), "the set ran once; the pause lands after it");

            gate.Resume();
            await run.WaitAsync(Soon);
            Assert.That((flats.Calls, after.Ran), Is.EqualTo((1, true)));
        }
    }
}
