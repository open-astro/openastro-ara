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
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Sequencer.SequenceItem.Telescope;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §45 (polar-align engine) — the full <see cref="PolarAlignService"/> state machine driven against the
    /// in-process <see cref="FakeGuideCapture"/> (capture, frame completion and PA-session lease, no socket and
    /// no timing; ADR 0016 rule 5), a scripted <see cref="IPolarAlignFrameSolver"/>, and a mocked mount. Covers the
    /// seed (frame A → RA slew → frame B → axis fit), the live-adjust loop (tracking off, errors populated,
    /// progress events), the §45.11 pause-after-5-failed-solves + auto-resume, seed-failure → failed +
    /// error event, and hand-back (tracking restored, lease released) on Stop and on failure. What the PHD2
    /// adapter does on the wire is in <see cref="Phd2GuideCaptureLinkTest"/>. A test waits on a published event,
    /// the fake's signals or a gate, never on a delay; the two waits that check a call is still blocked are the
    /// named exceptions the ADR 0016 rule 5 scan counts.
    /// </summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    [Category("bench")]
    public partial class PolarAlignServiceTest {

        // Synthetic sky: the mount's RA axis sits exactly at the true pole, the camera points at
        // dec 85° — so seed frames A/B are two points on the dec-85 circle, the fitted axis is the
        // pole, and the expected alt error is MINUS the refraction term at the pole altitude
        // (a geometrically-perfect axis appears ~1′ below the APPARENT pole) with ~0 az error.
        private static readonly bool[] AcquireThenRelease = { true, false };
        private static readonly bool[] AcquireReleaseAcquire = { true, false, true };
        private const double SiteLatDeg = 45.0;
        private const double SiteLonDeg = -75.0;
        private static readonly PolarAlignSolveOutcome SolveA = new(true, 0.0, 85.0);
        private static readonly PolarAlignSolveOutcome SolveB = new(true, 30.0, 85.0);
        // The third seed solve: one more 30° step around the pole (the bench store's SeedRotationDeg).
        private static readonly PolarAlignSolveOutcome SolveC = new(true, 60.0, 85.0);
        private static readonly PolarAlignSolveOutcome Unsolved = new(false, 0, 0);

        private sealed class ScriptedSolver : IPolarAlignFrameSolver {
            private readonly ConcurrentQueue<PolarAlignSolveOutcome> _script = new();
            /// <summary>The answer once the script runs out; null = the solve lands where the mount points (the
            /// hint), as a real sky would, so the three-point seed sees three distinct pointings on one circle.</summary>
            public PolarAlignSolveOutcome? Fallback { get; set; }
            public void Enqueue(params PolarAlignSolveOutcome[] outcomes) {
                foreach (var o in outcomes) {
                    _script.Enqueue(o);
                }
            }
            public ConcurrentQueue<int> Binnings { get; } = new();
            public double? Measured { get; set; }
            public int Measures;
            public Task<double?> MeasureFocalLengthAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) {
                Interlocked.Increment(ref Measures);
                return Task.FromResult(Measured);
            }
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) {
                Binnings.Enqueue(binning);
                return Task.FromResult(_script.TryDequeue(out var o) ? o
                    : Fallback ?? (hintRa is double ra && hintDec is double dec ? new PolarAlignSolveOutcome(true, ra, dec) : SolveB));
            }
        }

        private sealed class WsRecorder : IWsBroadcaster {
            private readonly object _gate = new();
            private readonly List<(Func<bool> Done, TaskCompletionSource Signal)> _waiters = new();
            public ConcurrentQueue<(string Type, JsonElement Payload)> Events { get; } = new();
            public long CurrentSequence => 0;

            /// <summary>Called with each event's type before it is recorded, on the publisher's thread: a test can hold
            /// the publisher there.</summary>
            public Action<string>? OnPublish { get; set; }

            public Task PublishAsync(OpenAstroAra.Server.Contracts.WsEvents.WsEvent eventType, JsonElement payload, CancellationToken ct) {
                OnPublish?.Invoke(eventType);
                Events.Enqueue((eventType, payload.Clone()));
                List<TaskCompletionSource> due;
                lock (_gate) {
                    var ready = _waiters.Where(w => w.Done()).ToList();
                    due = ready.Select(w => w.Signal).ToList();
                    foreach (var w in ready) {
                        _waiters.Remove(w);
                    }
                }
                foreach (var signal in due) {
                    signal.TrySetResult();
                }
                return Task.CompletedTask;
            }

            public int Count(string type) => Events.Count(e => e.Type == type);

            /// <summary>Completes once <paramref name="done"/> holds for the events recorded so far; checked again
            /// after every event, so a state that is gone a moment later is still seen.</summary>
            public Task WhenAsync(Func<(string Type, JsonElement Payload)[], bool> done) {
                lock (_gate) {
                    bool Holds() => done(Events.ToArray());
                    if (Holds()) {
                        return Task.CompletedTask;
                    }
                    var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters.Add((Holds, signal));
                    return signal.Task;
                }
            }

            /// <summary>Completes when <paramref name="count"/> events of <paramref name="type"/> have been recorded
            /// after the first <paramref name="skip"/> events (a restart waits for its own run's events).</summary>
            public Task WhenCountAsync(string type, int count, int skip = 0) =>
                WhenAsync(events => events.Skip(skip).Count(e => e.Type == type) >= count);
        }

        // The recorder a service publishes to, so a state wait finds the events of the service it is given.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PolarAlignService, WsRecorder> Recorders = new();

        private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(20);

        /// <summary>The in-process Guider: an ASI120-class guide camera (§45.4) unless the test says otherwise;
        /// <paramref name="noFrameSize"/> = a daemon without get_camera_frame_size; <paramref name="answerCaptures"/>
        /// false = a capture is accepted and never completes, which parks the routine on the capture-complete wait
        /// and keeps lifecycle tests free of solver/loop side-effects.</summary>
        private static FakeGuideCapture NewFake(bool answerCaptures = true, int frameWidth = 1280, int frameHeight = 960,
                bool noFrameSize = false) {
            var fake = new FakeGuideCapture { FrameSize = noFrameSize ? null : (frameWidth, frameHeight) };
            if (!answerCaptures) {
                fake.Script = _ => FakeCaptureOutcome.NeverCompletes;
            }
            return fake;
        }

        /// <summary>The PA-session lease calls in order: true = acquire or renew, false = release.</summary>
        private static bool[] PaCalls(FakeGuideCapture fake) => fake.LeaseCalls.Select(c => c != "off").ToArray();

        /// <summary>§45.4 — the binning each capture carried (null = the guider's 1×1 default).</summary>
        private static int?[] CaptureBinnings(FakeGuideCapture fake) => fake.Captures.Select(c => c.Binning).ToArray();

        private static Mock<ITelescopeMediator> NewMount(bool connected = true, bool tracking = true) {
            var mount = new Mock<ITelescopeMediator>();
            var info = new TelescopeInfo {
                // LST 20 h puts RA 0 east of the meridian, so the seed slew turns RA up like the SolveA→SolveB pair.
                // #2064 — a physical pose: HA −4 h, Dec 85°, counterweight down (φ 30°), which is the west side of the
                // pier (default(PierSide) would say east).
                Connected = connected, SiderealTime = 20.0, TrackingEnabled = tracking, SideOfPier = PierSide.pierWest,
            };
            mount.Setup(m => m.GetInfo()).Returns(info);
            // The reported pointing follows slews, so the routine reads the mount's own RA turn.
            var position = new Coordinates(0.0, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees);
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => position);
            mount.Setup(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Callback<Coordinates, CancellationToken>((target, _) => position = target)
                .ReturnsAsync(true);
            SimulateMotors(mount, () => position, c => position = c);
            mount.Setup(m => m.WaitForSlew(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            // The mount reports the tracking it was last set to: the live readout confirms it off (#2072).
            mount.Setup(m => m.SetTrackingEnabled(It.IsAny<bool>())).Returns((bool on) => {
                info.TrackingEnabled = on;
                return true;
            });
            return mount;
        }

        /// <summary>#2066 — a fresh reading of the telescope service as Polar Align takes it: stamped now, mount session 0.</summary>
        private static MountReading FreshReading(double raHours, double decDeg) =>
            new(raHours, decDeg, Slewing: false, PierSide.pierWest, DateTimeOffset.UtcNow, SessionEpoch: 0);

        private static double Wrap180Test(double deg) => ((deg + 540.0) % 360.0) - 180.0;

        /// <summary>#2064 — the pier side of a counterweight-down pose at RA <paramref name="raDeg"/> and LST
        /// <paramref name="lstHours"/>: east for an hour angle in [0, 12 h), west otherwise.</summary>
        private static PierSide CounterweightDownSide(double lstHours, double raDeg) =>
            Wrap180Test((lstHours * 15.0) - raDeg) >= 0.0 ? PierSide.pierEast : PierSide.pierWest;

        /// <summary>A raw reading past the pole (|Dec| > 90°) as the same sky point: (RA+180°, ±180° − Dec).</summary>
        private static Coordinates Folded(Coordinates c) => Math.Abs(c.Dec) > 90.0
            ? new Coordinates((c.RADegrees + 180.0) % 360.0, Math.CopySign(180.0, c.Dec) - c.Dec, Epoch.JNOW, Coordinates.RAType.Degrees)
            : c;

        /// <summary>One motor command's worth of travel on the bench mount.</summary>
        private const double MotorStepDeg = 0.25;

        /// <summary>The bench mount's motors: every MoveAxis command with a non-zero rate steps the pointing
        /// <see cref="MotorStepDeg"/> on that axis (RA up for a positive rate, Dec toward the equator), and the
        /// stop reports where the move ended (<paramref name="stopped"/>) — a move reads like a slew to that point.</summary>
        private static void SimulateMotors(Mock<ITelescopeMediator> mount, Func<Coordinates> read, Action<Coordinates> write,
                Action<Coordinates>? stopped = null, bool raUpIsNegative = false) {
            var moving = false;
            mount.Setup(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.IsAny<double>()))
                .Callback<TelescopeAxes, double>((axis, rate) => {
                    var p = read();
                    if (rate == 0) {
                        if (moving) {
                            moving = false;
                            stopped?.Invoke(p);
                        }
                        return;
                    }
                    moving = true;
                    var step = Math.Sign(rate) * MotorStepDeg;
                    write(axis == TelescopeAxes.Primary
                        ? new Coordinates((p.RADegrees + (raUpIsNegative ? -step : step) + 360.0) % 360.0, p.Dec, Epoch.JNOW, Coordinates.RAType.Degrees)
                        : new Coordinates(p.RADegrees, p.Dec >= 0 ? p.Dec - step : p.Dec + step, Epoch.JNOW, Coordinates.RAType.Degrees));
                });
        }

        private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<PolarAlignService> {
            public ConcurrentQueue<(int EventId, string Message)> Entries { get; } = new();
            /// <summary>Completes when a superseded (zombie) run logs its failure: event 4514, an Error, so it is
            /// checked before the Information filter below.</summary>
            public TaskCompletionSource Superseded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                    TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                if (eventId.Id == 4514 && formatter(state, exception).EndsWith(": superseded", StringComparison.Ordinal)) {
                    Superseded.TrySetResult();
                }
                if (logLevel == Microsoft.Extensions.Logging.LogLevel.Information) {
                    Entries.Enqueue((eventId.Id, formatter(state, exception)));
                }
            }
        }

        private sealed class RecordingLog : IPolarAlignmentLog {
            public ConcurrentQueue<PolarAlignmentRecord> Rows { get; } = new();
            private readonly TaskCompletionSource _firstRow = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>Completes when the first row is written.</summary>
            public Task FirstRow => _firstRow.Task;
            /// <summary>Runs as each row is written, before it is recorded (the writer still holds its locks).</summary>
            public Action<PolarAlignmentRecord>? OnInsert { get; set; }
            public Task InsertAsync(PolarAlignmentRecord record, CancellationToken ct) {
                OnInsert?.Invoke(record);
                Rows.Enqueue(record);
                _firstRow.TrySetResult();
                return Task.CompletedTask;
            }
            public Task<PolarAlignmentRecord?> GetLatestMeasuredAsync(DateTimeOffset since, CancellationToken ct) =>
                Task.FromResult<PolarAlignmentRecord?>(null);
        }

        private static InMemoryProfileStore NewStore() {
            var store = new InMemoryProfileStore();
            store.PutSiteSettings(store.GetSiteSettings() with { LatitudeDeg = SiteLatDeg, LongitudeDeg = SiteLonDeg });
            // Bench cadences: the real §45.12 defaults would make loop tests take minutes. One frame per point (#2082): a
            // scripted solver's answers map one to one onto the measurement's points.
            store.PutPolarAlignSettings(new PolarAlignSettingsDto(
                ExposureSeconds: 0.05, LoopCadenceMs: 20, SettleSeconds: 0, SeedRotationDeg: 30.0, FramesPerPoint: 1).Confirmed());
            return store;
        }

        /// <summary>§45 capture-fetch bench fetcher — "downloads" by writing a placeholder file
        /// (the scripted solvers never read the bytes) and records the fetched URIs.</summary>
        private sealed class FakeFrameFetcher : IPolarAlignFrameFetcher {
            public ConcurrentQueue<(string Host, int RpcPort, string Filename)> Fetched { get; } = new();
            public bool Fail { get; set; }

            public async Task FetchAsync(string host, int rpcPort, string filename, string destinationPath, CancellationToken ct) {
                if (Fail) {
                    throw new HttpRequestException($"404 for {filename}");
                }
                Fetched.Enqueue((host, rpcPort, filename));
                await File.WriteAllTextAsync(destinationPath, "FAKE-FITS", ct).ConfigureAwait(false);
            }

            public static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xD9 };

            public Task<byte[]> FetchLiveFrameJpegAsync(string host, int rpcPort, CancellationToken ct) =>
                Fail ? Task.FromException<byte[]>(new HttpRequestException("404 for frame.jpg")) : Task.FromResult(Jpeg);
        }

        private static PolarAlignService NewService(IGuideCapture guider, IPolarAlignFrameSolver solver,
                Mock<ITelescopeMediator>? mount = null, WsRecorder? ws = null, IProfileStore? store = null,
                IPolarAlignmentLog? log = null, IPolarAlignFrameFetcher? fetcher = null,
                Microsoft.Extensions.Logging.ILogger<PolarAlignService>? logger = null,
                IPolarAlignMainCameraSolver? mainCamera = null, IPolarAlignerSource? aligner = null, IMountClockGate? mountClock = null,
                bool motorDirectionSaved = true) {
            return NewServiceOn(guider, solver, (mount ?? NewMount()).Object, ws, store, log, fetcher, logger, mainCamera, aligner, mountClock,
                motorDirectionSaved);
        }

        /// <summary><see cref="NewService"/> on any mount: a mock's object, or the fake GEM mount (#2064). With
        /// <paramref name="motorDirectionSaved"/> (the default) the store already holds a +1 RA motor direction for the
        /// mount's driver (#2081), so a run makes no 0.5° direction test and its turns are the measurement's alone; a test
        /// of the direction test itself passes false.</summary>
        private static PolarAlignService NewServiceOn(IGuideCapture guider, IPolarAlignFrameSolver solver,
                ITelescopeMediator mount, WsRecorder? ws = null, IProfileStore? store = null,
                IPolarAlignmentLog? log = null, IPolarAlignFrameFetcher? fetcher = null,
                Microsoft.Extensions.Logging.ILogger<PolarAlignService>? logger = null,
                IPolarAlignMainCameraSolver? mainCamera = null, IPolarAlignerSource? aligner = null, IMountClockGate? mountClock = null,
                bool motorDirectionSaved = true) {
            ws ??= new WsRecorder();
            store ??= NewStore();
            if (motorDirectionSaved && store.GetPolarAlignMotorDirection() is null) {
                var driver = PolarAlignService.MountDriverOf(mount.GetInfo());
                store.PutPolarAlignMotorDirection(new PolarAlignMotorDirectionDto(1, driver.Mount, driver.Info, driver.Version, DateTimeOffset.UtcNow));
            }
            if (mount is IMocked<ITelescopeMediator> mocked && mount is ITelescopeService service) {
                // #2067 — a mock set up as the telescope service answers a turn's poll (position and pier side) with its
                // position read, and takes every rate the turn asks for: a test that needs otherwise sets them up itself.
                var face = mocked.Mock.As<ITelescopeService>();
                face.Setup(s => s.ReadPoseNowAsync(It.IsAny<CancellationToken>())).Returns((CancellationToken c) => service.ReadPositionNowAsync(c));
                face.Setup(s => s.MatchMoveAxisRate(It.IsAny<int>(), It.IsAny<double>())).Returns((int _, double rate) => rate);
            }
            var svc = new PolarAlignService(guider, logger ?? NullLogger<PolarAlignService>.Instance, solver,
                mount, MediatorMotionControl.For(mount), store, ws, log,
                fetcher ?? new FakeFrameFetcher(), mainCamera: mainCamera, aligner: aligner, mountClock: mountClock) {
                PausedRetryDelay = TimeSpan.FromMilliseconds(20),
                TrackingConfirmTimeout = TimeSpan.FromMilliseconds(200),
                TrackingConfirmPoll = TimeSpan.FromMilliseconds(10),
                AxisMovePoll = TimeSpan.FromMilliseconds(1),
                AxisSettle = TimeSpan.Zero,
                AxisMoveStall = TimeSpan.FromMilliseconds(200),
                // #2048: the exit's stop confirmation at bench speed (the daemon waits 2 s at least, 8 s at most).
                StopVerifyPoll = TimeSpan.FromMilliseconds(1),
                StopVerifyStillFor = TimeSpan.Zero,
                StopVerifyNotBefore = TimeSpan.Zero,
                StopResendAfter = TimeSpan.FromMilliseconds(20),
                StopVerifyWindow = TimeSpan.FromMilliseconds(500),
                ShutdownGrace = TimeSpan.FromSeconds(2),
                // #2066 (ARA-2) — the two readings before each turn back to back (the daemon waits 1 s between them).
                MountSteadyFor = TimeSpan.Zero,
            };
            if (mount is FakeGemMount gem) {
                svc.Clock = gem.Clock; // #2066 — a reading's age on the fake's own clock
            }
            Recorders.Add(svc, ws);
            return svc;
        }

        /// <summary>The first recorded event of <paramref name="type"/>: for a state that is transient at test
        /// cadences (paused → adjusting within one 20 ms retry delay) the event is the witness, not a status read.</summary>
        private static async Task<JsonElement> WaitForEventAsync(WsRecorder ws, string type) {
            await ws.WhenCountAsync(type, 1).WaitAsync(SignalTimeout).ConfigureAwait(false);
            return ws.Events.First(e => e.Type == type).Payload;
        }

        /// <summary>How many events the service has published so far: pass it to <see cref="PollStateAsync(PolarAlignService, int, string[])"/>
        /// before a restart so the wait is for the new run's events.</summary>
        private static int Mark(PolarAlignService svc) => Recorders.TryGetValue(svc, out var ws) ? ws.Events.Count : 0;

        private static Task<PolarAlignStateDto> PollStateAsync(PolarAlignService svc, params string[] anyOf) =>
            PollStateAsync(svc, 0, anyOf);

        /// <summary>Waits for the event the service publishes right after it enters one of <paramref name="anyOf"/>
        /// (<c>failed</c>: error, set before <c>PublishErrorAsync</c>; <c>adjusting</c>: progress, set before the seed's
        /// progress; <c>stopped</c>, <c>checked</c>: their own events), then reads the status once. Only events after
        /// the first <paramref name="skip"/> count.</summary>
        private static async Task<PolarAlignStateDto> PollStateAsync(PolarAlignService svc, int skip, params string[] anyOf) {
            var types = anyOf.Select<string, string>(state => state switch {
                "failed" => WsEventCatalog.PolarAlignError,
                "adjusting" => WsEventCatalog.PolarAlignProgress,
                "stopped" => WsEventCatalog.PolarAlignStopped,
                "checked" => WsEventCatalog.PolarAlignChecked,
                _ => throw new ArgumentException($"no event follows the {state} state", nameof(anyOf)),
            }).ToArray();
            await Recorders.GetValue(svc, _ => throw new InvalidOperationException("service not made by NewService"))
                .WhenAsync(events => events.Skip(skip).Any(e => types.Contains(e.Type))).WaitAsync(SignalTimeout).ConfigureAwait(false);
            return await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>Waits until <paramref name="stage"/> has started and published its first progress or error:
        /// the stage event, then the next progress or error after it (the stage is adjusting, or failed).</summary>
        private static async Task<PolarAlignStateDto> StageSettledAsync(PolarAlignService svc, string stage) {
            await Recorders.GetValue(svc, _ => throw new InvalidOperationException("service not made by NewService"))
                .WhenAsync(events => {
                    var at = Array.FindLastIndex(events, e => e.Type == WsEventCatalog.PolarAlignStage
                        && e.Payload.GetProperty("stage").GetString() == stage);
                    return at >= 0 && events.Skip(at + 1).Any(e => e.Type == WsEventCatalog.PolarAlignProgress || e.Type == WsEventCatalog.PolarAlignError);
                }).WaitAsync(SignalTimeout).ConfigureAwait(false);
            return await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // ── lifecycle ────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Status_is_idle_before_start() {
            var fake = new FakeGuideCapture { Connected = false };
            using var svc = NewService(fake, new ScriptedSolver());

            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("idle"));
            Assert.That(status.FramesCaptured, Is.EqualTo(0));
            Assert.That(status.CurrentErrorArcmin, Is.Null);
        }

        [Test]
        public async Task Start_without_a_connected_guider_throws() {
            var fake = new FakeGuideCapture { Connected = false };
            using var svc = NewService(fake, new ScriptedSolver());

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None));
        }

        [Test]
        public async Task Start_without_a_connected_mount_throws_and_leaves_the_lease_untouched() {
            var fake = NewFake();
            using var svc = NewService(fake, new ScriptedSolver(), NewMount(connected: false));

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None));
            Assert.That(fake.LeaseCalls, Is.Empty, "a failed preflight must not acquire the lease");
        }

        // #2070 — a false zero after a power loss made the routine slew below the pole (RC51, 2026-11-19).
        [Test]
        public async Task Start_is_refused_while_the_mount_position_is_unverified_and_nothing_moves() {
            var fake = NewFake();
            var mount = NewMount();
            mount.As<ITelescopeService>().Setup(t => t.PositionUnverifiedReasonAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(MountPoseTrust.AtHomeOnConnect);
            using var svc = NewService(fake, new ScriptedSolver(), mount);

            // Refused before the pointing is read for the plan (#2048), whatever the user confirmed.
            var refusal = await Assert.ThrowsAsync<OpenAstroAra.Server.Contracts.Refusals.RefusalException>(
                () => svc.StartAsync(PolarAlignStages.Check, new PolarAlignStartConfirmation(PolarAlignDirections.East), null, CancellationToken.None));

            Assert.That(refusal!.Cause, Is.EqualTo(OpenAstroAra.Server.Contracts.Refusals.RefusalCatalog.MountPositionUnverified));
            Assert.That(fake.LeaseCalls, Is.Empty, "a refused start must not acquire the lease");
            mount.Verify(m => m.FindHome(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
            mount.Verify(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()), Times.Never);
            mount.Verify(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.IsAny<double>()), Times.Never);
        }

        // #2173 — on a mount whose driver says its Home is a real home-sensor search, the unverified row says Home clears
        // it by itself, with no eye check, even after a power loss.
        [Test]
        public async Task The_preflight_row_on_a_sensor_search_mount_says_Home_clears_it_by_itself() {
            var mount = NewMount();
            mount.As<ITelescopeService>().Setup(t => t.PositionUnverifiedReasonAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(MountPoseTrust.AtHomeOnConnect);
            mount.As<ITelescopeService>().Setup(t => t.HomeIsSensorSearch).Returns(true);
            mount.Object.GetInfo().CanFindHome = true;
            using var svc = NewService(NewFake(), new ScriptedSolver(), mount);

            var preflight = await svc.GetPreflightAsync(PolarAlignStages.Fine, CancellationToken.None);

            var row = preflight.Checks.Single(c => c.Id == PolarAlignPreflightChecks.PoseVerified);
            Assert.That(row.Status, Is.EqualTo(PolarAlignPreflightStatuses.Fail));
            Assert.That(row.Detail, Does.Contain("trusted again once the mount is seen to move"));
            Assert.That(row.Detail, Does.Not.Contain("confirm by eye"));
        }

        [Test]
        public async Task Start_acquires_the_lease_and_publishes_started() {
            var fake = NewFake(answerCaptures: false);
            var ws = new WsRecorder();
            using var svc = NewService(fake, new ScriptedSolver(), ws: ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(PaCalls(fake), Is.Not.Empty, "Start should take the lease");
            Assert.That(PaCalls(fake)[0], Is.True);
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("seeding"), "an active routine begins in the seeding state");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStarted), Is.EqualTo(1));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task Start_takes_the_camera_from_a_running_guide_camera_focus_loop_first() {
            // Both borrow frames through the guider: the focus loop must give the camera up before the
            // routine takes its lease (ADR 0016: the custody module stops the older holder).
            var fake = NewFake(answerCaptures: false);
            var stops = 0;
            await ((IGuideCapture)fake).TakeCameraAsync(CameraUse.GuideFocus, _ => { stops++; return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
            using var svc = NewService(fake, new ScriptedSolver());

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(stops, Is.EqualTo(1));
            Assert.That(fake.Custody.Holder, Is.EqualTo(CameraUse.PolarAlign));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(fake.Custody.Holder, Is.Null, "Stop gives the camera back");
        }

        [Test]
        public async Task Start_is_idempotent_and_acquires_the_lease_only_once() {
            var fake = NewFake(answerCaptures: false);
            using var svc = NewService(fake, new ScriptedSolver());

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(fake.LeaseCalls.Count, Is.EqualTo(1), "a second Start on an already-active routine is a no-op accept");
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task Stop_without_a_connected_guider_still_succeeds_and_reports_stopped() {
            var fake = new FakeGuideCapture { Connected = false };
            using var svc = NewService(fake, new ScriptedSolver());

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("stopped"));
        }

        [Test]
        public async Task Concurrent_starts_acquire_the_lease_and_publish_only_once() {
            // The endpoint calls straight into the singleton with no request serialization, so two
            // near-simultaneous Starts race. _opLock serializes them: exactly one acquires the lease +
            // publishes, the other then sees _active and is a no-op accept (guards the double-acquire).
            var fake = NewFake(answerCaptures: false);
            var ws = new WsRecorder();
            using var svc = NewService(fake, new ScriptedSolver(), ws: ws);

            await Task.WhenAll(
                svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None),
                svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None)).ConfigureAwait(false);

            var acquisitions = PaCalls(fake).Count(active => active);
            Assert.That(acquisitions, Is.EqualTo(1), "concurrent Starts must acquire the lease exactly once");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStarted), Is.EqualTo(1));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task Stop_waits_for_an_in_flight_start_and_leaves_the_lease_released() {
            // The Start/Stop race: a Stop issued while a Start is mid-lease-RPC must serialize behind it
            // (via _opLock), so the set_pa_session calls can't reorder on the wire and leave the daemon
            // holding the lease while the service reports "stopped". We block Start inside its active:true
            // RPC, fire Stop, and prove Stop waits — then the wire order is [true, false] and state=stopped.
            var startEnteredLease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseStartLease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fake = NewFake(answerCaptures: false);
            fake.LeaseGate = active => {
                if (!active) {
                    return Task.CompletedTask;
                }
                startEnteredLease.TrySetResult();
                return releaseStartLease.Task;
            };
            using var svc = NewService(fake, new ScriptedSolver());

            var startTaskPlan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            var startTask = svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(startTaskPlan.Direction, startTaskPlan.NewOrigin), null, CancellationToken.None);
            await startEnteredLease.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);

            // Start now holds _opLock inside the (blocked) lease call; a Stop must wait on the semaphore.
            var stopTask = svc.StopAsync(null, CancellationToken.None);
            await Task.Delay(200).ConfigureAwait(false); // ADR 0016 rule 5 exception: Stop waiting behind the blocked Start is an absence nothing signals; the gate keeps it blocked, so the bound only gives a wrongly unblocked Stop time to finish.
            Assert.That(stopTask.IsCompleted, Is.False, "Stop must wait for the in-flight Start (serialized by _opLock)");

            releaseStartLease.SetResult();
            // #2048 — a Stop that lands while the Start still holds the lock means the Start launches no run.
            await Assert.ThrowsAsync<InvalidOperationException>(() => startTask).ConfigureAwait(false);
            await stopTask.ConfigureAwait(false);

            Assert.That(PaCalls(fake), Is.EqualTo(AcquireThenRelease),
                "the lease calls must run in call order — acquire then release — not race");
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("stopped"), "final state matches the last op, with the lease released");
        }

        // ── the engine ───────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Happy_path_seeds_then_adjusts_with_populated_errors_and_progress_events() {
            var fake = NewFake();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC); // seed A, seed B; the live loop re-solves B (Fallback)
            var mount = NewMount(tracking: true);
            var ws = new WsRecorder();
            using var svc = NewService(fake, solver, mount, ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"));
            // Axis fitted at the true pole → alt error = −refraction at the pole altitude (~1.0′ at
            // lat 45°), az error ~0. The live loop re-solving the same pointing must not change it.
            Assert.That(status.AltitudeAdjustmentArcmin, Is.Not.Null.And.EqualTo(-1.0).Within(0.2));
            Assert.That(status.AzimuthAdjustmentArcmin, Is.Not.Null.And.EqualTo(0.0).Within(0.2));
            Assert.That(status.CurrentErrorArcmin, Is.Not.Null.And.EqualTo(1.0).Within(0.2));
            Assert.That(status.FramesCaptured, Is.GreaterThanOrEqualTo(3), "all three seed frames captured");

            mount.Verify(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()),
                Times.Never, "the seed turns with the RA motor, never a GoTo");
            mount.Verify(m => m.MoveAxis(TelescopeAxes.Primary, 0), Times.Exactly(2), "the seed performs exactly two RA turns");
            mount.Verify(m => m.MoveAxis(TelescopeAxes.Secondary, It.IsAny<double>()), Times.Never, "the Dec motor never runs off Home");
            mount.Verify(m => m.SetTrackingEnabled(false), Times.Once, "the adjust loop stops tracking");

            // Progress events stream with the live payload shape the client renders.
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, 2).WaitAsync(SignalTimeout).ConfigureAwait(false);
            var progress = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignProgress).Payload;
            Assert.That(progress.GetProperty("altitude_error_arcmin").GetDouble(), Is.EqualTo(-1.0).Within(0.2));
            Assert.That(progress.GetProperty("zone").GetString(), Is.EqualTo("green"));

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            // #2048: stop_tracking_when_done is on by default, as NINA's, and a Stop never turns tracking back on.
            mount.Verify(m => m.SetTrackingEnabled(false), Times.Exactly(2), "off for the adjust loop, and off again at the exit");
            mount.Verify(m => m.SetTrackingEnabled(true), Times.Never, "Stop does not turn tracking back on");
            Assert.That(PaCalls(fake).Last(), Is.False, "Stop releases the lease");
        }

        [Test]
        public async Task Five_failed_solves_pause_the_loop_and_a_good_solve_resumes_it() {
            var fake = NewFake();
            var solver = new ScriptedSolver();
            var failures = Enumerable.Repeat(Unsolved, PolarAlignService.MaxConsecutiveSolveFailures).ToArray();
            solver.Enqueue(new[] { SolveA, SolveB, SolveC }.Concat(failures).ToArray()); // then Fallback succeeds
            var ws = new WsRecorder();
            using var svc = NewService(fake, solver, ws: ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            // The paused state lasts one PausedRetryDelay (20 ms here) before the scripted fallback solve
            // resumes the loop, so a 50 ms status poll misses it about half the time and timed out at 20 s.
            // The WS event is the durable witness of the pause; the state poll only has to see the resume.
            var pausedEvent = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignPaused).ConfigureAwait(false);
            Assert.That(pausedEvent.GetProperty("state").GetString(), Is.EqualTo("paused"), "5 consecutive failed solves park the loop in paused");
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.Not.EqualTo("failed"), "a failure streak pauses, it does not fail the routine");

            // The first progress event after the pause is the resume's.
            await ws.WhenAsync(events => {
                var paused = Array.FindIndex(events, e => e.Type == WsEventCatalog.PolarAlignPaused);
                return paused >= 0 && events.Skip(paused + 1).Any(e => e.Type == WsEventCatalog.PolarAlignProgress || e.Type == WsEventCatalog.PolarAlignError);
            }).WaitAsync(SignalTimeout).ConfigureAwait(false);
            var resumed = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(resumed.State, Is.EqualTo("adjusting"), "the next good solve resumes the loop");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignPaused), Is.EqualTo(1), "paused is published once per streak");

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task After_a_refused_guider_push_the_routine_fails_with_the_reason_before_its_first_capture() {
            var fake = NewFake();
            // A refused push leaves the Guider holding another profile's settings (GuiderService's own case, in
            // GuiderFakeIntegrationTest); here the routine meets that state.
            fake.OutOfStep = "the guide camera is not the active profile's";
            var ws = new WsRecorder();
            using var svc = NewService(fake, new ScriptedSolver { Fallback = Unsolved }, ws: ws);

            // The fine stage: this bench wires no main camera, and the coarse stage is refused without one.
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("guider_out_of_step"));
            Assert.That(fake.Captures, Is.Empty, "no capture before the refusal");
        }

        [Test]
        public async Task Seed_solve_failure_fails_the_routine_and_releases_the_lease() {
            var fake = NewFake();
            var solver = new ScriptedSolver { Fallback = Unsolved };
            var ws = new WsRecorder();
            using var svc = NewService(fake, solver, ws: ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("failed"));
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignError, 1).WaitAsync(SignalTimeout).ConfigureAwait(false);
            var error = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("seed_solve_failed"));
            await fake.WhenLeaseReleasedAsync().WaitAsync(SignalTimeout).ConfigureAwait(false);
            Assert.That(PaCalls(fake).Last(), Is.False, "a failed routine releases the lease itself");
        }

        [Test]
        public async Task A_seed_that_fails_to_solve_but_solves_with_no_scale_hint_reports_the_measured_focal_length() {
            // #1715: a guide focal length copied from the main scope made every seed frame unsolvable and the
            // error did not say why. One extra solve with no scale hint measures the real one.
            var fake = NewFake();
            var solver = new ScriptedSolver { Fallback = Unsolved, Measured = 128.4 };
            var ws = new WsRecorder();
            using var svc = NewService(fake, solver, ws: ws);

            await svc.StartConfirmedAsync(null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("seed_solve_failed"));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("128 mm").And.Contain("Settings"));
            Assert.That(Volatile.Read(ref solver.Measures), Is.EqualTo(1), "one measuring solve, not one per attempt");
        }

        [Test]
        public async Task A_seed_that_does_not_solve_with_no_scale_hint_either_keeps_the_plain_message() {
            var fake = NewFake();
            var solver = new ScriptedSolver { Fallback = Unsolved, Measured = null };
            var ws = new WsRecorder();
            using var svc = NewService(fake, solver, ws: ws);

            await svc.StartConfirmedAsync(null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("message").GetString(), Does.Not.Contain(" mm"));
            Assert.That(Volatile.Read(ref solver.Measures), Is.EqualTo(1));
        }

        [Test]
        public async Task A_restart_after_a_self_terminated_failure_waits_for_the_old_lease_release() {
            // FailRoutineAsync flips _active synchronously but releases the lease from the run task
            // afterwards. A fast re-Start must drain that run first, or its acquire could race the
            // old release and end up leaseless. We block the release call, fire Start, prove it waits,
            // then release and assert the order acquire→release→acquire.
            var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fake = NewFake();
            fake.LeaseGate = active => active ? Task.CompletedTask : releaseGate.Task;
            var solver = new ScriptedSolver { Fallback = Unsolved }; // seed fails → FailRoutineAsync
            using var svc = NewService(fake, solver);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "failed").ConfigureAwait(false);

            solver.Fallback = SolveB;
            solver.Enqueue(SolveA, SolveB, SolveC);
            var mark = Mark(svc);
            var restartPlan = await svc.GetStartPlanAsync(PolarAlignStages.Fine, CancellationToken.None).ConfigureAwait(false);
            var restart = svc.StartAsync(PolarAlignStages.Fine, new PolarAlignStartConfirmation(restartPlan.Direction, restartPlan.NewOrigin), null, CancellationToken.None);
            await Task.Delay(200).ConfigureAwait(false); // ADR 0016 rule 5 exception: the re-Start waiting behind the gated release is an absence nothing signals; the gate keeps it blocked, so the bound only gives a wrongly unblocked Start time to finish.
            Assert.That(restart.IsCompleted, Is.False,
                "the re-Start must wait for the failed run's in-flight lease release");

            releaseGate.SetResult();
            await restart.ConfigureAwait(false);
            await PollStateAsync(svc, mark, "adjusting", "failed").ConfigureAwait(false);
            Assert.That(PaCalls(fake), Is.EqualTo(AcquireReleaseAcquire),
                "order must be acquire → release → acquire, never interleaved");
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task A_transient_capture_rpc_fault_counts_as_one_failed_solve_not_an_abort() {
            // The first two captures are refused by the guider (a GuiderRpcException); the seed's 3-attempt
            // retry loop must absorb them and the routine must still reach adjusting — not hard-fail with
            // internal_error.
            var fake = NewFake();
            fake.Script = n => n <= 2 ? FakeCaptureOutcome.Refused : FakeCaptureOutcome.Saved;
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            using var svc = NewService(fake, solver);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("adjusting"),
                "transient capture refusals must be absorbed by the seed retry loop");
            Assert.That(fake.Captures.Count, Is.GreaterThan(2));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task Every_capture_rejected_fails_the_routine_with_capture_rejected_not_seed_solve_failed() {
            // §45 capture-fetch: when the guider refuses EVERY capture (the pre-guider#77 sandbox
            // rejection, a busy camera that never frees) no frame ever reaches the solver — the
            // routine must say capture_rejected with the guider's message, not blame focus/sky.
            var ws = new WsRecorder();
            var fake = NewFake();
            fake.Script = _ => FakeCaptureOutcome.Refused;
            fake.RefusalMessage = "path must be in an existing directory";
            using var svc = NewService(fake, new ScriptedSolver { Fallback = SolveA }, ws: ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "failed").ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("failed"));
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignError, 1).WaitAsync(SignalTimeout).ConfigureAwait(false);
            var error = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("capture_rejected"));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("existing directory"),
                "the guider's own rejection text must reach the user");
        }

        [Test]
        public async Task Frames_are_fetched_from_the_daemon_http_capture_endpoint() {
            // §45 capture-fetch: the saved frame is retrieved from the endpoint the connected guider reports —
            // host and RPC port from the link, the filename the capture completed with. What the PHD2 client
            // sends on the wire and how the HTTP port derives from the RPC port is in Phd2GuideCaptureLinkTest.
            var fake = NewFake();
            fake.Host = "192.0.2.77";
            fake.RpcPort = 4401;
            var fetcher = new FakeFrameFetcher();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            using var svc = NewService(fake, solver, fetcher: fetcher);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(fetcher.Fetched, Is.Not.Empty, "the engine must download the guider-saved frame");
            var fetched = fetcher.Fetched.First();
            Assert.That(fetched.Host, Is.EqualTo("192.0.2.77"));
            Assert.That(fetched.RpcPort, Is.EqualTo(4401),
                "the fetch is addressed to the connected guider's endpoint (HTTP port derives from it)");
            Assert.That(fetched.Filename, Is.EqualTo("frame_1.fits"), "the filename the capture completed with");
        }

        [Test]
        public async Task Auto_binning_follows_the_guide_camera_frame_size_and_reaches_capture_and_solver() {
            // §45.4 — profile binning 0 (auto) + a 12 MP guide camera → 3×3 on the capture RPC AND
            // on the solver (the guider saves the binned frame, so the solver's scale must carry it).
            var fake = NewFake(frameWidth: 4144, frameHeight: 2822);
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            using var svc = NewService(fake, solver);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(CaptureBinnings(fake), Is.Not.Empty.And.All.EqualTo(3),
                "a 12 MP sensor is the §45.4 3×3 tier");
            Assert.That(solver.Binnings, Is.Not.Empty.And.All.EqualTo(3),
                "the solver must be told the frame's binning or it searches at the wrong scale");
        }

        [TestCase(false, TestName = "Auto binning stays 1x1 for a 1.2 MP guide camera")]
        [TestCase(true, TestName = "Auto binning stays 1x1 when the daemon has no get_camera_frame_size")]
        public async Task Auto_binning_stays_at_one_for_a_guide_camera_or_an_unknown_frame_size(bool noFrameSize) {
            // §45.4 — a 1.2 MP ASI120 (the StartFake default) stays 1×1, and so does a daemon that
            // has no get_camera_frame_size (the RPC errors → fallback, never a failed routine).
            var fake = NewFake(noFrameSize: noFrameSize);
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            using var svc = NewService(fake, solver);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("adjusting"), "a missing size never fails the routine");
            Assert.That(CaptureBinnings(fake), Is.Not.Empty.And.All.Null, "1×1 is the daemon default, not sent");
            Assert.That(solver.Binnings, Is.Not.Empty.And.All.EqualTo(1));
        }

        [Test]
        public async Task A_zero_guide_frame_size_is_logged_as_unavailable_not_as_a_size() {
            // #1730: a guider with no camera frame yet answers [0, 0] — the log said "guide camera frame 0×0". The
            // link turns that answer into no size (Phd2GuideCaptureLinkTest); the routine logs it as unavailable.
            var fake = NewFake(noFrameSize: true);
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            var logger = new CapturingLogger();
            using var svc = NewService(fake, solver, logger: logger);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(logger.Entries.Select(e => e.EventId), Does.Contain(4525), "LogFrameSizeUnavailable");
            Assert.That(solver.Binnings, Is.Not.Empty.And.All.EqualTo(1));
        }

        [Test]
        public async Task A_profile_binning_overrides_auto() {
            var fake = NewFake(frameWidth: 4144, frameHeight: 2822);
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { Binning = 2 });
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            using var svc = NewService(fake, solver, store: store);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(CaptureBinnings(fake), Is.Not.Empty.And.All.EqualTo(2), "the user's 2×2 wins over the 3×3 tier");
            Assert.That(solver.Binnings, Is.Not.Empty.And.All.EqualTo(2));
        }

        [Test]
        public async Task A_stale_completion_from_a_timed_out_capture_is_swallowed_not_adopted() {
            // Review r1 on the capture-fetch PR: with path correlation gone, the event a TIMED-OUT
            // capture still owes must not resolve the NEXT capture's wait — adopting it would
            // silently solve the previous (seconds-old) frame. The first capture is accepted and never
            // completes (times out); the second first delivers the STALE completion (frame_stale.fits)
            // and then its own. The engine must fetch ONLY fresh frames.
            var fake = NewFake();
            var stale = new SingleFrameCompleteEventArgs(true, null, "/fake/guider/frame_stale.fits", "frame_stale.fits");
            fake.Script = n => {
                if (n == 2) {
                    fake.RaiseFrameComplete(stale); // capture 1's debt, delivered late — exactly once
                }
                return n == 1 ? FakeCaptureOutcome.NeverCompletes : FakeCaptureOutcome.Saved;
            };
            var fetcher = new FakeFrameFetcher();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            using var svc = NewService(fake, solver, fetcher: fetcher);
            svc.CaptureCompleteTimeout = TimeSpan.FromMilliseconds(300); // fast first-capture timeout

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("adjusting"),
                "the seed retry loop absorbs the timed-out first capture");
            Assert.That(fetcher.Fetched, Is.Not.Empty);
            Assert.That(fetcher.Fetched.Select(f => f.Filename), Does.Not.Contain("frame_stale.fits"),
                "the stale event owed by the timed-out capture must be swallowed, never fetched");
        }

        [TestCase("Looping")]
        [TestCase("Selected")]
        public async Task A_looping_guide_camera_is_stopped_and_the_routine_carries_on(string state) {
            // #1749: the rough stage on the main camera worked, then the fine stage failed on the loop. What the
            // link sends to stop it (stop_capture) is in Phd2GuideCaptureLinkTest; here the routine asks the link once
            // and goes on.
            var fake = NewFake();
            fake.AppState = state;
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            using var svc = NewService(fake, solver);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"));
            Assert.That(fake.LoopStopCalls, Is.EqualTo(1), "the loop was stopped once");
        }

        [Test]
        public async Task A_loop_that_never_stops_fails_the_start_and_names_the_guide_camera_loop() {
            var fake = NewFake(answerCaptures: false);
            fake.LoopStops = false;
            using var svc = NewService(fake, new ScriptedSolver());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain("Guide Camera loop"));
            // The custody module takes the lease before the loop stop (a new loop cannot start under it); a start
            // that then fails gives it straight back and leaves nobody holding the camera.
            Assert.That(PaCalls(fake).Last(), Is.False, "the lease is released after a start that failed");
            Assert.That(fake.Custody.Holder, Is.Null);
        }

        [Test]
        public async Task A_start_right_after_a_stop_waits_for_the_guiders_capture_to_finish() {
            // #1711: Stop abandons the capture the guider is still exposing; a Start within seconds sent a
            // second capture the guider refused ("capture is currently active") and the seed failed. Start waits
            // for the earlier capture to end (the link's wait is in Phd2GuideCaptureLinkTest); the routine asks
            // before its first capture of each run, and the abandoned capture's frame never reaches run 2.
            var fake = NewFake();
            fake.Script = n => n == 1 ? FakeCaptureOutcome.NeverCompletes : FakeCaptureOutcome.Saved;
            var fetcher = new FakeFrameFetcher();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            using var svc = NewService(fake, solver, fetcher: fetcher);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            // Run 1 acks its first capture and parks on the completion wait.
            await fake.WhenCapturedAsync(1).WaitAsync(SignalTimeout).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(fake.IdleWaits, Is.EqualTo(1), "run 1 asked once");

            var mark = Mark(svc);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, mark, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(fake.IdleWaits, Is.EqualTo(2), "run 2 asked before its first capture");
            Assert.That(status.State, Is.EqualTo("adjusting"), "the restart seeds normally once the guider is idle");
            Assert.That(fetcher.Fetched.Select(f => f.Filename), Does.Not.Contain("frame_1.fits"),
                "run 1's abandoned capture must never leak its frame into run 2");
        }

        [Test]
        public async Task A_start_fails_with_a_busy_message_when_the_guiders_capture_never_ends() {
            // The link gives up on an earlier capture that outlives its exposure and grace (Phd2GuideCaptureLinkTest)
            // and says so; the routine takes the lease, the wait throws that message, and the lease is given back.
            var fake = NewFake(answerCaptures: false);
            fake.CaptureBusy = "the guide camera is still busy with an earlier capture — wait a few seconds and try again";
            using var svc = NewService(fake, new ScriptedSolver());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartConfirmedAsync(null, CancellationToken.None));

            Assert.That(ex!.Message, Does.Contain("guide camera is still busy"));
            Assert.That(PaCalls(fake).Last(), Is.False, "the lease is given back");
            Assert.That(fake.Custody.Holder, Is.Null);
        }

        [Test]
        public async Task A_trailing_capture_fault_cannot_relabel_a_real_solve_problem_as_capture_rejected() {
            // Review r4: attempts 1–2 deliver frames that FAIL TO SOLVE (a real focus/sky problem);
            // only attempt 3 hits a transient capture rejection. Classifying by the last attempt's
            // fault alone would report capture_rejected ("could not be captured") — false, and it
            // points the operator at the daemon instead of the sky. Any attempt that reached the
            // solver pins the exhausted seed on seed_solve_failed.
            var ws = new WsRecorder();
            var fake = NewFake();
            fake.Script = n => n >= 3 ? FakeCaptureOutcome.Refused : FakeCaptureOutcome.Saved; // transient, LAST attempt only
            using var svc = NewService(fake, new ScriptedSolver { Fallback = Unsolved }, ws: ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "failed").ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("failed"));
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignError, 1).WaitAsync(SignalTimeout).ConfigureAwait(false);
            var error = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("seed_solve_failed"),
                "frames DID reach the solver — the trailing capture fault must not steal the blame");
        }

        [Test]
        public async Task A_never_delivered_completion_costs_one_capture_not_the_whole_seed_budget() {
            // Review r3: a timed-out capture whose event NEVER arrives (daemon-side hang — the
            // common cause) leaves a debt that the next capture's OWN completion pays by mistake.
            // That mis-swallow must be terminal: the wronged capture's timeout must NOT enqueue a
            // fresh debt, or one lost event cascades (swallow → timeout → new debt → swallow …)
            // through the entire seed budget. Capture 1 is accepted and never completes; every later
            // capture delivers only its own fresh event. Capture 2's event is swallowed against
            // capture 1's debt and capture 2 times out — capture 3 must then succeed.
            var fake = NewFake();
            fake.Script = n => n == 1 ? FakeCaptureOutcome.NeverCompletes : FakeCaptureOutcome.Saved;
            var fetcher = new FakeFrameFetcher();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            using var svc = NewService(fake, solver, fetcher: fetcher);
            svc.CaptureCompleteTimeout = TimeSpan.FromMilliseconds(300);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("adjusting"),
                "the mis-swallowed capture must not re-arm the debt — capture 3 has to get through");
            Assert.That(fetcher.Fetched.Select(f => f.Filename), Is.Not.Empty.And.None.EqualTo("frame_1.fits").And.None.EqualTo("frame_2.fits"),
                "capture 1 never completed and capture 2's event paid capture 1's debt: only capture 3 on is fetched");
            // No capture-count assertion: the adjust loop keeps capturing after the seed, so the
            // count isn't pinnable — the "adjusting" state IS the proof (the cascade exhausts all
            // three seed attempts and lands in "failed").
        }

        [Test]
        public async Task A_failed_frame_fetch_fails_the_seed_as_capture_rejected() {
            // The guider saved the frame but the HTTP download fails (pre-#77 daemon, network) —
            // still a capture-layer fault: capture_rejected, with the fetch error surfaced.
            var ws = new WsRecorder();
            var fake = NewFake();
            using var svc = NewService(fake, new ScriptedSolver { Fallback = SolveA },
                ws: ws, fetcher: new FakeFrameFetcher { Fail = true });

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "failed").ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("failed"));
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignError, 1).WaitAsync(SignalTimeout).ConfigureAwait(false);
            var error = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("capture_rejected"));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("fetching the saved frame"));
        }

        [Test]
        public async Task A_failed_lease_renewal_does_not_abort_the_adjust_loop() {
            // Renewal is best-effort per iteration: the first lease call (the acquire) succeeds, every later
            // one (the renewals) is refused with a GuiderRpcException. The loop must keep adjusting, not land
            // in failed.
            var fake = NewFake();
            fake.RenewalsFail = true;
            fake.Custody.LeaseRenewInterval = TimeSpan.FromTicks(-1); // every pass renews
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            using var svc = NewService(fake, solver);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            // Several renewals are refused while the loop keeps solving: the fake signals each one before it throws.
            await fake.WhenLeaseCallsAsync(fake.LeaseCalls.Count + 3).WaitAsync(SignalTimeout).ConfigureAwait(false);
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("adjusting"), "failed renewals must not abort the session");
            Assert.That(fake.LeaseCalls.Count, Is.GreaterThan(1), "renewals were actually attempted");
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task A_zombie_run_outliving_the_stop_grace_cannot_clobber_the_stopped_state() {
            // Stop bumps the run generation BEFORE cancelling: a run that ignores cancellation past
            // the unwind grace and later fails must find its generation stale — no failed-state
            // clobber, no spurious polar_align.error, after Stop already reported "stopped".
            var fake = NewFake();
            var solver = new BlockingSolver();
            var ws = new WsRecorder();
            var logger = new CapturingLogger();
            using var svc = NewService(fake, solver, ws: ws, logger: logger);
            svc.RunUnwindGrace = TimeSpan.FromMilliseconds(100);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            // Wait until the run is parked inside the cancellation-ignoring solve.
            await solver.Entered.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var stopped = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(stopped.State, Is.EqualTo("stopped"), "Stop abandoned the wedged run and reported stopped");

            // Release the zombie: it throws, but its generation is stale — state must stay stopped. The zombie's
            // catch logs "superseded" (event 4514); that log is the signal it has finished.
            solver.Gate.SetResult();
            await logger.Superseded.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
            var after = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(after.State, Is.EqualTo("stopped"), "the zombie's late failure must not clobber stopped");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignError), Is.Zero, "no spurious error event from the zombie");
        }

        /// <summary>A solver that IGNORES the cancellation token and blocks on a gate, then throws —
        /// simulates an RPC that outlives Stop's unwind grace and fails late.</summary>
        private sealed class BlockingSolver : IPolarAlignFrameSolver {
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public async Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) {
                Entered.TrySetResult();
                await Gate.Task.ConfigureAwait(false);
                // The seed's retry loop absorbs an ordinary exception as one failed solve; a solver-configuration fault
                // is the failure that leaves the run and meets the generation check.
                throw new OpenAstroAra.PlateSolving.PlateSolverConfigurationException("late zombie failure");
            }
        }

        [Test]
        public async Task Stop_after_a_self_terminated_failure_keeps_the_failed_state() {
            // A stray/cleanup Stop after the routine already failed must not clobber the terminal
            // "failed" back to "stopped" (a polling client would lose the real outcome), and must
            // not publish a spurious polar_align.stopped event.
            var fake = NewFake();
            var solver = new ScriptedSolver { Fallback = Unsolved };
            var ws = new WsRecorder();
            using var svc = NewService(fake, solver, ws: ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("failed"), "a late Stop must not clobber the terminal failed state");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStopped), Is.Zero,
                "nothing was stopped — no polar_align.stopped event");

            // And a fresh Start still works after the failure (the stale CTS is disposed, state resets).
            solver.Fallback = SolveB;
            solver.Enqueue(SolveA, SolveB, SolveC);
            var mark = Mark(svc);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var resumed = await PollStateAsync(svc, mark, "adjusting", "failed").ConfigureAwait(false);
            Assert.That(resumed.State, Is.EqualTo("adjusting"));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task Inconsistent_seed_solves_fail_the_axis_fit_with_an_actionable_reason() {
            // Solved pointings further apart than the commanded rotation allows → the geometry's
            // inconsistent-chord rejection must surface as a failed routine, not an internal error.
            var fake = NewFake();
            var solver = new ScriptedSolver();
            solver.Enqueue(new PolarAlignSolveOutcome(true, 0.0, 20.0), new PolarAlignSolveOutcome(true, 10.0, -40.0));
            var ws = new WsRecorder();
            using var svc = NewService(fake, solver, ws: ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("failed"));
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignError, 1).WaitAsync(SignalTimeout).ConfigureAwait(false);
            var error = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("axis_fit_failed"));
        }

        [Test]
        public async Task A_seed_turn_the_mount_accepts_but_never_turns_fails_as_slew_stalled() {
            // The mount takes the motor command, but its reported RA does not move: there is no turn
            // to fit, so the routine must stop (and stop the motor) rather than run on. #2067 — a stall is its own cause.
            var mount = NewMount();
            mount.Setup(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.IsAny<double>()));

            Assert.That(await SeedFailureReasonAsync(mount).ConfigureAwait(false), Is.EqualTo(PolarAlignService.SlewStalled));
        }

        // #1222 — the mediator refuses a slew it cannot frame (#1124) by throwing; that is a slew
        // failure for the user, not internal_error.
        [Test]
        public async Task A_seed_slew_the_mediator_refuses_by_throwing_fails_as_slew_failed() {
            var mount = NewMount();
            mount.Setup(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.Is<double>(r => r != 0)))
                .Throws(new SequenceEntityFailedException("the mount refused the move"));

            Assert.That(await SeedFailureReasonAsync(mount).ConfigureAwait(false), Is.EqualTo("slew_failed"));
        }

        [Test]
        public async Task A_mount_with_no_pointing_after_the_seed_slew_fails_as_pointing_unknown() {
            var mount = NewMount();
            var position = new Coordinates(0.0, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees);
            var slewed = false;
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => slewed ? null! : position);
            mount.Setup(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.IsAny<double>()))
                .Callback(() => slewed = true);

            Assert.That(await SeedFailureReasonAsync(mount).ConfigureAwait(false), Is.EqualTo(PolarAlignService.PointingUnknown));
        }

        // #1591 — seed close to a mount axis that sits 10° off the pole: two pointings admit two mirror-image
        // axis lines, and the pole-nearest pick took the mirror for some camera azimuths. Three pointings fix
        // one circle, so every azimuth and both turn directions keep the true axis.
        [TestCase(0.0, 20.0, 30.0)]
        [TestCase(90.0, 20.0, 30.0)]
        [TestCase(180.0, 20.0, 30.0)]
        [TestCase(270.0, 20.0, 30.0)]
        [TestCase(0.0, 2.0, -30.0)]
        [TestCase(90.0, 2.0, -30.0)]
        [TestCase(180.0, 2.0, -30.0)]
        [TestCase(270.0, 2.0, -30.0)]
        public async Task A_seed_close_to_the_pole_fits_the_true_axis_not_its_mirror(double phiDeg, double lstHours, double turnDeg) {
            static (double X, double Y, double Z) Unit(double raDeg, double decDeg) {
                var ra = raDeg * Math.PI / 180.0;
                var dec = decDeg * Math.PI / 180.0;
                return (Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec));
            }
            static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
                (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
            static (double X, double Y, double Z) Turn((double X, double Y, double Z) v, (double X, double Y, double Z) k, double deg) {
                var t = deg * Math.PI / 180.0;
                var c = Cross(k, v);
                var d = k.X * v.X + k.Y * v.Y + k.Z * v.Z;
                return (v.X * Math.Cos(t) + c.X * Math.Sin(t) + k.X * d * (1 - Math.Cos(t)),
                        v.Y * Math.Cos(t) + c.Y * Math.Sin(t) + k.Y * d * (1 - Math.Cos(t)),
                        v.Z * Math.Cos(t) + c.Z * Math.Sin(t) + k.Z * d * (1 - Math.Cos(t)));
            }
            static PolarAlignSolveOutcome Solved((double X, double Y, double Z) v) =>
                new(true, (Math.Atan2(v.Y, v.X) * 180.0 / Math.PI + 360.0) % 360.0, Math.Asin(v.Z) * 180.0 / Math.PI);

            const double AxisRaDeg = 40.0;
            const double AxisDecDeg = 80.0;
            var axis = Unit(AxisRaDeg, AxisDecDeg);
            var side = Turn(Unit(AxisRaDeg + 90.0, 0.0), axis, phiDeg);
            var camera = Turn(axis, side, 2.0); // 2° from the axis, as NINA's start near the pole
            var seedA = Solved(camera);
            var seedB = Solved(Turn(camera, axis, turnDeg));
            var seedC = Solved(Turn(camera, axis, 2.0 * turnDeg));

            var mount = NewMount();
            // NewMount's info, which follows the tracking writes (#2072).
            mount.Object.GetInfo().SiderealTime = lstHours;
            mount.Object.GetInfo().SideOfPier = CounterweightDownSide(lstHours, 0.0);
            // The mount's own reading only cross-checks the turns (the solves carry the geometry): 5° off the pole, as #2082
            // asks of a start (within 3° is refused).
            var position = new Coordinates(0.0, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees);
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => position);
            mount.Setup(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Callback<Coordinates, CancellationToken>((target, _) => position = target)
                .ReturnsAsync(true);
            SimulateMotors(mount, () => position, c => position = c);
            var fake = NewFake();
            var solver = new ScriptedSolver { Fallback = seedC };
            solver.Enqueue(seedA, seedB, seedC);
            using var svc = NewService(fake, solver, mount);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"));
            var (alt, az) = PolarAlignGeometry.AxisError(AxisRaDeg, AxisDecDeg, SiteLatDeg, SiteLonDeg, DateTimeOffset.UtcNow);
            Assert.That(status.CurrentErrorArcmin, Is.EqualTo(Math.Sqrt(alt * alt + az * az)).Within(3.0),
                "the error of the true axis, not of its mirror ~2° away");
        }

        // NINA's Three Point Polar Alignment: three solves 10° apart in RA from near the pole, the axis is the
        // circle through them. The same routine serves both hemispheres: the fit takes the end of the axis
        // nearer the site's pole, and the error is read against that pole's altitude.
        [TestCase(45.0, 89.5)]
        [TestCase(-33.0, -89.5)]
        public async Task The_three_point_seed_measures_the_true_axis_in_either_hemisphere(double latitudeDeg, double axisDecDeg) {
            static (double X, double Y, double Z) Unit(double raDeg, double decDeg) {
                var ra = raDeg * Math.PI / 180.0;
                var dec = decDeg * Math.PI / 180.0;
                return (Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec));
            }
            static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
                (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
            static (double X, double Y, double Z) Turn((double X, double Y, double Z) v, (double X, double Y, double Z) k, double deg) {
                var t = deg * Math.PI / 180.0;
                var c = Cross(k, v);
                var d = k.X * v.X + k.Y * v.Y + k.Z * v.Z;
                return (v.X * Math.Cos(t) + c.X * Math.Sin(t) + k.X * d * (1 - Math.Cos(t)),
                        v.Y * Math.Cos(t) + c.Y * Math.Sin(t) + k.Y * d * (1 - Math.Cos(t)),
                        v.Z * Math.Cos(t) + c.Z * Math.Sin(t) + k.Z * d * (1 - Math.Cos(t)));
            }
            static PolarAlignSolveOutcome Solved((double X, double Y, double Z) v) =>
                new(true, (Math.Atan2(v.Y, v.X) * 180.0 / Math.PI + 360.0) % 360.0, Math.Asin(v.Z) * 180.0 / Math.PI);

            const double AxisRaDeg = 40.0;
            var axis = Unit(AxisRaDeg, axisDecDeg);
            var camera = Turn(axis, Unit(AxisRaDeg + 90.0, 0.0), 5.0); // 5° off the axis, as after Home → Dec 85°
            var store = NewStore();
            store.PutSiteSettings(store.GetSiteSettings() with { LatitudeDeg = latitudeDeg });
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { SeedRotationDeg = 10.0 });
            var mount = NewMount();
            var position = new Coordinates(0.0, Math.CopySign(85.0, latitudeDeg), Epoch.JNOW, Coordinates.RAType.Degrees);
            var targets = new List<Coordinates>();
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => position);
            mount.Setup(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Callback<Coordinates, CancellationToken>((target, _) => { position = target; targets.Add(target); })
                .ReturnsAsync(true);
            var gotos = 0;
            mount.Setup(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Callback(() => gotos++).ReturnsAsync(true);
            SimulateMotors(mount, () => position, c => position = c, stopped: targets.Add);
            var seedC = Solved(Turn(camera, axis, 20.0));
            var solver = new ScriptedSolver { Fallback = seedC };
            solver.Enqueue(Solved(camera), Solved(Turn(camera, axis, 10.0)), seedC);
            using var svc = NewService(NewFake(), solver, mount, store: store);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"));
            var (alt, az) = PolarAlignGeometry.AxisError(AxisRaDeg, axisDecDeg, latitudeDeg, SiteLonDeg, DateTimeOffset.UtcNow);
            Assert.That(status.CurrentErrorArcmin, Is.EqualTo(Math.Sqrt(alt * alt + az * az)).Within(0.5));
            Assert.That(targets, Has.Count.EqualTo(2), "two RA turns, nothing else");
            Assert.That(gotos, Is.Zero, "the turns are motor moves, never a GoTo the firmware could route over the pier");
            static double Turned(double fromDeg, double toDeg) => Math.Abs(((toDeg - fromDeg + 540.0) % 360.0) - 180.0);
            Assert.That(Turned(0.0, targets[0].RADegrees), Is.EqualTo(10.0).Within(1e-6), "the first turn is the 10° step");
            Assert.That(Turned(targets[0].RADegrees, targets[1].RADegrees), Is.EqualTo(10.0).Within(1e-6), "so is the second");
            Assert.That(targets.Select(t => t.Dec), Is.All.EqualTo(Math.CopySign(85.0, latitudeDeg)).Within(1e-6), "the turns keep the Dec");
        }

        // #1536 — a synced iOptron at home reported Dec 90.9°. Past the pole the same sky point is (RA+180°, 180°−Dec), and
        // the routine used to fold the reading and turn from it. #2066 (ARA-5) — such a reading leaves the pier side and the
        // hour angle ambiguous (which side of the pole is the Dec axis really on?): the start is refused as
        // polar-align-pose-ambiguous before the camera is taken or anything moves, in either hemisphere.
        [TestCase(95.0)]
        [TestCase(-95.0)]
        public async Task Dec_over_90_reading_refuses_turns(double reportedDec) {
            var mount = NewMount();
            var position = new Coordinates(0.0, reportedDec, Epoch.JNOW, Coordinates.RAType.Degrees);
            mount.Object.GetInfo().SiderealTime = 2.0;
            mount.Object.GetInfo().SideOfPier = PierSide.pierWest;
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => position);
            var fake = NewFake();
            using var svc = NewService(fake, new ScriptedSolver(), mount);

            var plan = await Assert.ThrowsAsync<RefusalException>(() => svc.GetStartPlanAsync(null, CancellationToken.None));
            var start = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(
                null, new PolarAlignStartConfirmation(PolarAlignDirections.East, NewOrigin: true), null, CancellationToken.None));

            Assert.That(plan!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignPoseAmbiguous), "the confirm step already says so");
            Assert.That(start!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignPoseAmbiguous));
            mount.Verify(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.IsAny<double>()), Times.Never, "no turn");
            mount.Verify(m => m.SetTrackingEnabled(It.IsAny<bool>()), Times.Never, "nothing touched the mount");
            Assert.That(fake.LeaseCalls, Is.Empty, "refused before the camera is taken");
        }

        // #2081 — the saved RA motor direction says a positive rate raises RA; this driver lowers it (a driver upgrade that
        // changed its sign). The turn used to be stopped and reversed mid-move, trusting the reading's sign; now it is
        // stopped within 0.5° (plus one command's travel) and the run fails. The motor never runs the other way.
        [Test]
        public async Task A_motor_that_runs_the_other_way_from_its_saved_direction_fails_and_is_never_reversed() {
            var mount = NewMount();
            var position = new Coordinates(0.0, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees);
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => position);
            SimulateMotors(mount, () => position, c => position = c, raUpIsNegative: true);
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), solver, mount, ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("failed"));
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.TurnWrongWay));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("Test direction again"));
            // LST 20 h, RA 0, east (the profile's default in the north): the turn was asked for RA up and went down.
            var wrongWay = -Wrap180Test(position.RADegrees);
            Assert.That(wrongWay, Is.GreaterThanOrEqualTo(PolarAlignService.WrongWayDeg).And.LessThanOrEqualTo(PolarAlignService.WrongWayDeg + MotorStepDeg),
                "stopped within 0.5° and one command of travel the wrong way");
            var rates = mount.Invocations.Where(i => i.Method.Name == nameof(ITelescopeMediator.MoveAxis)
                    && i.Arguments[0] is TelescopeAxes.Primary)
                .Select(i => i.Arguments[1] is double r ? r : double.NaN).Where(r => r != 0).ToArray();
            Assert.That(rates, Is.Not.Empty);
            Assert.That(rates, Has.All.GreaterThan(0), "never reversed: every motor command is the confirmed way's");
        }

        // On the daemon the mount is the telescope service: each turn runs on its leased MoveAxis (the motor stops by
        // itself if the routine stops renewing) and stops on the driver's position read now. The cached reading here
        // never moves (the 2 s refresh never catches up), so a turn that waited on it would stall.
        [Test]
        public async Task On_the_telescope_service_the_turns_run_on_a_leased_motor_and_stop_on_fresh_reads() {
            var mount = NewMount();
            var cached = new Coordinates(0.0, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees);
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => cached);
            var freshRaHours = 0.0;
            var service = mount.As<ITelescopeService>();
            service.Setup(s => s.ReadNowAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => FreshReading(freshRaHours, 85.0));
            service.Setup(s => s.ReadPositionNowAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => FreshReading(freshRaHours, 85.0));
            var calls = new ConcurrentQueue<(int Axis, double Rate, int? LeaseMs)>();
            service.Setup(s => s.MoveAxisAsync(It.IsAny<int>(), It.IsAny<double>(), It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
                .Callback<int, double, int?, long?, CancellationToken>((axis, rate, lease, _, _) => {
                    calls.Enqueue((axis, rate, lease));
                    if (rate != 0) {
                        freshRaHours = (freshRaHours + (Math.Sign(rate) * MotorStepDeg / 15.0) + 24.0) % 24.0;
                    }
                })
                .Returns(Task.CompletedTask);
            using var svc = NewService(NewFake(), new ScriptedSolver(), mount);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"));
            Assert.That(freshRaHours * 15.0, Is.EqualTo(60.0).Within(1e-6), "two 30° turns, stopped on the fresh reading");
            var moves = calls.Where(c => c.Rate != 0).ToArray();
            Assert.That(moves, Is.Not.Empty);
            Assert.That(moves.Select(c => c.LeaseMs), Is.All.EqualTo(3000), "every motor command carries the lease");
            Assert.That(calls.Count(c => c.Rate == 0 && c.LeaseMs is null), Is.GreaterThanOrEqualTo(2), "each turn ends with a stop");
            mount.Verify(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.IsAny<double>()), Times.Never, "never the mediator's unleased call");
        }

        // The turn mismatch is the clutch-slip diagnostic: on a perfect mount it is zero. Between solves a tracking
        // tube also turns with the Earth, which the mount's sky-RA reading does not show; 30 s apart that is 0.125°.
        [TestCase(10.0)]
        [TestCase(-10.0)]
        public void The_turn_mismatch_counts_the_Earths_turn_between_solves(double turnDeg) {
            var t0 = new DateTimeOffset(2026, 11, 19, 6, 0, 0, TimeSpan.Zero);
            // The axis on the pole: the solves read what the mount reads, each 30 s after the last.
            var a = (new PolarAlignSolveOutcome(true, 100.0, 85.0), t0);
            var b = (new PolarAlignSolveOutcome(true, 100.0 + turnDeg, 85.0), t0.AddSeconds(30));
            var c = (new PolarAlignSolveOutcome(true, 100.0 + (2 * turnDeg), 85.0), t0.AddSeconds(60));

            var (axis, mismatch) = PolarAlignService.FitThreePointOrFail(a, b, c, turnDeg, turnDeg, north: true);

            Assert.That(axis.DecDeg, Is.EqualTo(90.0).Within(1e-6));
            Assert.That(mismatch, Is.EqualTo(0.0).Within(1e-6));
        }

        // Stop Mount in the app aborts the mount from outside the routine and cancels the motor's lease. The routine's
        // next renewal must not start the motor again: the run ends, and nothing after the abort moves the mount.
        [Test]
        public async Task Stop_Mount_during_a_turn_ends_the_run_and_the_motor_is_not_restarted() {
            var mount = NewMount();
            var cached = new Coordinates(0.0, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees);
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => cached);
            var freshRaHours = 0.0;
            var abortEpoch = 7L;
            var afterAbort = new ConcurrentQueue<(double Rate, long? Since)>();
            var service = mount.As<ITelescopeService>();
            service.SetupGet(s => s.AbortEpoch).Returns(() => Interlocked.Read(ref abortEpoch));
            service.Setup(s => s.ReadNowAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => FreshReading(freshRaHours, 85.0));
            service.Setup(s => s.ReadPositionNowAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => FreshReading(freshRaHours, 85.0));
            var moves = 0;
            service.Setup(s => s.MoveAxisAsync(It.IsAny<int>(), It.IsAny<double>(), It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
                .Callback<int, double, int?, long?, CancellationToken>((_, rate, _, since, _) => {
                    if (Interlocked.Read(ref abortEpoch) != 7L) {
                        afterAbort.Enqueue((rate, since));
                        return;
                    }
                    if (rate != 0) {
                        freshRaHours += MotorStepDeg / 15.0;
                        if (++moves == 5) {
                            Interlocked.Increment(ref abortEpoch); // the user taps Stop Mount mid-turn
                        }
                    }
                })
                .Returns(Task.CompletedTask);
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), new ScriptedSolver(), mount, ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("failed"));
            var error = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.SlewAbortedExternally));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("Stop Mount"));
            Assert.That(afterAbort.Where(c => c.Rate != 0), Is.Empty, "no motor command after the abort");
            Assert.That(afterAbort.Count(c => c.Rate == 0), Is.GreaterThanOrEqualTo(1), "the routine's own stop still goes out");
            Assert.That(freshRaHours * 15.0, Is.LessThan(2.0), "the turn ended where the abort caught it");
        }

        // The routine hands the telescope service the abort count it started under, so the service itself can refuse a
        // renewal that races the abort.
        [Test]
        public async Task Each_motor_command_of_a_turn_carries_the_abort_count_it_started_under() {
            var mount = NewMount();
            var cached = new Coordinates(0.0, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees);
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => cached);
            var freshRaHours = 0.0;
            var service = mount.As<ITelescopeService>();
            service.SetupGet(s => s.AbortEpoch).Returns(42L);
            service.Setup(s => s.ReadNowAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => FreshReading(freshRaHours, 85.0));
            service.Setup(s => s.ReadPositionNowAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => FreshReading(freshRaHours, 85.0));
            var guards = new ConcurrentQueue<(double Rate, long? Since)>();
            service.Setup(s => s.MoveAxisAsync(It.IsAny<int>(), It.IsAny<double>(), It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
                .Callback<int, double, int?, long?, CancellationToken>((_, rate, _, since, _) => {
                    guards.Enqueue((rate, since));
                    if (rate != 0) {
                        freshRaHours = (freshRaHours + (Math.Sign(rate) * MotorStepDeg / 15.0) + 24.0) % 24.0;
                    }
                })
                .Returns(Task.CompletedTask);
            using var svc = NewService(NewFake(), new ScriptedSolver(), mount);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"));
            Assert.That(guards.Where(g => g.Rate != 0).Select(g => g.Since), Is.All.EqualTo(42L));
            Assert.That(guards.Where(g => g.Rate == 0).Select(g => g.Since), Is.All.Null, "a stop is never refused");
        }

        // Each turn runs at 3°/s (NINA's speed) and slows to 1°/s for its last 3°, so the stop on the mount's reading
        // lands close to the target; it never speeds up again.
        [Test]
        public async Task A_turn_runs_at_3_degrees_a_second_and_slows_to_1_for_its_last_3_degrees() {
            var mount = NewMount();
            var cached = new Coordinates(0.0, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees);
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => cached);
            var freshRaDeg = 0.0;
            var service = mount.As<ITelescopeService>();
            service.Setup(s => s.ReadNowAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => FreshReading(freshRaDeg / 15.0, 85.0));
            service.Setup(s => s.ReadPositionNowAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => FreshReading(freshRaDeg / 15.0, 85.0));
            var commands = new ConcurrentQueue<(double Rate, double AtDeg)>();
            service.Setup(s => s.MoveAxisAsync(It.IsAny<int>(), It.IsAny<double>(), It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
                .Callback<int, double, int?, long?, CancellationToken>((_, rate, _, _, _) => {
                    commands.Enqueue((rate, freshRaDeg));
                    if (rate != 0) {
                        freshRaDeg += Math.Sign(rate) * MotorStepDeg;
                    }
                })
                .Returns(Task.CompletedTask);
            using var svc = NewService(NewFake(), new ScriptedSolver(), mount);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"));
            // The first 30° turn (the bench store's step): every command until its stop.
            var turn = commands.TakeWhile(c => c.Rate != 0).ToArray();
            Assert.That(turn.Select(c => Math.Abs(c.Rate)), Is.All.EqualTo(3.0).Or.EqualTo(1.0));
            Assert.That(turn.First().Rate, Is.EqualTo(3.0), "it starts at 3°/s");
            Assert.That(turn.Last().Rate, Is.EqualTo(1.0), "it ends at 1°/s");
            var firstSlow = Array.FindIndex(turn, c => Math.Abs(c.Rate) == 1.0);
            Assert.That(turn.Skip(firstSlow).Select(c => Math.Abs(c.Rate)), Is.All.EqualTo(1.0), "never fast again");
            Assert.That(turn[firstSlow].AtDeg, Is.GreaterThanOrEqualTo(30.0 - PolarAlignService.AxisSlowZoneDeg), "slow only for the last 3°");
            Assert.That(turn[firstSlow - 1].AtDeg, Is.LessThan(30.0 - PolarAlignService.AxisSlowZoneDeg), "fast until then");
        }

        // A mount that creeps (each poll a little progress, so never a stall) but far slower than asked must not
        // run on: #2067 — the move has a time bound of twice its travel at the rates it runs at (NINA's), slew_timeout.
        [Test]
        public async Task A_turn_that_creeps_past_its_time_bound_fails_and_stops_the_motor() {
            var mount = NewMount();
            var position = new Coordinates(0.0, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees);
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => position);
            var stops = 0;
            mount.Setup(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.IsAny<double>()))
                .Callback<TelescopeAxes, double>((_, rate) => {
                    if (rate == 0) {
                        Interlocked.Increment(ref stops);
                        return;
                    }
                    position = new Coordinates((position.RADegrees + (Math.Sign(rate) * 0.06) + 360.0) % 360.0, position.Dec, Epoch.JNOW, Coordinates.RAType.Degrees);
                });
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), new ScriptedSolver(), mount, ws);
            svc.AxisMovePoll = TimeSpan.FromMilliseconds(10);
            svc.AxisFastRateDegPerSec = 100.0;
            svc.AxisSlowRateDegPerSec = 100.0; // bound = 30° / 100°/s × 2 = 0.6 s; the creep needs 5 s
            svc.AxisMoveBoundSlack = TimeSpan.Zero;
            svc.AxisMoveStall = TimeSpan.FromMinutes(5);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("failed"));
            var error = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.SlewTimeout));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("turned only").And.Contain("twice the time"));
            Assert.That(stops, Is.GreaterThanOrEqualTo(1), "the motor is stopped");
        }

        // Stop must stop the motor at once, not leave it running to the end of the turn.
        [Test]
        public async Task Stop_during_a_turn_stops_the_RA_motor() {
            var mount = NewMount();
            var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopped = false;
            mount.Setup(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.IsAny<double>()))
                .Callback<TelescopeAxes, double>((_, rate) => {
                    if (rate != 0) {
                        running.TrySetResult(); // the mount takes the command but never moves: the turn runs until Stop
                    } else {
                        stopped = true;
                    }
                });
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            using var svc = NewService(NewFake(), solver, mount);
            svc.AxisMoveStall = TimeSpan.FromMinutes(5);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await running.Task.WaitAsync(SignalTimeout).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(stopped, Is.True, "Stop sent the motor stop");
            mount.Verify(m => m.MoveAxis(TelescopeAxes.Primary, 0), Times.AtLeastOnce());
        }

        // #1536 — the user-facing reason names the recovery; the driver's own text stays in the log.
        [Test]
        public async Task A_refused_seed_slew_fails_with_a_plain_recovery_message() {
            var mount = NewMount();
            mount.Setup(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.Is<double>(r => r != 0)))
                .Throws(new InvalidOperationException("MoveAxis failed: Invalid rate (driver text)"));

            var message = (await SeedFailureAsync(mount).ConfigureAwait(false)).GetProperty("message").GetString();

            Assert.That(message, Does.Contain("free to move").And.Contain("start again"));
            Assert.That(message, Does.Not.Contain("driver text"));
        }

        /// <summary>Runs the routine against <paramref name="mount"/> with solvable seeds and
        /// returns the reason on its error event once it has failed.</summary>
        /// <summary>A mount that reports AtPark until <see cref="ITelescopeMediator.UnparkTelescope"/> succeeds
        /// (or forever when <paramref name="unparks"/> is false), recording the order of park and slew calls.</summary>
        private static (Mock<ITelescopeMediator> Mount, List<string> Calls) ParkedMount(bool unparks) {
            var mount = NewMount();
            var parked = true;
            var calls = new List<string>();
            mount.Setup(m => m.GetInfo()).Returns(() => new TelescopeInfo {
                // RA 0 at LST 0 h: on the meridian, the east side of the pier counterweight down.
                Connected = true, SiderealTime = 0.0, TrackingEnabled = !parked, AtPark = parked, SideOfPier = PierSide.pierEast,
            });
            mount.Setup(m => m.UnparkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Returns(() => {
                    lock (calls) { calls.Add("unpark"); }
                    parked = !unparks;
                    return Task.FromResult(unparks);
                });
            var position = new Coordinates(0.0, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees);
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => position);
            SimulateMotors(mount, () => position, c => position = c, stopped: _ => { lock (calls) { calls.Add("slew"); } });
            mount.Setup(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Returns<Coordinates, CancellationToken>((target, _) => {
                    lock (calls) { calls.Add("slew"); }
                    if (parked) {
                        return Task.FromResult(false); // the mediator refuses a slew while parked
                    }
                    position = target;
                    return Task.FromResult(true);
                });
            return (mount, calls);
        }

        private static async Task<string?> SeedFailureReasonAsync(Mock<ITelescopeMediator> mount) =>
            (await SeedFailureAsync(mount).ConfigureAwait(false)).GetProperty("reason").GetString();

        private static async Task<System.Text.Json.JsonElement> SeedFailureAsync(Mock<ITelescopeMediator> mount) {
            var fake = NewFake();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            var ws = new WsRecorder();
            using var svc = NewService(fake, solver, mount, ws: ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "failed", "adjusting").ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("failed"));
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignError, 1).WaitAsync(SignalTimeout).ConfigureAwait(false);
            return ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
        }

        /// <summary>The mount calls in order; <see cref="WhenCountAsync"/> completes once that many are recorded.</summary>
        private sealed class CallLog : List<string> {
            private readonly List<(int Count, TaskCompletionSource Done)> _waiters = new();

            public void Record(string call) {
                List<TaskCompletionSource> due;
                lock (_waiters) {
                    Add(call);
                    due = _waiters.Where(w => w.Count <= Count).Select(w => w.Done).ToList();
                    _waiters.RemoveAll(w => w.Count <= Count);
                }
                foreach (var done in due) {
                    done.TrySetResult();
                }
            }

            public Task WhenCountAsync(int count) {
                lock (_waiters) {
                    if (Count >= count) {
                        return Task.CompletedTask;
                    }
                    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters.Add((count, done));
                    return done.Task;
                }
            }
        }

        /// <summary>A mount whose tracking is real state: the bridge refuses a slew while it is off, so the
        /// call log records what tracking was at each slew. <paramref name="dropAtPointingRead"/> drops it
        /// on that pointing read, as a §42.3 mount reconnect did on the CM4 on 2026-11-05.</summary>
        private static (Mock<ITelescopeMediator> Mount, CallLog Calls) TrackingMount(
                bool tracking, Func<bool, bool>? onSet = null, int dropAtPointingRead = 0,
                Func<bool>? connected = null, Func<bool>? slewing = null) {
            var mount = NewMount(tracking: tracking);
            var calls = new CallLog();
            var state = tracking;
            var reads = 0;
            var position = new Coordinates(0.0, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees);
            mount.Setup(m => m.GetInfo()).Returns(() => new TelescopeInfo {
                // As TelescopeService does: a disconnected mount reads tracking (and slewing) off.
                Connected = connected?.Invoke() ?? true, SiderealTime = 20.0,
                TrackingEnabled = (connected?.Invoke() ?? true) && state,
                Slewing = (connected?.Invoke() ?? true) && (slewing?.Invoke() ?? false),
                SideOfPier = PierSide.pierWest, // NewMount's pose
            });
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => {
                if (++reads == dropAtPointingRead) {
                    state = false;
                }
                return position;
            });
            mount.Setup(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Callback<Coordinates, CancellationToken>((target, _) => {
                    calls.Record($"slew tracking={state}");
                    position = target;
                })
                .ReturnsAsync(true);
            SimulateMotors(mount, () => position, c => position = c, stopped: _ => calls.Record($"slew tracking={state}"));
            var writes = 0L;
            mount.SetupGet(m => m.TrackingWriteCount).Returns(() => Interlocked.Read(ref writes));
            mount.Setup(m => m.SetTrackingEnabled(It.IsAny<bool>())).Returns((bool on) => {
                Interlocked.Increment(ref writes);
                calls.Record($"tracking {on}");
                if (onSet is not null) {
                    return onSet(on);
                }
                state = on;
                return true;
            });
            // As TelescopeService does: a mode write counts and turns tracking on (Stopped turns it off).
            mount.Setup(m => m.SetTrackingMode(It.IsAny<TrackingMode>())).Returns((TrackingMode mode) => {
                Interlocked.Increment(ref writes);
                state = mode != TrackingMode.Stopped;
                calls.Record($"tracking {state}");
                return true;
            });
            return (mount, calls);
        }

        private static async Task RunToAdjustingThenStopAsync(Mock<ITelescopeMediator> mount) {
            var fake = NewFake();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            using var svc = NewService(fake, solver, mount);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("adjusting"));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task A_mount_that_is_not_tracking_tracks_through_the_seed_and_goes_back_to_off() {
            // The bridge refuses SlewToCoordinatesAsync without tracking ("requires Tracking to be true").
            var (mount, calls) = TrackingMount(tracking: false);

            await RunToAdjustingThenStopAsync(mount).ConfigureAwait(false);

            Assert.That(string.Join(", ", calls), Is.EqualTo("tracking True, slew tracking=True, slew tracking=True, tracking False, tracking False"),
                "on before the seed slew, off for the adjust loop, the user's off put back");
        }

        /// <summary>The bench store with <c>stop_tracking_when_done</c> off: a finished run hands tracking back.</summary>
        private static InMemoryProfileStore HandBackStore() {
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { StopTrackingWhenDone = false });
            return store;
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task Tracking_turned_on_by_another_caller_during_the_adjust_loop_is_left_as_set_at_Done_with_hand_back(bool sameValueAsRoutine) {
            // A sequence's Set Tracking writes while the run is live; with stop_tracking_when_done off, Done hands
            // tracking back, and that hand-back must not undo the other caller's write.
            // sameValueAsRoutine: the other caller writes the value the routine itself last wrote (off).
            var (mount, calls) = TrackingMount(tracking: false);
            var fake = NewFake();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            using var svc = NewService(fake, solver, mount, store: HandBackStore());
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);

            mount.Object.SetTrackingEnabled(!sameValueAsRoutine);
            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(string.Join(", ", calls), Is.EqualTo(
                $"tracking True, slew tracking=True, slew tracking=True, tracking False, tracking {!sameValueAsRoutine}"),
                "the routine's own restore is not written after the other caller's write");
        }

        [Test]
        public async Task Tracking_set_by_a_sequence_Set_Tracking_instruction_during_the_adjust_loop_is_left_on_at_Done_with_hand_back() {
            // Drives the real instruction, so the test follows the mediator member the sequence uses.
            var (mount, calls) = TrackingMount(tracking: false);
            var fake = NewFake();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            using var svc = NewService(fake, solver, mount, store: HandBackStore());
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);

            await new SetTracking(mount.Object) { TrackingMode = TrackingMode.Sidereal }
                .Execute(null!, CancellationToken.None).ConfigureAwait(false);
            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(string.Join(", ", calls), Is.EqualTo("tracking True, slew tracking=True, slew tracking=True, tracking False, tracking True"),
                "the sequence's Sidereal stays on: no restore to off after it");
        }

        private static async Task<(PolarAlignService Svc, RecordingLog Log)> AdjustingServiceAsync() {
            var fake = NewFake();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            var log = new RecordingLog();
            var svc = NewService(fake, solver, log: log);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            return (svc, log);
        }

        [Test]
        public async Task A_run_ended_by_a_sequence_start_reads_stopped_not_completed() {
            var (svc, _) = await AdjustingServiceAsync().ConfigureAwait(false);
            using var _s = svc;

            await svc.StopForSequenceAsync(CancellationToken.None).ConfigureAwait(false);

            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("stopped"));
            Assert.That(svc.IsActive, Is.False);
        }

        [Test]
        public async Task A_run_ended_by_a_sequence_start_stores_no_measured_result() {
            var (svc, log) = await AdjustingServiceAsync().ConfigureAwait(false);
            using var _s = svc;

            await svc.StopForSequenceAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).LastResult, Is.Null, "no last result");
            Assert.That(log.Rows, Is.Empty, "no session row: the readout was taken through a sequence's slews");
        }

        [Test]
        public async Task A_mount_already_tracking_is_not_touched_before_the_adjust_loop() {
            var (mount, calls) = TrackingMount(tracking: true);

            await RunToAdjustingThenStopAsync(mount).ConfigureAwait(false);

            // Untouched until the adjust loop turns it off; Stop then leaves it off (#2048), it does not turn it back on.
            Assert.That(string.Join(", ", calls), Is.EqualTo("slew tracking=True, slew tracking=True, tracking False, tracking False"));
        }

        [Test]
        public async Task Tracking_dropped_before_the_seed_slew_is_turned_back_on_for_the_turns() {
            var (mount, calls) = TrackingMount(tracking: true, dropAtPointingRead: 1);

            await RunToAdjustingThenStopAsync(mount).ConfigureAwait(false);

            Assert.That(string.Join(", ", calls), Is.EqualTo("tracking True, slew tracking=True, slew tracking=True, tracking False, tracking False"),
                "on again for the turns; the user had it on, but a Stop never turns it back on (#2048)");
        }

        [TestCase("refused")]
        [TestCase("thrown")]
        [TestCase("never_reported")]
        public async Task A_mount_that_will_not_start_tracking_fails_as_tracking_off(string how) {
            var (mount, calls) = TrackingMount(tracking: false, onSet: _ => how switch {
                "refused" => false,
                "thrown" => throw new InvalidOperationException("mount offline"),
                _ => true,
            });

            var failure = await SeedFailureAsync(mount).ConfigureAwait(false);

            Assert.That(failure.GetProperty("reason").GetString(), Is.EqualTo("tracking_off"));
            // Handed back even so: a mount that reports late must not stay tracking when the user had it off.
            // The error event goes out before the run's finally hands tracking back.
            await calls.WhenCountAsync(2).WaitAsync(SignalTimeout).ConfigureAwait(false);
            Assert.That(string.Join(", ", calls), Is.EqualTo("tracking True, tracking False"), "no slew, the user's off put back");
        }

        [Test]
        public async Task A_mount_that_keeps_tracking_ends_the_live_readout_as_tracking_not_off() {
            // #2072 — the mount accepts tracking off and keeps tracking: the readout would take its turn for knob motion.
            var (mount, calls) = TrackingMount(tracking: true, onSet: _ => true);
            var fake = NewFake();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            var ws = new WsRecorder();
            using var svc = NewService(fake, solver, mount, ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "failed", "adjusting").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("failed"), "no live readout from a tracking mount");
            var failure = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(failure.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.TrackingNotOff));
            Assert.That(failure.GetProperty("message").GetString(), Does.Contain("kept tracking after Polar Align turned tracking off"),
                "caught before the first live frame, not by the per-frame check");
            Assert.That(ws.Events.Any(e => e.Type == WsEventCatalog.PolarAlignProgress), Is.False, "no readout was shown");
            Assert.That(calls, Does.Contain("tracking False"), "the routine did ask for tracking off");
        }

        [Test]
        public async Task A_mount_that_refuses_tracking_off_ends_the_run_at_once_as_tracking_not_off() {
            // #2072 — the write is refused (a driver fault) and the mount still reads tracking on: no 10 s wait.
            var (mount, _) = TrackingMount(tracking: true, onSet: on => on);
            var ws = new WsRecorder();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            using var svc = NewService(NewFake(), solver, mount, ws);
            svc.TrackingConfirmTimeout = TimeSpan.FromMinutes(5); // a wait for the confirm would time the test out

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "failed", "adjusting").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("failed"));
            var failure = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(failure.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.TrackingNotOff));
            Assert.That(failure.GetProperty("message").GetString(), Does.Contain("refused tracking off"));
        }

        [Test]
        public async Task A_weak_seed_puts_the_caution_on_each_progress_event_and_the_status_while_adjusting() {
            // #2072 — the wire the clients read: every seed in the weak band (the threshold raised past 1).
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), solver, ws: ws);
            svc.WeakReadoutCosPsi = 1.01;

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, 2).WaitAsync(SignalTimeout).ConfigureAwait(false);

            var progress = ws.Events.Where(e => e.Type == WsEventCatalog.PolarAlignProgress).Select(e => e.Payload).ToArray();
            Assert.That(progress.Select(p => p.GetProperty("readout_caution").GetString()),
                Is.All.EqualTo(PolarAlignReadoutCautions.AltitudeWeak), "the seed's event and every live frame's");
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).ReadoutCaution,
                Is.EqualTo(PolarAlignReadoutCautions.AltitudeWeak));

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).ReadoutCaution, Is.Null,
                "no caution outside a live readout");
        }

        [Test]
        public async Task Tracking_that_comes_on_during_the_live_readout_ends_the_run_as_tracking_not_off() {
            // #2072 — the hand pad, another app or the firmware turns tracking back on mid-loop.
            var (mount, _) = TrackingMount(tracking: true);
            var fake = NewFake();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            var ws = new WsRecorder();
            using var svc = NewService(fake, solver, mount, ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            Assert.That(mount.Object.GetInfo().TrackingEnabled, Is.False, "confirmed off before the first live frame");
            mount.Object.SetTrackingEnabled(true); // not the routine's write

            var status = await PollStateAsync(svc, "failed").ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("failed"));
            var failure = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(failure.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.TrackingNotOff));
            Assert.That(failure.GetProperty("message").GetString(), Does.Contain("started tracking during the live readout"));
        }

        [Test]
        public async Task A_mount_that_drops_its_connection_at_tracking_off_ends_the_run_as_tracking_not_off() {
            // #2072 — the write finds no mount (it answers false) and a disconnected mount reads tracking off: that confirms
            // nothing, and the mount, still tracking where it was left, would turn the readout.
            var linked = true;
            var (mount, _) = TrackingMount(tracking: true, onSet: on => {
                if (!on) {
                    linked = false;
                }
                return linked;
            }, connected: () => linked);
            var ws = new WsRecorder();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            using var svc = NewService(NewFake(), solver, mount, ws);
            svc.TrackingConfirmTimeout = TimeSpan.FromMinutes(5); // a wait for the confirm would time the test out

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "failed", "adjusting").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("failed"), "no live readout without a mount to confirm tracking off");
            var failure = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(failure.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.TrackingNotOff));
            Assert.That(failure.GetProperty("message").GetString(), Does.Contain("not connected"));
            Assert.That(ws.Events.Any(e => e.Type == WsEventCatalog.PolarAlignProgress), Is.False, "no readout was shown");
        }

        [Test]
        public async Task A_mount_that_slews_during_the_live_readout_ends_the_run_as_tracking_not_off() {
            // #2072 — an iOptron reads tracking off while it slews: a hand-pad slew mid-loop must still end the readout.
            var moving = false;
            var (mount, _) = TrackingMount(tracking: true, slewing: () => Volatile.Read(ref moving));
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), solver, mount, ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            Volatile.Write(ref moving, true); // not the routine's move; tracking still reads off

            var status = await PollStateAsync(svc, "failed").ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("failed"));
            var failure = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(failure.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.TrackingNotOff));
            Assert.That(failure.GetProperty("message").GetString(), Does.Contain("reads as slewing in the live readout"));
        }

        [Test]
        public void A_seed_due_east_of_the_pole_cautions_the_readout_and_a_seed_facing_it_clears_it() {
            // #2072 — the readout reads the altitude bolt poorly near ψ = ±90°: a seed there cautions it, the next
            // seed facing the pole clears it (the wire is A_weak_seed_puts_the_caution_on_each_progress_event…).
            using var svc = NewService(NewFake(), new ScriptedSolver());
            Assert.That(svc.NoteReadoutReference(gen: 0, (AltDeg: 30.0, AzDeg: 88.0), latitudeDeg: SiteLatDeg),
                Is.EqualTo(PolarAlignReadoutCautions.AltitudeWeak));
            Assert.That(svc.NoteReadoutReference(gen: 0, (AltDeg: 30.0, AzDeg: 101.0), latitudeDeg: SiteLatDeg),
                Is.EqualTo(PolarAlignReadoutCautions.AltitudeWeak), "|cos ψ| 0.19, inside the 0.2 band");
            Assert.That(svc.NoteReadoutReference(gen: 0, (AltDeg: 40.0, AzDeg: 5.0), latitudeDeg: SiteLatDeg), Is.Null);
            Assert.That(svc.NoteReadoutReference(gen: 0, (AltDeg: 30.0, AzDeg: 77.0), latitudeDeg: SiteLatDeg), Is.Null,
                "|cos ψ| 0.22, outside the band");
        }

        [Test]
        public async Task Tracking_that_drops_and_will_not_come_back_fails_before_the_slew() {
            var (mount, calls) = TrackingMount(tracking: true, onSet: _ => false, dropAtPointingRead: 1);

            Assert.That(await SeedFailureReasonAsync(mount).ConfigureAwait(false), Is.EqualTo("tracking_off"));
            Assert.That(calls.Where(c => c.StartsWith("slew", StringComparison.Ordinal)), Is.Empty);
        }

        [Test]
        public async Task A_mount_axis_well_off_the_pole_still_seeds_from_the_mounts_own_rotation() {
            // Field seeds from 2026-10-02 (iOptron HAE29C, axis ~4.6° off the NCP): a 30° RA turn moved the
            // near-pole guide field only 0.8° in solved RA. The three-point fit works on the pointings
            // themselves, so an axis that far out still fits, and the mount's 30° turns match the solved ones.
            static (double X, double Y, double Z) Unit(double raDeg, double decDeg) {
                var ra = raDeg * Math.PI / 180.0;
                var dec = decDeg * Math.PI / 180.0;
                return (Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec));
            }
            static (double X, double Y, double Z) Turn((double X, double Y, double Z) v, (double X, double Y, double Z) k, double deg) {
                var t = deg * Math.PI / 180.0;
                var c = (k.Y * v.Z - k.Z * v.Y, k.Z * v.X - k.X * v.Z, k.X * v.Y - k.Y * v.X);
                var d = k.X * v.X + k.Y * v.Y + k.Z * v.Z;
                return (v.X * Math.Cos(t) + c.Item1 * Math.Sin(t) + k.X * d * (1 - Math.Cos(t)),
                        v.Y * Math.Cos(t) + c.Item2 * Math.Sin(t) + k.Y * d * (1 - Math.Cos(t)),
                        v.Z * Math.Cos(t) + c.Item3 * Math.Sin(t) + k.Z * d * (1 - Math.Cos(t)));
            }
            static PolarAlignSolveOutcome Solved((double X, double Y, double Z) v) =>
                new(true, (Math.Atan2(v.Y, v.X) * 180.0 / Math.PI + 360.0) % 360.0, Math.Asin(v.Z) * 180.0 / Math.PI);
            var axis = Unit(138.0, 85.4);
            var camera = Turn(axis, Unit(228.0, 0.0), 5.0);
            var fake = NewFake();
            var seedC = Solved(Turn(camera, axis, 60.0));
            var solver = new ScriptedSolver { Fallback = seedC };
            solver.Enqueue(Solved(camera), Solved(Turn(camera, axis, 30.0)), seedC);
            using var svc = NewService(fake, solver);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"));
            Assert.That(status.CurrentErrorArcmin, Is.GreaterThan(200.0), "the 4.6° axis error is measured, not hidden");
        }

        // ── single-frame mode + frame readout ────────────────────────────────────────────────

        [Test]
        public async Task Single_frame_mode_parks_after_the_seed_and_shoots_one_frame_per_request() {
            var fake = NewFake();
            fake.Custody.LeaseRenewInterval = TimeSpan.FromTicks(-1); // every parked pass renews: each renewal is a signal
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { LoopMode = PolarAlignLoopModes.SingleFrame });
            var ws = new WsRecorder();
            using var svc = NewService(fake, solver, ws: ws, store: store);
            svc.CaptureRequestPoll = TimeSpan.FromMilliseconds(10);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            // The seed's progress, then a renewal from inside the park (it comes after WaitingForCapture is set).
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, 1).WaitAsync(SignalTimeout).ConfigureAwait(false);
            await fake.WhenLeaseCallsAsync(fake.LeaseCalls.Count + 1).WaitAsync(SignalTimeout).ConfigureAwait(false);
            var parked = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(parked.WaitingForCapture, Is.True);
            var shots = fake.Captures.Count;

            // Three more parked passes and no frame.
            await fake.WhenLeaseCallsAsync(fake.LeaseCalls.Count + 3).WaitAsync(SignalTimeout).ConfigureAwait(false);
            var still = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(still.FramesCaptured, Is.EqualTo(parked.FramesCaptured),
                "single-frame mode must not shoot until asked");
            Assert.That(fake.Captures.Count, Is.EqualTo(shots), "single-frame mode must not shoot until asked");
            Assert.That(still.LastFrame?.FrameId, Is.EqualTo("seed-c-1"));

            await svc.RequestCaptureAsync(null, CancellationToken.None).ConfigureAwait(false);
            await fake.WhenCapturedAsync(shots + 1).WaitAsync(SignalTimeout).ConfigureAwait(false);
            await fake.WhenLeaseCallsAsync(fake.LeaseCalls.Count + 1).WaitAsync(SignalTimeout).ConfigureAwait(false); // parked again
            var shot = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(shot.LastFrame?.FrameId, Is.EqualTo("live-1"));
            Assert.That(shot.WaitingForCapture, Is.True);
            Assert.That(shot.FramesCaptured, Is.EqualTo(parked.FramesCaptured + 1), "exactly one frame per request");
            Assert.That(fake.Captures.Count, Is.EqualTo(shots + 1), "exactly one frame per request");

            // Switching back to loop mode mid-run releases the park without a request: two more frames, each
            // published as a progress event after it is counted.
            var progress = ws.Count(WsEventCatalog.PolarAlignProgress);
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { LoopMode = PolarAlignLoopModes.Loop });
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, progress + 2).WaitAsync(SignalTimeout).ConfigureAwait(false);
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).FramesCaptured,
                Is.GreaterThanOrEqualTo(shot.FramesCaptured + 2));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task Capture_request_outside_the_adjust_phase_is_refused() {
            var fake = new FakeGuideCapture { Connected = false };
            using var svc = NewService(fake, new ScriptedSolver());

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RequestCaptureAsync(null, CancellationToken.None));
        }

        [Test]
        public async Task Each_frame_reports_its_exposure_timings_and_solved_pointing() {
            var fake = NewFake();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            var ws = new WsRecorder();
            using var svc = NewService(fake, solver, ws: ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            // The live loop publishes progress after it records its frame: the second progress event is its first frame.
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, 2).WaitAsync(SignalTimeout).ConfigureAwait(false);
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.LastFrame?.FrameId, Does.StartWith("live-"));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var frame = status.LastFrame!;
            Assert.That(frame.Solved, Is.True);
            Assert.That(frame.ExposureSeconds, Is.EqualTo(0.05));
            Assert.That(frame.CaptureMs, Is.GreaterThanOrEqualTo(0));
            Assert.That(frame.SolveMs, Is.Not.Null);
            Assert.That(frame.RaDeg, Is.EqualTo(SolveC.RaDegJnow));
            Assert.That(frame.DecDeg, Is.EqualTo(SolveC.DecDegJnow));

            Assert.That(ws.Count(WsEventCatalog.PolarAlignFrameStarted), Is.GreaterThanOrEqualTo(3));
            var complete = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignFrameComplete).Payload;
            Assert.That(complete.GetProperty("exposure_seconds").GetDouble(), Is.EqualTo(0.05));
            Assert.That(complete.TryGetProperty("capture_ms", out _), Is.True);
            Assert.That(complete.GetProperty("ra_deg").GetDouble(), Is.EqualTo(SolveA.RaDegJnow));

            // Every frame's started and complete events carry the same frame_id, seed attempts
            // included, so a consumer can pair them.
            string[] Ids(string type) => ws.Events.Where(e => e.Type == type)
                .Select(e => e.Payload.GetProperty("frame_id").GetString()!).ToArray();
            var completedIds = Ids(WsEventCatalog.PolarAlignFrameComplete);
            Assert.That(completedIds, Does.Contain("seed-a-1").And.Contain("seed-b-1").And.Contain("seed-c-1"));
            Assert.That(completedIds, Is.SubsetOf(Ids(WsEventCatalog.PolarAlignFrameStarted)));
        }

        [Test]
        public async Task Live_frame_is_proxied_from_the_connected_guider_and_null_without_one() {
            var fake = NewFake();
            var fetcher = new FakeFrameFetcher();
            using var svc = NewService(fake, new ScriptedSolver(), fetcher: fetcher);

            Assert.That(await svc.GetLiveFrameJpegAsync(CancellationToken.None).ConfigureAwait(false),
                Is.EqualTo(FakeFrameFetcher.Jpeg));
            fetcher.Fail = true;
            Assert.That(await svc.GetLiveFrameJpegAsync(CancellationToken.None).ConfigureAwait(false), Is.Null,
                "a failed fetch is no preview, not an error");

            var disconnected = new FakeGuideCapture { Connected = false };
            using var noGuider = NewService(disconnected, new ScriptedSolver());
            Assert.That(await noGuider.GetLiveFrameJpegAsync(CancellationToken.None).ConfigureAwait(false), Is.Null);
        }

        [Test]
        public async Task Stop_mid_seed_unwinds_cleanly_and_releases_the_lease_in_order() {
            // The routine is parked on a capture that never completes; Stop must cancel it, unwind,
            // and the wire order of the lease RPCs stays acquire-then-release.
            var fake = NewFake(answerCaptures: false);
            var ws = new WsRecorder();
            using var svc = NewService(fake, new ScriptedSolver(), ws: ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(PaCalls(fake), Is.EqualTo(AcquireThenRelease),
                "lease RPCs must run acquire-then-release, never race");
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("stopped"));
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStopped), Is.EqualTo(1));
        }

        // ── §45.13 session log + complete ────────────────────────────────────────────────────

        // #1591 — a run's log lets a reader rebuild seeds, fit and final error without the client.
        [Test]
        public async Task A_run_logs_its_seeds_fit_live_readouts_and_final_error_at_Information() {
            var fake = NewFake();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            var logger = new CapturingLogger();
            var ws = new WsRecorder();
            using var svc = NewService(fake, solver, NewMount(), ws, log: new RecordingLog(), logger: logger);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, 2).WaitAsync(SignalTimeout).ConfigureAwait(false);
            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);

            var ids = logger.Entries.Select(e => e.EventId).ToList();
            Assert.That(ids.Count(i => i == 4527), Is.EqualTo(3), "all three seed solves");
            Assert.That(ids, Does.Contain(4528).And.Contain(4529).And.Contain(4530));
            Assert.That(logger.Entries.Single(e => e.EventId == 4528).Message, Does.Contain("axis RA").And.Contain("seed error"));
        }

        [Test]
        public async Task Complete_logs_the_achieved_error_and_unwinds_like_stop() {
            var fake = NewFake();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            var log = new RecordingLog();
            var mount = NewMount();
            var ws = new WsRecorder();
            using var svc = NewService(fake, solver, mount, ws, log: log);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            // Wait for the first LIVE iteration (not just the seed) so the row's iteration count is
            // real. The signal must be the second progress EVENT (seed publishes iteration 0, the
            // live loop publishes after recording its count) — LastFrameId flips to "live-1" at
            // capture time, BEFORE the solve bumps the counter, so polling it races Complete.
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, 2).WaitAsync(SignalTimeout).ConfigureAwait(false);
            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(log.Rows.TryDequeue(out var row), Is.True, "Complete writes exactly one session row");
            Assert.That(row!.Outcome, Is.EqualTo("complete"));
            Assert.That(row.FinalErrorArcmin, Is.Not.Null.And.EqualTo(1.0).Within(0.2));
            Assert.That(row.Iterations, Is.GreaterThanOrEqualTo(1));
            Assert.That(log.Rows, Is.Empty);
            Assert.That(PaCalls(fake).Last(), Is.False, "Complete releases the lease");
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("stopped"));
        }

        [Test]
        public async Task Stop_logs_an_aborted_row_and_a_second_stop_does_not_duplicate_it() {
            var fake = NewFake();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            var log = new RecordingLog();
            using var svc = NewService(fake, solver, log: log);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(log.Rows.Count, Is.EqualTo(1), "only the Stop that ended the routine logs a row");
            Assert.That(log.Rows.TryDequeue(out var row), Is.True);
            Assert.That(row!.Outcome, Is.EqualTo("aborted"));
        }

        [Test]
        public async Task Stop_with_an_error_reading_records_the_last_result() {
            var fake = NewFake();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            using var svc = NewService(fake, solver, log: new RecordingLog());
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).LastResult, Is.Null, "none by default");

            var before = DateTimeOffset.UtcNow;
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.LastResult, Is.Not.Null);
            Assert.That(status.LastResult!.ErrorArcmin, Is.EqualTo(1.0).Within(0.2));
            Assert.That(status.LastResult.CompletedUtc, Is.GreaterThanOrEqualTo(before));
        }

        [Test]
        public async Task A_routine_that_never_produced_an_error_reading_records_no_last_result() {
            var fake = NewFake();
            var solver = new ScriptedSolver { Fallback = Unsolved };
            var log = new RecordingLog();
            using var svc = NewService(fake, solver, log: log);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).LastResult, Is.Null);
        }

        [Test]
        public async Task A_Stop_in_the_coarse_stage_records_no_last_result() {
            // The rough readout is not an alignment (Stop there records nothing), so Night Setup must not count it.
            var fake = NewFake();
            using var svc = NewService(fake, new HintFollowingSolver(), NewPoleMount(), store: NewStore(), log: new RecordingLog(), mainCamera: new FakeMainCamera());

            await svc.StartConfirmedAsync(PolarAlignStages.Coarse, null, CancellationToken.None).ConfigureAwait(false);
            var coarse = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            Assert.That(coarse.Stage, Is.EqualTo(PolarAlignStages.Coarse));
            Assert.That(coarse.CurrentErrorArcmin, Is.Not.Null, "the rough step has a reading");
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).LastResult, Is.Null);
        }

        [Test]
        public async Task A_failed_routine_logs_a_failed_row_and_stop_does_not_add_another() {
            var fake = NewFake();
            var solver = new ScriptedSolver { Fallback = Unsolved };
            var log = new RecordingLog();
            using var svc = NewService(fake, solver, log: log);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "failed").ConfigureAwait(false);
            await log.FirstRow.WaitAsync(SignalTimeout).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(log.Rows.Count, Is.EqualTo(1));
            Assert.That(log.Rows.TryDequeue(out var row), Is.True);
            Assert.That(row!.Outcome, Is.EqualTo("failed"));
            Assert.That(row.FinalErrorArcmin, Is.Null, "a routine that never reached adjusting has no error");
        }

        [Test]
        public void Polar_align_settings_round_trip_through_the_store_with_defaults() {
            var store = new InMemoryProfileStore();
            var defaults = store.GetPolarAlignSettings();
            Assert.That(defaults.ExposureSeconds, Is.EqualTo(1.0));
            Assert.That(defaults.Binning, Is.EqualTo(PolarAlignBinning.Auto), "§45.12: binning is auto by default");
            Assert.That(defaults.TargetToleranceArcmin, Is.EqualTo(1.0));
            Assert.That(defaults.SeedRotationDeg, Is.EqualTo(10.0));
            Assert.That(defaults.CheckStepDeg, Is.EqualTo(10.0));
            Assert.That(defaults.CoarseExposureSeconds, Is.EqualTo(3.0));
            Assert.That(defaults.PolarAlignerAuto, Is.True, "#1971: a rig with a Polar Aligner uses it unless told not to");
            Assert.That(defaults.StopTrackingWhenDone, Is.True, "#2048: tracking off at the end of a run, as NINA's default");
            Assert.That(defaults.LoopCadenceMs, Is.EqualTo(1000));
            Assert.That(defaults.SettleSeconds, Is.EqualTo(2.0));
            Assert.That(defaults.Camera, Is.EqualTo(PolarAlignCameras.Guide), "#2082: the guide camera measures unless told otherwise");
            Assert.That(defaults.FramesPerPoint, Is.EqualTo(3), "#2082: three frames averaged at each point");

            store.PutPolarAlignSettings(defaults with { ExposureSeconds = 0.5, Binning = 2 });
            var updated = store.GetPolarAlignSettings();
            Assert.That(updated.ExposureSeconds, Is.EqualTo(0.5));
            Assert.That(updated.Binning, Is.EqualTo(2));
        }

        // ── one measurement per Start (#2082): coarse (main camera), fine, check — all from where the mount points ──

        /// <summary>A guide-camera solver that answers with the hint's RA at Dec 85° — the check's three
        /// frames then land on the Dec-85 circle around the true pole whatever the loop did before.</summary>
        private sealed class HintFollowingSolver : IPolarAlignFrameSolver {
            public ConcurrentQueue<(double? Ra, double? Dec)> Hints { get; } = new();
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) {
                Hints.Enqueue((hintRa, hintDec));
                return Task.FromResult(hintRa is double ra ? new PolarAlignSolveOutcome(true, ra, 85.0) : SolveA);
            }
        }

        /// <summary>The main camera's capture-and-solve: a camera <see cref="OffsetDeg"/> off the RA axis on a
        /// mount whose axis sits on the true pole. It answers at the hint's RA, <see cref="OffsetDeg"/> further
        /// from the pole than the Dec-85° circle the mount points on — so every frame lies on one circle about
        /// the pole and only a measured axis (not the pointing read as the axis) comes out error-free. A fixed
        /// <see cref="Answer"/> overrides that.</summary>
        private sealed class FakeMainCamera : IPolarAlignMainCameraSolver {
            public PolarAlignSolveOutcome? Answer { get; set; }
            public double OffsetDeg { get; set; } = 1.25;
            /// <summary>Non-null = the camera cannot shoot (not connected), with the reason.</summary>
            public string? Unavailable { get; set; }
            public int Calls;
            public ConcurrentQueue<double> Exposures { get; } = new();
            public Task<string?> UnavailableReasonAsync(CancellationToken ct) => Task.FromResult(Unavailable);
            public ConcurrentQueue<bool> BlindFailovers { get; } = new();
            public Task<PolarAlignSolveOutcome> CaptureAndSolveAsync(double exposureSeconds, (double RaDeg, double DecDeg)? hintJnow, bool blindFailover, CancellationToken ct) {
                Interlocked.Increment(ref Calls);
                Exposures.Enqueue(exposureSeconds);
                BlindFailovers.Enqueue(blindFailover);
                if (Answer is { } fixedAnswer) {
                    return Task.FromResult(fixedAnswer);
                }
                return Task.FromResult(new PolarAlignSolveOutcome(true, hintJnow?.RaDeg ?? 0.0, PoleMountDecDeg - OffsetDeg));
            }
        }

        /// <summary>A mount the user pointed near the pole (RA 0, Dec 85: 5° off it, as #2082 asks), counterweights down. It
        /// can find Home and slew, so a test sees that the routine does neither (#2048): <paramref name="motorStops"/>
        /// records where each motor move ended, and <see cref="NoHomeNoGoTo"/> checks the rest.</summary>
        private static Mock<ITelescopeMediator> NewPoleMount(ConcurrentQueue<Coordinates>? motorStops = null) {
            var mount = new Mock<ITelescopeMediator>();
            var tracking = true;
            mount.Setup(m => m.GetInfo()).Returns(() => new TelescopeInfo {
                // RA 0 at LST 20 h is HA −4 h, the west side by the hour-angle model.
                Connected = true, SiderealTime = 20.0, TrackingEnabled = tracking, CanFindHome = true, CanSlew = true, SideOfPier = PierSide.pierWest,
            });
            var position = new Coordinates(0.0, PoleMountDecDeg, Epoch.JNOW, Coordinates.RAType.Degrees);
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => position);
            mount.Setup(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            mount.Setup(m => m.FindHome(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            SimulateMotors(mount, () => position, c => position = c, stopped: c => motorStops?.Enqueue(c));
            mount.Setup(m => m.WaitForSlew(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            mount.Setup(m => m.SetTrackingEnabled(It.IsAny<bool>())).Returns((bool on) => {
                tracking = on;
                return true;
            });
            return mount;
        }

        /// <summary>Where <see cref="NewPoleMount"/> points: 5° from the pole, inside the 2–5° #2082 asks for.</summary>
        private const double PoleMountDecDeg = 85.0;

        /// <summary>#2048 — the routine never sends the mount Home, never slews it and never unparks it.</summary>
        private static void NoHomeNoGoTo(Mock<ITelescopeMediator> mount) {
            mount.Verify(m => m.FindHome(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never, "no Find Home");
            mount.Verify(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()), Times.Never, "no GoTo");
            mount.Verify(m => m.UnparkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never, "no unpark");
        }

        [Test]
        public async Task Start_refuses_the_coarse_stage_without_a_main_camera_solver() {
            var fake = NewFake();
            using var svc = NewService(fake, new ScriptedSolver());

            await AssertThrowsAsync<InvalidOperationException>(() => svc.StartConfirmedAsync(PolarAlignStages.Coarse, null, CancellationToken.None)).ConfigureAwait(false);
            await AssertThrowsAsync<ArgumentException>(() => svc.StartConfirmedAsync("sideways", null, CancellationToken.None)).ConfigureAwait(false);
            Assert.That(fake.LeaseCalls, Is.Empty, "a refused start takes no lease");
        }

        [Test]
        public async Task Next_is_refused_when_nothing_waits_on_the_user() {
            var fake = NewFake();
            using var svc = NewService(fake, new ScriptedSolver());

            await AssertThrowsAsync<InvalidOperationException>(() => svc.NextAsync(null, CancellationToken.None)).ConfigureAwait(false);
        }

        [Test]
        public async Task Coarse_stage_starts_where_the_mount_points_and_Next_never_starts_the_fine_stage() {
            var fake = NewFake();
            var stops = new ConcurrentQueue<Coordinates>();
            var mount = NewPoleMount(stops);
            // #1727 — a main camera 1.25° off the RA axis on a mount whose axis IS the pole. Read as the
            // axis (the 2026-11-07 coarse stage) that camera reports a 75′ error that is not there.
            var mainCamera = new FakeMainCamera { OffsetDeg = 1.25 };
            var ws = new WsRecorder();
            using var svc = NewService(fake, new HintFollowingSolver(), mount, ws, mainCamera: mainCamera);

            await svc.StartConfirmedAsync(PolarAlignStages.Coarse, null, CancellationToken.None).ConfigureAwait(false);
            var started = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignStarted).ConfigureAwait(false);
            Assert.That(started.GetProperty("state").GetString(), Is.EqualTo("seeding"), "no homing state: the run starts where the mount points");
            var coarse = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);

            Assert.That(coarse.State, Is.EqualTo("adjusting"));
            Assert.That(coarse.Stage, Is.EqualTo(PolarAlignStages.Coarse));
            Assert.That(mainCamera.Calls, Is.GreaterThanOrEqualTo(4), "three seed frames and at least one live frame");
            Assert.That(mainCamera.Exposures.TryPeek(out var exposure) && exposure == 3.0, Is.True, "the coarse exposure, not the guide exposure");
            var blind = mainCamera.BlindFailovers.ToArray();
            Assert.That(blind.Take(3), Is.All.True, "the seed frames may need the blind fallback");
            Assert.That(blind.Skip(3), Is.All.False, "the live loop takes one fast look per frame");
            // The axis is measured from the seed turn, so the camera's offset cancels: only the ~1′
            // refraction term is left — never the 75′ the pointing-as-axis reading gave.
            Assert.That(coarse.CurrentErrorArcmin, Is.LessThan(3.0));
            Assert.That(stops.ToArray(), Has.Length.EqualTo(2), "the two seed turns from where the mount points, before the first live frame");
            Assert.That(stops.Select(c => c.RADegrees).ToArray(), Is.EqualTo(CoarseSeedTurns).Within(1e-6), "both 30° turns east: east_direction unset, east in the north (#2081)");
            Assert.That(stops.Select(c => c.Dec), Is.All.EqualTo(PoleMountDecDeg).Within(1e-6), "no Dec motion");
            var progress = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignProgress).ConfigureAwait(false);
            Assert.That(progress.GetProperty("stage").GetString(), Is.EqualTo(PolarAlignStages.Coarse));
            mount.Verify(m => m.SetTrackingEnabled(false), Times.AtLeastOnce, "the pointing must move only with the knobs");

            // #2082 — one measurement per Start: Next is refused even here, and the rough loop runs on with no turn.
            await AssertThrowsAsync<InvalidOperationException>(() => svc.NextAsync(null, CancellationToken.None)).ConfigureAwait(false);
            var mark = ws.Events.Count;
            await ws.WhenAsync(events => events.Skip(mark).Count(e => e.Type == WsEventCatalog.PolarAlignProgress) >= 2).WaitAsync(SignalTimeout).ConfigureAwait(false);
            var still = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That((still.State, still.Stage), Is.EqualTo(("adjusting", PolarAlignStages.Coarse)));
            Assert.That(stops, Has.Count.EqualTo(2), "no further turn");
            Assert.That(ws.Events.Count(e => e.Type == WsEventCatalog.PolarAlignStage), Is.EqualTo(1), "never a second stage");

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            NoHomeNoGoTo(mount);
        }

        [Test]
        public async Task Start_refuses_the_coarse_stage_when_the_main_camera_is_not_connected() {
            var fake = NewFake();
            var mount = NewPoleMount();
            var mainCamera = new FakeMainCamera { Unavailable = "the main camera is not connected" };
            using var svc = NewService(fake, new HintFollowingSolver(), mount, mainCamera: mainCamera, store: NewStore());

            // Refused before the mount moves: no lease, no motor, no frame.
            await AssertThrowsAsync<InvalidOperationException>(() => svc.StartConfirmedAsync(PolarAlignStages.Coarse, null, CancellationToken.None)).ConfigureAwait(false);
            Assert.That(fake.LeaseCalls, Is.Empty);
            mount.Verify(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.Is<double>(r => r != 0)), Times.Never);
            Assert.That(mainCamera.Calls, Is.Zero);

            // The fine stage needs no main camera and still starts.
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task A_start_that_names_no_stage_begins_at_the_fine_stage_when_the_main_camera_cannot_shoot() {
            // Simple Mode and the Preflight pilot start without a stage: a guide-camera-only session (main
            // camera off) must still polar-align, not be told to use a button it does not have.
            var fake = NewFake();
            var mainCamera = new FakeMainCamera { Unavailable = "the main camera is not connected" };
            var ws = new WsRecorder();
            using var svc = NewService(fake, new HintFollowingSolver(), NewPoleMount(), ws, store: NewStore(), mainCamera: mainCamera);

            await svc.StartConfirmedAsync(null, CancellationToken.None).ConfigureAwait(false);
            var started = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignStarted).ConfigureAwait(false);
            Assert.That(started.GetProperty("stage").GetString(), Is.EqualTo(PolarAlignStages.Fine));
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("adjusting"));
            Assert.That(status.Stage, Is.EqualTo(PolarAlignStages.Fine));
            Assert.That(mainCamera.Calls, Is.Zero);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task A_start_that_names_no_stage_is_the_fine_measurement_even_with_the_main_camera_ready() {
            // #2082 — the default Start is one guide-camera measurement. It used to begin at the rough step on the main
            // camera and lead from there into the fine step and the check.
            var fake = NewFake();
            var mainCamera = new FakeMainCamera();
            var ws = new WsRecorder();
            using var svc = NewService(fake, new HintFollowingSolver(), NewPoleMount(), ws, store: NewStore(), mainCamera: mainCamera);

            var plan = await svc.GetStartPlanAsync(null, CancellationToken.None).ConfigureAwait(false);
            await svc.StartConfirmedAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That((plan.Stage, plan.StepDeg, plan.MaxSweepDeg), Is.EqualTo(((string?)null, 30.0, 60.0)), "the fine measurement's two turns");
            Assert.That((status.State, status.Stage), Is.EqualTo(("adjusting", PolarAlignStages.Fine)));
            Assert.That(mainCamera.Calls, Is.Zero, "the main camera is never used");
        }

        [Test]
        public async Task A_fine_start_seeds_from_where_the_mount_points_and_never_sends_it_Home() {
            // #2048 — the mount that hit the pier went Home on every stage, and AlpacaBridge's fresh "at Home" was taken
            // as proof of where it was. A fine start (also "Adjust again") starts from where the user pointed the mount,
            // on a mount that can find Home as on any other, like NINA's Three Point Polar Alignment.
            var fake = NewFake();
            var stops = new ConcurrentQueue<Coordinates>();
            var mount = NewPoleMount(stops);
            var ws = new WsRecorder();
            using var svc = NewService(fake, new HintFollowingSolver(), mount, ws, store: NewStore());

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var started = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignStarted).ConfigureAwait(false);
            Assert.That(started.GetProperty("state").GetString(), Is.EqualTo("seeding"));
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            NoHomeNoGoTo(mount);
            Assert.That(ws.Count("polar_align.needs_home"), Is.Zero, "nobody is asked to put the mount at Home");
            var targets = stops.ToArray();
            Assert.That(targets.Length, Is.EqualTo(2), "the two seed turns, no Dec move");
            Assert.That(targets.Select(t => t.Dec), Is.All.EqualTo(PoleMountDecDeg).Within(1e-6));
        }

        [Test]
        public async Task Done_after_the_check_ends_the_routine_and_publishes_stopped() {
            var fake = NewFake();
            var ws = new WsRecorder();
            var log = new RecordingLog();
            using var svc = NewService(fake, new HintFollowingSolver(), NewPoleMount(), ws, log: log, store: NewStore());

            await svc.StartConfirmedAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "checked", "failed").ConfigureAwait(false)).State, Is.EqualTo("checked"));
            Assert.That(log.Rows.Count, Is.EqualTo(1), "the check wrote its row");

            // The user's Done on the verdict: the routine ends and the client hears so — no second row.
            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("stopped"));
            Assert.That(ws.Events.Any(e => e.Type == WsEventCatalog.PolarAlignStopped), Is.True);
            Assert.That(log.Rows.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task The_check_refuses_a_verdict_when_the_three_solves_do_not_describe_a_turn() {
            // The mount reports its 30° turns but the sky never moved (a slipping clutch): three solves of one
            // field, apart by noise. No fit from that may call the alignment excellent.
            var fake = NewFake();
            var solver = new ScriptedSolver();
            solver.Enqueue(
                new PolarAlignSolveOutcome(true, 10.0, 85.0),
                new PolarAlignSolveOutcome(true, 10.0001, 85.0001),
                new PolarAlignSolveOutcome(true, 9.9999, 85.0002));
            var ws = new WsRecorder();
            using var svc = NewService(fake, solver, NewPoleMount(), ws, store: NewStore());

            await svc.StartConfirmedAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            var error = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignError).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("axis_fit_failed"));
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).CheckResult, Is.Null, "no verdict");
        }

        [Test]
        public async Task Done_is_refused_during_the_coarse_stage_while_the_error_is_outside_the_tolerance() {
            // A stage-unaware client's Done on a rough readout still off the profile's tolerance would write a
            // "complete" row the guiding and "last night" readouts trust.
            var fake = NewFake();
            var log = new RecordingLog();
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { TargetToleranceArcmin = 0.001 });
            using var svc = NewService(fake, new HintFollowingSolver(), NewPoleMount(), store: store, log: log, mainCamera: new FakeMainCamera());

            await svc.StartConfirmedAsync(PolarAlignStages.Coarse, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).Stage, Is.EqualTo(PolarAlignStages.Coarse));

            await AssertThrowsAsync<InvalidOperationException>(() => svc.CompleteAsync(null, CancellationToken.None)).ConfigureAwait(false);
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("adjusting"), "still running");
            Assert.That(log.Rows, Is.Empty, "no complete row from an out-of-tolerance rough step");
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task Done_in_the_coarse_stage_inside_the_tolerance_completes_and_records_the_result() {
            var fake = NewFake();
            var log = new RecordingLog();
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { TargetToleranceArcmin = 100000 });
            using var svc = NewService(fake, new HintFollowingSolver(), NewPoleMount(), store: store, log: log, mainCamera: new FakeMainCamera());

            await svc.StartConfirmedAsync(PolarAlignStages.Coarse, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).Stage, Is.EqualTo(PolarAlignStages.Coarse));

            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);

            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("stopped"));
            Assert.That(status.LastResult, Is.Not.Null, "Done in the rough step is an alignment");
            Assert.That(log.Rows.Count, Is.EqualTo(1));
            Assert.That(log.Rows.TryPeek(out var row) && row.Outcome == "complete", Is.True);
        }

        [Test]
        public async Task A_check_only_run_reads_the_guide_frame_size_afresh() {
            var fake = NewFake();
            using var svc = NewService(fake, new HintFollowingSolver(), NewPoleMount(), store: NewStore());

            await svc.StartConfirmedAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "checked", "failed").ConfigureAwait(false)).State, Is.EqualTo("checked"));
            Assert.That(fake.FrameSizeReads, Is.EqualTo(1));

            // The guide camera may have changed between runs: a second check-only run asks again instead
            // of binning by the last run's frame size.
            var mark = Mark(svc);
            await svc.StartConfirmedAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, mark, "checked", "failed").ConfigureAwait(false)).State, Is.EqualTo("checked"));
            Assert.That(fake.FrameSizeReads, Is.EqualTo(2));
        }

        [Test]
        public async Task A_check_only_start_opens_in_checking() {
            var fake = NewFake();
            var store = NewStore();
            var ws = new WsRecorder();
            using var svc = NewService(fake, new HintFollowingSolver(), NewMount(), ws, store: store);

            await svc.StartConfirmedAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            var started = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignStarted).ConfigureAwait(false);

            Assert.That(started.GetProperty("state").GetString(), Is.EqualTo("checking"), "the check never seeds");
            Assert.That((await PollStateAsync(svc, "checked", "failed").ConfigureAwait(false)).State, Is.EqualTo("checked"));
        }

        [Test]
        public async Task Check_stage_measures_the_axis_from_three_solves_and_ends_checked() {
            var fake = NewFake();
            var slews = new ConcurrentQueue<Coordinates>();
            var mount = NewPoleMount(slews);
            var solver = new HintFollowingSolver(); // every solve lands on the Dec-85 circle around the TRUE pole
            var ws = new WsRecorder();
            var log = new RecordingLog();
            using var svc = NewService(fake, solver, mount, ws, log: log, store: NewStore());

            await svc.StartConfirmedAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "checked", "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("checked"));
            Assert.That(status.Stage, Is.EqualTo(PolarAlignStages.Check));
            Assert.That(status.CheckSolvesDone, Is.EqualTo(3));
            Assert.That(status.CheckResult, Is.Not.Null);
            // The axis IS the pole: the only error is the ~1′ refraction term (the apparent pole sits above it).
            Assert.That(status.CheckResult!.TotalErrorArcmin, Is.EqualTo(0.98).Within(0.1));
            Assert.That(status.CheckResult.AltitudeErrorArcmin, Is.LessThan(0));
            // #2072: the band rates the error plus twice its 1σ; one frame per point (1″ floor) at 10° gives about 1.3′,
            // so 0.98′ ± 1.3′ is "good", not "excellent".
            Assert.That(status.CheckResult.Verdict, Is.EqualTo("good"));
            Assert.That(status.CheckResult.ConeHalfAngleDeg, Is.EqualTo(5.0).Within(1e-6));
            Assert.That(status.CheckResult.TurnMismatchDeg, Is.EqualTo(0.0).Within(1e-6));
            Assert.That(status.LastResult, Is.Not.Null, "#1565: a finished check is the last result");
            Assert.That(status.LastResult!.ErrorArcmin, Is.EqualTo(status.CheckResult.TotalErrorArcmin).Within(1e-6));
            Assert.That(status.CheckResult.FineErrorArcmin, Is.Null, "no run before it had a readout");
            // From where the user pointed it: two 10° turns (the default check step) the same way, no Dec move.
            var targets = slews.ToArray();
            Assert.That(targets.Length, Is.EqualTo(2));
            Assert.That(targets.Select(t => t.Dec), Is.All.EqualTo(PoleMountDecDeg).Within(1e-6));
            Assert.That(Math.Abs(((targets[0].RADegrees + 540) % 360) - 180), Is.EqualTo(10.0).Within(1e-6));
            Assert.That(Math.Abs(((targets[1].RADegrees - targets[0].RADegrees + 540) % 360) - 180), Is.EqualTo(10.0).Within(1e-6));
            // The row, the event and the lease.
            var checkedEvent = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignChecked).ConfigureAwait(false);
            Assert.That(checkedEvent.GetProperty("verdict").GetString(), Is.EqualTo("good"));
            Assert.That(log.Rows.Single().Outcome, Is.EqualTo("checked"));
            Assert.That(log.Rows.Single().FinalErrorArcmin, Is.EqualTo(status.CheckResult.TotalErrorArcmin).Within(1e-6));
            Assert.That(PaCalls(fake), Is.EqualTo(AcquireThenRelease), "the check releases the lease itself");

            // A stray Stop afterwards keeps the terminal result and writes no second row.
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var after = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(after.State, Is.EqualTo("checked"));
            Assert.That(log.Rows.Count, Is.EqualTo(1));
            NoHomeNoGoTo(mount);
        }

        private static readonly double[] CoarseSeedTurns = { 30.0, 60.0 };
        private static readonly double[] LoopThenCheckTurns = { 10.0, 20.0, 10.0, 0.0 };
        private static readonly string[] StoppedThenStarted = { WsEventCatalog.PolarAlignStopped, WsEventCatalog.PolarAlignStarted };
        private static readonly string[] AbortedThenChecked = { "aborted", "checked" };

        [Test]
        public async Task A_Check_from_the_adjust_loop_ends_it_and_compares_with_its_readout() {
            // #2082 — Check on the adjust screen is a confirmed Start(check): it ends the live loop like Stop (the mount stopped
            // and confirmed), then measures from where the mount is, its turns back toward the session's origin. Its verdict
            // sits beside the last run's readout: the loop's (Joey may overrule what it is compared with).
            var fake = NewFake();
            var stops = new ConcurrentQueue<Coordinates>();
            var mount = NewPoleMount(stops);
            var ws = new WsRecorder();
            var log = new RecordingLog();
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { SeedRotationDeg = 10.0, CheckStepDeg = 10.0 });
            using var svc = NewService(fake, new HintFollowingSolver(), mount, ws, log: log, store: store);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var fine = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            Assert.That((fine.State, fine.Stage), Is.EqualTo(("adjusting", PolarAlignStages.Fine)));

            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Check, CancellationToken.None).ConfigureAwait(false);
            Assert.That((plan.NewOrigin, plan.Direction, plan.StepDeg, plan.MaxSweepDeg), Is.EqualTo((false, PolarAlignDirections.West, 10.0, 20.0)),
                "back toward the origin, 2 × 10°");
            var mark = Mark(svc);
            await svc.StartAsync(PolarAlignStages.Check, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, mark, "checked", "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("checked"));
            Assert.That(status.CheckResult!.FineErrorArcmin, Is.Not.Null, "the loop's readout travels into the check");
            // Both measured the same axis (the pole): the distance between the two measured axes is ~0.
            Assert.That(status.CheckResult.FineDeltaArcmin, Is.Not.Null.And.LessThan(0.1));
            Assert.That(stops.Select(c => c.RADegrees).ToArray(), Is.EqualTo(LoopThenCheckTurns).Within(1e-6),
                "the loop's two turns east, then the check's two back west");
            Assert.That(ws.Events.Skip(mark).Select(e => e.Type).Where(StoppedThenStarted.Contains),
                Is.EqualTo(StoppedThenStarted), "the loop ended, then the check started");
            Assert.That(log.Rows.Select(r => r.Outcome), Is.EqualTo(AbortedThenChecked));
            // The verdict ends the run: stop_tracking_when_done (on by default, #2048) leaves tracking off.
            var trackingWrites = mount.Invocations.Where(i => i.Method.Name == nameof(ITelescopeMediator.SetTrackingEnabled)).ToArray();
            Assert.That(trackingWrites.Last().Arguments[0], Is.False, "tracking off after the verdict");
            NoHomeNoGoTo(mount);
        }

        [Test]
        public async Task A_Stop_while_a_Check_ends_the_adjust_loop_starts_no_check() {
            // #2082 review — the Check's own end of the loop counts as one end request. A Stop tapped while that end runs
            // (the shutter still shows Stop) is a second one: the Check must start nothing, as for a Stop during any Start.
            var fake = NewFake();
            var stops = new ConcurrentQueue<Coordinates>();
            var mount = NewPoleMount(stops);
            var ws = new WsRecorder();
            var log = new RecordingLog();
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { SeedRotationDeg = 10.0, CheckStepDeg = 10.0 });
            using var svc = NewService(fake, new HintFollowingSolver(), mount, ws, log: log, store: store);
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            var turnsBefore = stops.Count;

            Task? userStop = null;
            log.OnInsert = row => {
                if (row.Outcome == "aborted" && userStop is null) {
                    userStop = svc.StopAsync(null, CancellationToken.None); // lands while the loop's end holds the lock
                }
            };
            var plan = await svc.GetStartPlanAsync(PolarAlignStages.Check, CancellationToken.None).ConfigureAwait(false);
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(
                PolarAlignStages.Check, new PolarAlignStartConfirmation(plan.Direction, plan.NewOrigin), null, CancellationToken.None));
            await userStop!.WaitAsync(SignalTimeout).ConfigureAwait(false);

            Assert.That(refused!.Message, Does.Contain("stopped while it was starting"));
            Assert.That(stops.Count, Is.EqualTo(turnsBefore), "no check turn after the Stop");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignChecked), Is.Zero);
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("stopped"));
        }

        [Test]
        public async Task A_second_Check_compares_with_the_first_Checks_verdict() {
            // #2082 review — a check's verdict is the last run's readout too: Check, then Check again compares with it.
            var fake = NewFake();
            var ws = new WsRecorder();
            using var svc = NewService(fake, new HintFollowingSolver(), NewPoleMount(), ws, store: NewStore());

            await svc.StartConfirmedAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            var first = await PollStateAsync(svc, "checked", "failed").ConfigureAwait(false);
            Assert.That(first.State, Is.EqualTo("checked"));
            Assert.That(first.CheckResult!.FineErrorArcmin, Is.Null, "no run before it");

            var mark = Mark(svc);
            await svc.StartConfirmedAsync(PolarAlignStages.Check, null, CancellationToken.None).ConfigureAwait(false);
            var second = await PollStateAsync(svc, mark, "checked", "failed").ConfigureAwait(false);

            Assert.That(second.State, Is.EqualTo("checked"));
            Assert.That(second.CheckResult!.FineErrorArcmin, Is.EqualTo(first.CheckResult.TotalErrorArcmin).Within(0.02),
                "the first check's verdict is the last run's readout");
            Assert.That(second.CheckResult.FineDeltaArcmin, Is.Not.Null.And.LessThan(0.1));
        }

        private static async Task AssertThrowsAsync<T>(Func<Task> act) where T : Exception {
            try {
                await act().ConfigureAwait(false);
            } catch (T) {
                return;
            }
            Assert.Fail($"expected {typeof(T).Name}");
        }
    }
}