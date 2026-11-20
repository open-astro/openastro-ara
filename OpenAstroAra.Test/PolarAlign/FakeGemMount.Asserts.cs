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
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenAstroAra.Test.PolarAlign {

    /// <summary>The ledger's questions about the true pose (#2048 "Asserts common to every scenario"). Each check answers
    /// with the violations it found, so the fake's own tests can see a check fire; the <c>Assert…</c> forms fail the test.</summary>
    internal sealed partial class FakeGemMount {

        /// <summary>
        /// The five asserts common to every scenario, plus ARA-15's forbidden calls:
        /// <list type="number">
        /// <item><c>limit_hit</c> is <paramref name="expectLimitHit"/> (false except in M33);</item>
        /// <item>no motion was sent while the mount was really moving;</item>
        /// <item>no motion command came after the run's end event (<paramref name="endEventIndex"/>, its index among the
        /// published WS events; null skips this check);</item>
        /// <item><c>max_true_phi</c> ≤ <paramref name="limitDeg"/> (L);</item>
        /// <item>the true pier side never changed;</item>
        /// <item>no forbidden call was made.</item>
        /// </list>
        /// Empty when all hold.
        /// </summary>
        public IReadOnlyList<string> CommonViolations(int? endEventIndex, double limitDeg, bool expectLimitHit = false) {
            var found = new List<string>();
            if (LimitHit != expectLimitHit) {
                found.Add(expectLimitHit ? "limit_hit is false; a soft limit was expected to fire" : $"limit_hit is true ({LimitReason})");
            }
            foreach (var m in Motions.Where(m => m.SentWhileSlewing)) {
                found.Add(F($"motion {m.Index} ({m.Kind}) was sent at {m.StartedAt.TotalSeconds:F2} s while the mount was moving"));
            }
            if (endEventIndex is int end) {
                foreach (var c in Commands.Where(c => c.IsMotion && c.EventsBefore > end)) {
                    found.Add(F($"'{c.Text}' was sent after the run's end event (event {end}; {c.EventsBefore} events before it)"));
                }
            }
            if (MaxTruePhiDeg > limitDeg) {
                found.Add(F($"max_true_phi {MaxTruePhiDeg:F2}° is past L = {limitDeg:F2}°"));
            }
            foreach (var change in Trajectory.Where(p => p.Side != InitialTrueSide).Take(1)) {
                found.Add(F($"the true pier side changed from {InitialTrueSide} to {change.Side} at {change.At.TotalSeconds:F2} s"));
            }
            foreach (var call in Forbidden) {
                found.Add(F($"forbidden call {call.Member} at {call.At.TotalSeconds:F2} s"));
            }
            return found;
        }

        /// <summary>Fails the test on any of <see cref="CommonViolations"/>.</summary>
        public void AssertCommon(int? endEventIndex, double limitDeg, bool expectLimitHit = false) =>
            Assert.That(CommonViolations(endEventIndex, limitDeg, expectLimitHit), Is.Empty, Describe());

        /// <summary>The true counterweight angle never went past <paramref name="limitDeg"/>.</summary>
        public void AssertNeverExceededCounterweight(double limitDeg) =>
            Assert.That(MaxTruePhiDeg, Is.LessThanOrEqualTo(limitDeg), Describe());

        /// <summary>How far the motors really moved the mount since <paramref name="since"/> (a time on <see cref="Now"/>).</summary>
        /// <remarks>The end point is the live travel (<see cref="Trajectory"/> adds a sample now). Take
        /// <paramref name="since"/> at a stop or abort, which add a sample too, for an exact start; between samples the
        /// start is the last sample before it, so the answer can only be larger.</remarks>
        public double MotorTravelSince(TimeSpan since) {
            var samples = Trajectory;
            var at = samples.LastOrDefault(p => p.At <= since) ?? samples[0];
            return samples[^1].MotorTravelDeg - at.MotorTravelDeg;
        }

        /// <summary>Nothing moved the mount (tracking aside) after <paramref name="since"/>: no motor, no GoTo.</summary>
        public void AssertNoMotionSince(TimeSpan since) =>
            Assert.That(MotorTravelSince(since), Is.LessThan(1e-9), "the mount moved after " + since + ". " + Describe());

        /// <summary>The total true RA sweep: the sum of every motion's |true RA axis turn| (degrees).</summary>
        public double TotalTrueRaSweepDeg => Motions.Where(m => !m.Refused).Sum(m => Math.Abs(m.TrueDeltaHaDeg));

        /// <summary>The most the true RA axis turned from where it was at <paramref name="since"/>, by the trajectory
        /// (degrees, tracking included).</summary>
        public double MaxTrueRaExcursionDeg(TimeSpan since) {
            var samples = Trajectory;
            var from = (samples.LastOrDefault(p => p.At <= since) ?? samples[0]).Pose.MechHaDeg;
            return samples.Where(p => p.At >= since).Max(p => Math.Abs(GemGeometry.Wrap180(p.Pose.MechHaDeg - from)));
        }

        /// <summary>The mount ended still (no motor, no runaway, no GoTo) and, unless <paramref name="tracking"/>, not tracking.</summary>
        public void AssertEndedStopped(bool tracking = false) {
            Assert.That(IsMoving, Is.False, "the mount still moves. " + Describe());
            Assert.That(Tracking, Is.EqualTo(tracking), tracking ? "the mount should track" : "the mount should not track");
        }

        /// <summary>The ledger as text, for a failure message.</summary>
        public string Describe() {
            var lines = new List<string> {
                F($"true pose HA {TruePose.MechHaDeg:F2}° Dec axis {TruePose.DecAxisDeg:F2}° φ {TruePhiDeg:F2}° side {TrueSide}; max φ {MaxTruePhiDeg:F2}°; limit_hit {LimitHit}; aborts {AbortCount}"),
            };
            lines.AddRange(Motions.Select(m => F(
                $"  #{m.Index} {m.Kind} {m.StartedAt.TotalSeconds:F2}-{m.EndedAt?.TotalSeconds:F2} s rate {m.Rate} ΔHA {m.TrueDeltaHaDeg:F2}° φ {m.TruePhiBeforeDeg:F1}->{m.TruePhiAfterDeg:F1} (max {m.TruePhiMaxDeg:F1}) side {m.TrueSideBefore}->{m.TrueSideAfter} {m.End}{(m.Refused ? " refused: " + m.RefusedReason : "")}{(m.SentWhileSlewing ? " SENT WHILE SLEWING" : "")}")));
            lines.AddRange(Stops.Select(s => F($"  stop {s.Kind} axis {s.Axis} {s.Reason} at {s.At.TotalSeconds:F2} s honoured {s.Honoured}")));
            return string.Join(Environment.NewLine, lines);
        }

        private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    }
}
