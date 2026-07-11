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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Guider;
using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §63.8 (guider-dark-build-progress-service) — the <see cref="GuiderService"/> progress-poll loop driven
    /// against the bench's <see cref="FakeGuider"/>. Proves the concurrency claim the §63.6 build slice deferred:
    /// while the single blocking <c>build_dark_library</c> / <c>build_defect_map_darks</c> RPC is in flight, a
    /// SEPARATE poll of <c>get_dark_build_progress</c> (on its own short-lived connection) fires
    /// <c>guider.*.progress</c> WS events. The build here is deliberately held open on a gate so the assertion can
    /// only pass if the poll runs concurrently with — not after — the build.
    /// </summary>
    [TestFixture]
    [Category("bench")]
    public class GuiderServiceDarkLibraryProgressTest {

        private static GuiderRecoveryCoordinator NewRecovery() =>
            new(Mock.Of<IGuiderProcessSupervisor>(),
                Mock.Of<INotificationService>(),
                Mock.Of<IDiagnosticsService>(),
                NullLogger<GuiderRecoveryCoordinator>.Instance);

        [Test]
        public async Task Dark_library_build_emits_progress_events_while_the_build_blocks() {
            using var buildGate = new ManualResetEventSlim(false);
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_dark_build_progress", _ => new JsonObject {
                ["active"] = true, ["exposure_index"] = 1, ["exposure_count"] = 5,
                ["exposure_ms"] = 3000, ["frame"] = 7, ["frame_count"] = 20,
            });
            // Hold build_dark_library open until the test releases the gate — the blocking RPC that the poll must
            // run alongside. A 20s ceiling keeps a stuck test from hanging the connection handler forever.
            fake.OnRpc("build_dark_library", _ => {
                buildGate.Wait(TimeSpan.FromSeconds(20));
                return new JsonObject {
                    ["profile_id"] = 3, ["dark_library_path"] = "/darks/ara.fits",
                    ["frame_count"] = 5, ["exposure_count"] = 4,
                    ["exposures_ms"] = new JsonArray(1000, 2000, 3000, 5000),
                };
            });

            var events = new ConcurrentQueue<string>();
            using var svc = await ConnectGuiderAsync(fake, events).ConfigureAwait(false);
            try {
                _ = await svc.BuildDarkLibraryAsync(new BuildDarkLibraryRequestDto(FrameCount: 5), null, CancellationToken.None)
                    .ConfigureAwait(false);

                // The progress event can only arrive while build_dark_library is still blocked on the gate.
                var sawProgress = await WaitForEventAsync(events, GuiderService.DarkLibraryProgressEvent).ConfigureAwait(false);
                Assert.That(sawProgress, Is.True,
                    "expected a guider.dark_library.progress event to fire while the build was blocked");
            } finally {
                buildGate.Set(); // release the build so the background task completes before dispose
            }

            var sawComplete = await WaitForEventAsync(events, GuiderService.DarkLibraryCompleteEvent).ConfigureAwait(false);
            Assert.That(sawComplete, Is.True, "the build should finish and emit its complete event once unblocked");
            AssertNoProgressAfterTerminal(events, GuiderService.DarkLibraryProgressEvent, GuiderService.DarkLibraryCompleteEvent);
        }

        [Test]
        public async Task Defect_map_build_emits_progress_events_while_the_build_blocks() {
            using var buildGate = new ManualResetEventSlim(false);
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_dark_build_progress", _ => new JsonObject {
                ["active"] = true, ["exposure_index"] = 2, ["exposure_count"] = 10,
                ["exposure_ms"] = 2000, ["frame"] = 3, ["frame_count"] = 30,
            });
            fake.OnRpc("build_defect_map_darks", _ => {
                buildGate.Wait(TimeSpan.FromSeconds(20));
                return new JsonObject {
                    ["profile_id"] = 3, ["defect_map_path"] = "/defect.fit",
                    ["defect_count"] = 42, ["exposure_ms"] = 2000, ["frame_count"] = 10,
                };
            });

            var events = new ConcurrentQueue<string>();
            using var svc = await ConnectGuiderAsync(fake, events).ConfigureAwait(false);
            try {
                _ = await svc.BuildDefectMapDarksAsync(new BuildDefectMapDarksRequestDto(ExposureMs: 2000, FrameCount: 10), null, CancellationToken.None)
                    .ConfigureAwait(false);

                var sawProgress = await WaitForEventAsync(events, GuiderService.DefectMapProgressEvent).ConfigureAwait(false);
                Assert.That(sawProgress, Is.True,
                    "expected a guider.defect_map.progress event to fire while the build was blocked");
            } finally {
                buildGate.Set();
            }

            var sawComplete = await WaitForEventAsync(events, GuiderService.DefectMapCompleteEvent).ConfigureAwait(false);
            Assert.That(sawComplete, Is.True, "the build should finish and emit its complete event once unblocked");
            AssertNoProgressAfterTerminal(events, GuiderService.DefectMapProgressEvent, GuiderService.DefectMapCompleteEvent);
        }

        [Test]
        public async Task A_build_whose_progress_polls_all_fail_logs_one_warning() {
            using var buildGate = new ManualResetEventSlim(false);
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            var pollAttempts = 0;
            // Every progress poll errors (the fake maps a throwing factory to a JSON-RPC error response) — the
            // #770 round-2 case: an older/unreachable daemon whose progress RPC never answers usefully.
            fake.OnRpc("get_dark_build_progress", _ => {
                Interlocked.Increment(ref pollAttempts);
                throw new InvalidOperationException("progress unavailable");
            });
            fake.OnRpc("build_dark_library", _ => {
                buildGate.Wait(TimeSpan.FromSeconds(20));
                return new JsonObject {
                    ["profile_id"] = 3, ["dark_library_path"] = "/darks/ara.fits",
                    ["frame_count"] = 5, ["exposure_count"] = 4,
                    ["exposures_ms"] = new JsonArray(1000, 2000, 3000, 5000),
                };
            });

            var events = new ConcurrentQueue<string>();
            var logger = new RecordingLogger();
            using var svc = await ConnectGuiderAsync(fake, events, logger).ConfigureAwait(false);
            try {
                _ = await svc.BuildDarkLibraryAsync(new BuildDarkLibraryRequestDto(FrameCount: 5), null, CancellationToken.None)
                    .ConfigureAwait(false);
                // Hold the build open until the poll has demonstrably failed at least once, so the drain path
                // sees a zero-success build with a non-zero attempt count.
                var polled = await WaitUntilAsync(() => Volatile.Read(ref pollAttempts) >= 1).ConfigureAwait(false);
                Assert.That(polled, Is.True, "the progress poll never reached the fake's failing RPC");
            } finally {
                buildGate.Set();
            }

            var sawComplete = await WaitForEventAsync(events, GuiderService.DarkLibraryCompleteEvent).ConfigureAwait(false);
            Assert.That(sawComplete, Is.True, "the build should still complete — failing progress polls never disturb it");
            // The poll is drained before the complete event publishes, so the warning is on the log by now.
            var warnings = logger.Warnings.FindAll(m => m.Contains("progress poll", StringComparison.OrdinalIgnoreCase));
            Assert.That(warnings, Has.Count.EqualTo(1),
                "exactly one poll-failure warning per build — silent is invisible, per-tick is noise");
            Assert.That(warnings[0], Does.Contain(GuiderService.DarkLibraryProgressEvent));
            // No progress event can have fired — every poll failed.
            Assert.That(events, Does.Not.Contain(GuiderService.DarkLibraryProgressEvent));
        }

        [Test]
        public async Task Five_consecutive_poll_failures_mid_build_log_the_streak_warning() {
            using var buildGate = new ManualResetEventSlim(false);
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            var pollAttempts = 0;
            fake.OnRpc("get_dark_build_progress", _ => {
                Interlocked.Increment(ref pollAttempts);
                throw new InvalidOperationException("progress unavailable");
            });
            fake.OnRpc("build_dark_library", _ => {
                buildGate.Wait(TimeSpan.FromSeconds(30));
                return new JsonObject {
                    ["profile_id"] = 3, ["dark_library_path"] = "/darks/ara.fits",
                    ["frame_count"] = 5, ["exposure_count"] = 4,
                    ["exposures_ms"] = new JsonArray(1000, 2000, 3000, 5000),
                };
            });

            var events = new ConcurrentQueue<string>();
            var logger = new RecordingLogger();
            using var svc = await ConnectGuiderAsync(fake, events, logger).ConfigureAwait(false);
            try {
                _ = await svc.BuildDarkLibraryAsync(new BuildDarkLibraryRequestDto(FrameCount: 5), null, CancellationToken.None)
                    .ConfigureAwait(false);
                // Hold the build open past the streak threshold (5 consecutive failures at the 1 s poll cadence)
                // so the MID-BUILD warning fires while the build is still running — the headline behavior, as
                // opposed to the drain-time zero-success fallback the other bench exercises.
                var streaked = await WaitUntilAsync(() => Volatile.Read(ref pollAttempts) >= 5, TimeSpan.FromSeconds(20))
                    .ConfigureAwait(false);
                Assert.That(streaked, Is.True, "the poll never accumulated 5 attempts against the failing RPC");
                var warnedMidBuild = await WaitUntilAsync(() => {
                    lock (logger.Warnings) {
                        return logger.Warnings.Exists(m => m.Contains("consecutive", StringComparison.OrdinalIgnoreCase));
                    }
                }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                Assert.That(warnedMidBuild, Is.True,
                    "the streak warning should fire MID-BUILD, on the 5th consecutive failure — not wait for the drain");
            } finally {
                buildGate.Set();
            }

            var sawComplete = await WaitForEventAsync(events, GuiderService.DarkLibraryCompleteEvent).ConfigureAwait(false);
            Assert.That(sawComplete, Is.True, "the build should still complete — failing progress polls never disturb it");
            // Still exactly ONE warning for the whole build: the streak warning suppresses the drain-time one.
            var warnings = logger.Warnings.FindAll(m => m.Contains("progress poll", StringComparison.OrdinalIgnoreCase));
            Assert.That(warnings, Has.Count.EqualTo(1), "the drain must not add a second warning after the streak one");
            Assert.That(warnings[0], Does.Contain("consecutive"));
        }

        [Test]
        public async Task Completed_builds_stamp_calibration_state_in_the_profile_store() {
            // §30.7.4 (e-4b-2) — a completed dark-library build stamps calibration_state.guider.dark_library
            // valid (and a completed defect-map build stamps defect_map) without touching the other entry.
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_dark_build_progress", _ => new JsonObject { ["active"] = false });
            fake.OnRpc("build_dark_library", _ => new JsonObject {
                ["profile_id"] = 3, ["dark_library_path"] = "/darks/ara.fits",
                ["frame_count"] = 5, ["exposure_count"] = 4,
                ["exposures_ms"] = new JsonArray(1000, 2000, 3000, 5000),
            });
            fake.OnRpc("build_defect_map_darks", _ => new JsonObject {
                ["profile_id"] = 3, ["defect_map_path"] = "/defect.fit",
                ["defect_count"] = 42, ["exposure_ms"] = 2000, ["frame_count"] = 10,
            });

            var store = new InMemoryProfileStore();
            var events = new ConcurrentQueue<string>();
            using var svc = await ConnectGuiderAsync(fake, events, profileStore: store).ConfigureAwait(false);

            _ = await svc.BuildDarkLibraryAsync(new BuildDarkLibraryRequestDto(FrameCount: 5), null, CancellationToken.None)
                .ConfigureAwait(false);
            var sawDarkComplete = await WaitForEventAsync(events, GuiderService.DarkLibraryCompleteEvent).ConfigureAwait(false);
            Assert.That(sawDarkComplete, Is.True, "the dark build should complete against the fake");
            // The stamp lands right after the complete event on the same background task — poll for it rather
            // than assume perfect ordering with the WS capture.
            var darkStamped = await WaitUntilAsync(() => store.GetCalibrationState().DarkLibrary.Valid).ConfigureAwait(false);
            Assert.That(darkStamped, Is.True, "a completed dark build must stamp dark_library valid");
            var afterDark = store.GetCalibrationState();
            Assert.That(afterDark.DarkLibrary.LastBuiltAt, Is.Not.Null);
            Assert.That(afterDark.DefectMap.Valid, Is.False, "the dark build must not touch the defect_map entry");

            _ = await svc.BuildDefectMapDarksAsync(new BuildDefectMapDarksRequestDto(ExposureMs: 2000, FrameCount: 10), null, CancellationToken.None)
                .ConfigureAwait(false);
            var defectStamped = await WaitUntilAsync(() => store.GetCalibrationState().DefectMap.Valid).ConfigureAwait(false);
            Assert.That(defectStamped, Is.True, "a completed defect-map build must stamp defect_map valid");
            var afterDefect = store.GetCalibrationState();
            Assert.That(afterDefect.DefectMap.LastBuiltAt, Is.Not.Null);
            Assert.That(afterDefect.DarkLibrary.Valid, Is.True, "the defect-map stamp must preserve the dark_library entry");
        }

        [Test]
        public async Task A_failed_build_does_not_stamp_calibration_state() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_dark_build_progress", _ => new JsonObject { ["active"] = false });
            // The fake maps a throwing factory to a JSON-RPC error response — the failed-build path.
            fake.OnRpc("build_dark_library", _ => throw new InvalidOperationException("camera not connected"));

            var store = new InMemoryProfileStore();
            var events = new ConcurrentQueue<string>();
            using var svc = await ConnectGuiderAsync(fake, events, profileStore: store).ConfigureAwait(false);

            _ = await svc.BuildDarkLibraryAsync(new BuildDarkLibraryRequestDto(FrameCount: 5), null, CancellationToken.None)
                .ConfigureAwait(false);
            var sawFailed = await WaitForEventAsync(events, GuiderService.DarkLibraryFailedEvent).ConfigureAwait(false);
            Assert.That(sawFailed, Is.True, "the build should fail against the erroring fake");
            var state = store.GetCalibrationState();
            Assert.That(state.DarkLibrary.Valid, Is.False, "a failed build must leave dark_library invalid");
            Assert.That(state.DarkLibrary.LastBuiltAt, Is.Null);
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition) =>
            await WaitUntilAsync(condition, TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan deadline) {
            using var cts = new CancellationTokenSource(deadline);
            try {
                while (!cts.IsCancellationRequested) {
                    if (condition()) {
                        return true;
                    }
                    await Task.Delay(50, cts.Token).ConfigureAwait(false);
                }
            } catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token) {
                // Deadline elapsed — the caller's assertion reports the miss.
            }
            return false;
        }

        private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<GuiderService> {
            public System.Collections.Generic.List<string> Warnings { get; } = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                    TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning) {
                    lock (Warnings) {
                        Warnings.Add(formatter(state, exception));
                    }
                }
            }
        }

        // The poll is drained before the terminal event, so every progress tick must precede complete on the
        // stream — a progress event after complete would flicker a WILMA progress bar backward after it finished.
        private static void AssertNoProgressAfterTerminal(ConcurrentQueue<string> events, string progressEvent, string terminalEvent) {
            var ordered = events.ToArray();
            var terminalIndex = Array.IndexOf(ordered, terminalEvent);
            Assert.That(terminalIndex, Is.GreaterThanOrEqualTo(0), "the terminal event should be on the stream");
            var lastProgressIndex = Array.LastIndexOf(ordered, progressEvent);
            Assert.That(lastProgressIndex, Is.GreaterThanOrEqualTo(0), "at least one progress event should have fired");
            Assert.That(lastProgressIndex, Is.LessThan(terminalIndex),
                "every progress event must precede the terminal (complete/failed) event on the WS stream");
        }

        // Connects the real GuiderService to the fake with a capturing WS broadcaster that records every published
        // event type into <paramref name="events"/>. Pass a logger to additionally capture log output; pass a
        // profile store to exercise the §30.7.4 calibration-state stamp.
        private static async Task<GuiderService> ConnectGuiderAsync(FakeGuider fake, ConcurrentQueue<string> events,
                Microsoft.Extensions.Logging.ILogger<GuiderService>? logger = null, IProfileStore? profileStore = null) {
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback((string type, JsonElement _, CancellationToken _) => events.Enqueue(type))
                .Returns(Task.CompletedTask);
            var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                logger ?? NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(), ws.Object, profileStore);
            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            var connected = await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false);
            Assert.That(connected, Is.Not.Null, "the guider never reached Connected against the fake");
            return svc;
        }

        private static async Task<bool> WaitForEventAsync(ConcurrentQueue<string> events, string type) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try {
                while (!cts.IsCancellationRequested) {
                    if (events.Contains(type)) {
                        return true;
                    }
                    await Task.Delay(50, cts.Token).ConfigureAwait(false);
                }
            } catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token) {
                // 15s deadline elapsed — let the caller's assertion report the miss.
            }
            return false;
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
            } catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token) {
                // Our own 15s deadline elapsed — let the caller's assertion report the miss.
            }
            return null;
        }
    }
}
