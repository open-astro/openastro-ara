#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Hosting;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using OpenAstroAra.Sequencer.Serialization;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38 — verifies the real <see cref="SequencerService"/> actually EXECUTES
    /// a deserialized sequence body through NINA's inherited sequencer (the first
    /// time a sequence runs, vs. the prior placeholder's mock <c>Task.Delay</c>
    /// loop). No-equipment instructions (Annotation, WaitForTimeSpan) run for
    /// real against the headless stub set.
    /// </summary>
    [TestFixture]
    public class SequencerServiceTest {

        private static readonly SequenceStartRequestDto StartReq = new(DryRun: false, StartFromInstructionIndex: null, ContinueOnRecoverableErrors: false);

        /// <summary>Serialize a SequentialContainer (populated by <paramref name="populate"/>) to a body JsonElement.</summary>
        private static JsonElement BuildBody(Action<SequentialContainer>? populate = null,
                HeadlessSequencerFactory? factory = null) {
            factory ??= HeadlessSequencerFactory.WithDefaults();
            var converter = new SequenceJsonConverter(factory);
            var root = new SequentialContainer { Name = "Test sequence" };
            populate?.Invoke(root);
            var json = converter.Serialize(root);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        private static SequencerService BuildService(Guid id, JsonElement? body, IWsBroadcaster? ws = null,
                IFrameRepository? frames = null, HeadlessSequencerFactory? factory = null,
                UnattendedShutdownService? unattendedShutdown = null,
                IProfileStore? profileStore = null, ICalibrationService? calibration = null,
                INotificationService? notifications = null) {
            factory ??= HeadlessSequencerFactory.WithDefaults();
            var deserializer = new SequenceBodyDeserializer(factory);
            var fake = new FakeSequenceService(id, body);
            return new SequencerService(deserializer, ws: ws, sequencesResolver: () => fake, checkpoint: null,
                frames: frames, unattendedShutdown: unattendedShutdown,
                profileStore: profileStore, calibrationResolver: () => calibration, notifications: notifications);
        }

        private static Mock<IProfileStore> ProfileWithCaptureDefault(string token) {
            var store = new Mock<IProfileStore>();
            store.Setup(p => p.GetSafetyPolicies()).Returns(new SafetyPoliciesDto(
                OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                MeridianFlipAuto: true, MeridianPauseMin: 2, MeridianRecenter: true, MeridianRecalGuider: false,
                OnAltitudeLimit: "pause", ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
                GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: false,
                CalibrationCaptureDefault: token));
            return store;
        }

        private static async Task WaitForEventAsync(RecordingWsBroadcaster ws, string type) {
            for (var i = 0; i < 100; i++) {
                if (ws.Events.Contains(type)) return;
                await Task.Delay(50);
            }
            Assert.That(ws.Events, Does.Contain(type));
        }

        private static async Task<SequenceRunStateDto?> WaitForTerminalAsync(SequencerService svc, Guid id) {
            for (var i = 0; i < 250; i++) { // up to ~5s
                var s = await svc.GetRunStateAsync(id, CancellationToken.None);
                if (s is not null && s.State is SequenceRunState.Completed or SequenceRunState.Failed or SequenceRunState.Stopped) {
                    return s;
                }
                await Task.Delay(20);
            }
            return await svc.GetRunStateAsync(id, CancellationToken.None);
        }

        private static async Task WaitForStateAsync(SequencerService svc, Guid id, SequenceRunState target) {
            for (var i = 0; i < 250; i++) { // up to ~5s
                var s = await svc.GetRunStateAsync(id, CancellationToken.None);
                if (s is not null && s.State == target) return;
                await Task.Delay(20);
            }
        }

        [Test]
        public async Task Runs_empty_sequence_to_completion() {
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody());
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state, Is.Not.Null);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed));
        }

        [Test]
        public async Task Runs_sequence_with_no_equipment_instructions_to_completion() {
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody(c => {
                c.Items.Add(new Annotation { Name = "note" });
                c.Items.Add(new WaitForTimeSpan { Time = 0 });
            }));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed));
        }

        [Test]
        public void CountTerminalLeaves_counts_disabled_as_done() {
            // A DISABLED leaf never runs (SequentialStrategy only picks CREATED), so
            // it stays DISABLED — it must count as "done" or a successful run with a
            // disabled instruction would report instructions_completed < instructions_total.
            var leaves = new List<ISequenceItem> {
                new Annotation { Status = SequenceEntityStatus.FINISHED },
                new Annotation { Status = SequenceEntityStatus.DISABLED },
                new Annotation { Status = SequenceEntityStatus.SKIPPED },
                new Annotation { Status = SequenceEntityStatus.CREATED },
                new Annotation { Status = SequenceEntityStatus.RUNNING },
            };
            // FINISHED + DISABLED + SKIPPED = 3 done; CREATED + RUNNING are not.
            Assert.That(SequencerService.CountTerminalLeaves(leaves), Is.EqualTo(3));
            Assert.That(SequencerService.RunningLeafIndex(leaves), Is.EqualTo(4));
        }

        [Test]
        public async Task Completed_run_reports_all_instructions_done() {
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody(c => {
                c.Items.Add(new Annotation { Name = "a" });
                c.Items.Add(new WaitForTimeSpan { Time = 0 });
                c.Items.Add(new Annotation { Name = "b" });
            }));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed));
            Assert.That(state.InstructionsTotal, Is.EqualTo(3), "3 leaf instructions");
            Assert.That(state.InstructionsCompleted, Is.EqualTo(3), "all completed");
            Assert.That(state.CurrentInstructionIndex, Is.Null, "nothing running at completion");
        }

        [Test]
        public async Task Run_owns_a_capture_session_from_before_execution_to_terminal() {
            // §40/§50 — the run opens its own catalog session, executes inside its
            // ambient scope (probed at each WS emit: terminal events fire inside
            // the scope, session.ended after it), and ends it on completion.
            var id = Guid.NewGuid();
            var sid = Guid.NewGuid();
            var frames = new Mock<IFrameRepository>();
            frames.Setup(f => f.CreateRunSessionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(sid);
            var scopeAtEvent = new System.Collections.Concurrent.ConcurrentDictionary<string, Guid?>();
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
              .Returns<string, JsonElement, CancellationToken>((type, _, _) => {
                  scopeAtEvent.TryAdd(type, CaptureSessionScope.Current);
                  return Task.CompletedTask;
              });

            var svc = BuildService(id, BuildBody(c => c.Items.Add(new Annotation { Name = "note" })),
                ws: ws.Object, frames: frames.Object);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed));

            frames.Verify(f => f.CreateRunSessionAsync(It.IsAny<CancellationToken>()), Times.Once);
            frames.Verify(f => f.EndSessionAsync(sid, It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(scopeAtEvent.Keys, Does.Contain("session.started"));
            Assert.That(scopeAtEvent.Keys, Does.Contain("session.ended"));
            Assert.That(scopeAtEvent["sequence.complete"], Is.EqualTo(sid),
                "execution (and its terminal emit) runs inside the run's session scope");
            Assert.That(scopeAtEvent["session.ended"], Is.Null,
                "the scope is exited before the session is closed");
            Assert.That(CaptureSessionScope.Current, Is.Null, "nothing leaks to the test flow");
        }

        [Test]
        public async Task Catalog_failure_never_blocks_the_run() {
            var id = Guid.NewGuid();
            var frames = new Mock<IFrameRepository>();
            frames.Setup(f => f.CreateRunSessionAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("catalog db locked"));

            var svc = BuildService(id, BuildBody(c => c.Items.Add(new Annotation { Name = "note" })),
                frames: frames.Object);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);

            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed),
                "a sessions-table fault must not fail imaging");
            frames.Verify(f => f.EndSessionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never,
                "no session was opened, none may be ended");
        }

        [Test]
        public async Task Missing_body_fails_the_run() {
            var id = Guid.NewGuid();
            var svc = BuildService(id, body: null);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            // Body load + the Failed transition happen on the worker now, so wait
            // for the terminal state rather than reading immediately.
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Failed));
        }

        [Test]
        public async Task GetRunState_is_null_before_any_start() {
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody());
            Assert.That(await svc.GetRunStateAsync(id, CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task Concurrent_starts_yield_a_single_coherent_run() {
            // Fire many simultaneous starts for the same id. The atomic slot
            // reservation must let exactly one win; the run resolves to a single
            // coherent terminal state with no exception/corruption from the race.
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody());
            var starts = Enumerable.Range(0, 8)
                .Select(_ => svc.StartAsync(id, StartReq, null, CancellationToken.None))
                .ToArray();
            await Task.WhenAll(starts);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed));
        }

        [Test]
        public async Task Second_start_while_running_is_idempotent_same_run() {
            // A second start for an already-running id must NOT spawn a second
            // worker — the run id stays the same (the atomic reservation keeps the
            // live run).
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody(c => c.Items.Add(new WaitForTimeSpan { Time = 30 })));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running);
            var firstRunId = (await svc.GetRunStateAsync(id, CancellationToken.None))!.RunId;
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            var secondRunId = (await svc.GetRunStateAsync(id, CancellationToken.None))!.RunId;
            Assert.That(secondRunId, Is.EqualTo(firstRunId));
            await svc.AbortAsync(id, null, CancellationToken.None); // clean up the long run
        }

        [Test]
        public async Task Skip_current_skips_the_running_item_and_advances() {
            // §38 — the only item is a 30s wait; skipping the current item cancels it so the run
            // completes well within that wait (proving SkipCurrentRunningItems reaches the run).
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody(c => c.Items.Add(new WaitForTimeSpan { Time = 30 })));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running);

            // Poll-skip until the run terminates rather than sleeping a fixed interval and
            // skipping once: until the 30s wait leaf has registered as a running item, skip
            // is a harmless no-op, so retrying absorbs scheduler starvation on a busy CI host
            // without ever waiting out the full 30s wait. Each skip is idempotent (no-op on a
            // terminal run), so over-calling is safe.
            SequenceRunStateDto? state = null;
            for (var i = 0; i < 250; i++) { // up to ~5s
                await svc.SkipAsync(id, null, CancellationToken.None);
                state = await svc.GetRunStateAsync(id, CancellationToken.None);
                if (state is not null && state.State is SequenceRunState.Completed or SequenceRunState.Failed or SequenceRunState.Stopped) {
                    break;
                }
                await Task.Delay(20);
            }
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed),
                "skipping the running 30s wait should let the run finish promptly");
        }

        [Test]
        public async Task Skip_on_unknown_run_is_an_accepted_noop() {
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody());
            Assert.That(await svc.SkipAsync(id, null, CancellationToken.None), Is.Not.Null);
        }

        [Test]
        public async Task Host_shutdown_stops_live_runs() {
            // On daemon shutdown (IHostedService.StopAsync), in-flight runs must be
            // cancelled rather than abandoned mid-execution.
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody(c => c.Items.Add(new WaitForTimeSpan { Time = 30 })));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running);
            await ((IHostedService)svc).StopAsync(CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Stopped));
        }

        [Test]
        public async Task Abort_during_run_stops_the_sequence() {
            // A long wait keeps the run in Running long enough to abort it; the
            // run must end as Stopped (not mis-reported Completed — guards the
            // abort-state-vs-worker race).
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody(c => c.Items.Add(new WaitForTimeSpan { Time = 30 })));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running); // deterministic, not a fixed sleep
            await svc.AbortAsync(id, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Stopped));
        }

        [Test]
        public async Task AbortActiveRunsAsync_halts_the_running_sequence_and_counts_only_real_aborts() {
            // §29 hard-stop path: a running sequence is aborted and counted once; a re-entrant call (the disk
            // oscillating back to Critical) finds it already Aborting/terminal and counts nothing.
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody(c => c.Items.Add(new WaitForTimeSpan { Time = 30 })));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running);

            var aborted = await svc.AbortActiveRunsAsync(CancellationToken.None);
            Assert.That(aborted, Is.EqualTo(1));

            var second = await svc.AbortActiveRunsAsync(CancellationToken.None);
            Assert.That(second, Is.EqualTo(0), "an already-aborting/terminal run is not re-counted");

            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Stopped));
        }

        [Test]
        public async Task Completed_run_emits_started_then_complete_WS_events() {
            var id = Guid.NewGuid();
            var ws = new RecordingWsBroadcaster();
            var svc = BuildService(id, BuildBody(), ws);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForTerminalAsync(svc, id);
            Assert.That(ws.Events, Does.Contain("sequence.started"));
            Assert.That(ws.Events, Does.Contain("sequence.complete"));
            Assert.That(ws.Events, Does.Not.Contain("sequence.aborted"));
            Assert.That(ws.Events, Does.Not.Contain("sequence.stopped"));
        }

        [Test]
        public async Task Pause_suspends_at_the_instruction_boundary_and_resume_completes() {
            // §38 pause: the current instruction (a 2s wait) always finishes; the
            // run then suspends BETWEEN instructions, reports Paused, and emits
            // sequence.paused. Resume releases the gate, emits sequence.resumed,
            // and the run completes.
            var id = Guid.NewGuid();
            var ws = new RecordingWsBroadcaster();
            var svc = BuildService(id, BuildBody(c => {
                c.Items.Add(new WaitForTimeSpan { Time = 2 });
                c.Items.Add(new Annotation { Name = "after-pause" });
            }), ws);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running);

            await svc.PauseAsync(id, null, CancellationToken.None);
            // The 2s wait must finish first — Paused is reported only when the
            // engine actually suspends at the boundary, never on the mere request.
            await WaitForStateAsync(svc, id, SequenceRunState.Paused);
            var paused = await svc.GetRunStateAsync(id, CancellationToken.None);
            Assert.That(paused!.State, Is.EqualTo(SequenceRunState.Paused));
            Assert.That(paused.InstructionsCompleted, Is.EqualTo(1), "the in-flight wait ran to completion before the suspension");
            Assert.That(ws.Events, Does.Contain("sequence.paused"));

            // Suspended means suspended: the run must still be Paused after a beat.
            await Task.Delay(200);
            Assert.That((await svc.GetRunStateAsync(id, CancellationToken.None))!.State, Is.EqualTo(SequenceRunState.Paused));

            await svc.ResumeAsync(id, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed));
            Assert.That(state.InstructionsCompleted, Is.EqualTo(2), "the post-pause instruction ran after resume");
            Assert.That(ws.Events, Does.Contain("sequence.resumed"));
        }

        [Test]
        public async Task Start_with_ask_default_emits_the_auto_flats_prompt() {
            var id = Guid.NewGuid();
            var ws = new RecordingWsBroadcaster();
            var svc = BuildService(id, BuildBody(c => c.Items.Add(new Annotation { Name = "one" })), ws,
                profileStore: ProfileWithCaptureDefault("ask").Object);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForEventAsync(ws, "sequence.auto_flats_prompt");
        }

        [Test]
        public async Task Profile_panel_at_end_generates_and_starts_flats_after_completion() {
            // §48.1 end-to-end: the profile default auto-decides (Decided event,
            // source=profile), the run completes with its catalog session, and
            // the §39.5 generator is invoked + the flats run is kicked off.
            var id = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var ws = new RecordingWsBroadcaster();
            var frames = new Mock<IFrameRepository>();
            frames.Setup(f => f.CreateRunSessionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(sessionId);
            frames.Setup(f => f.EndSessionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var calibration = new Mock<ICalibrationService>();
            var generated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            calibration.Setup(c => c.GenerateMatchingFlatsAsync(sessionId, It.IsAny<MatchingFlatsRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback(() => generated.TrySetResult())
                .ReturnsAsync(new GeneratedFlatSequenceDto(sessionId, Guid.NewGuid(), "Flats — tonight", 30,
                    Array.Empty<GeneratedFlatStepDto>()));
            var notifications = new Mock<INotificationService>();
            notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var svc = BuildService(id, BuildBody(c => c.Items.Add(new Annotation { Name = "one" })), ws,
                frames: frames.Object, profileStore: ProfileWithCaptureDefault("panel_at_end").Object,
                calibration: calibration.Object, notifications: notifications.Object);

            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForTerminalAsync(svc, id);

            Assert.That(await Task.WhenAny(generated.Task, Task.Delay(TimeSpan.FromSeconds(10))), Is.SameAs(generated.Task),
                "completion of a panel_at_end run must invoke the §39.5 generator with the run's session");
            await WaitForEventAsync(ws, "sequence.auto_flats_decided");
            Assert.That(ws.Events, Does.Not.Contain("sequence.auto_flats_prompt"), "a profile default never prompts");
        }

        [Test]
        public async Task An_aborted_run_never_triggers_end_of_session_flats() {
            var id = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var frames = new Mock<IFrameRepository>();
            frames.Setup(f => f.CreateRunSessionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(sessionId);
            frames.Setup(f => f.EndSessionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var calibration = new Mock<ICalibrationService>();
            var svc = BuildService(id, BuildBody(c => c.Items.Add(new WaitForTimeSpan { Time = 3600 })),
                new RecordingWsBroadcaster(), frames: frames.Object,
                profileStore: ProfileWithCaptureDefault("panel_at_end").Object, calibration: calibration.Object);

            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running);
            await svc.AbortAsync(id, null, CancellationToken.None);
            await WaitForTerminalAsync(svc, id);
            await Task.Delay(200);

            calibration.Verify(c => c.GenerateMatchingFlatsAsync(It.IsAny<Guid>(), It.IsAny<MatchingFlatsRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
                Times.Never, "the user ended the night on their own terms — no auto-flats");
        }

        [Test]
        public async Task ProvideDecision_records_the_choice_and_remember_persists_the_profile() {
            var id = Guid.NewGuid();
            var ws = new RecordingWsBroadcaster();
            var store = ProfileWithCaptureDefault("ask");
            SafetyPoliciesDto? persisted = null;
            store.Setup(p => p.PutSafetyPolicies(It.IsAny<SafetyPoliciesDto>()))
                .Callback<SafetyPoliciesDto>(p => persisted = p);
            var svc = BuildService(id, BuildBody(c => c.Items.Add(new WaitForTimeSpan { Time = 3600 })), ws,
                profileStore: store.Object);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running);

            await svc.ProvideDecisionAsync(id, new AutoFlatsDecisionRequestDto("panel_at_end", Remember: true), null, CancellationToken.None);
            await WaitForEventAsync(ws, "sequence.auto_flats_decided");
            Assert.That(persisted?.CalibrationCaptureDefault, Is.EqualTo("panel_at_end"));

            Assert.ThrowsAsync<ArgumentException>(() =>
                svc.ProvideDecisionAsync(id, new AutoFlatsDecisionRequestDto("do_a_barrel_roll", Remember: false), null, CancellationToken.None));

            await svc.AbortAsync(id, null, CancellationToken.None);
            await WaitForTerminalAsync(svc, id);
        }

        [Test]
        public async Task PauseActiveRunsAsync_pauses_the_running_run_and_ResumeRunsAsync_releases_it() {
            // §35 safety bulk pause/resume: PauseActiveRunsAsync arms the gate on the
            // running run and reports its id; ResumeRunsAsync releases exactly that
            // run (same boundary semantics as the user pause above).
            var id = Guid.NewGuid();
            var ws = new RecordingWsBroadcaster();
            var svc = BuildService(id, BuildBody(c => {
                c.Items.Add(new WaitForTimeSpan { Time = 2 });
                c.Items.Add(new Annotation { Name = "after-safety-pause" });
            }), ws);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running);

            var pausedIds = await svc.PauseActiveRunsAsync(CancellationToken.None);
            Assert.That(pausedIds, Is.EquivalentTo(new[] { id }));
            await WaitForStateAsync(svc, id, SequenceRunState.Paused);

            // A second bulk pause finds nothing to arm — the run is already Paused,
            // so the safety engine can never "adopt" a pause it didn't create.
            Assert.That(await svc.PauseActiveRunsAsync(CancellationToken.None), Is.Empty);

            var resumed = await svc.ResumeRunsAsync(pausedIds, CancellationToken.None);
            Assert.That(resumed, Is.EqualTo(1));
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed));
            Assert.That(ws.Events, Does.Contain("sequence.resumed"));
        }

        [Test]
        public async Task SkipActiveRunsAsync_advances_a_running_run_past_its_current_instruction() {
            // §42.2 skip_target: the bulk skip cancels the currently-running items
            // (here a long wait) so the engine advances — the run completes early
            // instead of sitting out the full wait.
            var id = Guid.NewGuid();
            var ws = new RecordingWsBroadcaster();
            var svc = BuildService(id, BuildBody(c => {
                c.Items.Add(new WaitForTimeSpan { Time = 3600 });
                c.Items.Add(new Annotation { Name = "after-skip" });
            }), ws);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running);

            var skipped = await svc.SkipActiveRunsAsync(CancellationToken.None);
            Assert.That(skipped, Is.EqualTo(1));
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed),
                "the hour-long wait was skipped, so the run finishes promptly");
        }

        [Test]
        public async Task Failed_meridian_flip_pauses_the_run_awaiting_user_and_resume_reattempts_the_flip() {
            // §58.12 END-TO-END through the real engine: a MeridianFlipTrigger whose
            // executor fails arms the pause gate as AwaitingUser → the run suspends
            // BEFORE the next instruction (imaging must not continue on a rig in
            // safe rest) and reports PausedAwaitingUser. The user's explicit resume
            // releases it; the trigger re-fires at the next boundary, this time the
            // flip succeeds, and the run completes.
            var coords = new OpenAstroAra.Astrometry.Coordinates(
                OpenAstroAra.Astrometry.Angle.ByHours(5), OpenAstroAra.Astrometry.Angle.ByDegree(20),
                OpenAstroAra.Astrometry.Epoch.J2000);
            var telescope = new Mock<OpenAstroAra.Equipment.Interfaces.Mediator.ITelescopeMediator>();
            telescope.Setup(t => t.GetCurrentPosition()).Returns(coords);
            telescope.Setup(t => t.GetInfo()).Returns(new OpenAstroAra.Equipment.Equipment.MyTelescope.TelescopeInfo {
                Connected = true,
                TrackingEnabled = true,
                TimeToMeridianFlip = 0, // the flip window has arrived at the very first boundary
                Coordinates = coords,
            });
            var flipSucceeds = false;
            var executor = new Mock<OpenAstroAra.Sequencer.Trigger.MeridianFlip.IMeridianFlipExecutor>();
            executor
                .Setup(x => x.MeridianFlip(It.IsAny<OpenAstroAra.Astrometry.Coordinates>(), It.IsAny<TimeSpan>(),
                    It.IsAny<IProgress<OpenAstroAra.Core.Model.ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(flipSucceeds));
            // A real profile (not a loose mock — other prototypes read it during
            // construction) with side-of-pier off so ShouldTrigger is purely time-based.
            var profile = new HeadlessProfileService();
            profile.ActiveProfile.MeridianFlipSettings.UseSideOfPier = false;
            var factory = HeadlessSequencerFactory.WithDefaults(
                telescopeMediator: telescope.Object, profileService: profile,
                meridianFlipExecutor: executor.Object);

            var id = Guid.NewGuid();
            var ws = new RecordingWsBroadcaster();
            var svc = BuildService(id, BuildBody(c => {
                c.Triggers.Add(new OpenAstroAra.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger(
                    profile, telescope.Object, executor.Object));
                c.Items.Add(new Annotation { Name = "first" });
                c.Items.Add(new Annotation { Name = "second" });
            }, factory), ws, factory: factory);

            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.PausedAwaitingUser);
            var paused = await svc.GetRunStateAsync(id, CancellationToken.None);
            Assert.That(paused!.State, Is.EqualTo(SequenceRunState.PausedAwaitingUser));
            Assert.That(paused.InstructionsCompleted, Is.EqualTo(0),
                "the suspension lands BEFORE the instruction that follows the failed flip");
            Assert.That(ws.Events, Does.Contain("sequence.paused"));

            // Suspended means suspended — nothing advances while the user is away.
            await Task.Delay(200);
            Assert.That((await svc.GetRunStateAsync(id, CancellationToken.None))!.State,
                Is.EqualTo(SequenceRunState.PausedAwaitingUser));

            // §35 guard: the safety engine's automated bulk resume must never
            // settle a debt owed to the user — the run stays PausedAwaitingUser.
            Assert.That(await svc.ResumeRunsAsync(new[] { id }, CancellationToken.None), Is.EqualTo(0));
            Assert.That((await svc.GetRunStateAsync(id, CancellationToken.None))!.State,
                Is.EqualTo(SequenceRunState.PausedAwaitingUser));

            // The user sorts the rig out and resumes: the trigger re-fires at the
            // next boundary and this time the flip goes through.
            flipSucceeds = true;
            await svc.ResumeAsync(id, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed));
            Assert.That(state.InstructionsCompleted, Is.EqualTo(2), "both instructions ran after resume");
            Assert.That(ws.Events, Does.Contain("sequence.resumed"));
            executor.Verify(x => x.MeridianFlip(It.IsAny<OpenAstroAra.Astrometry.Coordinates>(), It.IsAny<TimeSpan>(),
                It.IsAny<IProgress<OpenAstroAra.Core.Model.ApplicationStatus>>(), It.IsAny<CancellationToken>()),
                Times.AtLeast(2), "the flip was re-attempted after resume");
        }

        [Test]
        public async Task Awaiting_user_pause_arms_the_unattended_shutdown_countdown_and_resume_cancels_it() {
            // §58.12 integration: the engine entering PausedAwaitingUser starts
            // the countdown; the user's explicit resume (the "user came back"
            // signal) cancels it. Countdown wait is left at real minutes so the
            // ladder can never fire inside this test.
            var coords = new OpenAstroAra.Astrometry.Coordinates(
                OpenAstroAra.Astrometry.Angle.ByHours(5), OpenAstroAra.Astrometry.Angle.ByDegree(20),
                OpenAstroAra.Astrometry.Epoch.J2000);
            var telescope = new Mock<OpenAstroAra.Equipment.Interfaces.Mediator.ITelescopeMediator>();
            telescope.Setup(t => t.GetCurrentPosition()).Returns(coords);
            telescope.Setup(t => t.GetInfo()).Returns(new OpenAstroAra.Equipment.Equipment.MyTelescope.TelescopeInfo {
                Connected = true, TrackingEnabled = true, TimeToMeridianFlip = 0, Coordinates = coords,
            });
            var flipSucceeds = false;
            var executor = new Mock<OpenAstroAra.Sequencer.Trigger.MeridianFlip.IMeridianFlipExecutor>();
            executor
                .Setup(x => x.MeridianFlip(It.IsAny<OpenAstroAra.Astrometry.Coordinates>(), It.IsAny<TimeSpan>(),
                    It.IsAny<IProgress<OpenAstroAra.Core.Model.ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(flipSucceeds));
            var profile = new HeadlessProfileService();
            profile.ActiveProfile.MeridianFlipSettings.UseSideOfPier = false;
            var factory = HeadlessSequencerFactory.WithDefaults(
                telescopeMediator: telescope.Object, profileService: profile,
                meridianFlipExecutor: executor.Object);

            // Default profile policy (no IProfileStore) = shutdown enabled, 10 min.
            using var shutdown = new UnattendedShutdownService();
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody(c => {
                c.Triggers.Add(new OpenAstroAra.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger(
                    profile, telescope.Object, executor.Object));
                c.Items.Add(new Annotation { Name = "first" });
            }, factory), factory: factory, unattendedShutdown: shutdown);

            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.PausedAwaitingUser);
            Assert.That(shutdown.IsCountingDown, Is.True,
                "entering PausedAwaitingUser must start the §58.12 clock");

            flipSucceeds = true;
            await svc.ResumeAsync(id, null, CancellationToken.None);
            Assert.That(shutdown.IsCountingDown, Is.False,
                "the explicit resume IS the user coming back — countdown cancelled");
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed));
        }

        [Test]
        public async Task Abort_wins_over_a_pause_awaiting_user() {
            // §58.12: abort must win over an awaiting-user suspension exactly as it
            // does over an ordinary pause — the run ends Stopped, never wedged.
            var coords = new OpenAstroAra.Astrometry.Coordinates(
                OpenAstroAra.Astrometry.Angle.ByHours(5), OpenAstroAra.Astrometry.Angle.ByDegree(20),
                OpenAstroAra.Astrometry.Epoch.J2000);
            var telescope = new Mock<OpenAstroAra.Equipment.Interfaces.Mediator.ITelescopeMediator>();
            telescope.Setup(t => t.GetCurrentPosition()).Returns(coords);
            telescope.Setup(t => t.GetInfo()).Returns(new OpenAstroAra.Equipment.Equipment.MyTelescope.TelescopeInfo {
                Connected = true, TrackingEnabled = true, TimeToMeridianFlip = 0, Coordinates = coords,
            });
            var executor = new Mock<OpenAstroAra.Sequencer.Trigger.MeridianFlip.IMeridianFlipExecutor>();
            executor
                .Setup(x => x.MeridianFlip(It.IsAny<OpenAstroAra.Astrometry.Coordinates>(), It.IsAny<TimeSpan>(),
                    It.IsAny<IProgress<OpenAstroAra.Core.Model.ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var profile = new HeadlessProfileService();
            profile.ActiveProfile.MeridianFlipSettings.UseSideOfPier = false;
            var factory = HeadlessSequencerFactory.WithDefaults(
                telescopeMediator: telescope.Object, profileService: profile,
                meridianFlipExecutor: executor.Object);

            var id = Guid.NewGuid();
            var ws = new RecordingWsBroadcaster();
            var svc = BuildService(id, BuildBody(c => {
                c.Triggers.Add(new OpenAstroAra.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger(
                    profile, telescope.Object, executor.Object));
                c.Items.Add(new Annotation { Name = "never-reached" });
            }, factory), ws, factory: factory);

            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.PausedAwaitingUser);

            await svc.AbortAsync(id, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Stopped));
            Assert.That(ws.Events, Does.Contain("sequence.aborted"));
        }

        [Test]
        public async Task Abort_wins_over_an_active_pause() {
            // Abort while suspended at the gate must cancel the wait and end the
            // run as Stopped (aborted event) — never leave it wedged in Paused.
            var id = Guid.NewGuid();
            var ws = new RecordingWsBroadcaster();
            var svc = BuildService(id, BuildBody(c => {
                c.Items.Add(new WaitForTimeSpan { Time = 2 });
                c.Items.Add(new WaitForTimeSpan { Time = 30 });
            }), ws);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running);
            await svc.PauseAsync(id, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Paused);

            await svc.AbortAsync(id, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Stopped));
            Assert.That(ws.Events, Does.Contain("sequence.aborted"));
        }

        [Test]
        public async Task Pause_racing_completion_is_a_harmless_accepted_noop() {
            // A pause request that never reaches another instruction boundary
            // (the run completes first) must not wedge or mislabel the run.
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody(c => c.Items.Add(new Annotation { Name = "only" })));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await svc.PauseAsync(id, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed).Or.EqualTo(SequenceRunState.Paused));
            if (state.State == SequenceRunState.Paused) {
                // The request landed before the last boundary — resume finishes it.
                await svc.ResumeAsync(id, null, CancellationToken.None);
                state = await WaitForTerminalAsync(svc, id);
                Assert.That(state!.State, Is.EqualTo(SequenceRunState.Completed));
            }
        }

        [Test]
        public async Task Pause_and_resume_on_unknown_or_finished_runs_are_accepted_noops() {
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody());
            // Unknown run — accepted, nothing to do.
            Assert.That(await svc.PauseAsync(id, null, CancellationToken.None), Is.Not.Null);
            Assert.That(await svc.ResumeAsync(id, null, CancellationToken.None), Is.Not.Null);
            // Finished run — still accepted no-ops.
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForTerminalAsync(svc, id);
            Assert.That(await svc.PauseAsync(id, null, CancellationToken.None), Is.Not.Null);
            Assert.That(await svc.ResumeAsync(id, null, CancellationToken.None), Is.Not.Null);
            Assert.That((await svc.GetRunStateAsync(id, CancellationToken.None))!.State, Is.EqualTo(SequenceRunState.Completed));
        }

        [Test]
        public async Task Host_shutdown_stops_a_paused_run() {
            // Daemon shutdown must not hang on a suspended gate — the cancelled
            // token aborts the wait and the run ends Stopped.
            var id = Guid.NewGuid();
            var svc = BuildService(id, BuildBody(c => {
                c.Items.Add(new WaitForTimeSpan { Time = 2 });
                c.Items.Add(new WaitForTimeSpan { Time = 30 });
            }));
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Running);
            await svc.PauseAsync(id, null, CancellationToken.None);
            await WaitForStateAsync(svc, id, SequenceRunState.Paused);
            await ((IHostedService)svc).StopAsync(CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Stopped));
        }

        [Test]
        public async Task Abort_during_body_load_reports_stopped_not_failed() {
            // GetAsync blocks (honoring the token) so abort lands while the body is
            // still loading; the run must end Stopped, not Failed.
            var id = Guid.NewGuid();
            var factory = HeadlessSequencerFactory.WithDefaults();
            var deserializer = new SequenceBodyDeserializer(factory);
            var fake = new DelayedSequenceService(id, BuildBody());
            var svc = new SequencerService(deserializer, ws: null, sequencesResolver: () => fake, checkpoint: null);
            await svc.StartAsync(id, StartReq, null, CancellationToken.None);
            // The run is registered synchronously by StartAsync, and the cancelled
            // token makes the blocking GetAsync throw whenever the worker reaches it
            // — so aborting now is deterministic, no fixed sleep needed to hit a window.
            Assert.That(await svc.GetRunStateAsync(id, CancellationToken.None), Is.Not.Null);
            await svc.AbortAsync(id, null, CancellationToken.None);
            var state = await WaitForTerminalAsync(svc, id);
            Assert.That(state!.State, Is.EqualTo(SequenceRunState.Stopped));
        }

        /// <summary>ISequenceService whose GetAsync blocks (honoring the token) to exercise abort-during-load.</summary>
        private sealed class DelayedSequenceService : ISequenceService {
            private readonly Guid _id;
            private readonly JsonElement _body;
            public DelayedSequenceService(Guid id, JsonElement body) { _id = id; _body = body; }

            public async Task<SequenceDto?> GetAsync(Guid id, CancellationToken ct) {
                await Task.Delay(TimeSpan.FromSeconds(30), ct); // cancelled by abort -> OperationCanceledException
                if (id != _id) return null;
                return new SequenceDto(id, "Test", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, _body, null);
            }

            public Task<CursorPage<SequenceListItemDto>> ListAsync(int limit, string? cursor, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceDto> CreateAsync(SequenceCreateRequestDto request, string? idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceUpdateResult> UpdateAsync(Guid id, SequenceUpdateRequestDto request, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceDeleteResult> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceShareDto?> ShareExportAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        }

        /// <summary>Records the event types published, to assert the WS lifecycle.</summary>
        private sealed class RecordingWsBroadcaster : IWsBroadcaster {
            private readonly System.Collections.Concurrent.ConcurrentQueue<string> _events = new();
            public System.Collections.Generic.IReadOnlyCollection<string> Events => _events;
            public long CurrentSequence => _events.Count;
            public Task PublishAsync(string eventType, JsonElement payload, CancellationToken ct) {
                _events.Enqueue(eventType);
                return Task.CompletedTask;
            }
        }

        /// <summary>Minimal ISequenceService whose GetAsync returns the test body; the rest are unused.</summary>
        private sealed class FakeSequenceService : ISequenceService {
            private readonly Guid _id;
            private readonly JsonElement? _body;
            public FakeSequenceService(Guid id, JsonElement? body) { _id = id; _body = body; }

            public Task<SequenceDto?> GetAsync(Guid id, CancellationToken ct) {
                if (id != _id || _body is null) return Task.FromResult<SequenceDto?>(null);
                return Task.FromResult<SequenceDto?>(new SequenceDto(
                    Id: id, Name: "Test", Description: null,
                    CreatedUtc: DateTimeOffset.UnixEpoch, ModifiedUtc: DateTimeOffset.UnixEpoch,
                    Body: _body.Value, TemplateOrigin: null));
            }

            public Task<CursorPage<SequenceListItemDto>> ListAsync(int limit, string? cursor, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceDto> CreateAsync(SequenceCreateRequestDto request, string? idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceUpdateResult> UpdateAsync(Guid id, SequenceUpdateRequestDto request, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceDeleteResult> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
            public Task<SequenceShareDto?> ShareExportAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        }
    }
}
