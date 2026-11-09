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
using OpenAstroAra.Astrometry.Interfaces;
using OpenAstroAra.Astrometry.RiseAndSet;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Equipment.Equipment.MyCamera;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.Utility.DateTimeProvider;
using OpenAstroAra.Sequencer;
using OpenAstroAra.Server.Services;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace OpenAstroAra.Test {

    /// <summary>#1657 — Simple Mode's queue relies on a dawn <see cref="TimeCondition"/> ending a target
    /// block while the root carries on to Park Scope. Nothing cancels the run's own token.</summary>
    [TestFixture]
    public class DawnStopReachesParkTest {

        private static readonly DateTime Day = new(2026, 7, 1);
        private static readonly DateTime Dawn = Day.AddHours(5);
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

        private sealed class Clock : ICustomDateTime {
            public DateTime Now { get; set; }
            public DateTime UtcNow => Now;
        }

        private sealed class RunsUntilCancelled : OpenAstroAra.Sequencer.SequenceItem.SequenceItem {
            public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Cancelled { get; private set; }
            public override object Clone() => new RunsUntilCancelled();
            public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
                Started.TrySetResult();
                try {
                    await Task.Delay(Timeout.Infinite, token);
                } catch (OperationCanceledException) {
                    Cancelled = true;
                    throw;
                }
            }
        }

        private sealed class ParkStandIn : OpenAstroAra.Sequencer.SequenceItem.SequenceItem {
            public int Ran { get; private set; }
            public override object Clone() => new ParkStandIn();
            public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
                Ran++;
                return Task.CompletedTask;
            }
        }

        private static TimeCondition DawnCondition(Clock clock) {
            var provider = new Mock<IDateTimeProvider>();
            provider.Setup(p => p.Name).Returns("Nautical Dawn");
            provider.Setup(p => p.GetDateTime(It.IsAny<ISequenceEntity>())).Returns(Dawn);
            provider.Setup(p => p.GetRolloverTime(It.IsAny<ISequenceEntity>())).Returns(new TimeOnly(12, 0));
            return new TimeCondition(new List<IDateTimeProvider> { provider.Object }, provider.Object) { DateTime = clock };
        }

        private static (SequenceRootContainer root, RunsUntilCancelled item, ParkStandIn park, TimeCondition[] stops) Queue(Clock clock) {
            var blockStop = DawnCondition(clock);
            var loopStop = DawnCondition(clock);
            var item = new RunsUntilCancelled();
            var loop = new SequentialContainer();
            loop.Add(loopStop);
            loop.Add(item);
            var block = new SequentialContainer();
            block.Add(blockStop);
            block.Add(loop);
            var park = new ParkStandIn();
            var root = new SequenceRootContainer();
            root.Add(block);
            root.Add(park);
            return (root, item, park, new[] { blockStop, loopStop });
        }

        [Test]
        public async Task Dawn_during_a_target_block_cancels_its_item_and_the_root_goes_on_to_park() {
            var clock = new Clock { Now = Dawn.AddSeconds(-2) };
            var (root, item, park, stops) = Queue(clock);
            using var run = new CancellationTokenSource();
            try {
                var running = root.Run(new Progress<ApplicationStatus>(), run.Token);
                await item.Started.Task.WaitAsync(Deadline);

                clock.Now = Dawn.AddSeconds(5);
                await running.WaitAsync(Deadline);

                Assert.Multiple(() => {
                    Assert.That(item.Cancelled, Is.True, "dawn ends the running item");
                    Assert.That(park.Ran, Is.EqualTo(1), "the root carries on to Park Scope");
                    Assert.That(root.Status, Is.EqualTo(SequenceEntityStatus.FINISHED));
                    Assert.That(run.IsCancellationRequested, Is.False, "an interrupt, not a stop");
                });
            } finally {
                foreach (var stop in stops) { stop.ConditionWatchdog?.Cancel(); }
            }
        }

        [Test]
        public async Task A_target_block_that_starts_after_dawn_is_skipped_and_the_root_still_parks() {
            var clock = new Clock { Now = Dawn.AddMinutes(10) };
            var (root, item, park, stops) = Queue(clock);
            using var run = new CancellationTokenSource();
            try {
                await root.Run(new Progress<ApplicationStatus>(), run.Token).WaitAsync(Deadline);

                Assert.Multiple(() => {
                    Assert.That(item.Started.Task.IsCompleted, Is.False, "the expired block runs nothing");
                    Assert.That(park.Ran, Is.EqualTo(1));
                    Assert.That(root.Status, Is.EqualTo(SequenceEntityStatus.FINISHED));
                    Assert.That(run.IsCancellationRequested, Is.False);
                });
            } finally {
                foreach (var stop in stops) { stop.ConditionWatchdog?.Cancel(); }
            }
        }
    }
}
