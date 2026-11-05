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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Astrometry.Interfaces;
using OpenAstroAra.Astrometry.RiseAndSet;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Sequencer;
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using OpenAstroAra.Sequencer.Utility.DateTimeProvider;

namespace OpenAstroAra.Test {

    // #1576 - a plain clock time targets its next occurrence from now. The back-a-day rule
    // stays only for night-anchored (fixed) providers.
    [TestFixture]
    public class PlainClockNextOccurrenceTest {
        private static readonly DateTime Day = new DateTime(2026, 11, 5);

        private static ICustomDateTime Clock(int h, int m) {
            var c = new Mock<ICustomDateTime>();
            c.Setup(x => x.Now).Returns(Day.AddHours(h).AddMinutes(m));
            return c.Object;
        }

        // dawn == null: no sunrise data, rollover 12:00.
        private static OpenAstroAra.Sequencer.Utility.DateTimeProvider.TimeProvider Plain(TimeOnly? dawn) {
            var calc = new Mock<INighttimeCalculator>();
            RiseAndSetEvent? sun = null;
            if (dawn.HasValue) {
                var ev = new Mock<RiseAndSetEvent>(Day, -33.856784, 151.215297, 42d) { CallBase = true };
                ev.SetupGet(e => e.Rise).Returns(Day.Add(dawn.Value.ToTimeSpan()));
                sun = ev.Object;
            }
            var data = new NighttimeData(Day, Day, AstroUtil.MoonPhase.Unknown, null, null, null, sun, null, null);
            calc.Setup(c => c.Calculate(It.IsAny<DateTime?>())).Returns(data);
            return new OpenAstroAra.Sequencer.Utility.DateTimeProvider.TimeProvider(calc.Object);
        }

        private static IDateTimeProvider Fixed(TimeOnly rollover) {
            var p = new Mock<IDateTimeProvider>();
            p.Setup(x => x.Name).Returns("fixed");
            p.Setup(x => x.GetDateTime(It.IsAny<ISequenceEntity>())).Returns(Day.AddHours(1));
            p.Setup(x => x.GetRolloverTime(It.IsAny<ISequenceEntity>())).Returns(rollover);
            return p.Object;
        }

        private static WaitForTime Wait(IDateTimeProvider p, int nowH, int nowM, int h, int m) {
            var w = new WaitForTime(new List<IDateTimeProvider> { p }, p) { DateTime = Clock(nowH, nowM) };
            w.Hours = h; w.Minutes = m; w.Seconds = 0;
            return w;
        }

        private static TimeCondition Cond(IDateTimeProvider p, int nowH, int nowM, int h, int m) {
            var c = new TimeCondition(new List<IDateTimeProvider> { p }, p) { DateTime = Clock(nowH, nowM) };
            c.Hours = h; c.Minutes = m; c.Seconds = 0;
            return c;
        }

        // now, target, dawn, expected wait
        private static readonly object[] PlainCases = {
            new object[] { 0, 30, 21, 47, null!, new TimeSpan(21, 17, 0) },   // was: ran at once
            new object[] { 11, 0, 13, 0, null!, TimeSpan.FromHours(2) },      // was: ran at once
            new object[] { 22, 0, 2, 0, null!, TimeSpan.FromHours(4) },       // already worked
            new object[] { 22, 0, 6, 30, new TimeOnly(6, 0), new TimeSpan(8, 30, 0) },
            new object[] { 22, 30, 22, 0, null!, new TimeSpan(23, 30, 0) },   // was: ran at once (late start)
        };

        [TestCaseSource(nameof(PlainCases))]
        public void WaitForTime_plain_time_waits_for_next_occurrence(int nh, int nm, int h, int m, TimeOnly? dawn, TimeSpan expected) {
            Assert.That(Wait(Plain(dawn), nh, nm, h, m).GetEstimatedDuration(), Is.EqualTo(expected));
        }

        [TestCaseSource(nameof(PlainCases))]
        public void TimeCondition_plain_time_remaining_is_next_occurrence(int nh, int nm, int h, int m, TimeOnly? dawn, TimeSpan expected) {
            Assert.That(Cond(Plain(dawn), nh, nm, h, m).RemainingTime, Is.EqualTo(expected));
        }

        [Test]
        public void TimeCondition_plain_time_Check_holds_until_next_occurrence() {
            var next = new Mock<OpenAstroAra.Sequencer.SequenceItem.ISequenceItem>();
            next.Setup(n => n.GetEstimatedDuration()).Returns(TimeSpan.FromHours(1));
            // 00:30 -> 21:47: 21 h 17 m remain, a 1 h item fits.
            Assert.That(Cond(Plain(null), 0, 30, 21, 47).Check(null, next.Object), Is.True);
        }

        // 22:00 -> 02:00: a 5 h item does not fit and sets the cut-off. The cut-off is the target
        // plus 10 s (tomorrow 02:00:10), not today's 02:00:10, so a 1 h item after it does not run.
        [Test]
        public void TimeCondition_plain_time_cutoff_is_on_the_target_day() {
            var item5h = new Mock<OpenAstroAra.Sequencer.SequenceItem.ISequenceItem>();
            item5h.Setup(n => n.GetEstimatedDuration()).Returns(TimeSpan.FromHours(5));
            var item1h = new Mock<OpenAstroAra.Sequencer.SequenceItem.ISequenceItem>();
            item1h.Setup(n => n.GetEstimatedDuration()).Returns(TimeSpan.FromHours(1));
            var c = Cond(Plain(null), 22, 0, 2, 0);
            Assert.That(c.Check(null, item5h.Object), Is.False);
            Assert.That(c.Check(null, item1h.Object), Is.False);
        }

        // Night-anchored: rollover 12:00. now 00:30 < rollover, target 21:47 >= rollover -> yesterday (zero wait).
        [Test]
        public void WaitForTime_fixed_provider_keeps_the_back_a_day_rule() {
            Assert.That(Wait(Fixed(new TimeOnly(12, 0)), 0, 30, 21, 47).GetEstimatedDuration(), Is.EqualTo(TimeSpan.Zero));
        }

        // now 22:00 >= rollover, target 02:00 < rollover -> tomorrow (4 h).
        [Test]
        public void WaitForTime_fixed_provider_keeps_the_forward_a_day_rule() {
            Assert.That(Wait(Fixed(new TimeOnly(12, 0)), 22, 0, 2, 0).GetEstimatedDuration(), Is.EqualTo(TimeSpan.FromHours(4)));
        }

        [Test]
        public void TimeCondition_fixed_provider_keeps_both_rules() {
            Assert.That(Cond(Fixed(new TimeOnly(12, 0)), 0, 30, 21, 47).RemainingTime, Is.EqualTo(TimeSpan.Zero));
            Assert.That(Cond(Fixed(new TimeOnly(12, 0)), 22, 0, 2, 0).RemainingTime, Is.EqualTo(TimeSpan.FromHours(4)));
        }

        private static TimeCondition Running(DateTime[] now, int h, int m) {
            var clock = new Mock<ICustomDateTime>();
            clock.Setup(x => x.Now).Returns(() => now[0]);
            var p = Plain(null);
            var c = new TimeCondition(new List<IDateTimeProvider> { p }, p) { DateTime = clock.Object };
            c.Hours = h; c.Minutes = m; c.Seconds = 0;
            c.SequenceBlockInitialize();
            return c;
        }

        // A running plain-time condition keeps the target it resolved when the run started:
        // it must expire at the time and stay expired, not roll over to tomorrow.
        [Test]
        public void TimeCondition_plain_time_expires_and_stays_expired_once_running() {
            var now = new[] { Day.AddMinutes(30) };
            var c = Running(now, 21, 47);
            Assert.That(c.Check(null, null), Is.True);
            now[0] = Day.AddHours(21).AddMinutes(47).AddSeconds(1);
            Assert.That(c.Check(null, null), Is.False);
            Assert.That(c.RemainingTime, Is.EqualTo(TimeSpan.Zero));
            now[0] = Day.AddHours(23);
            Assert.That(c.Check(null, null), Is.False);
        }

        [Test]
        public void TimeCondition_plain_time_expires_across_midnight_once_running() {
            var now = new[] { Day.AddHours(22) };
            var c = Running(now, 2, 0);
            Assert.That(c.Check(null, null), Is.True);
            now[0] = Day.AddDays(1).AddHours(2).AddSeconds(1);
            Assert.That(c.Check(null, null), Is.False);
        }

        // Attached and idle at 20:00 (the watchdog calls Check every second), the run starts the next
        // day at 00:30: the target is the run's next 21:47, not one held from the idle Check.
        [Test]
        public void TimeCondition_plain_time_idle_Check_does_not_hold_a_target_before_the_run_starts() {
            var now = new[] { Day.AddHours(20) };
            var clock = new Mock<ICustomDateTime>();
            clock.Setup(x => x.Now).Returns(() => now[0]);
            var p = Plain(null);
            var c = new TimeCondition(new List<IDateTimeProvider> { p }, p) { DateTime = clock.Object };
            c.Hours = 21; c.Minutes = 47; c.Seconds = 0;
            c.Check(null, null);
            now[0] = Day.AddDays(1).AddMinutes(30);
            c.SequenceBlockInitialize();
            Assert.That(c.Check(null, null), Is.True);
            Assert.That(c.RemainingTime, Is.EqualTo(new TimeSpan(21, 17, 0)));
        }

        // A Parallel Instruction Set never calls SequenceBlockInitialize: with its parent running, the
        // condition holds the target at its first read, so the 1 s watchdog still ends the block.
        [Test]
        public void TimeCondition_plain_time_on_a_running_parallel_block_expires() {
            var now = new[] { Day.AddHours(21) };
            var clock = new Mock<ICustomDateTime>();
            clock.Setup(x => x.Now).Returns(() => now[0]);
            var p = Plain(null);
            var c = new TimeCondition(new List<IDateTimeProvider> { p }, p) { DateTime = clock.Object };
            c.Hours = 21; c.Minutes = 47; c.Seconds = 0;
            var parallel = new ParallelContainer();
            c.AttachNewParent(parallel);
            parallel.Status = SequenceEntityStatus.RUNNING;
            Assert.That(c.Check(null, null), Is.True);
            now[0] = Day.AddHours(21).AddMinutes(48);
            Assert.That(c.Check(null, null), Is.False);
        }

        [Test]
        public void TimeCondition_plain_time_ResetProgress_targets_the_next_occurrence() {
            var now = new[] { Day.AddMinutes(30) };
            var c = Running(now, 21, 47);
            c.Check(null, null);
            now[0] = Day.AddHours(21).AddMinutes(48);
            Assert.That(c.Check(null, null), Is.False);
            c.ResetProgress();
            c.SequenceBlockInitialize();
            Assert.That(c.Check(null, null), Is.True);
            Assert.That(c.RemainingTime, Is.EqualTo(new TimeSpan(23, 59, 0)));
        }
    }
}