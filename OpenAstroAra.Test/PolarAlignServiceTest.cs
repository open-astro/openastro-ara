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
    /// no timing; ADR 0016 rule 5), a scripted <see cref="IPolarAlignFrameSolver"/>, and a mocked mount whose motors turn
    /// it on a manual clock that the routine's timed turns move (<see cref="BenchClockOf"/>). Covers the
    /// seed (frame A → RA turn → frame B → RA turn → frame C → axis fit), the live-adjust loop (tracking off, errors populated,
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

        /// <summary>A solver that can answer with where the bench mount really points: <see cref="NewServiceOn"/> binds it
        /// to its mount.</summary>
        private interface IMountSky {
            Func<(double RaDeg, double DecDeg)>? Sky { get; set; }
        }

        private sealed class ScriptedSolver : IPolarAlignFrameSolver, IMountSky {
            private readonly ConcurrentQueue<PolarAlignSolveOutcome> _script = new();
            /// <summary>The answer once the script runs out; null = the solve lands where the mount points
            /// (<see cref="Sky"/>), as a real sky would, so the three-point seed sees three distinct pointings on one
            /// circle.</summary>
            public PolarAlignSolveOutcome? Fallback { get; set; }
            public Func<(double RaDeg, double DecDeg)>? Sky { get; set; }
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
                    : Fallback ?? (Sky?.Invoke() is { } sky ? new PolarAlignSolveOutcome(true, sky.RaDeg, sky.DecDeg) : SolveB));
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
            // The pointing follows the motors: the bench sky (IMountSky) solves where they left the mount.
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

        /// <summary>A fresh reading of a mock set up as the telescope service, as the stop's confirmation takes it: stamped
        /// now on the mount's bench clock, mount session 0.</summary>
        private static MountReading FreshReading(Mock<ITelescopeMediator> mount, double raHours, double decDeg) =>
            new(raHours, decDeg, Slewing: false, PierSide.pierWest, BenchClockOf(mount).GetUtcNow(), SessionEpoch: 0);

        private static double Wrap180Test(double deg) => ((deg + 540.0) % 360.0) - 180.0;

        /// <summary>#2064 — the pier side of a counterweight-down pose at RA <paramref name="raDeg"/> and LST
        /// <paramref name="lstHours"/>: east for an hour angle in [0, 12 h), west otherwise.</summary>
        private static PierSide CounterweightDownSide(double lstHours, double raDeg) =>
            Wrap180Test((lstHours * 15.0) - raDeg) >= 0.0 ? PierSide.pierEast : PierSide.pierWest;

        // The manual clock each bench mount's motors run on; NewServiceOn hands it to the service, whose timed turns move it.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mock<ITelescopeMediator>, ManualMountClock> BenchClocks = new();

        /// <summary>The bench mount's clock: it advances by itself as the routine times a turn on it, so a turn takes no
        /// real time and the mount turns exactly the rate times the time its motor ran.</summary>
        private static ManualMountClock BenchClockOf(Mock<ITelescopeMediator> mount) =>
            BenchClocks.GetValue(mount, _ => new ManualMountClock { AutoAdvance = true });

        /// <summary>The bench mount's motors: a MoveAxis command with a non-zero rate starts the axis, and it turns at
        /// that rate on the bench clock until its stop (RA up for a positive rate, Dec toward the equator); the stop
        /// reports where the move ended (<paramref name="stopped"/>). <paramref name="raUpIsNegative"/>: a driver whose
        /// positive rate turns RA down.</summary>
        private static void SimulateMotors(Mock<ITelescopeMediator> mount, Func<Coordinates> read, Action<Coordinates> write,
                Action<Coordinates>? stopped = null, bool raUpIsNegative = false) {
            var clock = BenchClockOf(mount);
            var gate = new object();
            var rates = new double[2];
            var since = clock.GetTimestamp();
            mount.Setup(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.IsAny<double>()))
                .Callback<TelescopeAxes, double>((axis, rate) => {
                    lock (gate) {
                        var seconds = clock.GetElapsedTime(since).TotalSeconds;
                        since = clock.GetTimestamp();
                        var p = read();
                        var ra = (raUpIsNegative ? -rates[0] : rates[0]) * seconds;
                        var dec = rates[1] * seconds;
                        if (ra != 0 || dec != 0) {
                            p = new Coordinates((p.RADegrees + ra + 720.0) % 360.0, p.Dec >= 0 ? p.Dec - dec : p.Dec + dec, Epoch.JNOW, Coordinates.RAType.Degrees);
                            write(p);
                        }
                        var index = axis == TelescopeAxes.Primary ? 0 : 1;
                        var wasMoving = rates[index] != 0;
                        rates[index] = rate;
                        if (rate == 0 && wasMoving) {
                            stopped?.Invoke(p);
                        }
                    }
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
                ExposureSeconds: 0.05, LoopCadenceMs: 20, SettleSeconds: 0, SeedRotationDeg: 30.0, FramesPerPoint: 1));
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
                IPolarAlignMainCameraSolver? mainCamera = null, IPolarAlignerSource? aligner = null, IMountClockGate? mountClock = null) {
            return NewServiceOn(guider, solver, (mount ?? NewMount()).Object, ws, store, log, fetcher, logger, mainCamera, aligner, mountClock);
        }

        /// <summary><see cref="NewService"/> on any mount: a mock's object, or the fake GEM mount (#2064). The service
        /// times its turns on the mount's clock (a bench mock's <see cref="BenchClockOf"/>, the fake GEM's own), and a
        /// solver that follows the mount (<see cref="IMountSky"/>) solves where the mount's motors left it.</summary>
        private static PolarAlignService NewServiceOn(IGuideCapture guider, IPolarAlignFrameSolver solver,
                ITelescopeMediator mount, WsRecorder? ws = null, IProfileStore? store = null,
                IPolarAlignmentLog? log = null, IPolarAlignFrameFetcher? fetcher = null,
                Microsoft.Extensions.Logging.ILogger<PolarAlignService>? logger = null,
                IPolarAlignMainCameraSolver? mainCamera = null, IPolarAlignerSource? aligner = null, IMountClockGate? mountClock = null) {
            ws ??= new WsRecorder();
            store ??= NewStore();
            TimeProvider clock = mount switch {
                FakeGemMount gem => gem.Clock,
                IMocked<ITelescopeMediator> mocked => BenchClockOf(mocked.Mock),
                _ => TimeProvider.System,
            };
            if (mount is IMocked<ITelescopeMediator> mock && mount is ITelescopeService) {
                // A mock set up as the telescope service takes every rate a turn asks for: a test that needs otherwise
                // sets it up itself.
                mock.Mock.As<ITelescopeService>().Setup(s => s.MatchMoveAxisRate(It.IsAny<int>(), It.IsAny<double>())).Returns((int _, double rate) => rate);
            }
            foreach (var sky in new object?[] { solver, mainCamera }.OfType<IMountSky>()) {
                sky.Sky ??= () => {
                    var at = mount.GetCurrentPosition();
                    return (at.RADegrees, at.Dec);
                };
            }
            var svc = new PolarAlignService(guider, logger ?? NullLogger<PolarAlignService>.Instance, solver,
                mount, MediatorMotionControl.For(mount, clock), store, ws, log,
                fetcher ?? new FakeFrameFetcher(), mainCamera: mainCamera, aligner: aligner, mountClock: mountClock) {
                PausedRetryDelay = TimeSpan.FromMilliseconds(20),
                TrackingConfirmTimeout = TimeSpan.FromMilliseconds(200),
                TrackingConfirmPoll = TimeSpan.FromMilliseconds(10),
                AxisSettle = TimeSpan.Zero,
                // #2048: the exit's stop confirmation at bench speed (the daemon waits 2 s at least, 8 s at most).
                StopVerifyPoll = TimeSpan.FromMilliseconds(1),
                StopVerifyStillFor = TimeSpan.Zero,
                StopVerifyNotBefore = TimeSpan.Zero,
                StopResendAfter = TimeSpan.FromMilliseconds(20),
                StopVerifyWindow = TimeSpan.FromMilliseconds(500),
                ShutdownGrace = TimeSpan.FromSeconds(2),
                Clock = clock,
            };
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

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(null, CancellationToken.None));
        }

        [Test]
        public async Task Start_without_a_connected_mount_throws_and_leaves_the_lease_untouched() {
            var fake = NewFake();
            using var svc = NewService(fake, new ScriptedSolver(), NewMount(connected: false));

            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(null, CancellationToken.None));
            Assert.That(fake.LeaseCalls, Is.Empty, "a refused start must not acquire the lease");
        }

        // #2070 — a false zero after a power loss made the routine slew below the pole (RC51, 2026-11-19).
        [Test]
        public async Task Start_is_refused_while_the_mount_position_is_unverified_and_nothing_moves() {
            var fake = NewFake();
            var mount = NewMount();
            mount.As<ITelescopeService>().Setup(t => t.PositionUnverifiedReasonAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(MountPoseTrust.AtHomeOnConnect);
            using var svc = NewService(fake, new ScriptedSolver(), mount);

            // Refused before the camera is taken, with the mount layer's own refusal.
            var refusal = await Assert.ThrowsAsync<OpenAstroAra.Server.Contracts.Refusals.RefusalException>(
                () => svc.StartAsync(null, CancellationToken.None));

            Assert.That(refusal!.Cause, Is.EqualTo(OpenAstroAra.Server.Contracts.Refusals.RefusalCatalog.MountPositionUnverified));
            Assert.That(fake.LeaseCalls, Is.Empty, "a refused start must not acquire the lease");
            mount.Verify(m => m.FindHome(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
            mount.Verify(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()), Times.Never);
            mount.Verify(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.IsAny<double>()), Times.Never);
        }

        [Test]
        public async Task Start_acquires_the_lease_and_publishes_started() {
            var fake = NewFake(answerCaptures: false);
            var ws = new WsRecorder();
            using var svc = NewService(fake, new ScriptedSolver(), ws: ws);

            await svc.StartAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);

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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(stops, Is.EqualTo(1));
            Assert.That(fake.Custody.Holder, Is.EqualTo(CameraUse.PolarAlign));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(fake.Custody.Holder, Is.Null, "Stop gives the camera back");
        }

        [Test]
        public async Task Start_is_idempotent_and_acquires_the_lease_only_once() {
            var fake = NewFake(answerCaptures: false);
            using var svc = NewService(fake, new ScriptedSolver());

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);

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
                svc.StartAsync(null, CancellationToken.None),
                svc.StartAsync(null, CancellationToken.None)).ConfigureAwait(false);

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

            var startTask = svc.StartAsync(null, CancellationToken.None);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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
            // Every end of a run turns tracking off, and a Stop never turns it back on.
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "failed").ConfigureAwait(false);

            solver.Fallback = SolveB;
            solver.Enqueue(SolveA, SolveB, SolveC);
            var mark = Mark(svc);
            var restart = svc.StartAsync(null, CancellationToken.None);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(null, CancellationToken.None));

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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            // Run 1 acks its first capture and parks on the completion wait.
            await fake.WhenCapturedAsync(1).WaitAsync(SignalTimeout).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(fake.IdleWaits, Is.EqualTo(1), "run 1 asked once");

            var mark = Mark(svc);
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(null, CancellationToken.None));

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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("failed"));
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignError, 1).WaitAsync(SignalTimeout).ConfigureAwait(false);
            var error = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("axis_fit_failed"));
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
            // The solves carry the geometry; the bench mount only takes the motor commands.
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"));
            var (alt, az) = PolarAlignGeometry.AxisError(AxisRaDeg, axisDecDeg, latitudeDeg, SiteLonDeg, DateTimeOffset.UtcNow);
            Assert.That(status.CurrentErrorArcmin, Is.EqualTo(Math.Sqrt(alt * alt + az * az)).Within(0.5));
            Assert.That(targets, Has.Count.EqualTo(2), "two RA turns, nothing else");
            Assert.That(gotos, Is.Zero, "the turns are motor moves, never a GoTo the firmware could route over the pier");
            static double Turned(double fromDeg, double toDeg) => Math.Abs(((toDeg - fromDeg + 540.0) % 360.0) - 180.0);
            // A timed turn waits whole milliseconds: 10° at 3°/s is 3.334 s of motor, 10.002°.
            Assert.That(Turned(0.0, targets[0].RADegrees), Is.EqualTo(10.0).Within(0.005), "the first turn is the 10° step");
            Assert.That(Turned(targets[0].RADegrees, targets[1].RADegrees), Is.EqualTo(10.0).Within(0.005), "so is the second");
            Assert.That(targets.Select(t => t.Dec), Is.All.EqualTo(Math.CopySign(85.0, latitudeDeg)).Within(1e-6), "the turns keep the Dec");
        }

        // The turn mismatch is the clutch-slip diagnostic: on a perfect mount it is zero. Between solves a tracking
        // tube also turns with the Earth, which the turn the motor was told does not show; 30 s apart that is 0.125°.
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
            var abortEpoch = 7L;
            var afterAbort = new ConcurrentQueue<(double Rate, long? Since)>();
            var service = mount.As<ITelescopeService>();
            service.SetupGet(s => s.AbortEpoch).Returns(() => Interlocked.Read(ref abortEpoch));
            service.Setup(s => s.ReadNowAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => FreshReading(mount, 0.0, 85.0));
            var moves = 0;
            service.Setup(s => s.MoveAxisAsync(It.IsAny<int>(), It.IsAny<double>(), It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
                .Callback<int, double, int?, long?, CancellationToken>((_, rate, _, since, _) => {
                    if (Interlocked.Read(ref abortEpoch) != 7L) {
                        afterAbort.Enqueue((rate, since));
                        return;
                    }
                    if (rate != 0 && ++moves == 5) {
                        Interlocked.Increment(ref abortEpoch); // the user taps Stop Mount mid-turn: the start and four renewals in
                    }
                })
                .Returns(Task.CompletedTask);
            var ws = new WsRecorder();
            using var svc = NewService(NewFake(), new ScriptedSolver(), mount, ws);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("failed"));
            var error = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo(PolarAlignService.SlewAbortedExternally));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("Stop Mount"));
            Assert.That(afterAbort.Where(c => c.Rate != 0), Is.Empty, "no motor command after the abort");
            Assert.That(afterAbort.Count(c => c.Rate == 0), Is.GreaterThanOrEqualTo(1), "the routine's own stop still goes out");
            Assert.That(moves, Is.EqualTo(5), "the 30° turn (ten seconds of motor) ended where the abort caught it");
        }

        // The routine hands the telescope service the abort count it started under, so the service itself can refuse a
        // renewal that races the abort.
        [Test]
        public async Task Each_motor_command_of_a_turn_carries_the_abort_count_it_started_under() {
            var mount = NewMount();
            var cached = new Coordinates(0.0, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees);
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => cached);
            var service = mount.As<ITelescopeService>();
            service.SetupGet(s => s.AbortEpoch).Returns(42L);
            service.Setup(s => s.ReadNowAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => FreshReading(mount, 0.0, 85.0));
            var guards = new ConcurrentQueue<(double Rate, long? Since, int? LeaseMs)>();
            service.Setup(s => s.MoveAxisAsync(It.IsAny<int>(), It.IsAny<double>(), It.IsAny<int?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
                .Callback<int, double, int?, long?, CancellationToken>((_, rate, lease, since, _) => guards.Enqueue((rate, since, lease)))
                .Returns(Task.CompletedTask);
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            using var svc = NewService(NewFake(), solver, mount);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"));
            Assert.That(guards.Where(g => g.Rate != 0).Select(g => g.Since), Is.All.EqualTo(42L));
            Assert.That(guards.Where(g => g.Rate == 0).Select(g => g.Since), Is.All.Null, "a stop is never refused");
            Assert.That(guards.Where(g => g.Rate != 0).Select(g => g.LeaseMs), Is.All.EqualTo(3000), "every motor command carries the lease");
            mount.Verify(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.IsAny<double>()), Times.Never, "never the mediator's unleased call");
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
                        running.TrySetResult();
                    } else {
                        stopped = true;
                    }
                });
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            using var svc = NewService(NewFake(), solver, mount);
            BenchClockOf(mount).AutoAdvance = false; // the bench clock stands still: the turn's time never runs out

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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
            var ws = new WsRecorder();
            using var svc = NewService(fake, new ScriptedSolver(), mount, ws: ws); // each solve reads where the mount points

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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
        /// call log records what tracking was at each slew. <paramref name="dropAtSolve"/> drops it
        /// as that solve reads the sky (the first: between the measurement's switch-on and its first turn), as a §42.3
        /// mount reconnect did on the CM4 on 2026-11-05.</summary>
        private static (Mock<ITelescopeMediator> Mount, CallLog Calls) TrackingMount(
                bool tracking, Func<bool, bool>? onSet = null, int dropAtSolve = 0,
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
                if (++reads == dropAtSolve) {
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
            using var svc = NewService(fake, new ScriptedSolver(), mount); // each solve reads where the mount points

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

        private static async Task<(PolarAlignService Svc, RecordingLog Log)> AdjustingServiceAsync() {
            var fake = NewFake();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            var log = new RecordingLog();
            var svc = NewService(fake, solver, log: log);
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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
            var (mount, calls) = TrackingMount(tracking: true, dropAtSolve: 1);

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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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
            var (mount, calls) = TrackingMount(tracking: true, onSet: _ => false, dropAtSolve: 1);

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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await ws.WhenCountAsync(WsEventCatalog.PolarAlignProgress, 2).WaitAsync(SignalTimeout).ConfigureAwait(false);
            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);
            await WaitForChainStateAsync(ws, "checking").ConfigureAwait(false); // Done's row is written before the verify stage is announced
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var ids = logger.Entries.Select(e => e.EventId).ToList();
            Assert.That(ids.Count(i => i == 4527), Is.EqualTo(3), "all three seed solves");
            Assert.That(ids, Does.Contain(4528).And.Contain(4529).And.Contain(4530));
            Assert.That(logger.Entries.Single(e => e.EventId == 4528).Message, Does.Contain("axis RA").And.Contain("seed error"));
        }

        [Test]
        public async Task Stop_logs_an_aborted_row_and_a_second_stop_does_not_duplicate_it() {
            var fake = NewFake();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB, SolveC);
            var log = new RecordingLog();
            using var svc = NewService(fake, solver, log: log);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).LastResult, Is.Null);
        }

        [Test]
        public async Task A_failed_routine_logs_a_failed_row_and_stop_does_not_add_another() {
            var fake = NewFake();
            var solver = new ScriptedSolver { Fallback = Unsolved };
            var log = new RecordingLog();
            using var svc = NewService(fake, solver, log: log);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
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
            Assert.That(defaults.CoarseExposureSeconds, Is.EqualTo(3.0));
            Assert.That(defaults.PolarAlignerAuto, Is.True, "#1971: a rig with a Polar Aligner uses it unless told not to");
            Assert.That(defaults.LoopCadenceMs, Is.EqualTo(1000));
            Assert.That(defaults.SettleSeconds, Is.EqualTo(2.0));
            Assert.That(defaults.Camera, Is.EqualTo(PolarAlignCameras.Guide), "#2082: the guide camera measures unless told otherwise");
            Assert.That(defaults.FramesPerPoint, Is.EqualTo(3), "#2082: three frames averaged at each point");

            store.PutPolarAlignSettings(defaults with { ExposureSeconds = 0.5, Binning = 2 });
            var updated = store.GetPolarAlignSettings();
            Assert.That(updated.ExposureSeconds, Is.EqualTo(0.5));
            Assert.That(updated.Binning, Is.EqualTo(2));
        }

        // ── one measurement per Start: fine or check, on the guide camera or the main one ────

        /// <summary>A guide-camera solver that answers where the bench mount's motors left it: the mount's RA, at
        /// <see cref="DecDeg"/> (Dec 85°: a camera 5° off the axis of a mount whose axis sits on the true pole). It records
        /// the hint each frame came with.</summary>
        private sealed class MountFollowingSolver : IPolarAlignFrameSolver, IMountSky {
            public Func<(double RaDeg, double DecDeg)>? Sky { get; set; }
            public double DecDeg { get; init; } = 85.0;
            public ConcurrentQueue<(double? Ra, double? Dec)> Hints { get; } = new();
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) {
                Hints.Enqueue((hintRa, hintDec));
                return Task.FromResult(Sky?.Invoke() is { } sky ? new PolarAlignSolveOutcome(true, sky.RaDeg, DecDeg) : SolveA);
            }
        }

        /// <summary>The main camera's capture-and-solve: a camera <see cref="OffsetDeg"/> off the RA axis on a
        /// mount whose axis sits on the true pole. It answers at the bench mount's RA, <see cref="OffsetDeg"/> further
        /// from the pole than the Dec-85° circle the mount points on — so every frame lies on one circle about
        /// the pole and only a measured axis (not the pointing read as the axis) comes out error-free. A fixed
        /// <see cref="Answer"/> overrides that.</summary>
        private sealed class FakeMainCamera : IPolarAlignMainCameraSolver, IMountSky {
            public Func<(double RaDeg, double DecDeg)>? Sky { get; set; }
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
                return Task.FromResult(new PolarAlignSolveOutcome(true, Sky?.Invoke().RaDeg ?? 0.0, PoleMountDecDeg - OffsetDeg));
            }
        }

        /// <summary>A mount the user pointed near the pole (RA 0, Dec 85: 5° off it, as #2082 asks), counterweights down. It
        /// can find Home and slew, so a test sees that the routine does neither (#2048): <paramref name="motorStops"/>
        /// records where each motor move ended, and <see cref="NoHomeNoGoTo"/> checks the rest.</summary>
        private static Mock<ITelescopeMediator> NewPoleMount(ConcurrentQueue<Coordinates>? motorStops = null) {
            var mount = new Mock<ITelescopeMediator>();
            var tracking = true;
            mount.Setup(m => m.GetInfo()).Returns(() => new TelescopeInfo {
                // RA 0 at LST 20 h is HA −4 h, the west side by the hour-angle model. A mount that can find Home, and says it
                // is there (the bench does not model leaving it).
                Connected = true, SiderealTime = 20.0, TrackingEnabled = tracking, CanFindHome = true, AtHome = true, CanSlew = true, SideOfPier = PierSide.pierWest,
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

        /// <summary>Where <see cref="NewPoleMount"/> points: 5° from the pole, as a camera 5° off the RA axis sees the sky
        /// from Home.</summary>
        private const double PoleMountDecDeg = 85.0;

        /// <summary>The routine never sends the mount Home, never slews it and never unparks it.</summary>
        private static void NoHomeNoGoTo(Mock<ITelescopeMediator> mount) {
            mount.Verify(m => m.FindHome(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never, "no Find Home");
            mount.Verify(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()), Times.Never, "no GoTo");
            mount.Verify(m => m.UnparkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()), Times.Never, "no unpark");
        }

        /// <summary>The bench store with the profile's <c>polar_align.camera</c> set to the main camera.</summary>
        private static InMemoryProfileStore MainCameraStore() {
            var store = NewStore();
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { Camera = PolarAlignCameras.Main });
            return store;
        }

        [Test]
        public async Task A_main_camera_start_is_refused_without_a_main_camera_solver() {
            var fake = NewFake();
            using var svc = NewService(fake, new ScriptedSolver(), store: MainCameraStore());

            await AssertThrowsAsync<InvalidOperationException>(() => svc.StartAsync(null, CancellationToken.None)).ConfigureAwait(false);
            Assert.That(fake.LeaseCalls, Is.Empty, "a refused start takes no lease");
        }

        [Test]
        public async Task A_main_camera_measurement_seeds_with_the_blind_fallback_and_runs_its_live_loop_without_it() {
            var fake = NewFake();
            var stops = new ConcurrentQueue<Coordinates>();
            var mount = NewPoleMount(stops);
            // #1727 — a main camera 1.25° off the RA axis on a mount whose axis IS the pole. Read as the
            // axis that camera reports a 75′ error that is not there.
            var mainCamera = new FakeMainCamera { OffsetDeg = 1.25 };
            var ws = new WsRecorder();
            using var svc = NewService(fake, new MountFollowingSolver(), mount, ws, store: MainCameraStore(), mainCamera: mainCamera);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var started = await WaitForEventAsync(ws, WsEventCatalog.PolarAlignStarted).ConfigureAwait(false);
            Assert.That((started.GetProperty("state").GetString(), started.GetProperty("camera").GetString()), Is.EqualTo(("seeding", PolarAlignCameras.Main)));
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);

            Assert.That((status.State, status.Stage, status.Camera), Is.EqualTo(("adjusting", PolarAlignStages.Align, PolarAlignCameras.Main)));
            Assert.That(mainCamera.Calls, Is.GreaterThanOrEqualTo(4), "three seed frames and at least one live frame");
            Assert.That(mainCamera.Exposures.TryPeek(out var exposure) && exposure == 3.0, Is.True, "the main-camera exposure, not the guide exposure");
            var blind = mainCamera.BlindFailovers.ToArray();
            Assert.That(blind.Take(3), Is.All.True, "the seed frames may need the blind fallback");
            Assert.That(blind.Skip(3), Is.All.False, "the live loop takes one fast look per frame");
            // The axis is measured from the seed turns, so the camera's offset cancels: only the ~1′
            // refraction term is left — never the 75′ the pointing-as-axis reading gave.
            Assert.That(status.CurrentErrorArcmin, Is.LessThan(3.0));
            Assert.That(stops.Select(c => c.RADegrees).ToArray(), Is.EqualTo(SeedTurns).Within(1e-6), "both 30° turns east: east_direction unset, east in the north");
            Assert.That(stops.Select(c => c.Dec), Is.All.EqualTo(PoleMountDecDeg).Within(1e-6), "no Dec motion");
            mount.Verify(m => m.SetTrackingEnabled(false), Times.AtLeastOnce, "the pointing must move only with the knobs");
            Assert.That(fake.Captures, Is.Empty, "the guide camera is never asked for a frame");

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            NoHomeNoGoTo(mount);
        }

        [Test]
        public async Task A_main_camera_start_is_refused_when_the_main_camera_is_not_connected() {
            var fake = NewFake();
            var mount = NewPoleMount();
            var mainCamera = new FakeMainCamera { Unavailable = "the main camera is not connected" };
            var store = MainCameraStore();
            using var svc = NewService(fake, new MountFollowingSolver(), mount, mainCamera: mainCamera, store: store);

            // Refused before the mount moves: no lease, no motor, no frame.
            await AssertThrowsAsync<InvalidOperationException>(() => svc.StartAsync(null, CancellationToken.None)).ConfigureAwait(false);
            Assert.That(fake.LeaseCalls, Is.Empty);
            mount.Verify(m => m.MoveAxis(It.IsAny<TelescopeAxes>(), It.Is<double>(r => r != 0)), Times.Never);
            Assert.That(mainCamera.Calls, Is.Zero);

            // A guide-camera measurement needs no main camera and still starts (the refused start gave the mount back).
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { Camera = PolarAlignCameras.Guide });
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That((await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false)).State, Is.EqualTo("adjusting"));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        private static readonly double[] SeedTurns = { 30.0, 60.0 };

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