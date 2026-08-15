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
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Guider;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §45 (polar-align engine) — the full <see cref="PolarAlignService"/> state machine driven against
    /// the bench's <see cref="FakeGuider"/> (capture RPC + <c>SingleFrameComplete</c> event + PA-session
    /// lease), a scripted <see cref="IPolarAlignFrameSolver"/>, and a mocked mount. Covers the seed
    /// (frame A → RA slew → frame B → axis fit), the live-adjust loop (tracking off, errors populated,
    /// progress events), the §45.11 pause-after-5-failed-solves + auto-resume, seed-failure → failed +
    /// error event, and hand-back (tracking restored, lease released) on Stop and on failure.
    /// </summary>
    [TestFixture]
    [Category("bench")]
    public class PolarAlignServiceTest {

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
        private static readonly PolarAlignSolveOutcome Unsolved = new(false, 0, 0);

        private sealed class ScriptedSolver : IPolarAlignFrameSolver {
            private readonly ConcurrentQueue<PolarAlignSolveOutcome> _script = new();
            public PolarAlignSolveOutcome Fallback { get; set; } = SolveB;
            public void Enqueue(params PolarAlignSolveOutcome[] outcomes) {
                foreach (var o in outcomes) {
                    _script.Enqueue(o);
                }
            }
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, double? hintRa, double? hintDec, CancellationToken ct)
                => Task.FromResult(_script.TryDequeue(out var o) ? o : Fallback);
        }

        private sealed class WsRecorder : IWsBroadcaster {
            public ConcurrentQueue<(string Type, JsonElement Payload)> Events { get; } = new();
            public long CurrentSequence => 0;
            public Task PublishAsync(string eventType, JsonElement payload, CancellationToken ct) {
                Events.Enqueue((eventType, payload.Clone()));
                return Task.CompletedTask;
            }
            public int Count(string type) => Events.Count(e => e.Type == type);
        }

        private static GuiderRecoveryCoordinator NewRecovery() =>
            new(Mock.Of<IGuiderProcessSupervisor>(),
                Mock.Of<INotificationService>(),
                Mock.Of<IDiagnosticsService>(),
                NullLogger<GuiderRecoveryCoordinator>.Instance);

        /// <summary>A FakeGuider that answers the connect handshake, records PA-session lease calls,
        /// and (when <paramref name="answerCaptures"/>) completes every <c>capture_single_frame</c>
        /// with a successful <c>SingleFrameComplete</c> event carrying the requested path.</summary>
        // The path each capture_single_frame request carried (null = daemon-side default
        // save, the §45 capture-fetch contract). Asserted from TEST bodies — never inside
        // the fake's handler, which runs on a socket thread with no NUnit context.
        private static readonly ConcurrentQueue<string?> CaptureRequestPaths = new();

        private static FakeGuider StartFake(ConcurrentQueue<bool> paSessionActiveCalls, bool answerCaptures = true) {
            var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_pixel_scale", JsonValue.Create(1.5));
            fake.OnRpc("set_pa_session", req => {
                var active = req["params"]?["active"]?.GetValue<bool>() ?? false;
                paSessionActiveCalls.Enqueue(active);
                return new JsonObject { ["active"] = active, ["expires_in_s"] = active ? 600 : null };
            });
            if (answerCaptures) {
                fake.OnRpc("capture_single_frame", req => {
                    // §45 capture-fetch: the request must carry NO path (daemon-side default
                    // save) — recorded here, asserted from test bodies.
                    CaptureRequestPaths.Enqueue(req["params"]?["path"]?.GetValue<string>());
                    _ = Task.Run(async () => {
                        await Task.Delay(20).ConfigureAwait(false);
                        await fake.BroadcastAsync(new JsonObject {
                            ["Event"] = "SingleFrameComplete", ["Timestamp"] = 0.0, ["Host"] = "g",
                            ["Inst"] = 1, ["Success"] = true,
                            ["Path"] = "/var/lib/openastro-guider/.openastro-guider/save_image_test",
                            ["Filename"] = "save_image_test",
                        }).ConfigureAwait(false);
                    });
                    return JsonValue.Create(0);
                });
            } else {
                // Ack but never complete — the routine parks on the capture-complete wait, which
                // keeps lifecycle tests free of solver/loop side-effects.
                fake.OnRpc("capture_single_frame", JsonValue.Create(0));
            }
            return fake;
        }

        private static Mock<ITelescopeMediator> NewMount(bool connected = true, bool tracking = true) {
            var mount = new Mock<ITelescopeMediator>();
            mount.Setup(m => m.GetInfo()).Returns(new TelescopeInfo {
                Connected = connected, SiderealTime = 0.0, TrackingEnabled = tracking,
            });
            mount.Setup(m => m.GetCurrentPosition())
                .Returns(new Coordinates(0.0, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees));
            mount.Setup(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            mount.Setup(m => m.WaitForSlew(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            mount.Setup(m => m.SetTrackingEnabled(It.IsAny<bool>())).Returns(true);
            return mount;
        }

        private sealed class RecordingLog : IPolarAlignmentLog {
            public ConcurrentQueue<PolarAlignmentRecord> Rows { get; } = new();
            public Task InsertAsync(PolarAlignmentRecord record, CancellationToken ct) {
                Rows.Enqueue(record);
                return Task.CompletedTask;
            }
        }

        private static InMemoryProfileStore NewStore() {
            var store = new InMemoryProfileStore();
            store.PutSiteSettings(store.GetSiteSettings() with { LatitudeDeg = SiteLatDeg, LongitudeDeg = SiteLonDeg });
            // Bench cadences: the real §45.12 defaults would make loop tests take minutes.
            store.PutPolarAlignSettings(new PolarAlignSettingsDto(
                ExposureSeconds: 0.05, LoopCadenceMs: 20, SettleSeconds: 0));
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
        }

        private static PolarAlignService NewService(GuiderService guider, IPolarAlignFrameSolver solver,
                Mock<ITelescopeMediator>? mount = null, IWsBroadcaster? ws = null, IProfileStore? store = null,
                IPolarAlignmentLog? log = null, IPolarAlignFrameFetcher? fetcher = null) {
            var svc = new PolarAlignService(guider, NullLogger<PolarAlignService>.Instance, solver,
                (mount ?? NewMount()).Object, store ?? NewStore(), ws, log,
                fetcher ?? new FakeFrameFetcher()) {
                PausedRetryDelay = TimeSpan.FromMilliseconds(20),
            };
            return svc;
        }

        private static async Task<PolarAlignStateDto> PollStateAsync(PolarAlignService svc, params string[] anyOf) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            PolarAlignStateDto status = await svc.GetStatusAsync(cts.Token).ConfigureAwait(false);
            while (!anyOf.Contains(status.State)) {
                await Task.Delay(50, cts.Token).ConfigureAwait(false);
                status = await svc.GetStatusAsync(cts.Token).ConfigureAwait(false);
            }
            return status;
        }

        // ── lifecycle ────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Status_is_idle_before_start() {
            using var guider = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            using var svc = NewService(guider, new ScriptedSolver());

            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("idle"));
            Assert.That(status.FramesCaptured, Is.EqualTo(0));
            Assert.That(status.CurrentErrorArcmin, Is.Null);
        }

        [Test]
        public void Start_without_a_connected_guider_throws() {
            using var guider = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            using var svc = NewService(guider, new ScriptedSolver());

            Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(null, CancellationToken.None));
        }

        [Test]
        public async Task Start_without_a_connected_mount_throws_and_leaves_the_lease_untouched() {
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFake(paCalls);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            using var svc = NewService(guider, new ScriptedSolver(), NewMount(connected: false));

            Assert.ThrowsAsync<InvalidOperationException>(() => svc.StartAsync(null, CancellationToken.None));
            Assert.That(paCalls, Is.Empty, "a failed preflight must not acquire the lease");
        }

        [Test]
        public async Task Start_acquires_the_lease_and_publishes_started() {
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFake(paCalls, answerCaptures: false);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var ws = new WsRecorder();
            using var svc = NewService(guider, new ScriptedSolver(), ws: ws);

            await svc.StartAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(paCalls.TryDequeue(out var active), Is.True, "Start should send set_pa_session");
            Assert.That(active, Is.True);
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("seeding"), "an active routine begins in the seeding state");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStarted), Is.EqualTo(1));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task Start_is_idempotent_and_acquires_the_lease_only_once() {
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFake(paCalls, answerCaptures: false);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            using var svc = NewService(guider, new ScriptedSolver());

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(paCalls.Count, Is.EqualTo(1), "a second Start on an already-active routine is a no-op accept");
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task Stop_without_a_connected_guider_still_succeeds_and_reports_stopped() {
            using var guider = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            using var svc = NewService(guider, new ScriptedSolver());

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("stopped"));
        }

        [Test]
        public async Task Concurrent_starts_acquire_the_lease_and_publish_only_once() {
            // The endpoint calls straight into the singleton with no request serialization, so two
            // near-simultaneous Starts race. _opLock serializes them: exactly one acquires the lease +
            // publishes, the other then sees _active and is a no-op accept (guards the double-acquire).
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFake(paCalls, answerCaptures: false);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var ws = new WsRecorder();
            using var svc = NewService(guider, new ScriptedSolver(), ws: ws);

            await Task.WhenAll(
                svc.StartAsync(null, CancellationToken.None),
                svc.StartAsync(null, CancellationToken.None)).ConfigureAwait(false);

            var acquisitions = 0;
            while (paCalls.TryDequeue(out var active)) {
                if (active) {
                    acquisitions++;
                }
            }
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
            var paCalls = new ConcurrentQueue<bool>();
            using var startEnteredRpc = new ManualResetEventSlim(false);
            using var releaseStartRpc = new ManualResetEventSlim(false);
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_pixel_scale", JsonValue.Create(1.5));
            fake.OnRpc("capture_single_frame", JsonValue.Create(0)); // ack, never complete
            fake.OnRpc("set_pa_session", req => {
                var active = req["params"]?["active"]?.GetValue<bool>() ?? false;
                if (active) {
                    startEnteredRpc.Set();
                    releaseStartRpc.Wait(TimeSpan.FromSeconds(10));
                }
                paCalls.Enqueue(active);
                return new JsonObject { ["active"] = active };
            });
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            using var svc = NewService(guider, new ScriptedSolver());

            var startTask = svc.StartAsync(null, CancellationToken.None);
            Assert.That(startEnteredRpc.Wait(TimeSpan.FromSeconds(10)), Is.True, "Start should reach its lease RPC");

            // Start now holds _opLock inside the (blocked) lease RPC; a Stop must wait on the semaphore.
            var stopTask = svc.StopAsync(null, CancellationToken.None);
            await Task.Delay(200).ConfigureAwait(false);
            Assert.That(stopTask.IsCompleted, Is.False, "Stop must wait for the in-flight Start (serialized by _opLock)");

            releaseStartRpc.Set();
            await Task.WhenAll(startTask, stopTask).ConfigureAwait(false);

            Assert.That(paCalls.ToArray(), Is.EqualTo(AcquireThenRelease),
                "the lease RPCs must run in call order — acquire then release — not race on the wire");
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("stopped"), "final state matches the last op, with the lease released");
        }

        // ── the engine ───────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Happy_path_seeds_then_adjusts_with_populated_errors_and_progress_events() {
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFake(paCalls);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB); // seed A, seed B; the live loop re-solves B (Fallback)
            var mount = NewMount(tracking: true);
            var ws = new WsRecorder();
            using var svc = NewService(guider, solver, mount, ws);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("adjusting"));
            // Axis fitted at the true pole → alt error = −refraction at the pole altitude (~1.0′ at
            // lat 45°), az error ~0. The live loop re-solving the same pointing must not change it.
            Assert.That(status.AltitudeAdjustmentArcmin, Is.Not.Null.And.EqualTo(-1.0).Within(0.2));
            Assert.That(status.AzimuthAdjustmentArcmin, Is.Not.Null.And.EqualTo(0.0).Within(0.2));
            Assert.That(status.CurrentErrorArcmin, Is.Not.Null.And.EqualTo(1.0).Within(0.2));
            Assert.That(status.FramesCaptured, Is.GreaterThanOrEqualTo(2), "both seed frames captured");

            mount.Verify(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()),
                Times.Once, "the seed performs exactly one RA slew");
            mount.Verify(m => m.SetTrackingEnabled(false), Times.Once, "the adjust loop stops tracking");

            // Progress events stream with the live payload shape the client renders.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (ws.Count(WsEventCatalog.PolarAlignProgress) < 2) {
                await Task.Delay(25, cts.Token).ConfigureAwait(false);
            }
            var progress = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignProgress).Payload;
            Assert.That(progress.GetProperty("altitude_error_arcmin").GetDouble(), Is.EqualTo(-1.0).Within(0.2));
            Assert.That(progress.GetProperty("zone").GetString(), Is.EqualTo("green"));

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            mount.Verify(m => m.SetTrackingEnabled(true), Times.Once, "Stop restores the pre-routine tracking state");
            Assert.That(paCalls.ToArray().Last(), Is.False, "Stop releases the lease");
        }

        [Test]
        public async Task Five_failed_solves_pause_the_loop_and_a_good_solve_resumes_it() {
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFake(paCalls);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var solver = new ScriptedSolver();
            var failures = Enumerable.Repeat(Unsolved, PolarAlignService.MaxConsecutiveSolveFailures).ToArray();
            solver.Enqueue(new[] { SolveA, SolveB }.Concat(failures).ToArray()); // then Fallback succeeds
            var ws = new WsRecorder();
            using var svc = NewService(guider, solver, ws: ws);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var paused = await PollStateAsync(svc, "paused", "failed").ConfigureAwait(false);
            Assert.That(paused.State, Is.EqualTo("paused"), "5 consecutive failed solves park the loop in paused");

            var resumed = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            Assert.That(resumed.State, Is.EqualTo("adjusting"), "the next good solve resumes the loop");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignPaused), Is.EqualTo(1), "paused is published once per streak");

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task Seed_solve_failure_fails_the_routine_and_releases_the_lease() {
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFake(paCalls);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var solver = new ScriptedSolver { Fallback = Unsolved };
            var ws = new WsRecorder();
            using var svc = NewService(guider, solver, ws: ws);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("failed"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (ws.Count(WsEventCatalog.PolarAlignError) < 1) {
                await Task.Delay(25, cts.Token).ConfigureAwait(false);
            }
            var error = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("seed_solve_failed"));
            while (!paCalls.Contains(false)) {
                await Task.Delay(25, cts.Token).ConfigureAwait(false);
            }
            Assert.That(paCalls.ToArray().Last(), Is.False, "a failed routine releases the lease itself");
        }

        [Test]
        public async Task A_restart_after_a_self_terminated_failure_waits_for_the_old_lease_release() {
            // FailRoutineAsync flips _active synchronously but releases the lease from the run task
            // afterwards. A fast re-Start must drain that run first, or its acquire could race the
            // old release on the wire and end up leaseless. We block the release RPC, fire Start,
            // prove it waits, then release and assert the wire order acquire→release→acquire.
            var paCalls = new ConcurrentQueue<bool>();
            using var releaseGate = new ManualResetEventSlim(false);
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_pixel_scale", JsonValue.Create(1.5));
            fake.OnRpc("set_pa_session", req => {
                var active = req["params"]?["active"]?.GetValue<bool>() ?? false;
                if (!active) {
                    releaseGate.Wait(TimeSpan.FromSeconds(10));
                }
                paCalls.Enqueue(active);
                return new JsonObject { ["active"] = active, ["expires_in_s"] = active ? 600 : null };
            });
            fake.OnRpc("capture_single_frame", req => {
                _ = Task.Run(async () => {
                    await Task.Delay(20).ConfigureAwait(false);
                    await fake.BroadcastAsync(new JsonObject {
                        ["Event"] = "SingleFrameComplete", ["Timestamp"] = 0.0, ["Host"] = "g",
                        ["Inst"] = 1, ["Success"] = true,
                        ["Path"] = "/var/lib/openastro-guider/.openastro-guider/save_image_test",
                        ["Filename"] = "save_image_test",
                    }).ConfigureAwait(false);
                });
                return JsonValue.Create(0);
            });
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var solver = new ScriptedSolver { Fallback = Unsolved }; // seed fails → FailRoutineAsync
            using var svc = NewService(guider, solver);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "failed").ConfigureAwait(false);

            solver.Fallback = SolveB;
            solver.Enqueue(SolveA, SolveB);
            var restart = svc.StartAsync(null, CancellationToken.None);
            await Task.Delay(200).ConfigureAwait(false);
            Assert.That(restart.IsCompleted, Is.False,
                "the re-Start must wait for the failed run's in-flight lease release");

            releaseGate.Set();
            await restart.ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            Assert.That(paCalls.ToArray(), Is.EqualTo(AcquireReleaseAcquire),
                "wire order must be acquire → release → acquire, never interleaved");
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task A_transient_capture_rpc_fault_counts_as_one_failed_solve_not_an_abort() {
            // The first two capture_single_frame RPCs are rejected daemon-side (→ GuiderRpcException
            // client-side); the seed's 3-attempt retry loop must absorb them and the routine must
            // still reach adjusting — not hard-fail with internal_error.
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFake(paCalls);
            var captureAttempts = 0;
            fake.OnRpc("capture_single_frame", req => {
                if (Interlocked.Increment(ref captureAttempts) <= 2) {
                    throw new InvalidOperationException("camera busy"); // → JSON-RPC error → GuiderRpcException
                }
                _ = Task.Run(async () => {
                    await Task.Delay(20).ConfigureAwait(false);
                    await fake.BroadcastAsync(new JsonObject {
                        ["Event"] = "SingleFrameComplete", ["Timestamp"] = 0.0, ["Host"] = "g",
                        ["Inst"] = 1, ["Success"] = true,
                        ["Path"] = "/var/lib/openastro-guider/.openastro-guider/save_image_test",
                        ["Filename"] = "save_image_test",
                    }).ConfigureAwait(false);
                });
                return JsonValue.Create(0);
            });
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB);
            using var svc = NewService(guider, solver);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("adjusting"),
                "transient capture RPC faults must be absorbed by the seed retry loop");
            Assert.That(Volatile.Read(ref captureAttempts), Is.GreaterThan(2));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task Every_capture_rejected_fails_the_routine_with_capture_rejected_not_seed_solve_failed() {
            // §45 capture-fetch: when the daemon refuses EVERY capture (the pre-guider#77 sandbox
            // rejection, a busy camera that never frees) no frame ever reaches the solver — the
            // routine must say capture_rejected with the daemon's message, not blame focus/sky.
            var paCalls = new ConcurrentQueue<bool>();
            var ws = new WsRecorder();
            await using var fake = StartFake(paCalls, answerCaptures: false);
            fake.OnRpc("capture_single_frame",
                _ => throw new InvalidOperationException("path must be in an existing directory"));
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            using var svc = NewService(guider, new ScriptedSolver { Fallback = SolveA }, ws: ws);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "failed").ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("failed"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (ws.Count(WsEventCatalog.PolarAlignError) < 1) {
                await Task.Delay(25, cts.Token).ConfigureAwait(false);
            }
            var error = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("capture_rejected"));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("existing directory"),
                "the daemon's own rejection text must reach the user");
        }

        [Test]
        public async Task Frames_are_fetched_from_the_daemon_http_capture_endpoint() {
            // §45 capture-fetch: the saved frame is retrieved over the guider#77 HTTP endpoint —
            // host = the dialed guider, port = rpc + HttpPortOffsetFromRpc, path = the reported
            // filename. The bench fetcher records the URIs the engine built.
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFake(paCalls);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var fetcher = new FakeFrameFetcher();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB);
            using var svc = NewService(guider, solver, fetcher: fetcher);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(fetcher.Fetched, Is.Not.Empty, "the engine must download the daemon-saved frame");
            var fetched = fetcher.Fetched.First();
            Assert.That(fetched.RpcPort, Is.EqualTo(fake.Port),
                "the fetch is addressed to the DIALED guider endpoint (HTTP port derives from it)");
            Assert.That(fetched.Filename, Is.EqualTo("save_image_test"));
            Assert.That(CaptureRequestPaths, Is.All.Null,
                "captures must not pass a foreign path — the daemon sandbox rejects it");
            // The real HTTP derivation, pinned with REALISTIC daemon ports (instance 1 and 2).
            Assert.That(PHD2Guider.CaptureFrameUri("rc91.lan", 4400, "f.fits").ToString(),
                Is.EqualTo("http://rc91.lan:8080/api/capture/f.fits"));
            Assert.That(PHD2Guider.CaptureFrameUri("rc91.lan", 4401, "f.fits").Port, Is.EqualTo(8081));
        }

        [Test]
        public async Task A_stale_completion_from_a_timed_out_capture_is_swallowed_not_adopted() {
            // Review r1 on the capture-fetch PR: with path correlation gone, the event a TIMED-OUT
            // capture still owes must not resolve the NEXT capture's wait — adopting it would
            // silently solve the previous (seconds-old) frame. The first capture acks and never
            // completes (times out); from then on every capture first delivers the STALE completion
            // (filename save_image_stale) and then its own fresh one. The engine must fetch ONLY
            // fresh frames.
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFake(paCalls, answerCaptures: false);
            var captureCount = 0;
            fake.OnRpc("capture_single_frame", req => {
                var n = Interlocked.Increment(ref captureCount);
                if (n == 1) {
                    return JsonValue.Create(0); // ack, never complete → the wait times out
                }
                _ = Task.Run(async () => {
                    await Task.Delay(20).ConfigureAwait(false);
                    if (n == 2) {
                        // Capture 1's debt, delivered late — exactly once.
                        await fake.BroadcastAsync(new JsonObject {
                            ["Event"] = "SingleFrameComplete", ["Timestamp"] = 0.0, ["Host"] = "g",
                            ["Inst"] = 1, ["Success"] = true,
                            ["Path"] = "/var/lib/openastro-guider/.openastro-guider/save_image_stale",
                            ["Filename"] = "save_image_stale",
                        }).ConfigureAwait(false);
                        await Task.Delay(30).ConfigureAwait(false);
                    }
                    // This capture's own completion.
                    await fake.BroadcastAsync(new JsonObject {
                        ["Event"] = "SingleFrameComplete", ["Timestamp"] = 0.0, ["Host"] = "g",
                        ["Inst"] = 1, ["Success"] = true,
                        ["Path"] = "/var/lib/openastro-guider/.openastro-guider/save_image_fresh",
                        ["Filename"] = "save_image_fresh",
                    }).ConfigureAwait(false);
                });
                return JsonValue.Create(0);
            });
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var fetcher = new FakeFrameFetcher();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB);
            using var svc = NewService(guider, solver, fetcher: fetcher);
            svc.CaptureCompleteTimeout = TimeSpan.FromMilliseconds(300); // fast first-capture timeout

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("adjusting"),
                "the seed retry loop absorbs the timed-out first capture");
            Assert.That(fetcher.Fetched, Is.Not.Empty);
            Assert.That(fetcher.Fetched.Select(f => f.Filename), Is.All.EqualTo("save_image_fresh"),
                "the stale event owed by the timed-out capture must be swallowed, never fetched");
        }

        [Test]
        public async Task A_capture_abandoned_by_stop_cannot_leak_its_frame_into_the_next_run() {
            // Review r2: Stop/restart cancels the capture wait via ct (OperationCanceledException,
            // not TimeoutException) — the daemon still owes that capture's event, and the debt must
            // survive the run boundary so the NEXT run swallows the late event instead of adopting
            // a stale frame into fresh geometry. Run 1's first capture acks and never completes;
            // Stop lands while it waits. Run 2's captures each deliver the OLD run's stale event
            // once, then their own — only fresh frames may be fetched.
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFake(paCalls, answerCaptures: false);
            var captureCount = 0;
            var staleDelivered = 0;
            fake.OnRpc("capture_single_frame", req => {
                var n = Interlocked.Increment(ref captureCount);
                if (n == 1) {
                    return JsonValue.Create(0); // run 1: ack, never complete — Stop abandons the wait
                }
                _ = Task.Run(async () => {
                    await Task.Delay(20).ConfigureAwait(false);
                    if (Interlocked.Exchange(ref staleDelivered, 1) == 0) {
                        // Run 1's debt, delivered late into run 2 — exactly once.
                        await fake.BroadcastAsync(new JsonObject {
                            ["Event"] = "SingleFrameComplete", ["Timestamp"] = 0.0, ["Host"] = "g",
                            ["Inst"] = 1, ["Success"] = true,
                            ["Path"] = "/var/lib/openastro-guider/.openastro-guider/save_image_stale",
                            ["Filename"] = "save_image_stale",
                        }).ConfigureAwait(false);
                        await Task.Delay(30).ConfigureAwait(false);
                    }
                    await fake.BroadcastAsync(new JsonObject {
                        ["Event"] = "SingleFrameComplete", ["Timestamp"] = 0.0, ["Host"] = "g",
                        ["Inst"] = 1, ["Success"] = true,
                        ["Path"] = "/var/lib/openastro-guider/.openastro-guider/save_image_fresh",
                        ["Filename"] = "save_image_fresh",
                    }).ConfigureAwait(false);
                });
                return JsonValue.Create(0);
            });
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var fetcher = new FakeFrameFetcher();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB);
            using var svc = NewService(guider, solver, fetcher: fetcher);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            // Give run 1 time to ack its first capture and park on the completion wait.
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) {
                while (Volatile.Read(ref captureCount) < 1) {
                    await Task.Delay(25, cts.Token).ConfigureAwait(false);
                }
            }
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("adjusting"));
            Assert.That(fetcher.Fetched, Is.Not.Empty);
            Assert.That(fetcher.Fetched.Select(f => f.Filename), Is.All.EqualTo("save_image_fresh"),
                "run 1's abandoned capture must never leak its frame into run 2");
        }

        [Test]
        public async Task A_trailing_capture_fault_cannot_relabel_a_real_solve_problem_as_capture_rejected() {
            // Review r4: attempts 1–2 deliver frames that FAIL TO SOLVE (a real focus/sky problem);
            // only attempt 3 hits a transient capture rejection. Classifying by the last attempt's
            // fault alone would report capture_rejected ("could not be captured") — false, and it
            // points the operator at the daemon instead of the sky. Any attempt that reached the
            // solver pins the exhausted seed on seed_solve_failed.
            var paCalls = new ConcurrentQueue<bool>();
            var ws = new WsRecorder();
            await using var fake = StartFake(paCalls, answerCaptures: false);
            var captureCount = 0;
            fake.OnRpc("capture_single_frame", req => {
                if (Interlocked.Increment(ref captureCount) >= 3) {
                    throw new InvalidOperationException("camera busy"); // transient, LAST attempt only
                }
                _ = Task.Run(async () => {
                    await Task.Delay(20).ConfigureAwait(false);
                    await fake.BroadcastAsync(new JsonObject {
                        ["Event"] = "SingleFrameComplete", ["Timestamp"] = 0.0, ["Host"] = "g",
                        ["Inst"] = 1, ["Success"] = true,
                        ["Path"] = "/var/lib/openastro-guider/.openastro-guider/save_image_test",
                        ["Filename"] = "save_image_test",
                    }).ConfigureAwait(false);
                });
                return JsonValue.Create(0);
            });
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            using var svc = NewService(guider, new ScriptedSolver { Fallback = Unsolved }, ws: ws);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "failed").ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("failed"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (ws.Count(WsEventCatalog.PolarAlignError) < 1) {
                await Task.Delay(25, cts.Token).ConfigureAwait(false);
            }
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
            // through the entire seed budget. Capture 1 acks and never completes; every later
            // capture delivers only its own fresh event. Capture 2's event is swallowed against
            // capture 1's debt and capture 2 times out — capture 3 must then succeed.
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFake(paCalls, answerCaptures: false);
            var captureCount = 0;
            fake.OnRpc("capture_single_frame", req => {
                var n = Interlocked.Increment(ref captureCount);
                if (n == 1) {
                    return JsonValue.Create(0); // ack, never complete — and never deliver, ever
                }
                _ = Task.Run(async () => {
                    await Task.Delay(20).ConfigureAwait(false);
                    await fake.BroadcastAsync(new JsonObject {
                        ["Event"] = "SingleFrameComplete", ["Timestamp"] = 0.0, ["Host"] = "g",
                        ["Inst"] = 1, ["Success"] = true,
                        ["Path"] = "/var/lib/openastro-guider/.openastro-guider/save_image_fresh",
                        ["Filename"] = "save_image_fresh",
                    }).ConfigureAwait(false);
                });
                return JsonValue.Create(0);
            });
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var fetcher = new FakeFrameFetcher();
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB);
            using var svc = NewService(guider, solver, fetcher: fetcher);
            svc.CaptureCompleteTimeout = TimeSpan.FromMilliseconds(300);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("adjusting"),
                "the mis-swallowed capture must not re-arm the debt — capture 3 has to get through");
            Assert.That(fetcher.Fetched.Select(f => f.Filename), Is.All.EqualTo("save_image_fresh"));
            // No capture-count assertion: the adjust loop keeps capturing after the seed, so the
            // count isn't pinnable — the "adjusting" state IS the proof (the cascade exhausts all
            // three seed attempts and lands in "failed").
        }

        [Test]
        public async Task A_failed_frame_fetch_fails_the_seed_as_capture_rejected() {
            // The daemon saved the frame but the HTTP download fails (pre-#77 daemon, network) —
            // still a capture-layer fault: capture_rejected, with the fetch error surfaced.
            var paCalls = new ConcurrentQueue<bool>();
            var ws = new WsRecorder();
            await using var fake = StartFake(paCalls);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            using var svc = NewService(guider, new ScriptedSolver { Fallback = SolveA },
                ws: ws, fetcher: new FakeFrameFetcher { Fail = true });

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "failed").ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("failed"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (ws.Count(WsEventCatalog.PolarAlignError) < 1) {
                await Task.Delay(25, cts.Token).ConfigureAwait(false);
            }
            var error = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("capture_rejected"));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("fetching the saved frame"));
        }

        [Test]
        public async Task A_failed_lease_renewal_does_not_abort_the_adjust_loop() {
            // Renewal is best-effort per iteration: the first set_pa_session (the acquire) succeeds,
            // every later one (the renewals) returns a malformed response → GuiderRpcException.
            // The loop must keep adjusting, not land in failed.
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_pixel_scale", JsonValue.Create(1.5));
            var leaseCalls = 0;
            fake.OnRpc("set_pa_session", req => {
                var active = req["params"]?["active"]?.GetValue<bool>() ?? false;
                paCalls.Enqueue(active);
                return Interlocked.Increment(ref leaseCalls) == 1
                    ? new JsonObject { ["active"] = active, ["expires_in_s"] = 600 }
                    : null; // missing result → GuiderRpcException at the renewal site
            });
            fake.OnRpc("capture_single_frame", req => {
                _ = Task.Run(async () => {
                    await Task.Delay(20).ConfigureAwait(false);
                    await fake.BroadcastAsync(new JsonObject {
                        ["Event"] = "SingleFrameComplete", ["Timestamp"] = 0.0, ["Host"] = "g",
                        ["Inst"] = 1, ["Success"] = true,
                        ["Path"] = "/var/lib/openastro-guider/.openastro-guider/save_image_test",
                        ["Filename"] = "save_image_test",
                    }).ConfigureAwait(false);
                });
                return JsonValue.Create(0);
            });
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB);
            using var svc = NewService(guider, solver);
            svc.LeaseRenewInterval = TimeSpan.FromMilliseconds(30);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            // Let several renew windows elapse (and fail) while the loop keeps solving.
            await Task.Delay(300).ConfigureAwait(false);
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("adjusting"), "failed renewals must not abort the session");
            Assert.That(Volatile.Read(ref leaseCalls), Is.GreaterThan(1), "renewals were actually attempted");
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task A_zombie_run_outliving_the_stop_grace_cannot_clobber_the_stopped_state() {
            // Stop bumps the run generation BEFORE cancelling: a run that ignores cancellation past
            // the unwind grace and later fails must find its generation stale — no failed-state
            // clobber, no spurious polar_align.error, after Stop already reported "stopped".
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFake(paCalls);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            using var solverGate = new SemaphoreSlim(0);
            var solver = new BlockingSolver(solverGate);
            var ws = new WsRecorder();
            using var svc = NewService(guider, solver, ws: ws);
            svc.RunUnwindGrace = TimeSpan.FromMilliseconds(100);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            // Wait until the run is parked inside the cancellation-ignoring solve.
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) {
                while (!solver.Entered) {
                    await Task.Delay(20, cts.Token).ConfigureAwait(false);
                }
            }
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            var stopped = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(stopped.State, Is.EqualTo("stopped"), "Stop abandoned the wedged run and reported stopped");

            // Release the zombie: it throws, but its generation is stale — state must stay stopped.
            solverGate.Release(100);
            await Task.Delay(300).ConfigureAwait(false);
            var after = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(after.State, Is.EqualTo("stopped"), "the zombie's late failure must not clobber stopped");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignError), Is.Zero, "no spurious error event from the zombie");
        }

        /// <summary>A solver that IGNORES the cancellation token and blocks on a gate, then throws —
        /// simulates an RPC that outlives Stop's unwind grace and fails late.</summary>
        private sealed class BlockingSolver : IPolarAlignFrameSolver {
            private readonly SemaphoreSlim _gate;
            public volatile bool Entered;
            public BlockingSolver(SemaphoreSlim gate) => _gate = gate;
            public async Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, double? hintRa, double? hintDec, CancellationToken ct) {
                Entered = true;
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException("late zombie failure");
            }
        }

        [Test]
        public async Task Stop_after_a_self_terminated_failure_keeps_the_failed_state() {
            // A stray/cleanup Stop after the routine already failed must not clobber the terminal
            // "failed" back to "stopped" (a polling client would lose the real outcome), and must
            // not publish a spurious polar_align.stopped event.
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFake(paCalls);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var solver = new ScriptedSolver { Fallback = Unsolved };
            var ws = new WsRecorder();
            using var svc = NewService(guider, solver, ws: ws);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("failed"), "a late Stop must not clobber the terminal failed state");
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStopped), Is.Zero,
                "nothing was stopped — no polar_align.stopped event");

            // And a fresh Start still works after the failure (the stale CTS is disposed, state resets).
            solver.Fallback = SolveB;
            solver.Enqueue(SolveA, SolveB);
            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var resumed = await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            Assert.That(resumed.State, Is.EqualTo("adjusting"));
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task Inconsistent_seed_solves_fail_the_axis_fit_with_an_actionable_reason() {
            // Solved pointings further apart than the commanded rotation allows → the geometry's
            // inconsistent-chord rejection must surface as a failed routine, not an internal error.
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFake(paCalls);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var solver = new ScriptedSolver();
            solver.Enqueue(new PolarAlignSolveOutcome(true, 0.0, 20.0), new PolarAlignSolveOutcome(true, 10.0, -40.0));
            var ws = new WsRecorder();
            using var svc = NewService(guider, solver, ws: ws);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            var status = await PollStateAsync(svc, "failed").ConfigureAwait(false);

            Assert.That(status.State, Is.EqualTo("failed"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (ws.Count(WsEventCatalog.PolarAlignError) < 1) {
                await Task.Delay(25, cts.Token).ConfigureAwait(false);
            }
            var error = ws.Events.First(e => e.Type == WsEventCatalog.PolarAlignError).Payload;
            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("axis_fit_failed"));
        }

        [Test]
        public async Task Stop_mid_seed_unwinds_cleanly_and_releases_the_lease_in_order() {
            // The routine is parked on a capture that never completes; Stop must cancel it, unwind,
            // and the wire order of the lease RPCs stays acquire-then-release.
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFake(paCalls, answerCaptures: false);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var ws = new WsRecorder();
            using var svc = NewService(guider, new ScriptedSolver(), ws: ws);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(paCalls.ToArray(), Is.EqualTo(AcquireThenRelease),
                "lease RPCs must run acquire-then-release, never race");
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("stopped"));
            Assert.That(ws.Count(WsEventCatalog.PolarAlignStopped), Is.EqualTo(1));
        }

        // ── §45.13 session log + complete ────────────────────────────────────────────────────

        [Test]
        public async Task Complete_logs_the_achieved_error_and_unwinds_like_stop() {
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFake(paCalls);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB);
            var log = new RecordingLog();
            var mount = NewMount();
            var ws = new WsRecorder();
            using var svc = NewService(guider, solver, mount, ws, log: log);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            // Wait for the first LIVE iteration (not just the seed) so the row's iteration count is
            // real. The signal must be the second progress EVENT (seed publishes iteration 0, the
            // live loop publishes after recording its count) — LastFrameId flips to "live-1" at
            // capture time, BEFORE the solve bumps the counter, so polling it races Complete.
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) {
                while (ws.Count(WsEventCatalog.PolarAlignProgress) < 2) {
                    await Task.Delay(25, cts.Token).ConfigureAwait(false);
                }
            }
            await svc.CompleteAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(log.Rows.TryDequeue(out var row), Is.True, "Complete writes exactly one session row");
            Assert.That(row!.Outcome, Is.EqualTo("complete"));
            Assert.That(row.FinalErrorArcmin, Is.Not.Null.And.EqualTo(1.0).Within(0.2));
            Assert.That(row.Iterations, Is.GreaterThanOrEqualTo(1));
            Assert.That(log.Rows, Is.Empty);
            Assert.That(paCalls.ToArray().Last(), Is.False, "Complete releases the lease");
            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.State, Is.EqualTo("stopped"));
        }

        [Test]
        public async Task Stop_logs_an_aborted_row_and_a_second_stop_does_not_duplicate_it() {
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFake(paCalls);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var solver = new ScriptedSolver();
            solver.Enqueue(SolveA, SolveB);
            var log = new RecordingLog();
            using var svc = NewService(guider, solver, log: log);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "adjusting", "failed").ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(log.Rows.Count, Is.EqualTo(1), "only the Stop that ended the routine logs a row");
            Assert.That(log.Rows.TryDequeue(out var row), Is.True);
            Assert.That(row!.Outcome, Is.EqualTo("aborted"));
        }

        [Test]
        public async Task A_failed_routine_logs_a_failed_row_and_stop_does_not_add_another() {
            var paCalls = new ConcurrentQueue<bool>();
            await using var fake = StartFake(paCalls);
            using var guider = await ConnectGuiderAsync(fake).ConfigureAwait(false);
            var solver = new ScriptedSolver { Fallback = Unsolved };
            var log = new RecordingLog();
            using var svc = NewService(guider, solver, log: log);

            await svc.StartAsync(null, CancellationToken.None).ConfigureAwait(false);
            await PollStateAsync(svc, "failed").ConfigureAwait(false);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (log.Rows.IsEmpty) {
                await Task.Delay(25, cts.Token).ConfigureAwait(false);
            }
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
            Assert.That(defaults.Binning, Is.EqualTo(1));
            Assert.That(defaults.TargetToleranceArcmin, Is.EqualTo(1.0));
            Assert.That(defaults.SeedRotationDeg, Is.EqualTo(30.0));
            Assert.That(defaults.LoopCadenceMs, Is.EqualTo(1000));
            Assert.That(defaults.SettleSeconds, Is.EqualTo(2.0));

            store.PutPolarAlignSettings(defaults with { ExposureSeconds = 0.5, Binning = 2 });
            var updated = store.GetPolarAlignSettings();
            Assert.That(updated.ExposureSeconds, Is.EqualTo(0.5));
            Assert.That(updated.Binning, Is.EqualTo(2));
        }

        private static async Task<GuiderService> ConnectGuiderAsync(FakeGuider fake) {
            var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            var connected = await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false);
            Assert.That(connected, Is.Not.Null, "the guider never reached Connected against the fake");
            return svc;
        }

        private static async Task<GuiderDto?> PollAsync(GuiderService svc, Func<GuiderDto, bool> predicate) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try {
                while (!cts.IsCancellationRequested) {
                    var dto = await svc.GetAsync(cts.Token).ConfigureAwait(false);
                    if (dto is not null && predicate(dto)) {
                        return dto;
                    }
                    await Task.Delay(100, cts.Token).ConfigureAwait(false);
                }
            } catch (OperationCanceledException) {
                // fall through to null
            }
            return null;
        }
    }
}
