#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using TelescopeAxes = OpenAstroAra.Core.Enums.TelescopeAxes;
using PierSide = OpenAstroAra.Core.Enums.PierSide;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2048 — "Stop does not stop it". Every exit of a run (Stop, a client that gave up, daemon shutdown, a failure, an
    /// exception, a time bound and the verdict) sends the mount its stop — AbortSlew while a motor move may
    /// run, then MoveAxis 0 on both axes — confirms it, and then turns tracking off (Guiding taking the camera gets the
    /// run's tracking back). Each test drives <see cref="FakeStopMount"/>, a mount with real motion state that logs every
    /// command in order, and asserts the commands and the final tracking.
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>
        /// The bench mount for the exit paths: a mocked mediator that is also the daemon's telescope service (the path
        /// production takes), with real state — motor rates, tracking — and an ordered log of every motion, park and
        /// tracking command the routine sends. A GoTo, Find Home or unpark is logged too, so a test sees which the routine
        /// sends. A running motor turns its axis at its rate on the bench clock (<see cref="Clock"/>), which the routine's
        /// timed turns move; a mount that ignored a stop runs on by <see cref="StepDeg"/> at every fresh read.
        /// </summary>
        private sealed class FakeStopMount {
            private readonly object _lock = new();
            private readonly List<(string Command, int EventsBefore)> _log = new();
            private readonly double[] _rates = new double[2];
            private double _raDeg;
            private double _decDeg = 85.0;
            private long _epoch;
            private long _trackingWrites;
            private bool _tracking;
            private bool _runaway;
            private bool _threwOnTrackingOff;
            private long _since;

            public Mock<ITelescopeMediator> Mock { get; } = new();

            /// <summary>The clock the motors turn on: it advances by itself as the routine times a turn on it. A test that
            /// needs to act in the middle of a turn turns <see cref="ManualMountClock.AutoAdvance"/> off first.</summary>
            public ManualMountClock Clock { get; }

            /// <summary>The mount reports AtHome (only a mount that can find Home is asked).</summary>
            public bool AtHome { get; set; } = true;

            /// <summary>Each fresh read moves a mount that ignored a stop this far (degrees).</summary>
            public double StepDeg { get; set; } = 0.25;
            /// <summary>The mount refuses every motor command that would move it.</summary>
            public bool RefusesMotor { get; set; }
            /// <summary>The motor takes its commands, but the mount never turns (a slipping clutch, a stall).</summary>
            public bool Frozen { get; set; }
            /// <summary>AbortSlew and MoveAxis 0 are not honored: once one arrives, the RA axis runs on.</summary>
            public bool IgnoresStops { get; set; }
            /// <summary>The first tracking-off write throws (a mount fault the routine does not expect).</summary>
            public bool ThrowsOnFirstTrackingOff { get; set; }
            /// <summary>Awaited by the next MoveAxis 0 (once, then cleared; the axis has stopped by then): a test holds a stop
            /// here to keep the end that sent it in flight.</summary>
            public Func<Task>? StopGate { get; set; }
            /// <summary>How many WS events were published when a command arrived: the order against the run's last event.</summary>
            public Func<int>? EventCount { get; set; }
            /// <summary>Each fresh reading is stamped this long before the read: a sample the mount took earlier (a driver's
            /// cache).</summary>
            public TimeSpan ReadSampledBefore { get; set; }
            /// <summary>Each fresh reading reaches the daemon this long after the mount sampled it (a reply that stalls on
            /// the link): the bench clock is moved on by it before the read returns.</summary>
            public TimeSpan ReadArrivesLate { get; set; }
            /// <summary>The mediator's Find Home answers this (false: the mount refused it or did not finish).</summary>
            public bool FindHomeSucceeds { get; set; } = true;
            /// <summary>Awaited by each Find Home before it answers: a test holds the mount on its way Home here. The token
            /// is the caller's.</summary>
            public Func<CancellationToken, Task>? FindHomeGate { get; set; }

            public TaskCompletionSource FirstMove { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public FakeStopMount(bool tracking = true, bool canFindHome = false) {
                _tracking = tracking;
                Clock = BenchClockOf(Mock);
                _since = Clock.GetTimestamp();
                Mock.Setup(m => m.GetInfo()).Returns(() => {
                    lock (_lock) {
                        return new TelescopeInfo {
                            Connected = true, SiderealTime = Lst, TrackingEnabled = _tracking, CanFindHome = canFindHome, AtHome = AtHome,
                            Slewing = SlewingLocked(), SideOfPier = SideLocked(),
                        };
                    }
                });
                Mock.Setup(m => m.GetCurrentPosition()).Returns(() => {
                    lock (_lock) {
                        TurnLocked();
                        return new Coordinates(_raDeg, _decDeg, Epoch.JNOW, Coordinates.RAType.Degrees);
                    }
                });
                Mock.SetupGet(m => m.TrackingWriteCount).Returns(() => Interlocked.Read(ref _trackingWrites));
                Mock.Setup(m => m.SetTrackingEnabled(It.IsAny<bool>())).Returns((bool on) => {
                    lock (_lock) {
                        Interlocked.Increment(ref _trackingWrites);
                        Record(on ? "tracking on" : "tracking off");
                        if (!on && ThrowsOnFirstTrackingOff && !_threwOnTrackingOff) {
                            _threwOnTrackingOff = true;
                            throw new InvalidCastException("the driver answered tracking with garbage");
                        }
                        _tracking = on;
                        return true;
                    }
                });
                // Each is logged (and does what a mount would) so a test sees it: the routine sends no GoTo and no unpark.
                Mock.Setup(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                    .Returns((Coordinates target, CancellationToken _) => {
                        lock (_lock) {
                            Record("goto");
                            (_raDeg, _decDeg) = (target.RADegrees, target.Dec);
                        }
                        return Task.FromResult(true);
                    });
                Mock.Setup(m => m.FindHome(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                    .Returns(async (IProgress<ApplicationStatus> _, CancellationToken token) => {
                        lock (_lock) {
                            Record("findhome");
                        }
                        if (FindHomeGate is { } gate) {
                            await gate(token).ConfigureAwait(false);
                        }
                        lock (_lock) {
                            if (FindHomeSucceeds) {
                                (_raDeg, _decDeg) = (0.0, 90.0);
                            }
                            return FindHomeSucceeds;
                        }
                    });
                Mock.Setup(m => m.UnparkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                    .Returns((IProgress<ApplicationStatus> _, CancellationToken _) => {
                        lock (_lock) {
                            Record("unpark");
                            return Task.FromResult(true);
                        }
                    });

                var service = Mock.As<ITelescopeService>();
                service.SetupGet(s => s.AbortEpoch).Returns(() => Interlocked.Read(ref _epoch));
                service.SetupGet(s => s.SessionEpoch).Returns(() => Interlocked.Read(ref _sessionEpoch));
                service.Setup(s => s.ReadNowAsync(It.IsAny<CancellationToken>())).Returns(Read);
                Task<MountReading?> Read() {
                    MountReading reading;
                    lock (_lock) {
                        TurnLocked();
                        if (_runaway && !Frozen) {
                            _raDeg = (_raDeg + StepDeg + 360.0) % 360.0;
                        }
                        reading = new MountReading(_raDeg / 15.0, _decDeg, SlewingLocked(),
                            SideLocked(), Clock.GetUtcNow() - ReadSampledBefore, Interlocked.Read(ref _sessionEpoch));
                    }
                    if (ReadArrivesLate > TimeSpan.Zero) {
                        Clock.Advance(ReadArrivesLate);
                    }
                    return Task.FromResult<MountReading?>(reading);
                }
                service.Setup(s => s.MoveAxisAsync(It.IsAny<int>(), It.IsAny<double>(), It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
                    .Returns((int axis, double rate, int? _, long? since, CancellationToken _) => {
                        lock (_lock) {
                            TurnLocked();
                            var command = string.Create(CultureInfo.InvariantCulture, $"move {(axis == 0 ? "primary" : "secondary")} {rate}");
                            if (rate != 0 && RefusesMotor) {
                                Record(command + " refused");
                                return Task.FromException(new InvalidOperationException("the mount refused the move"));
                            }
                            if (rate != 0 && since is long s && s != Interlocked.Read(ref _epoch)) {
                                // As TelescopeService: a renewal after an abort never restarts the motor.
                                Record(command + " refused");
                                return Task.FromException(new InvalidOperationException("the mount was stopped (Stop Mount) after this move began"));
                            }
                            Record(command);
                            if (rate == 0 && IgnoresStops) {
                                _runaway = true;
                                return Task.CompletedTask;
                            }
                            _rates[axis] = rate;
                            if (rate != 0) {
                                FirstMove.TrySetResult();
                            }
                            if (rate == 0 && StopGate is { } gate) {
                                StopGate = null;
                                return gate();
                            }
                            return Task.CompletedTask;
                        }
                    });
                service.Setup(s => s.AbortSlewAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .Returns((string reason, CancellationToken _) => {
                        Abort(reason);
                        return Task.CompletedTask;
                    });
                // #2066 — the daemon's other stop sources, as TelescopeService counts them: Stop Mount and Emergency Stop's
                // passes (AbortSlew), its first step (the latch, no command), and what raised each count.
                service.Setup(s => s.AbortSlewAsync(It.IsAny<CancellationToken>()))
                    .Returns((CancellationToken _) => {
                        Abort("user_request");
                        return Task.CompletedTask;
                    });
                service.Setup(s => s.FenceMotion(It.IsAny<string>())).Callback((string reason) => {
                    lock (_lock) {
                        Record("fence " + reason);
                        _reasons.Add((Interlocked.Increment(ref _epoch), reason));
                    }
                });
                service.Setup(s => s.AbortReasonSince(It.IsAny<long>(), It.IsAny<string?>()))
                    .Returns((long since, string? except) => {
                        lock (_lock) {
                            return _reasons.Where(r => r.Epoch > since && r.Reason != except).Select(r => r.Reason).FirstOrDefault();
                        }
                    });
                service.Setup(s => s.SetTrackingAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                    .Returns((bool on, CancellationToken _) => {
                        Mock.Object.SetTrackingEnabled(on);
                        return Task.CompletedTask;
                    });
                service.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new TelescopeDto("t1", "Mount", EquipmentConnectionState.Connected, null, null!));
            }

            private readonly List<(long Epoch, string Reason)> _reasons = new();

            /// <summary>Brings the axes up to the bench clock: each has turned at its motor's rate since the last call.</summary>
            private void TurnLocked() {
                var seconds = Clock.GetElapsedTime(_since).TotalSeconds;
                _since = Clock.GetTimestamp();
                if (Frozen) {
                    return;
                }
                _raDeg = (_raDeg + (_rates[0] * seconds) + 720.0) % 360.0;
                var dec = _rates[1] * seconds;
                _decDeg = _decDeg >= 0 ? _decDeg - dec : _decDeg + dec;
            }

            private void Abort(string reason) {
                lock (_lock) {
                    TurnLocked();
                    Record("abort " + reason);
                    _reasons.Add((Interlocked.Increment(ref _epoch), reason));
                    if (IgnoresStops) {
                        _runaway = true;
                        return;
                    }
                    _rates[0] = 0;
                    _rates[1] = 0;
                }
            }

            /// <summary>#2066 — the user taps Stop Mount in the app (MountStopHandler's AbortSlew).</summary>
            public void StopMount() => Abort("user_request");

            /// <summary>#2066 — a park from outside Polar Align: TelescopeService counts it as a stop, then the mount parks.</summary>
            public void ParkFromOutside() {
                lock (_lock) {
                    Record("park");
                    _reasons.Add((Interlocked.Increment(ref _epoch), "park"));
                }
            }

            private bool SlewingLocked() => _rates[0] != 0 || _rates[1] != 0 || _runaway;

            private long _sessionEpoch;
            /// <summary>#2064 — the bench's LST, set explicitly: RA 0 is then HA −4 h, east of the meridian.</summary>
            private const double Lst = 20.0;
            /// <summary>The mount disconnects and connects again: a new mount session.</summary>
            public void Reconnect() => Interlocked.Increment(ref _sessionEpoch);

            /// <summary>The hour-angle model's side of the pose (#2064: a counterweight-down mount at RA 0 is on the west
            /// side, which default(PierSide) would call east).</summary>
            private PierSide SideLocked() => CounterweightDownSide(Lst, _raDeg);

            private void Record(string command) => _log.Add((command, EventCount?.Invoke() ?? 0));

            public bool Tracking {
                get {
                    lock (_lock) {
                        return _tracking;
                    }
                }
            }

            /// <summary>Nothing moves: no motor rate, no runaway.</summary>
            public bool AtRest {
                get {
                    lock (_lock) {
                        return !SlewingLocked();
                    }
                }
            }

            public double RaDeg {
                get {
                    lock (_lock) {
                        TurnLocked();
                        return _raDeg;
                    }
                }
            }

            /// <summary>The mount's Dec reading; set it to put the scope somewhere else between two of the routine's reads.</summary>
            public double DecDeg {
                get {
                    lock (_lock) {
                        return _decDeg;
                    }
                }
                set {
                    lock (_lock) {
                        _decDeg = value;
                    }
                }
            }

            public int Count {
                get {
                    lock (_lock) {
                        return _log.Count;
                    }
                }
            }

            public (string Command, int EventsBefore)[] Entries {
                get {
                    lock (_lock) {
                        return _log.ToArray();
                    }
                }
            }

            public string[] Log => Entries.Select(e => e.Command).ToArray();

            /// <summary>The commands from <paramref name="from"/> on.</summary>
            public string[] Since(int from) => Log.Skip(from).ToArray();
        }

        private static bool IsMotion(string command) =>
            command is "goto" or "findhome" or "unpark"
            || (command.StartsWith("move ", StringComparison.Ordinal) && !command.EndsWith(" 0", StringComparison.Ordinal)
                && !command.EndsWith(" refused", StringComparison.Ordinal));

        /// <summary>The stop round sent from <paramref name="from"/> on: AbortSlew (only when <paramref name="abort"/>),
        /// then MoveAxis 0 on the primary axis, then on the secondary axis, in that order. The run's own commands may
        /// interleave (its motor's last stop, a refused renewal), so the round is checked as an ordered subsequence.</summary>
        private static void AssertStopRoundFrom(string[] log, int from, bool abort) {
            var all = string.Join(", ", log.Skip(from));
            var i = from;
            if (abort) {
                i = Array.IndexOf(log, "abort polar_align", from);
                Assert.That(i, Is.GreaterThanOrEqualTo(0), $"AbortSlew after {from}: {all}");
            } else {
                Assert.That(log.Skip(from), Does.Not.Contain("abort polar_align"), $"nothing to abort: {all}");
            }
            var primary = Array.IndexOf(log, "move primary 0", i);
            Assert.That(primary, Is.GreaterThanOrEqualTo(0), $"MoveAxis primary 0: {all}");
            Assert.That(Array.IndexOf(log, "move secondary 0", primary), Is.GreaterThan(primary), $"then MoveAxis secondary 0: {all}");
        }

        /// <summary>Where the exit's stop round begins: the last round before the exit's tracking write.</summary>
        private static int ExitRoundStart(string[] log, bool abort) {
            var secondary = Array.LastIndexOf(log, "move secondary 0");
            var primary = secondary > 0 ? Array.LastIndexOf(log, "move primary 0", secondary - 1) : -1;
            Assert.That(primary, Is.GreaterThanOrEqualTo(0), string.Join(", ", log));
            var start = abort ? Array.LastIndexOf(log, "abort polar_align", primary) : primary;
            Assert.That(start, Is.GreaterThanOrEqualTo(0), string.Join(", ", log));
            Assert.That(log.Skip(start).Where(IsMotion), Is.Empty, "no motion after the exit's stop");
            return start;
        }

        private static PolarAlignService NewStopService(FakeStopMount mount, WsRecorder ws, IProfileStore? store = null,
                FakeGuideCapture? guider = null) {
            mount.EventCount = () => ws.Events.Count;
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            return NewService(guider ?? NewFake(), solver, mount.Mock, ws, store: store ?? NewStore());
        }

        private static async Task<FakeStopMount> AdjustingAsync(PolarAlignService svc, FakeStopMount mount) {
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            return mount;
        }

        /// <summary>A run in its first seed turn, held there: the bench clock stands still, so the turn's time never runs
        /// out and its motor runs until the test ends the run.</summary>
        private static async Task InFirstTurnAsync(PolarAlignService svc, FakeStopMount mount) {
            mount.Clock.AutoAdvance = false;
            var turning = mount.Clock.TimerSet.Task;
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await mount.FirstMove.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
            await turning.WaitAsync(SignalTimeout).ConfigureAwait(false);
        }

        private static void AssertNoMotionAfter(FakeStopMount mount, int from) =>
            Assert.That(mount.Since(from).Where(IsMotion), Is.Empty, $"no motion command after the stop: {string.Join(", ", mount.Since(from))}");

        /// <summary>The review of #2086: the app's Stop and Done wait <c>PolarAlignApi.endTimeout</c> for the daemon's answer. It
        /// must outlast the daemon's slowest end (a stuck run, from the default bounds) with room for tracking off, the session
        /// row and the camera release, or the app reports a failed Stop while the daemon is still stopping the mount.</summary>
        [Test]
        public void The_clients_end_timeout_outlasts_the_daemons_slowest_end() {
            var mount = NewMount().Object;
            using var svc = new PolarAlignService(NewFake(), NullLogger<PolarAlignService>.Instance, new ScriptedSolver(),
                mount, MediatorMotionControl.For(mount), NewStore(), new WsRecorder(), log: null, new FakeFrameFetcher());
            Assert.That(svc.EndWorstCase, Is.EqualTo(TimeSpan.FromSeconds(47.25)), "the default bounds");

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenAstroAra.sln"))) {
                dir = dir.Parent;
            }
            Assert.That(dir, Is.Not.Null, "repo root above " + AppContext.BaseDirectory);
            var api = File.ReadAllText(Path.Combine(dir!.FullName,
                "client", "openastroara_client", "lib", "services", "polar_align_api.dart"));
            var match = Regex.Match(api, @"static const endTimeout = Duration\(seconds: (\d+)\);");
            Assert.That(match.Success, Is.True, "PolarAlignApi.endTimeout in polar_align_api.dart");
            var client = TimeSpan.FromSeconds(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            Assert.That(client - svc.EndWorstCase, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(10)),
                $"the client waits {client.TotalSeconds} s; the daemon may take {svc.EndWorstCase.TotalSeconds} s before the end's last writes");
        }

        // #2066 — the takeover fences a stuck run before it answers, and only that keeps the run's late stops off the park.
        // Its bound must end inside every caller's own bound, with margin: a caller that gives up first parks while the run
        // is still unfenced.
        [Test]
        public void The_takeovers_stop_bound_ends_inside_every_callers_bound() {
            var mount = NewMount().Object;
            using var svc = new PolarAlignService(NewFake(), NullLogger<PolarAlignService>.Instance, new ScriptedSolver(),
                mount, MediatorMotionControl.For(mount), NewStore(), new WsRecorder(), log: null,
                new FakeFrameFetcher());
            using var safety = new SafetyReactionService();
            using var fault = new FaultReactionService();
            var emergency = new EmergencyStopService();
            var margin = TimeSpan.FromSeconds(1);

            Assert.Multiple(() => {
                Assert.That(svc.TakeoverStopBound + margin, Is.LessThanOrEqualTo(safety.StopPolarAlignBeforeParkTimeout), "the safety park");
                Assert.That(svc.TakeoverStopBound + margin, Is.LessThanOrEqualTo(fault.StopPolarAlignBeforeParkTimeout), "the fault park");
                Assert.That(svc.TakeoverStopBound + margin, Is.LessThanOrEqualTo(emergency.RungTimeout), "Emergency Stop's rung");
            });
        }

        // ── user Stop ────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Stop_mid_turn_sends_AbortSlew_and_MoveAxis_0_on_both_axes_first_and_leaves_tracking_off() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws);
            await InFirstTurnAsync(svc, mount).ConfigureAwait(false);

            var at = mount.Count;
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var log = mount.Log;
            AssertStopRoundFrom(log, at, abort: true);
            AssertNoMotionAfter(mount, Array.IndexOf(log, "abort polar_align", at)); // the renewal it raced is refused
            Assert.That(mount.RaDeg, Is.LessThan(5.0), "the 30° turn ended where the Stop caught it");
            Assert.That(mount.AtRest, Is.True);
            Assert.That(mount.Tracking, Is.False, "every end of a run: tracking off");
            Assert.That(mount.Since(at).Last(), Is.EqualTo("tracking off"), "tracking is the exit's last command");
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("stopped"));
        }

        // A client that gave up (its 5 s timeout, a dropped link) cancels the request: the stop still goes out in full.
        [Test]
        public async Task Stop_with_an_already_cancelled_request_still_stops_the_mount() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws);
            await InFirstTurnAsync(svc, mount).ConfigureAwait(false);
            using var gone = new CancellationTokenSource();
            await gone.CancelAsync().ConfigureAwait(false);

            var at = mount.Count;
            await svc.StopAsync(null, gone.Token).ConfigureAwait(false);

            AssertStopRoundFrom(mount.Log, at, abort: true);
            Assert.That(mount.AtRest, Is.True);
            Assert.That(mount.Tracking, Is.False);
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("stopped"));
        }

        [Test]
        public async Task A_sequence_start_stops_the_mount_and_leaves_tracking_off() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws);
            await InFirstTurnAsync(svc, mount).ConfigureAwait(false);

            var at = mount.Count;
            await svc.StopForSequenceAsync(CancellationToken.None).ConfigureAwait(false);

            AssertStopRoundFrom(mount.Log, at, abort: true);
            Assert.That(mount.AtRest, Is.True);
            Assert.That(mount.Tracking, Is.False);
        }

        // Guide pressed while a run is still open ends the run (the guider takes the camera). Its mount is stopped like any
        // end, but guiding needs tracking, which a GoTo would turn on and guiding does not: the run's tracking is handed
        // back.
        [Test]
        public async Task Guiding_that_takes_the_camera_from_an_open_run_stops_the_mount_and_hands_back_its_tracking() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            var guider = NewFake();
            using var svc = NewStopService(mount, ws, guider: guider);
            await AdjustingAsync(svc, mount).ConfigureAwait(false);
            Assert.That(mount.Tracking, Is.False, "the adjust loop runs with tracking off");

            var at = mount.Count;
            await guider.TakeForGuidingAsync().ConfigureAwait(false);

            AssertStopRoundFrom(mount.Log, at, abort: false);
            Assert.That(mount.Tracking, Is.True, "the tracking the run found, for the guiding that follows");
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("stopped"));
        }

        // A stop the mount does not honor is resent, and never reported as "stopped": the client gets the cut-power alarm.
        [Test]
        public async Task A_stop_the_mount_ignores_is_resent_and_reported_as_mount_did_not_stop() {
            var mount = new FakeStopMount(tracking: true) { IgnoresStops = true };
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws);
            await InFirstTurnAsync(svc, mount).ConfigureAwait(false);

            var at = mount.Count;
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(mount.Since(at).Count(c => c == "abort polar_align"), Is.GreaterThanOrEqualTo(1 + PolarAlignService.StopSends),
                "the Stop's own round, then the exit's three sends");
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.MountDidNotStop));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("Cut the mount's power"));
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStopped), Is.Zero, "never 'stopped' for a mount that may still move");
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("failed"));
        }

        // A Stop that lands while a Start still holds the routine's lock (taking the camera from the guider) has no run to
        // stop yet. It must not let that Start launch a run that moves the mount after the Stop.
        [Test]
        public async Task A_Stop_while_Start_is_taking_the_camera_launches_no_run_and_moves_nothing() {
            var mount = new FakeStopMount(tracking: true, canFindHome: true);
            var ws = new WsRecorder();
            var guider = NewFake();
            var inLease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var leaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            guider.LeaseGate = active => {
                if (!active) {
                    return Task.CompletedTask;
                }
                inLease.TrySetResult();
                return leaseGate.Task;
            };
            using var svc = NewStopService(mount, ws, guider: guider);

            var start = svc.StartAsync(null, CancellationToken.None);
            await inLease.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
            var stop = svc.StopAsync(null, CancellationToken.None);
            leaseGate.SetResult();

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => start).ConfigureAwait(false);
            Assert.That(refused!.Message, Does.Contain("stopped while it was starting"));
            await stop.WaitAsync(SignalTimeout).ConfigureAwait(false);
            await guider.WhenLeaseReleasedAsync().WaitAsync(SignalTimeout).ConfigureAwait(false);
            await Task.Delay(200).ConfigureAwait(false); // a run launched anyway would have turned a motor by now
            Assert.That(mount.Log.Where(IsMotion), Is.Empty, $"nothing moves after the Stop: {string.Join(", ", mount.Log)}");
            Assert.That(mount.Log, Does.Not.Contain("tracking on"));
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStarted), Is.Zero, "no run was announced");
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.Not.EqualTo("seeding"));
        }

        // The hand-back gives tracking back only to a mount whose stop was confirmed: a mount that may still move is left
        // with tracking off, even for the guiding that follows.
        [Test]
        public async Task Guiding_that_takes_the_camera_hands_back_no_tracking_when_the_stop_is_not_confirmed() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            var guider = NewFake();
            using var svc = NewStopService(mount, ws, guider: guider);
            await AdjustingAsync(svc, mount).ConfigureAwait(false);
            mount.IgnoresStops = true;

            var at = mount.Count;
            await guider.TakeForGuidingAsync().ConfigureAwait(false);

            Assert.That(mount.Since(at), Does.Contain("move primary 0").And.Contain("abort polar_align"), "the stop, resent with AbortSlew");
            Assert.That(mount.Since(at), Does.Not.Contain("tracking on"));
            Assert.That(mount.Tracking, Is.False, "no tracking for a mount that did not confirm its stop");
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.MountDidNotStop));
        }

        // A run stuck in a call that ignores cancellation never reaches its own exit: the Stop confirms the stop itself and
        // turns tracking off now, rather than leave a tracking mount running toward the pier for as long as the call hangs.
        [Test]
        public async Task A_Stop_whose_run_is_stuck_turns_tracking_off_itself() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            var solver = new StuckSolver();
            mount.EventCount = () => ws.Events.Count;
            using var svc = NewService(NewFake(), solver, mount.Mock, ws, store: NewStore());
            svc.RunUnwindGrace = TimeSpan.FromMilliseconds(200);
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await solver.Entered.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);

            try {
                await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

                Assert.That(mount.Tracking, Is.False, "tracking off while the run is still stuck");
                Assert.That(mount.Log, Does.Not.Contain("tracking on"));
            } finally {
                solver.Release();
            }
        }

        /// <summary>A solve that ignores cancellation: the run is stuck in it until <see cref="Release"/>.</summary>
        private sealed class StuckSolver : IPolarAlignFrameSolver {
            private readonly TaskCompletionSource<PolarAlignSolveOutcome> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) {
                Entered.TrySetResult();
                return _gate.Task;
            }
            public void Release() => _gate.TrySetResult(new PolarAlignSolveOutcome(false, 0, 0));
        }

        // ── daemon shutdown ──────────────────────────────────────────────────────────────────

        [Test]
        public async Task Daemon_shutdown_mid_turn_stops_the_mount_and_leaves_tracking_off() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            var svc = NewStopService(mount, ws);
            await InFirstTurnAsync(svc, mount).ConfigureAwait(false);

            var at = mount.Count;
            svc.Dispose();

            AssertStopRoundFrom(mount.Log, at, abort: true);
            Assert.That(mount.AtRest, Is.True);
            Assert.That(mount.Tracking, Is.False);
        }

        // ── failures ─────────────────────────────────────────────────────────────────────────

        /// <summary>Waits for the run's error event; asserts the exit's stop round and tracking-off all came before it.</summary>
        private static async Task<(string Reason, string Message)> FailureAfterStopAsync(
                PolarAlignService svc, FakeStopMount mount, WsRecorder ws, bool abort) {
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);
            var errorAt = ws.Events.Select((e, i) => (e, i)).First(x => x.e.Type == WsEventCatalog.PolarAlignError).i;
            var entries = mount.Entries;
            var log = entries.Select(e => e.Command).ToArray();
            var round = ExitRoundStart(log, abort);
            if (!abort) {
                Assert.That(log.Skip(round), Does.Not.Contain("abort polar_align"), "nothing was moving: no AbortSlew");
            }
            Assert.That(log.Last(), Is.EqualTo("tracking off"));
            Assert.That(entries.Skip(round).Select(e => e.EventsBefore), Is.All.LessThanOrEqualTo(errorAt),
                "the mount is stopped and tracking off before the failure is reported");
            Assert.That(mount.AtRest, Is.True);
            Assert.That(mount.Tracking, Is.False, "a failure never turns tracking back on");
            return (error.GetProperty("reason").GetString()!, error.GetProperty("message").GetString()!);
        }

        [Test]
        public async Task A_refused_motor_move_fails_the_run_after_the_mount_is_stopped_and_tracking_off() {
            var mount = new FakeStopMount(tracking: true) { RefusesMotor = true };
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws);

            var (reason, _) = await FailureAfterStopAsync(svc, mount, ws, abort: true).ConfigureAwait(false);

            Assert.That(reason, Is.EqualTo("slew_failed"));
        }

        [Test]
        public async Task A_mount_that_takes_its_turns_but_never_turns_fails_the_fit_after_the_mount_is_stopped_and_tracking_off() {
            // A slipping clutch: the motor commands are taken, the tube stays. The three frames show the same field.
            var mount = new FakeStopMount(tracking: true) { Frozen = true };
            var ws = new WsRecorder();
            mount.EventCount = () => ws.Events.Count;
            using var svc = NewService(NewFake(), new MountFollowingSolver(), mount.Mock, ws, store: NewStore());

            var (reason, message) = await FailureAfterStopAsync(svc, mount, ws, abort: false).ConfigureAwait(false);

            Assert.That(reason, Is.EqualTo("axis_fit_failed"));
            Assert.That(message, Does.Contain("the three frames show the same field").And.Contain("the mount did not turn"));
        }

        [Test]
        public async Task An_unexpected_exception_fails_the_run_after_the_mount_is_stopped_and_tracking_off() {
            // The adjust loop's tracking-off write throws something the routine does not expect: internal_error.
            var mount = new FakeStopMount(tracking: true) { ThrowsOnFirstTrackingOff = true };
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws);

            var (reason, _) = await FailureAfterStopAsync(svc, mount, ws, abort: false).ConfigureAwait(false);

            Assert.That(reason, Is.EqualTo("internal_error"));
        }

        // A failed run reads "failed" at once, but its exit still sends stop rounds and the tracking-off write for a few
        // seconds. A sequence that starts in that window must wait for the exit: otherwise a resent AbortSlew lands on the
        // sequence's first slew, and the tracking-off write on the tracking the sequence just set.
        [Test]
        public async Task A_sequence_start_while_a_failed_run_still_stops_the_mount_waits_for_that_exit() {
            var mount = new FakeStopMount(tracking: true) { RefusesMotor = true };
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws);
            // Holds the exit open long enough to land inside it; the stop is still confirmed.
            svc.StopVerifyNotBefore = TimeSpan.FromSeconds(1.5);
            svc.StopVerifyWindow = TimeSpan.FromSeconds(10);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            while ((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State != "failed") {
                Assert.That(watch.Elapsed, Is.LessThan(SignalTimeout), "the refused move never failed the run");
                await Task.Delay(10).ConfigureAwait(false);
            }
            Assert.That(ws.Count(WsEventCatalog.PolarAlignError), Is.Zero, "inside the exit: the failure is not reported yet");

            Assert.That(svc.IsActive, Is.True, "a run whose exit still stops the mount counts as active");
            await svc.StopForSequenceAsync(CancellationToken.None).ConfigureAwait(false);

            var at = mount.Count;
            Assert.That(ws.Count(WsEventCatalog.PolarAlignError), Is.EqualTo(1), "the exit finished before the sequence goes on");
            Assert.That(ws.Events.Single(e => e.Type == WsEventCatalog.PolarAlignError).Payload.GetProperty("reason").GetString(),
                Is.EqualTo("slew_failed"), "the stop was confirmed: the run's own failure, no cut-power alarm");
            Assert.That(mount.Tracking, Is.False);
            await Task.Delay(200).ConfigureAwait(false);
            Assert.That(mount.Since(at), Is.Empty, $"nothing reaches the mount after the sequence start returns: {string.Join(", ", mount.Since(at))}");
            Assert.That(svc.IsActive, Is.False);
        }

    }
}
