#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Server.Contracts.Refusals;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using PierSide = OpenAstroAra.Core.Enums.PierSide;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// #2066 (ARA-5) — every pointing Polar Align plans or moves from is a fresh reading of the mount through
    /// <see cref="IMountMotionControl.ReadNowAsync"/>, never the daemon's refresh cache: after one failed status poll the
    /// cache answers the (0, 0) placeholder, and a turn planned or stopped on it moves the mount a way nobody confirmed. A
    /// reading the routine cannot trust ends it with <see cref="PointingUnknown"/> (a start is refused with
    /// <c>polar-align-pointing-unknown</c>): no answer, the (0, 0) placeholder, a reading of another mount session than the
    /// run's (the mount reconnected), or one older than <see cref="FreshReadMaxAge"/>. A Dec past ±90° (the Dec axis over the
    /// pole, which a synced mount can report) leaves the pier side and the hour angle ambiguous: <see cref="PoseAmbiguous"/>
    /// (<c>polar-align-pose-ambiguous</c>), before any motion. The #1536 fold over the pole stays only in
    /// <see cref="FoldTolerantSignedTurnDeg"/>.
    /// </summary>
    public sealed partial class PolarAlignService {

        /// <summary>The <c>polar_align.error</c> reason of a run that has no reading it can trust.</summary>
        internal const string PointingUnknown = "pointing_unknown";

        /// <summary>The <c>polar_align.error</c> reason of a run whose mount reads Dec past ±90°.</summary>
        internal const string PoseAmbiguous = "pose_ambiguous";

        /// <summary>The oldest reading a run plans or moves from: twice AlpacaBridge iOptron's 1 s status cache.</summary>
        internal static readonly TimeSpan FreshReadMaxAge = TimeSpan.FromSeconds(2);

        /// <summary>The clock a reading's age is measured on: the telescope service stamps its readings on the system clock;
        /// a test on a fake mount's own clock sets it to that. A turn's stall, time bound and settle are timed on its
        /// monotonic timestamp (<see cref="TimeProvider.GetTimestamp"/>), never its wall clock, which can step.</summary>
        internal TimeProvider Clock { get; set; } = TimeProvider.System;

        // The mount session the run of this generation started in (its confirmed plan's reading). Guarded by _gate.
        private (int Gen, long SessionEpoch)? _runSession;

        /// <summary>A reading of the mount checked as above: the pointing in JNOW (degrees) and its mount session, or why it
        /// cannot be trusted. <paramref name="runSession"/>: the session the reading must come from; null takes any.
        /// <paramref name="foldPastPole"/> (#2166, a Dec move's reads): a Dec past ±90° is read as the same pointing
        /// folded back over the pole (Dec mirrored, RA + 12 h, the pier side unknown), not as ambiguous: a Dec move near
        /// the pole may carry the axis over it on a driver that reports the axis unfolded.</summary>
        private async Task<FreshPointing> ReadFreshPointingAsync(long? runSession, CancellationToken ct, MountReadKind kind = MountReadKind.Full,
                bool foldPastPole = false) {
            var askedAt = Clock.GetUtcNow();
            var reading = await (kind switch {
                MountReadKind.Position => _motionControl.ReadPositionNowAsync(ct),
                MountReadKind.Pose => _motionControl.ReadPoseNowAsync(ct),
                _ => _motionControl.ReadNowAsync(ct),
            }).ConfigureAwait(false);
            var answeredAt = Clock.GetUtcNow();
            if (reading is null) {
                return FreshPointing.Unknown("the mount gave no reading");
            }
            // Exact (0, 0) can only be the placeholder here because Polar Align works within 5° of the pole (Dec 0 never comes up).
            if (reading.RaHours == 0.0 && reading.DecDegrees == 0.0) {
                return FreshPointing.Unknown("the mount answered the (0, 0) placeholder");
            }
            if (runSession is long session && reading.SessionEpoch != session) {
                return FreshPointing.Unknown("the mount reconnected since the start");
            }
            var age = answeredAt - reading.ReadAtUtc;
            if (age > FreshReadMaxAge) {
                // The read's own round trip beside its age tells a slow driver (a long round trip) from a stale value (a short one).
                LogStaleReading(age.TotalSeconds, (answeredAt - askedAt).TotalSeconds);
                return FreshPointing.Unknown(string.Create(CultureInfo.InvariantCulture, $"the reading was {age.TotalSeconds:F1} s old"));
            }
            var (raHours, decDeg, side) = (reading.RaHours, reading.DecDegrees, reading.SideOfPier);
            if (Math.Abs(decDeg) > 90.0) {
                if (!foldPastPole || Math.Abs(decDeg) > 180.0) {
                    return FreshPointing.Ambiguous(decDeg);
                }
                decDeg = Math.CopySign(180.0, decDeg) - decDeg;
                raHours = (raHours + 12.0) % 24.0;
                side = PierSide.pierUnknown; // drivers that read Dec past the pole disagree on the side they report there
            }
            var jnow = new Coordinates(Angle.ByHours(raHours), Angle.ByDegree(decDeg), reading.Frame).Transform(Epoch.JNOW);
            return FreshPointing.Read(jnow.RADegrees, jnow.Dec, reading.SessionEpoch, side, reading.Slewing) with { RawRaDeg = raHours * 15.0 };
        }

        /// <summary>A start's reading (its plan and the confirm step): refused with <c>polar-align-pointing-unknown</c> or
        /// <c>polar-align-pose-ambiguous</c> before the camera is taken or anything moves. Its pier side is the reading's,
        /// never the refresh cache's (#2066): the sweep plan compares it with where Polar Align left the mount. Its Slewing
        /// (null when that read failed) is the start's mount-busy check (<see cref="ThrowIfBusyAtStart"/>).</summary>
        private async Task<(double RaDeg, double DecDeg, long SessionEpoch, PierSide Side, bool? Slewing)> StartPointingJnowAsync(CancellationToken ct) {
            var p = await ReadFreshPointingAsync(runSession: null, ct).ConfigureAwait(false);
            if (p.DecPastPoleDeg is double dec) {
                LogPoseAmbiguous(dec);
                throw new RefusalException(RefusalCatalog.PolarAlignPoseAmbiguous, PoseAmbiguousMessage(dec));
            }
            if (p.Why is { } why) {
                LogPointingUnknown(why);
                throw new RefusalException(RefusalCatalog.PolarAlignPointingUnknown, PointingUnknownMessage(why));
            }
            return (p.RaDeg, p.DecDeg, p.SessionEpoch, p.Side, p.Slewing);
        }

        /// <summary>A run's reading: one it cannot trust fails the run (<see cref="PointingUnknown"/>,
        /// <see cref="PoseAmbiguous"/>), so a turn neither starts nor goes on from it. <paramref name="kind"/>: what is read
        /// (<see cref="MountReadKind"/>; a turn's polls read the position and the pier side).</summary>
        private async Task<(double RaDeg, double DecDeg)> RunPointingJnowAsync(int gen, CancellationToken ct, MountReadKind kind = MountReadKind.Full) {
            var (ra, dec, _) = await RunPoseJnowAsync(gen, ct, kind).ConfigureAwait(false);
            return (ra, dec);
        }

        /// <summary><see cref="RunPointingJnowAsync"/> with the reading's pier side (<c>pierUnknown</c> for a position-only
        /// read, or when the mount does not say).</summary>
        private async Task<(double RaDeg, double DecDeg, PierSide Side)> RunPoseJnowAsync(int gen, CancellationToken ct, MountReadKind kind = MountReadKind.Full) {
            var p = await RunReadingAsync(gen, kind, ct).ConfigureAwait(false);
            return (p.RaDeg, p.DecDeg, p.Side);
        }

        /// <summary>A run's checked reading, its Slewing included (null for a position-only read, or a failed Slewing
        /// read): one it cannot trust fails the run as <see cref="RunPointingJnowAsync"/> says.</summary>
        private async Task<FreshPointing> RunReadingAsync(int gen, MountReadKind kind, CancellationToken ct, bool foldPastPole = false) {
            var p = await ReadFreshPointingAsync(RunSessionEpoch(gen), ct, kind, foldPastPole).ConfigureAwait(false);
            if (p.Why is not null || p.DecPastPoleDeg is not null) {
                ThrowIfAbortedFromOutside(gen); // a park or Emergency Stop meanwhile owns the mount: its failure wins
            }
            if (p.DecPastPoleDeg is double dec) {
                LogPoseAmbiguous(dec);
                throw new RoutineFailedException(PoseAmbiguous, PoseAmbiguousMessage(dec));
            }
            if (p.Why is { } why) {
                LogPointingUnknown(why);
                throw new RoutineFailedException(PointingUnknown, PointingUnknownMessage(why));
            }
            return p;
        }

        /// <summary>A best-effort reading for the exit's bookkeeping (where Polar Align left the mount): null when it cannot
        /// be trusted.</summary>
        private async Task<(double RaDeg, double DecDeg, PierSide Side)?> TrustedPointingJnowOrNullAsync(CancellationToken ct) {
            var p = await ReadFreshPointingAsync(runSession: null, ct).ConfigureAwait(false);
            return p.Why is null && p.DecPastPoleDeg is null ? (p.RaDeg, p.DecDeg, p.Side) : null;
        }

        /// <summary>A reading the stop's confirmation must not count (#2048's abort contract: a disconnected, stale or
        /// failed read is "not confirmed", never "stopped"): none, the (0, 0) placeholder, or older than
        /// <see cref="FreshReadMaxAge"/>.</summary>
        private bool UntrustedForStillness([NotNullWhen(false)] MountReading? r) =>
            r is null || (r.RaHours == 0.0 && r.DecDegrees == 0.0) || Clock.GetUtcNow() - r.ReadAtUtc > FreshReadMaxAge;

        private long? RunSessionEpoch(int gen) {
            lock (_gate) {
                return _runSession is { } s && s.Gen == gen ? s.SessionEpoch : null;
            }
        }

        /// <summary>The run-wide watch (#2066, #2067): a reconnect (<see cref="ThrowIfMountReconnected"/>), then a stop the
        /// run did not raise (<see cref="ThrowIfAbortedFromOutside"/>). Checked before every motor move, before every tracking
        /// switch-on, after every seed frame and each live frame, between the stages, and on every poll of a move.</summary>
        private void ThrowIfStoppedFromOutside(int gen) {
            ThrowIfMountReconnected(gen);
            ThrowIfAbortedFromOutside(gen);
        }

        /// <summary>#2066 — the run-wide watch's part for a reconnect: the mount session changed since the run's start (a
        /// connect, a disconnect, a fault trip), so the mount may have been moved, re-homed or swapped. Fails the run with
        /// <see cref="PointingUnknown"/>, unless a stop from outside came meanwhile: a park, Find Home or Emergency Stop then
        /// owns the mount, and the run's exit must send no stop that would abort it.</summary>
        private void ThrowIfMountReconnected(int gen) {
            if (RunSessionEpoch(gen) is long session && _motionControl.SessionEpoch != session) {
                ThrowIfAbortedFromOutside(gen); // read after the session: a park counted before the reconnect is seen
                const string why = "the mount reconnected since the start";
                LogPointingUnknown(why);
                throw new RoutineFailedException(PointingUnknown, PointingUnknownMessage(why));
            }
        }

        internal static string PointingUnknownMessage(string why) =>
            $"Polar Align has no fresh reading of where the mount points ({why}), so it moves nothing more — check the mount's connection, then start again";

        internal static string PoseAmbiguousMessage(double decDeg) => string.Create(CultureInfo.InvariantCulture,
            $"The mount reports Dec {decDeg:F1}°, past the pole, so its pier side is not known and Polar Align moves nothing — point the tube 2–5° from the pole, counterweights down, with the hand controller (or send the mount Home), then start again");

        /// <summary>What a run reading asks the mount: everything (<see cref="IMountMotionControl.ReadNowAsync"/>), the
        /// position only (<see cref="IMountMotionControl.ReadPositionNowAsync"/>), or the position and the pier side (#2067,
        /// <see cref="IMountMotionControl.ReadPoseNowAsync"/>: a turn's polls).</summary>
        private enum MountReadKind { Full, Position, Pose }

        /// <summary>A checked reading: the pointing (JNOW), session, pier side and Slewing (null when not read or the read
        /// failed), or <see cref="Why"/> it cannot be trusted, or the Dec past the pole it reported. <see cref="RawRaDeg"/>
        /// (#2166): the RA as the mount reported it, in its own frame (degrees), before the JNOW conversion.</summary>
        private readonly record struct FreshPointing(double RaDeg, double DecDeg, long SessionEpoch, PierSide Side, string? Why, double? DecPastPoleDeg, bool? Slewing = null,
                double RawRaDeg = double.NaN) {
            public static FreshPointing Read(double raDeg, double decDeg, long session, PierSide side, bool? slewing) => new(raDeg, decDeg, session, side, null, null, slewing);
            public static FreshPointing Unknown(string why) => new(0, 0, 0, PierSide.pierUnknown, why, null);
            public static FreshPointing Ambiguous(double decDeg) => new(0, 0, 0, PierSide.pierUnknown, null, decDeg);
        }

        [LoggerMessage(EventId = 4580, Level = LogLevel.Warning, Message = "§45 polar-align: no fresh reading of the mount ({Why}) — nothing more moves (pointing_unknown)")]
        private partial void LogPointingUnknown(string why);

        [LoggerMessage(EventId = 4582, Level = LogLevel.Warning, Message = "§45 polar-align: a mount reading was {AgeS:F2} s old when it arrived; the read itself took {RoundTripS:F2} s")]
        private partial void LogStaleReading(double ageS, double roundTripS);

        [LoggerMessage(EventId = 4581, Level = LogLevel.Warning, Message = "§45 polar-align: the mount reads Dec {DecDeg:F2}°, past the pole — nothing moves (pose_ambiguous)")]
        private partial void LogPoseAmbiguous(double decDeg);
    }
}
