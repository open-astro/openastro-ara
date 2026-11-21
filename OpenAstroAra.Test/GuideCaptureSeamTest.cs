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
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// ADR 0016 rules 4 and 5 — the Polar Align Routine and guide focus reach the Guider through the custody and
    /// capture seam (<see cref="IGuideCapture"/>), here the in-process <see cref="FakeGuideCapture"/>: no socket,
    /// no timing in the fake. Covers the lease taken and given back through custody, the busy and out-of-step
    /// refusals, a refused or failed capture, and the frame-complete handler removed on every exit.
    /// </summary>
    [TestFixture]
    [Category("bench")]
    public class GuideCaptureSeamTest {

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
        private static readonly PolarAlignSolveOutcome SolveA = new(true, 0.0, 85.0);
        private static readonly PolarAlignSolveOutcome SolveB = new(true, 30.0, 85.0);

        // ── Polar Align through the seam ──────────────────────────────────────────────────────

        private sealed class ScriptedSolver : IPolarAlignFrameSolver {
            private readonly ConcurrentQueue<PolarAlignSolveOutcome> _script = new();
            public ScriptedSolver(params PolarAlignSolveOutcome[] outcomes) {
                foreach (var o in outcomes) {
                    _script.Enqueue(o);
                }
            }
            public PolarAlignSolveOutcome Fallback { get; set; } = SolveB;
            public Task<double?> MeasureFocalLengthAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) =>
                Task.FromResult<double?>(null);
            public Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) =>
                Task.FromResult(_script.TryDequeue(out var o) ? o : Fallback);
        }

        /// <summary>Records events and hands out the first one of a type as a task: no polling.</summary>
        private sealed class WsRecorder : IWsBroadcaster {
            private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _first = new();
            public long CurrentSequence => 0;
            public Task PublishAsync(WsEvent eventType, JsonElement payload, CancellationToken ct) {
                First(eventType).TrySetResult(payload.Clone());
                return Task.CompletedTask;
            }
            public Task<JsonElement> WhenAsync(string type) => First(type).Task;
            private TaskCompletionSource<JsonElement> First(string type) =>
                _first.GetOrAdd(type, _ => new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        private sealed class FileFetcher : IPolarAlignFrameFetcher {
            public ConcurrentQueue<(string Host, int RpcPort, string Filename)> Fetched { get; } = new();
            public async Task FetchAsync(string host, int rpcPort, string filename, string destinationPath, CancellationToken ct) {
                Fetched.Enqueue((host, rpcPort, filename));
                await File.WriteAllTextAsync(destinationPath, "FAKE-FITS", ct).ConfigureAwait(false);
            }
            public Task<byte[]> FetchLiveFrameJpegAsync(string host, int rpcPort, CancellationToken ct) =>
                Task.FromResult(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
        }

        private static Mock<ITelescopeMediator> NewMount() {
            var mount = new Mock<ITelescopeMediator>();
            mount.Setup(m => m.GetInfo()).Returns(new TelescopeInfo { Connected = true, SiderealTime = 20.0, TrackingEnabled = true });
            var position = new Coordinates(0.0, 85.0, Epoch.JNOW, Coordinates.RAType.Degrees);
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => position);
            mount.Setup(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Callback<Coordinates, CancellationToken>((target, _) => position = target)
                .ReturnsAsync(true);
            // Polar alignment turns with the RA motor: each command steps the reported RA 0.25°.
            mount.Setup(m => m.MoveAxis(It.IsAny<OpenAstroAra.Core.Enums.TelescopeAxes>(), It.IsAny<double>()))
                .Callback<OpenAstroAra.Core.Enums.TelescopeAxes, double>((_, rate) => {
                    if (rate != 0) {
                        position = new Coordinates((position.RADegrees + Math.Sign(rate) * 0.25 + 360.0) % 360.0, position.Dec,
                            Epoch.JNOW, Coordinates.RAType.Degrees);
                    }
                });
            mount.Setup(m => m.WaitForSlew(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            mount.Setup(m => m.SetTrackingEnabled(It.IsAny<bool>())).Returns(true);
            return mount;
        }

        private static PolarAlignService NewPolarAlign(FakeGuideCapture guider, IPolarAlignFrameSolver? solver = null,
                IWsBroadcaster? ws = null, FileFetcher? fetcher = null) {
            var store = new InMemoryProfileStore();
            store.PutSiteSettings(store.GetSiteSettings() with { LatitudeDeg = -33.856784, LongitudeDeg = 151.215297 });
            store.PutPolarAlignSettings(new PolarAlignSettingsDto(
                ExposureSeconds: 0.05, LoopCadenceMs: 20, SettleSeconds: 0, SeedRotationDeg: 30.0, FramesPerPoint: 1));
            var mount = NewMount().Object;
            return new PolarAlignService(guider, NullLogger<PolarAlignService>.Instance, solver ?? new ScriptedSolver(SolveA, SolveB),
                mount, OpenAstroAra.Test.PolarAlign.MediatorMotionControl.For(mount), store, ws, log: null, fetcher ?? new FileFetcher()) {
                PausedRetryDelay = TimeSpan.Zero,
                TrackingConfirmTimeout = TimeSpan.FromMilliseconds(200),
                TrackingConfirmPoll = TimeSpan.Zero,
                AxisMovePoll = TimeSpan.FromMilliseconds(1),
                AxisSettle = TimeSpan.Zero,
                StopVerifyPoll = TimeSpan.FromMilliseconds(1),
                StopVerifyStillFor = TimeSpan.Zero,
                StopVerifyNotBefore = TimeSpan.Zero,
                StopVerifyWindow = TimeSpan.FromMilliseconds(500),
            };
        }

        [Test]
        [Category("IO")] // the run's work directory is on disk
        public async Task Polar_align_takes_the_lease_through_custody_captures_through_the_seam_and_stop_gives_both_back() {
            var guider = new FakeGuideCapture();
            var fetcher = new FileFetcher();
            using var svc = NewPolarAlign(guider, fetcher: fetcher);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await guider.WhenCapturedAsync(2).WaitAsync(Timeout).ConfigureAwait(false);

            Assert.That(guider.Custody.Holder, Is.EqualTo(CameraUse.PolarAlign));
            Assert.That(guider.LeaseCalls[0], Is.EqualTo($"on:{GuideCameraCustody.LeaseTimeoutSeconds}"));
            Assert.That(guider.IdleWaits, Is.EqualTo(1), "the start waits once for an abandoned capture to end");
            Assert.That(guider.LoopStopCalls, Is.EqualTo(1), "the start stops a looping camera once");
            Assert.That(guider.Captures[0], Is.EqualTo((50, (int?)null)), "0.05 s at 1x1 (auto binning on a 1280x960 camera)");
            Assert.That(fetcher.Fetched.First(), Is.EqualTo(("192.0.2.10", 4400, "frame_1.fits")));

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(guider.Custody.Holder, Is.Null);
            Assert.That(guider.LeaseCalls[^1], Is.EqualTo("off"));
            Assert.That(guider.Subscribers, Is.Zero, "no frame-complete handler outlives the run");
        }

        [TestCase("Guiding")]
        [TestCase("Calibrating")]
        [TestCase("Paused")]
        [TestCase("LostLock")]
        public async Task Polar_align_is_refused_with_the_reason_while_the_guider_is_busy(string state) {
            var guider = new FakeGuideCapture { AppState = state };
            using var svc = NewPolarAlign(guider);

            // #2066 (ARA-2) — refused at the start's plan with the mount-busy cause, before the custody module is asked.
            var ex = await Assert.ThrowsAsync<RefusalException>(() => svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None)).ConfigureAwait(false);

            Assert.That(ex!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignMountBusy));
            // The sentence's fixed part says "stop guiding" whatever the state: the reason must name the state itself.
            Assert.That(ex.Message, Does.Contain("the guider is " + state).IgnoreCase.And.Contain("stop guiding"));
            Assert.That(guider.LeaseCalls, Is.Empty);
            Assert.That(guider.Captures, Is.Empty);
            Assert.That((await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false)).State, Is.EqualTo("idle"));
        }

        [Test]
        [Category("IO")]
        public async Task Polar_align_on_an_out_of_step_guider_fails_with_the_reason_before_its_first_capture() {
            const string reason = "The Guider does not have this profile's guider settings: no camera. Fix it in Guider settings and press Apply, or reconnect the Guider.";
            var guider = new FakeGuideCapture { OutOfStep = reason };
            var ws = new WsRecorder();
            using var svc = NewPolarAlign(guider, ws: ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var error = await ws.WhenAsync(WsEventCatalog.PolarAlignError).WaitAsync(Timeout).ConfigureAwait(false);
            await guider.WhenLeaseReleasedAsync().WaitAsync(Timeout).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("guider_out_of_step"));
            Assert.That(error.GetProperty("message").GetString(), Is.EqualTo(reason));
            Assert.That(guider.Captures, Is.Empty);
        }

        [Test]
        [Category("IO")]
        public async Task Polar_align_whose_captures_are_all_refused_fails_as_capture_rejected_and_leaves_no_handler() {
            var guider = new FakeGuideCapture {
                Script = _ => FakeCaptureOutcome.Refused, RefusalMessage = "path must be in an existing directory",
            };
            var ws = new WsRecorder();
            using var svc = NewPolarAlign(guider, ws: ws);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            var error = await ws.WhenAsync(WsEventCatalog.PolarAlignError).WaitAsync(Timeout).ConfigureAwait(false);

            Assert.That(error.GetProperty("reason").GetString(), Is.EqualTo("capture_rejected"));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("existing directory"));
            Assert.That(guider.Subscribers, Is.Zero);
        }

        [Test]
        [Category("IO")]
        public async Task A_stop_during_a_capture_that_never_completes_removes_the_frame_complete_handler() {
            var guider = new FakeGuideCapture { Script = _ => FakeCaptureOutcome.NeverCompletes };
            using var svc = NewPolarAlign(guider);

            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            await guider.WhenCapturedAsync(1).WaitAsync(Timeout).ConfigureAwait(false);
            Assert.That(guider.Subscribers, Is.EqualTo(1), "the run listens while the capture is out");

            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(guider.Subscribers, Is.Zero);
            Assert.That(guider.LeaseCalls[^1], Is.EqualTo("off"));
        }

        // ── guide focus through the seam ──────────────────────────────────────────────────────

        private static (ushort[] Pixels, int Width, int Height) StarFrame() {
            const int w = 128, h = 128;
            var f = new ushort[w * h];
            Array.Fill(f, (ushort)1000);
            foreach (var (cx, cy) in new[] { (30, 30), (90, 40), (60, 95), (100, 100) }) {
                for (int dy = -6; dy <= 6; dy++) {
                    for (int dx = -6; dx <= 6; dx++) {
                        var v = 20000 * Math.Exp(-(dx * dx + dy * dy) / (2 * 1.8 * 1.8));
                        var idx = (cy + dy) * w + cx + dx;
                        f[idx] = (ushort)Math.Min(ushort.MaxValue, f[idx] + v);
                    }
                }
            }
            return (f, w, h);
        }

        private static GuideFocusService NewGuideFocus(FakeGuideCapture guider, FileFetcher? fetcher = null) {
            var decoder = new Mock<IGuideFrameDecoder>();
            decoder.Setup(d => d.Decode(It.IsAny<string>())).Returns(StarFrame);
            return new GuideFocusService(guider, fetcher ?? new FileFetcher(), decoder.Object) {
                FailureBackoff = TimeSpan.Zero,
            };
        }

        [Test]
        public async Task Guide_focus_borrows_frames_through_the_seam_and_stop_gives_the_lease_back() {
            var guider = new FakeGuideCapture();
            var fetcher = new FileFetcher();
            using var svc = NewGuideFocus(guider, fetcher);

            await svc.StartAsync(new GuideFocusStartRequestDto(GuideFocusService.MinExposureSec), CancellationToken.None).ConfigureAwait(false);
            await guider.WhenCapturedAsync(2).WaitAsync(Timeout).ConfigureAwait(false);

            Assert.That(guider.Custody.Holder, Is.EqualTo(CameraUse.GuideFocus));
            Assert.That(guider.LeaseCalls[0], Is.EqualTo($"on:{GuideCameraCustody.LeaseTimeoutSeconds}"));
            Assert.That(guider.IdleWaits, Is.EqualTo(1));
            Assert.That(guider.Captures[0], Is.EqualTo((50, (int?)null)));
            Assert.That(fetcher.Fetched.First(), Is.EqualTo(("192.0.2.10", 4400, "frame_1.fits")));

            await svc.StopAsync().ConfigureAwait(false);

            Assert.That(svc.GetStatus().State, Is.EqualTo("stopped"));
            Assert.That(guider.Custody.Holder, Is.Null);
            Assert.That(guider.LeaseCalls[^1], Is.EqualTo("off"));
            Assert.That(guider.Subscribers, Is.Zero);
        }

        [TestCase("Guiding")]
        [TestCase("Calibrating")]
        [TestCase("Paused")]
        [TestCase("LostLock")]
        public async Task Guide_focus_is_refused_with_the_reason_while_the_guider_is_busy(string state) {
            var guider = new FakeGuideCapture { AppState = state };
            using var svc = NewGuideFocus(guider);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.StartAsync(new GuideFocusStartRequestDto(GuideFocusService.MinExposureSec), CancellationToken.None)).ConfigureAwait(false);

            Assert.That(ex!.Message, Does.Contain(state).IgnoreCase.And.Contain("stop guiding"));
            Assert.That(guider.LeaseCalls, Is.Empty);
            Assert.That(svc.GetStatus().State, Is.EqualTo("idle"));
        }

        [Test]
        public async Task Guide_focus_on_an_out_of_step_guider_is_refused_with_the_reason_and_takes_no_lease() {
            const string reason = "The Guider does not have this profile's guider settings: no camera. Fix it in Guider settings and press Apply, or reconnect the Guider.";
            var guider = new FakeGuideCapture { OutOfStep = reason };
            using var svc = NewGuideFocus(guider);

            var ex = await Assert.ThrowsAsync<GuiderOutOfStepException>(() =>
                svc.StartAsync(new GuideFocusStartRequestDto(GuideFocusService.MinExposureSec), CancellationToken.None)).ConfigureAwait(false);

            Assert.That(ex!.Message, Is.EqualTo(reason));
            Assert.That(guider.LeaseCalls, Is.Empty);
            Assert.That(guider.Custody.Holder, Is.Null);
        }

        [Test]
        public async Task Guide_focus_whose_captures_fail_ends_in_error_with_the_guiders_reason_and_gives_the_lease_back() {
            var guider = new FakeGuideCapture { Script = _ => FakeCaptureOutcome.Failed };
            using var svc = NewGuideFocus(guider);

            await svc.StartAsync(new GuideFocusStartRequestDto(GuideFocusService.MinExposureSec), CancellationToken.None).ConfigureAwait(false);
            await guider.WhenLeaseReleasedAsync().WaitAsync(Timeout).ConfigureAwait(false);

            var status = svc.GetStatus();
            Assert.That(status.State, Is.EqualTo("error"));
            Assert.That(status.ConsecutiveFailures, Is.EqualTo(GuideFocusService.MaxConsecutiveFailures));
            Assert.That(status.Error, Does.Contain(FakeGuideCapture.FailedCaptureError));
            Assert.That(guider.Captures, Has.Count.EqualTo(GuideFocusService.MaxConsecutiveFailures));
            Assert.That(guider.Subscribers, Is.Zero);
        }

        // ── rules 4 and 5, structurally ───────────────────────────────────────────────────────

        [TestCase(typeof(PolarAlignService))]
        [TestCase(typeof(GuideFocusService))]
        public void The_service_takes_the_capture_seam_and_never_sees_the_guider_class_or_the_phd2_client(Type service) {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var ctorParams = service.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType).ToList();
            var methodParams = service.GetMethods(all).SelectMany(m => m.GetParameters()).Select(p => p.ParameterType).ToList();
            var fields = service.GetFields(all).Select(f => f.FieldType).ToList();
            var forbidden = new[] { typeof(GuiderService), typeof(PHD2Guider) };

            Assert.That(ctorParams, Has.Member(typeof(IGuideCapture)));
            Assert.That(ctorParams.Concat(methodParams).Concat(fields).Intersect(forbidden), Is.Empty,
                "ADR 0016 rule 4: no constructor, method or field of the concrete Guider or the PHD2 client");
        }

        private static readonly string[] SocketTokens = { "FakeGuider.", "FakeGuider ", "TcpClient", "TcpListener", "Socket" };
        private static readonly string[] TimingTokens = { "Task.Delay(", "Thread.Sleep(" };

        // A wait the plan names as unavoidable carries this comment on the same line, so each one can be grepped.
        private const string Rule5Exception = "ADR 0016 rule 5 exception:";

        private static string RepoFile(string relative) {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenAstroAra.sln"))) {
                dir = dir.Parent;
            }
            var root = dir?.FullName ?? throw new InvalidOperationException("repo root not found above " + AppContext.BaseDirectory);
            return File.ReadAllText(Path.Combine(root, relative));
        }

        // maxExceptions caps the marked waits, so a new one needs a reviewed row change. The two in
        // PolarAlignServiceTest.cs each check that a call is still blocked behind a gate, which no signal can show.
        [TestCase("OpenAstroAra.Test/GuideFocusServiceTest.cs", 0)]
        [TestCase("OpenAstroAra.Test/PolarAlignServiceTest.cs", 2)]
        public void FakeGuider_and_fixed_delays_are_kept_out_of_polar_align_and_guide_focus_tests(string file, int maxExceptions) {
            var lines = RepoFile(file).Split('\n');

            Assert.That(SocketTokens.Where(t => lines.Any(l => l.Contains(t, StringComparison.Ordinal))), Is.Empty,
                $"ADR 0016 rule 5: {file} runs on the in-process fake, the TCP fake is for PHD2 adapter tests");
            Assert.That(lines.Where(l => TimingTokens.Any(t => l.Contains(t, StringComparison.Ordinal)) && !l.Contains(Rule5Exception, StringComparison.Ordinal))
                    .Select(l => l.Trim()), Is.Empty,
                $"ADR 0016 rule 5: {file} waits on a signal from the fake, the solver or an event, not on a delay");
            Assert.That(lines.Count(l => l.Contains(Rule5Exception, StringComparison.Ordinal)), Is.LessThanOrEqualTo(maxExceptions),
                $"ADR 0016 rule 5: {file} has more marked waits than the plan names");
        }

        [Test]
        public void FakeGuideCapture_has_no_socket_and_no_timing() {
            var text = RepoFile("OpenAstroAra.Test/FakeGuideCapture.cs");
            Assert.That(SocketTokens.Concat(TimingTokens).Where(t => text.Contains(t, StringComparison.Ordinal)), Is.Empty);
        }
    }
}