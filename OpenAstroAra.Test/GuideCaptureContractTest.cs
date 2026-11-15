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
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Guider;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// ADR 0016 — the custody and capture seam as both of its adapters answer it: the PHD2 adapter (the real
    /// <see cref="GuiderService"/> over the TCP <see cref="FakeGuider"/>, an adapter test) and the in-process
    /// <see cref="FakeGuideCapture"/> the Polar Align and guide-focus tests use. The same cases run on both, so
    /// the fake cannot drift from what the adapter does where the services rely on it.
    /// </summary>
    public abstract class GuideCaptureContract {

        protected static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
        private static readonly bool[] TakeThenRelease = { true, false };
        private static readonly (int, int?)[] OneCapture = { (50, 2) };

        /// <summary>A connected Guider in <paramref name="appState"/> whose captures are acked and not completed.</summary>
        private protected abstract Task<IGuideCapture> ConnectedAsync(string appState = "Stopped");

        private protected abstract IGuideCapture Disconnected();

        protected abstract void RefuseCaptures(string message);

        /// <summary>The guider announces the end of the capture that is out, saved as <paramref name="filename"/>.</summary>
        protected abstract Task CompleteAsync(string filename);

        protected abstract IReadOnlyList<(int ExposureMs, int? Binning)> Captures { get; }

        /// <summary>Every lease call the guider received: true = take, false = release.</summary>
        protected abstract IReadOnlyList<bool> LeaseCalls { get; }

        [Test]
        public void ConnectedLink_on_a_disconnected_guider_is_refused_as_not_connected() {
            var ex = Assert.Throws<InvalidOperationException>(() => Disconnected().ConnectedLink());
            Assert.That(ex!.Message, Is.EqualTo("guider is not connected"));
        }

        [Test]
        public async Task A_capture_carries_its_exposure_and_binning() {
            var link = (await ConnectedAsync().ConfigureAwait(false)).ConnectedLink();

            await link.CaptureFrameAsync(50, 2, CancellationToken.None).ConfigureAwait(false);

            Assert.That(Captures, Is.EqualTo(OneCapture));
        }

        [Test]
        public async Task A_frame_complete_handler_hears_the_completion_and_nothing_once_disposed() {
            var link = (await ConnectedAsync().ConfigureAwait(false)).ConnectedLink();
            var heard = 0;
            var first = new TaskCompletionSource<SingleFrameCompleteEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var subscription = link.OnFrameComplete(e => {
                Interlocked.Increment(ref heard);
                first.TrySetResult(e);
            });

            await link.CaptureFrameAsync(50, null, CancellationToken.None).ConfigureAwait(false);
            await CompleteAsync("frame_a.fits").ConfigureAwait(false);
            var completed = await first.Task.WaitAsync(Timeout).ConfigureAwait(false);
            Assert.That(completed.Success, Is.True);
            Assert.That(completed.Filename, Is.EqualTo("frame_a.fits"));

            subscription.Dispose();
            // A handler still subscribed is the witness that the second completion was delivered.
            var witness = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var _ = link.OnFrameComplete(_ => witness.TrySetResult());
            await CompleteAsync("frame_b.fits").ConfigureAwait(false);
            await witness.Task.WaitAsync(Timeout).ConfigureAwait(false);

            Assert.That(Volatile.Read(ref heard), Is.EqualTo(1), "a disposed subscription hears nothing");
        }

        [Test]
        public async Task A_refused_capture_throws_GuiderRpcException_with_the_guiders_message() {
            var link = (await ConnectedAsync().ConfigureAwait(false)).ConnectedLink();
            RefuseCaptures("path must be in an existing directory");

            var ex = await Assert.ThrowsAsync<GuiderRpcException>(() => link.CaptureFrameAsync(50, null, CancellationToken.None)).ConfigureAwait(false);

            Assert.That(ex!.RpcMethod, Is.EqualTo("capture_single_frame"));
            Assert.That(ex.Message, Does.Contain("path must be in an existing directory"));
        }

        [TestCase("Guiding")]
        [TestCase("Calibrating")]
        [TestCase("Paused")]
        [TestCase("LostLock")]
        public async Task A_setup_activity_is_refused_while_the_guider_is_busy_and_takes_no_lease(string state) {
            var guider = await ConnectedAsync(state).ConfigureAwait(false);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                guider.TakeCameraAsync(CameraUse.PolarAlign, _ => Task.CompletedTask, CancellationToken.None)).ConfigureAwait(false);

            Assert.That(ex!.Message, Does.Contain(state).IgnoreCase.And.Contain("stop guiding"));
            Assert.That(LeaseCalls, Is.Empty);
        }

        [Test]
        public async Task Taking_the_camera_takes_the_lease_and_releasing_it_gives_the_lease_back() {
            var guider = await ConnectedAsync().ConfigureAwait(false);

            var claim = await guider.TakeCameraAsync(CameraUse.GuideFocus, _ => Task.CompletedTask, CancellationToken.None).ConfigureAwait(false);
            await claim.ReleaseAsync().ConfigureAwait(false);

            Assert.That(LeaseCalls, Is.EqualTo(TakeThenRelease));
        }
    }

    /// <summary>The contract on the in-process fake.</summary>
    [TestFixture]
    [Category("bench")]
    public class FakeGuideCaptureContractTest : GuideCaptureContract {
        private FakeGuideCapture _fake = null!;

        [SetUp]
        public void SetUp() => _fake = new FakeGuideCapture { Script = _ => FakeCaptureOutcome.NeverCompletes };

        private protected override Task<IGuideCapture> ConnectedAsync(string appState = "Stopped") {
            _fake.AppState = appState;
            return Task.FromResult<IGuideCapture>(_fake);
        }

        private protected override IGuideCapture Disconnected() {
            _fake.Connected = false;
            return _fake;
        }

        protected override void RefuseCaptures(string message) {
            _fake.Script = _ => FakeCaptureOutcome.Refused;
            _fake.RefusalMessage = message;
        }

        protected override Task CompleteAsync(string filename) {
            _fake.RaiseFrameComplete(new SingleFrameCompleteEventArgs(true, null, "/fake/guider/" + filename, filename));
            return Task.CompletedTask;
        }

        protected override IReadOnlyList<(int ExposureMs, int? Binning)> Captures => _fake.Captures;

        protected override IReadOnlyList<bool> LeaseCalls => _fake.LeaseCalls.Select(c => c != "off").ToArray();
    }

    /// <summary>The contract on the PHD2 adapter: <see cref="GuiderService"/> over the TCP fake (a PHD2 adapter
    /// test, so the socket is allowed here, ADR 0016 rule 5).</summary>
    [TestFixture]
    [Category("IO")] // loopback socket
    [Category("bench")]
    public class Phd2GuideCaptureContractTest : GuideCaptureContract {
        // NUnit reuses one fixture instance for every case: SetUp gives each its own state.
        private FakeGuider? _tcp;
        private GuiderService? _guider;
        private ConcurrentQueue<(int, int?)> _captures = new();
        private ConcurrentQueue<JsonObject> _captureParams = new();
        private ConcurrentQueue<bool> _leaseCalls = new();
        private volatile string? _refusal;

        [SetUp]
        public void SetUp() {
            (_tcp, _guider, _refusal) = (null, null, null);
            (_captures, _captureParams, _leaseCalls) = (new(), new(), new());
        }

        [TearDown]
        public async Task TearDown() {
            _guider?.Dispose();
            if (_tcp is not null) {
                await _tcp.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static GuiderService NewGuider() => new(new HeadlessProfileService(),
            new GuiderRecoveryCoordinator(Mock.Of<IGuiderProcessSupervisor>(), Mock.Of<INotificationService>(),
                Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance),
            NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

        private static async Task<bool> WaitUntilAsync(Func<bool> condition) {
            using var cts = new CancellationTokenSource(Timeout);
            try {
                while (!condition()) {
                    await Task.Delay(20, cts.Token).ConfigureAwait(false);
                }
                return true;
            } catch (OperationCanceledException) {
                return false;
            }
        }

        private static FakeGuider StartTcp() {
            var tcp = FakeGuider.Start();
            tcp.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            tcp.OnRpc("get_pixel_scale", JsonValue.Create(1.5));
            return tcp;
        }

        private async Task ConnectToAsync(FakeGuider tcp) {
            await _guider!.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", tcp.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => _guider.ConnectedGuiderOrNull() is { Connected: true } c && c.ConnectedRpcPort == tcp.Port)
                .ConfigureAwait(false), Is.True, "the guider never reached Connected against the TCP fake");
        }

        private protected override async Task<IGuideCapture> ConnectedAsync(string appState = "Stopped") {
            _tcp = StartTcp();
            _tcp.OnRpc("set_pa_session", req => {
                var active = req["params"]?["active"]?.GetValue<bool>() ?? false;
                _leaseCalls.Enqueue(active);
                return new JsonObject { ["active"] = active };
            });
            _tcp.OnRpc("capture_single_frame", req => {
                if (_refusal is { } refusal) {
                    throw new InvalidOperationException(refusal);
                }
                var p = req["params"] as JsonObject ?? new JsonObject();
                _captureParams.Enqueue((JsonObject)p.DeepClone());
                _captures.Enqueue((p["exposure"]?.GetValue<int>() ?? -1, p["binning"]?.GetValue<int>()));
                return JsonValue.Create(0);
            });
            _guider = NewGuider();
            await ConnectToAsync(_tcp).ConfigureAwait(false);
            if (appState != "Stopped") {
                await _tcp.BroadcastAsync(PhdEvents.AppState(appState)).ConfigureAwait(false);
                Assert.That(await WaitUntilAsync(() => _guider.ConnectedGuiderOrNull()?.State == appState).ConfigureAwait(false), Is.True);
            }
            return _guider;
        }

        private protected override IGuideCapture Disconnected() => _guider = NewGuider();

        protected override void RefuseCaptures(string message) => _refusal = message;

        protected override Task CompleteAsync(string filename) => _tcp!.BroadcastAsync(new JsonObject {
            ["Event"] = "SingleFrameComplete", ["Timestamp"] = 0.0, ["Host"] = "g", ["Inst"] = 1, ["Success"] = true,
            ["Path"] = "/var/lib/openastro-guider/" + filename, ["Filename"] = filename,
        });

        protected override IReadOnlyList<(int ExposureMs, int? Binning)> Captures => _captures.ToArray();

        protected override IReadOnlyList<bool> LeaseCalls => _leaseCalls.ToArray();

        [Test]
        public async Task The_phd2_link_asks_for_a_daemon_side_save_with_no_path_gain_or_subframe() {
            // §45 capture-fetch: the daemon saves inside its own sandbox and names the file on the completion.
            var link = (await ConnectedAsync().ConfigureAwait(false)).ConnectedLink();

            await link.CaptureFrameAsync(50, null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(_captureParams.TryDequeue(out var p), Is.True);
            Assert.That(p!["save"]?.GetValue<bool>(), Is.True);
            Assert.That(p.ContainsKey("path") || p.ContainsKey("gain") || p.ContainsKey("subframe") || p.ContainsKey("binning"), Is.False,
                "only the exposure and the save go on the wire for a 1x1 capture");
        }

        [Test]
        public async Task The_phd2_link_reports_the_host_and_rpc_port_it_is_connected_to() {
            var link = (await ConnectedAsync().ConfigureAwait(false)).ConnectedLink();

            Assert.That(link.Host, Is.EqualTo("127.0.0.1"));
            Assert.That(link.RpcPort, Is.EqualTo(_tcp!.Port));
        }

        [Test]
        public async Task The_phd2_link_stays_on_the_client_it_was_taken_from_and_never_reaches_a_reconnected_one() {
            // A run keeps the client it started on: the guider it reconnects to holds no lease for that run.
            var link = (await ConnectedAsync().ConfigureAwait(false)).ConnectedLink();
            await _guider!.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            await using var second = StartTcp();
            var secondCaptures = 0;
            second.OnRpc("capture_single_frame", _ => {
                Interlocked.Increment(ref secondCaptures);
                return JsonValue.Create(0);
            });
            await ConnectToAsync(second).ConfigureAwait(false);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => link.CaptureFrameAsync(50, null, CancellationToken.None))
                .ConfigureAwait(false);

            Assert.That(ex!.Message, Is.EqualTo("guider is not connected"));
            Assert.That(Volatile.Read(ref secondCaptures), Is.Zero, "the capture never reached the reconnected guider");
            Assert.That(Captures, Is.Empty);
        }
    }
}