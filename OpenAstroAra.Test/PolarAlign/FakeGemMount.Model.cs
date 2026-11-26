#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enums;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test.PolarAlign {

    /// <summary>
    /// The mechanical pose of a German equatorial mount, the fake's truth (#2048 "Shared model").
    /// <para><see cref="MechHaDeg"/> is the RA axis's angle, as the hour angle (degrees, (−180, 180]) the tube points
    /// at while the Dec axis is on its normal side. 90° (6 h) is counterweight-down (Home), 0° and 180° are
    /// counterweight-horizontal, −90° (18 h) is counterweight straight up.</para>
    /// <para><see cref="DecAxisDeg"/> is the Dec axis's angle. Up to ±90° (the pole, the site's hemisphere) the tube is
    /// on its normal side (ASCOM <c>pierEast</c>) and points at Dec = the angle; beyond the pole (over 90° in the north,
    /// under −90° in the south) it is through the pole (<c>pierWest</c>) and points at hour angle
    /// <see cref="MechHaDeg"/> + 12 h and Dec 180° − the angle (−180° − the angle in the south).</para>
    /// </summary>
    internal readonly record struct GemPose(double MechHaDeg, double DecAxisDeg);

    /// <summary>The pure geometry of <see cref="GemPose"/>, written from the definitions in #2048 and independent of the
    /// daemon's own counterweight code, so a test on the fake never checks the daemon against itself.</summary>
    internal static class GemGeometry {

        /// <summary>The Earth's turn against the stars, degrees per second of time.</summary>
        public const double SiderealDegPerSecond = 360.98564736629 / 86400.0;

        /// <summary>An angle in (−180, 180].</summary>
        public static double Wrap180(double deg) {
            var d = ((deg % 360.0) + 360.0) % 360.0;
            return d > 180.0 ? d - 360.0 : d;
        }

        /// <summary>An angle in [0, 360).</summary>
        public static double Wrap360(double deg) => ((deg % 360.0) + 360.0) % 360.0;

        /// <summary>The true counterweight angle φ: how far the RA axis has turned from counterweight-down. 0 = Home,
        /// 90 = counterweight horizontal, 180 = straight up. It depends on the RA axis alone.</summary>
        public static double CounterweightAngleDeg(GemPose pose) => Math.Abs(Wrap180(pose.MechHaDeg - 90.0));

        /// <summary>#2048's φ_HA: the counterweight angle a reported hour angle gives, assuming counterweight-down.</summary>
        public static double CounterweightAngleFromHourAngleDeg(double haDeg) {
            var a = Math.Abs(Wrap180(haDeg));
            return 90.0 - Math.Min(a, 180.0 - a);
        }

        public static bool ThroughThePole(GemPose pose, bool south) => south ? pose.DecAxisDeg < -90.0 : pose.DecAxisDeg > 90.0;

        /// <summary>The true pier side: ASCOM's pointing state, normal (<c>pierEast</c>) or through the pole (<c>pierWest</c>).</summary>
        public static PierSide Side(GemPose pose, bool south) => ThroughThePole(pose, south) ? PierSide.pierWest : PierSide.pierEast;

        /// <summary>Where the tube points: hour angle (degrees, (−180, 180]) and Dec.</summary>
        public static (double HaDeg, double DecDeg) Pointing(GemPose pose, bool south) =>
            ThroughThePole(pose, south)
                ? (Wrap180(pose.MechHaDeg + 180.0), (south ? -180.0 : 180.0) - pose.DecAxisDeg)
                : (Wrap180(pose.MechHaDeg), pose.DecAxisDeg);

        /// <summary>The counterweight-down pose that points at (<paramref name="haDeg"/>, <paramref name="decDeg"/>):
        /// the normal side for an hour angle in [0, 12 h), through the pole for one in [−12 h, 0). Its φ is at most 90°.</summary>
        public static GemPose CounterweightDown(double haDeg, double decDeg, bool south) {
            var ha = Wrap180(haDeg);
            return ha >= 0.0
                ? new GemPose(ha, decDeg)
                : new GemPose(Wrap180(ha - 180.0), (south ? -180.0 : 180.0) - decDeg);
        }

        /// <summary>The other pose that points at the same place: RA axis + 12 h, Dec axis mirrored over the pole. From
        /// a counterweight-down pose it is the counterweight-up one (φ' = 180° − φ), the pose an iOptron <c>:MS2#</c> GoTo takes.</summary>
        public static GemPose OtherSide(GemPose pose, bool south) =>
            new(Wrap180(pose.MechHaDeg + 180.0), (south ? -180.0 : 180.0) - pose.DecAxisDeg);

        /// <summary>Home: counterweight down, the tube on the pole.</summary>
        public static GemPose Home(bool south) => new(90.0, south ? -90.0 : 90.0);

        /// <summary>The side an hour-angle-model driver reports: <c>pierEast</c> for an hour angle in [0, 12 h).</summary>
        public static PierSide HourAngleModelSide(double haDeg) => Wrap180(haDeg) >= 0.0 ? PierSide.pierEast : PierSide.pierWest;

        /// <summary>The altitude (degrees) of hour angle <paramref name="haDeg"/>, Dec <paramref name="decDeg"/> at latitude <paramref name="latDeg"/>.</summary>
        public static double AltitudeDeg(double haDeg, double decDeg, double latDeg) {
            const double R = Math.PI / 180.0;
            var s = (Math.Sin(latDeg * R) * Math.Sin(decDeg * R)) + (Math.Cos(latDeg * R) * Math.Cos(decDeg * R) * Math.Cos(haDeg * R));
            return Math.Asin(Math.Clamp(s, -1.0, 1.0)) / R;
        }
    }

    /// <summary>Where the reported pier side comes from (#2048 <c>pier_source</c>).</summary>
    internal enum PierSideSource {
        /// <summary>From the sign of the reported hour angle (AlpacaBridge iOptron, Celestron, ZWO, SynScan, Bisque).</summary>
        HourAngleModel,
        /// <summary>From the mount's own (believed) mechanical pose.</summary>
        Mechanical,
        /// <summary>Always <c>pierUnknown</c>.</summary>
        Unknown,
        /// <summary>The read fails; the mediator answers <c>pierUnknown</c> and <see cref="IMountMotionControl.ReadNowAsync"/> too.</summary>
        Error,
    }

    /// <summary>How DestinationSideOfPier answers (#2048 <c>dsop</c>).</summary>
    internal enum DestinationSideMode {
        HourAngleModel,
        /// <summary>The side the firmware's GoTo would really land on (counterweight-up after an <c>:MS2#</c> re-aim).</summary>
        Mechanical,
        /// <summary>The driver does not implement it: <c>pierUnknown</c>, as the mediator reports a refused call.</summary>
        NotImplemented,
        /// <summary>The read throws.</summary>
        Error,
        /// <summary>The opposite of the side the GoTo would land on.</summary>
        Lie,
    }

    internal enum MotionKind {
        RaMotor,
        DecMotor,
        GoTo,
        /// <summary>A refused GoTo the driver re-sent counterweight-up (<c>ms2_fallback</c>).</summary>
        GoToCounterweightUp,
        FindHome,
        Park,
    }

    internal enum MotionEnd {
        Running,
        /// <summary>A MoveAxis 0 the mount honoured.</summary>
        Stopped,
        /// <summary>The lease ran out and the service stopped the axis.</summary>
        LeaseLapsed,
        Aborted,
        /// <summary>A soft limit stopped it.</summary>
        LimitHit,
        Arrived,
        Refused,
        /// <summary>The motor was stopped to run the other way (a new entry follows).</summary>
        Reversed,
        /// <summary>The mount lost power or its connection.</summary>
        PowerLost,
    }

    internal enum StopKind {
        Abort,
        AxisStop,
        /// <summary>The service's own MoveAxis 0 when a lease lapsed.</summary>
        LeaseStop,
    }

    /// <summary>The firmware's soft limits (#2048), checked on the pose the mount believes it has. Null = no limit.</summary>
    internal sealed record SoftLimits(
        double? MaxCounterweightDeg = null, double? MaxAbsHourAngleHours = null, double? MaxGoToDeg = null, double? MinAltitudeDeg = null);

    /// <summary>One motion in the ledger (#2048): a motor move (one axis, start to stop; renewals and speed changes in one
    /// direction belong to it), a GoTo, a Find Home or a park. Times are the fake's clock from its start.</summary>
    internal sealed record MotionEntry {
        public int Index { get; init; }
        public MotionKind Kind { get; init; }
        public TimeSpan StartedAt { get; init; }
        public TimeSpan? EndedAt { get; init; }
        /// <summary>How many WS events were published when the motion was sent (<see cref="FakeGemMount.EventCount"/>).</summary>
        public int EventsBefore { get; init; }
        /// <summary>The commanded rate of a motor move (degrees per second, as sent; the last one for a renewed move).</summary>
        public double Rate { get; init; }
        public GemPose TrueFrom { get; init; }
        public GemPose TrueTo { get; init; }
        /// <summary>The target of a GoTo, Find Home or park as sent (RA degrees, Dec), null for a motor move.</summary>
        public (double RaDeg, double DecDeg)? Target { get; init; }
        public double ReportedRaFromDeg { get; init; }
        public double ReportedRaToDeg { get; init; }
        public double TrueHaFromDeg { get; init; }
        public double TrueHaToDeg { get; init; }
        public double ReportedHaFromDeg { get; init; }
        public double ReportedHaToDeg { get; init; }
        public double TruePhiBeforeDeg { get; init; }
        public double TruePhiAfterDeg { get; init; }
        public double TruePhiMaxDeg { get; init; }
        public PierSide TrueSideBefore { get; init; }
        public PierSide TrueSideAfter { get; init; }
        public PierSide ReportedSideBefore { get; init; }
        public PierSide ReportedSideAfter { get; init; }
        /// <summary>Sent while the mount was really moving (another motion, a GoTo, a runaway).</summary>
        public bool SentWhileSlewing { get; init; }
        public bool Refused { get; init; }
        public string? RefusedReason { get; init; }
        public MotionEnd End { get; init; }

        /// <summary>The signed change of the reported RA (degrees, + east).</summary>
        public double SignedDeltaRaDeg => GemGeometry.Wrap180(ReportedRaToDeg - ReportedRaFromDeg);

        /// <summary>The signed true turn of the RA axis (degrees, + west: the hour angle grows).</summary>
        public double TrueDeltaHaDeg => GemGeometry.Wrap180(TrueTo.MechHaDeg - TrueFrom.MechHaDeg);
    }

    /// <summary>One stop command (#2048): an abort, a MoveAxis 0, or the lease's own stop, and whether the mount honoured it.</summary>
    internal sealed record StopEntry(int Index, StopKind Kind, int? Axis, string? Reason, TimeSpan At, int EventsBefore, bool Honoured);

    /// <summary>One command that reached the mount, in order. <see cref="IsMotion"/>: a command that can start or keep up
    /// a motion (a non-zero MoveAxis, renewals included, a GoTo, Find Home, park or unpark), refused or not.</summary>
    internal sealed record CommandEntry(int Index, string Text, TimeSpan At, int EventsBefore, bool IsMotion);

    /// <summary>A call Polar Align must never make (ARA-15). The fake throws on it and keeps this record.</summary>
    internal sealed record ForbiddenCall(string Member, TimeSpan At, int EventsBefore);

    /// <summary>One point of the true trajectory: the pose, its φ and pier side, tracking, and the motors' cumulative
    /// travel (degrees, both axes, tracking excluded).</summary>
    internal sealed record PoseSample(TimeSpan At, GemPose Pose, double PhiDeg, PierSide Side, bool Tracking, double MotorTravelDeg);

    /// <summary>A clock a test moves by hand. Its monotonic timestamp (<see cref="GetTimestamp"/>, what
    /// <see cref="TimeProvider.GetElapsedTime(long)"/> measures) and its timers move with it: a
    /// <c>Task.Delay(span, clock)</c> ends when <see cref="Advance"/> takes the clock past it. With
    /// <see cref="AutoAdvance"/> a timer's wait passes at once: the clock jumps to the timer's due time as it is set, so a
    /// routine that times a turn on this clock runs through it without real time passing.</summary>
    internal sealed class ManualMountClock : TimeProvider {
        private readonly object _lock = new();
        private readonly List<ManualTimer> _timers = new();
        private DateTimeOffset _now = new(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);
        private long _ticks;

        /// <summary>A timer's wait passes as soon as it is set (the clock jumps to its due time).</summary>
        public bool AutoAdvance { get; set; }

        /// <summary>Completes when the next timer is set on a clock that does not advance by itself: a routine waits on
        /// this clock (a turn's motor runs), and a test can act in the middle of that wait.</summary>
        public TaskCompletionSource TimerSet { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>How many timers wait on the clock: the waits a test's next <see cref="Advance"/> can end.</summary>
        public int PendingTimers {
            get {
                lock (_lock) {
                    return _timers.Count;
                }
            }
        }

        public override DateTimeOffset GetUtcNow() {
            lock (_lock) {
                return _now;
            }
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() {
            lock (_lock) {
                return _ticks;
            }
        }

        public void Advance(TimeSpan by) {
            List<ManualTimer> due;
            lock (_lock) {
                _now += by;
                _ticks += by.Ticks;
                due = _timers.FindAll(t => t.DueTicks <= _ticks);
                _timers.RemoveAll(t => t.DueTicks <= _ticks);
            }
            foreach (var timer in due) {
                timer.Fire();
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        private void Set(ManualTimer timer, TimeSpan dueTime) {
            TaskCompletionSource? set = null;
            lock (_lock) {
                _timers.Remove(timer);
                if (dueTime == Timeout.InfiniteTimeSpan) {
                    return;
                }
                timer.DueTicks = _ticks + dueTime.Ticks;
                _timers.Add(timer);
                if (!AutoAdvance) {
                    set = TimerSet;
                    TimerSet = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            set?.TrySetResult();
            if (AutoAdvance) {
                Advance(dueTime);
            }
        }

        private void Drop(ManualTimer timer) {
            lock (_lock) {
                _timers.Remove(timer);
            }
        }

        /// <summary>A one-shot timer on the manual clock (a period is not modelled: <c>Task.Delay</c> sets none).</summary>
        private sealed class ManualTimer(ManualMountClock clock, TimerCallback callback, object? state) : ITimer {
            public long DueTicks { get; set; }

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period) {
                clock.Set(this, dueTime);
                return true;
            }

            public void Dispose() => clock.Drop(this);

            public ValueTask DisposeAsync() {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
