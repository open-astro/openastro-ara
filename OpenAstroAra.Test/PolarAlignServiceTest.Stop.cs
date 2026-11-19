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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using TelescopeAxes = OpenAstroAra.Core.Enums.TelescopeAxes;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2048 — "Stop does not stop it". Every exit of a run (Stop, a client that gave up, daemon shutdown, a failure, an
    /// exception, a time bound, Done and the check's verdict) sends the mount its stop — AbortSlew while a GoTo, Find Home
    /// or motor move may run, then MoveAxis 0 on both axes — confirms it, and then sets tracking by
    /// <c>stop_tracking_when_done</c> (on by default, as NINA's): off, or back to how the run found it only after a run
    /// that finished. Each test drives <see cref="FakeStopMount"/>, a mount with real motion state that logs every
    /// command in order, and asserts the commands and the final tracking.
    /// </summary>
    public partial class PolarAlignServiceTest {

        /// <summary>
        /// The bench mount for the exit paths: a mocked mediator that is also the daemon's telescope service (the path
        /// production takes), with real state — motor rates, a GoTo or Find Home in flight, tracking — and an ordered log
        /// of every motion and tracking command the routine sends. A fresh position read advances each running motor by
        /// <see cref="StepDeg"/>, as the leased motor turns between the routine's polls.
        /// </summary>
        private sealed class FakeStopMount {
            private readonly object _lock = new();
            private readonly List<(string Command, int EventsBefore)> _log = new();
            private readonly double[] _rates = new double[2];
            private double _raDeg;
            private double _decDeg = 85.0;
            private TaskCompletionSource<bool>? _goto;
            private TaskCompletionSource<bool>? _home;
            private long _epoch;
            private long _trackingWrites;
            private bool _tracking;
            private bool _runaway;
            private bool _threwOnTrackingOff;

            public Mock<ITelescopeMediator> Mock { get; } = new();

            /// <summary>Each fresh read moves a running motor this far (degrees).</summary>
            public double StepDeg { get; set; } = 0.25;
            /// <summary>A GoTo arrives at once; false = it runs until AbortSlew.</summary>
            public bool GotoArrives { get; set; } = true;
            /// <summary>Find Home arrives at once; false = it runs until AbortSlew.</summary>
            public bool HomeArrives { get; set; } = true;
            /// <summary>Find Home's call returns only when the mount stops homing, whatever the caller's token says.</summary>
            public bool HomeIgnoresCancel { get; set; }
            /// <summary>The mount refuses every motor command that would move it.</summary>
            public bool RefusesMotor { get; set; }
            /// <summary>The motor takes its commands, but the reading never changes (a slipping clutch, a stall).</summary>
            public bool Frozen { get; set; }
            /// <summary>AbortSlew and MoveAxis 0 are not honored: once one arrives, the RA axis runs on.</summary>
            public bool IgnoresStops { get; set; }
            /// <summary>The first tracking-off write throws (a mount fault the routine does not expect).</summary>
            public bool ThrowsOnFirstTrackingOff { get; set; }
            /// <summary>How many WS events were published when a command arrived: the order against the run's last event.</summary>
            public Func<int>? EventCount { get; set; }

            public TaskCompletionSource FirstMove { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource GotoStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource HomeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public FakeStopMount(bool tracking = true, bool canFindHome = false) {
                _tracking = tracking;
                Mock.Setup(m => m.GetInfo()).Returns(() => {
                    lock (_lock) {
                        return new TelescopeInfo {
                            Connected = true, SiderealTime = 20.0, TrackingEnabled = _tracking, CanFindHome = canFindHome,
                            Slewing = SlewingLocked(),
                        };
                    }
                });
                Mock.Setup(m => m.GetCurrentPosition()).Returns(() => {
                    lock (_lock) {
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
                Mock.Setup(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                    .Returns((Coordinates target, CancellationToken _) => {
                        lock (_lock) {
                            Record("goto");
                            if (GotoArrives) {
                                (_raDeg, _decDeg) = (target.RADegrees, target.Dec);
                            } else {
                                _goto = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                                GotoStarted.TrySetResult();
                            }
                        }
                        return Task.FromResult(true);
                    });
                // As the daemon's: a cancelled wait throws and leaves the mount slewing.
                Mock.Setup(m => m.WaitForSlew(It.IsAny<CancellationToken>())).Returns((CancellationToken ct) => {
                    TaskCompletionSource<bool>? pending;
                    lock (_lock) {
                        pending = _goto;
                    }
                    return pending is null ? Task.CompletedTask : pending.Task.WaitAsync(ct);
                });
                Mock.Setup(m => m.FindHome(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                    .Returns((IProgress<ApplicationStatus> _, CancellationToken ct) => {
                        lock (_lock) {
                            Record("findhome");
                            if (HomeArrives) {
                                (_raDeg, _decDeg) = (0.0, 90.0);
                                return Task.FromResult(true);
                            }
                            _home = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            HomeStarted.TrySetResult();
                            return HomeIgnoresCancel ? _home.Task : _home.Task.WaitAsync(ct);
                        }
                    });

                var service = Mock.As<ITelescopeService>();
                service.SetupGet(s => s.AbortEpoch).Returns(() => Interlocked.Read(ref _epoch));
                service.Setup(s => s.ReadPointingNowAsync(It.IsAny<CancellationToken>())).Returns(() => {
                    lock (_lock) {
                        if (!Frozen) {
                            _raDeg = (_raDeg + (Math.Sign(_rates[0]) * StepDeg) + (_runaway ? StepDeg : 0.0) + 360.0) % 360.0;
                            var dec = Math.Sign(_rates[1]) * StepDeg;
                            _decDeg = _decDeg >= 0 ? _decDeg - dec : _decDeg + dec;
                        }
                        return Task.FromResult<(double RaHours, double DecDegrees)?>((_raDeg / 15.0, _decDeg));
                    }
                });
                service.Setup(s => s.ReadSlewingNowAsync(It.IsAny<CancellationToken>())).Returns(() => {
                    lock (_lock) {
                        return Task.FromResult<bool?>(SlewingLocked());
                    }
                });
                service.Setup(s => s.MoveAxisAsync(It.IsAny<int>(), It.IsAny<double>(), It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
                    .Returns((int axis, double rate, int? _, long? since, CancellationToken _) => {
                        lock (_lock) {
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
                            return Task.CompletedTask;
                        }
                    });
                service.Setup(s => s.AbortSlewAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .Returns((string reason, CancellationToken _) => {
                        lock (_lock) {
                            Record("abort " + reason);
                            Interlocked.Increment(ref _epoch);
                            if (IgnoresStops) {
                                _runaway = true;
                                return Task.CompletedTask;
                            }
                            _rates[0] = 0;
                            _rates[1] = 0;
                            _goto?.TrySetResult(false);
                            _goto = null;
                            _home?.TrySetResult(false);
                            _home = null;
                        }
                        return Task.CompletedTask;
                    });
            }

            private bool SlewingLocked() => _rates[0] != 0 || _rates[1] != 0 || _goto is not null || _home is not null || _runaway;

            private void Record(string command) => _log.Add((command, EventCount?.Invoke() ?? 0));

            public bool Tracking {
                get {
                    lock (_lock) {
                        return _tracking;
                    }
                }
            }

            /// <summary>Nothing moves: no motor rate, no GoTo or Find Home in flight, no runaway.</summary>
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
                        return _raDeg;
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
            command is "goto" or "findhome"
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
            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            return mount;
        }

        /// <summary>A run in its first seed turn, slowed down so the test lands inside it: 30° at a step a poll would take
        /// about six seconds.</summary>
        private static async Task InFirstTurnAsync(PolarAlignService svc, FakeStopMount mount) {
            svc.AxisMovePoll = TimeSpan.FromMilliseconds(50);
            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await mount.FirstMove.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
        }

        private static void AssertNoMotionAfter(FakeStopMount mount, int from) =>
            Assert.That(mount.Since(from).Where(IsMotion), Is.Empty, $"no motion command after the stop: {string.Join(", ", mount.Since(from))}");

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
            Assert.That(mount.Tracking, Is.False, "stop_tracking_when_done (default on): tracking off");
            Assert.That(mount.Since(at).Last(), Is.EqualTo("tracking off"), "tracking is the exit's last command");
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("stopped"));
        }

        [Test]
        public async Task Stop_during_Find_Home_aborts_it() {
            var mount = new FakeStopMount(tracking: true, canFindHome: true) { HomeArrives = false };
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws, NewHomeStore());
            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await mount.HomeStarted.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);

            var at = mount.Count;
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            AssertStopRoundFrom(mount.Log, at, abort: true);
            AssertNoMotionAfter(mount, at);
            Assert.That(mount.AtRest, Is.True, "Find Home was aborted, not left running");
            Assert.That(mount.Tracking, Is.False);
        }

        // The stop must not wait on the operation it stops: a Find Home whose call ignores cancellation (it returns only
        // when the mount stops homing) would hold the run's unwind for the whole grace — and the mount would home on.
        [Test]
        public async Task Stop_aborts_a_Find_Home_that_ignores_cancellation_without_waiting_for_the_run() {
            var mount = new FakeStopMount(tracking: true, canFindHome: true) { HomeArrives = false, HomeIgnoresCancel = true };
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws, NewHomeStore());
            svc.RunUnwindGrace = TimeSpan.FromSeconds(30);
            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await mount.HomeStarted.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);

            var at = mount.Count;
            var watch = Stopwatch.StartNew();
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            watch.Stop();

            AssertStopRoundFrom(mount.Log, at, abort: true);
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)), "Stop did not wait out the 30 s unwind grace");
            Assert.That(mount.AtRest, Is.True);
            Assert.That(mount.Tracking, Is.False);
        }

        [Test]
        public async Task Stop_during_the_return_to_start_GoTo_aborts_it() {
            // use_home off: the check after the fine stage first slews back to where the run started.
            var mount = new FakeStopMount(tracking: true) { GotoArrives = false };
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws);
            await AdjustingAsync(svc, mount).ConfigureAwait(false);
            await svc.NextAsync(null, CancellationToken.None).ConfigureAwait(false);
            await mount.GotoStarted.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);

            var at = mount.Count;
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            AssertStopRoundFrom(mount.Log, at, abort: true);
            AssertNoMotionAfter(mount, at);
            Assert.That(mount.AtRest, Is.True, "the GoTo was aborted, not left running");
            Assert.That(mount.Tracking, Is.False, "the GoTo turned tracking on; the Stop leaves it off");
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
        public async Task Stop_never_turns_tracking_back_on_even_with_hand_back() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws, HandBackStore());
            await AdjustingAsync(svc, mount).ConfigureAwait(false);

            var at = mount.Count;
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            AssertStopRoundFrom(mount.Log, at, abort: false); // nothing moves while adjusting: no AbortSlew
            Assert.That(mount.Since(at), Does.Not.Contain("tracking on"));
            Assert.That(mount.Tracking, Is.False, "the mount tracked before the run, but a Stop leaves tracking off");
        }

        [Test]
        public async Task A_sequence_start_stops_the_mount_and_leaves_tracking_off() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws, HandBackStore());
            await InFirstTurnAsync(svc, mount).ConfigureAwait(false);

            var at = mount.Count;
            await svc.StopForSequenceAsync(CancellationToken.None).ConfigureAwait(false);

            AssertStopRoundFrom(mount.Log, at, abort: true);
            Assert.That(mount.AtRest, Is.True);
            Assert.That(mount.Tracking, Is.False);
        }

        // Guide pressed while a run is still open ends the run (the guider takes the camera). Its mount is stopped like any
        // end, but guiding needs tracking, which a GoTo would turn on and guiding does not: the run's tracking is handed
        // back even with stop_tracking_when_done on.
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

        // ── daemon shutdown ──────────────────────────────────────────────────────────────────

        [Test]
        public async Task Daemon_shutdown_mid_turn_stops_the_mount_and_leaves_tracking_off() {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            var svc = NewStopService(mount, ws, HandBackStore());
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
            await svc.StartAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
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
            Assert.That(mount.Tracking, Is.False, "a failure never turns tracking back on, whatever the setting");
            return (error.GetProperty("reason").GetString()!, error.GetProperty("message").GetString()!);
        }

        [Test]
        public async Task A_refused_motor_move_fails_the_run_after_the_mount_is_stopped_and_tracking_off() {
            var mount = new FakeStopMount(tracking: true) { RefusesMotor = true };
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws, HandBackStore());

            var (reason, _) = await FailureAfterStopAsync(svc, mount, ws, abort: true).ConfigureAwait(false);

            Assert.That(reason, Is.EqualTo("slew_failed"));
        }

        [Test]
        public async Task A_turn_that_stalls_fails_after_the_mount_is_stopped_and_tracking_off() {
            var mount = new FakeStopMount(tracking: true) { Frozen = true };
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws, HandBackStore());

            var (reason, message) = await FailureAfterStopAsync(svc, mount, ws, abort: true).ConfigureAwait(false);

            Assert.That(reason, Is.EqualTo("slew_failed"));
            Assert.That(message, Does.Contain("stopped moving"));
        }

        [Test]
        public async Task An_unexpected_exception_fails_the_run_after_the_mount_is_stopped_and_tracking_off() {
            // The adjust loop's tracking-off write throws something the routine does not expect: internal_error.
            var mount = new FakeStopMount(tracking: true) { ThrowsOnFirstTrackingOff = true };
            var ws = new WsRecorder();
            using var svc = NewStopService(mount, ws, HandBackStore());

            var (reason, _) = await FailureAfterStopAsync(svc, mount, ws, abort: false).ConfigureAwait(false);

            Assert.That(reason, Is.EqualTo("internal_error"));
        }

        // ── finished runs ────────────────────────────────────────────────────────────────────

        [TestCase(true, false)]
        [TestCase(false, true)]
        public async Task The_checks_verdict_stops_the_mount_first_then_sets_tracking_by_the_setting(bool stopTrackingWhenDone, bool trackingAfter) {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { StopTrackingWhenDone = stopTrackingWhenDone });
            using var svc = NewService(NewFake(), new HintFollowingSolver(), mount.Mock, ws, store: store);
            mount.EventCount = () => ws.Events.Count;

            await svc.StartAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "checked", "failed").ConfigureAwait(false)).State, Is.EqualTo("checked"));

            var entries = mount.Entries;
            var log = entries.Select(e => e.Command).ToArray();
            var round = ExitRoundStart(log, abort: false);
            // The last turn ended with its own stop: the exit's round has nothing to abort.
            Assert.That(log.Skip(Array.LastIndexOf(log, "move primary 0", round - 1) + 1), Does.Not.Contain("abort polar_align"));
            var checkedAt = ws.Events.Select((e, i) => (e, i)).First(x => x.e.Type == WsEventCatalog.PolarAlignChecked).i;
            Assert.That(entries.Skip(round).Select(e => e.EventsBefore), Is.All.LessThanOrEqualTo(checkedAt),
                "the mount is stopped before the verdict is reported");
            Assert.That(mount.Tracking, Is.EqualTo(trackingAfter));
            if (trackingAfter) {
                Assert.That(log.Skip(round), Does.Not.Contain("tracking off"), "the run never changed tracking: nothing to hand back");
            }
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public async Task Done_stops_the_mount_then_sets_tracking_by_the_setting(bool stopTrackingWhenDone, bool trackingAfter) {
            var mount = new FakeStopMount(tracking: true);
            var ws = new WsRecorder();
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { StopTrackingWhenDone = stopTrackingWhenDone });
            using var svc = NewStopService(mount, ws, store);
            await AdjustingAsync(svc, mount).ConfigureAwait(false);

            var at = mount.Count;
            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);

            AssertStopRoundFrom(mount.Log, at, abort: false);
            Assert.That(mount.Since(at).Last(), Is.EqualTo(trackingAfter ? "tracking on" : "tracking off"));
            Assert.That(mount.Tracking, Is.EqualTo(trackingAfter), "Done hands back the tracking the run found only with the setting off");
        }
    }
}
