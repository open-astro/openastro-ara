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
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// bench-3: the first end-to-end virtual-observatory scenario — the REAL §63
    /// <see cref="GuiderService"/> / <c>PHD2Guider</c> driven against the bench's
    /// <see cref="FakeGuider"/> over the loopback PHD2 wire protocol, no C++ daemon.
    ///
    /// Proves the connect path the §63 deployment exercises: the daemon connects to an
    /// already-running guider on localhost (no GUI process launch — the bench surfaced
    /// that the inherited NINA-desktop <c>StartPHD2Process</c> blocked this, now retired),
    /// opens the event-stream connection, reaches <c>Connected</c>, and reflects the live
    /// event stream (guiding state, GuideStep RMS) into its status — all the guider-path
    /// fixes the bench surfaced (#403 lifecycle, #404 read-driven listener, #405 SendMessage
    /// timeout) are exercised end-to-end here.
    /// </summary>
    [TestFixture]
    [Category("bench")] // §42.2 virtual-observatory bench — selected by bench/ (TestCategory=bench)
    public class GuiderFakeIntegrationTest {

        private static GuiderRecoveryCoordinator NewRecovery() =>
            new(Mock.Of<IGuiderProcessSupervisor>(),
                Mock.Of<INotificationService>(),
                Mock.Of<IDiagnosticsService>(),
                NullLogger<GuiderRecoveryCoordinator>.Instance);

        [Test]
        public async Task The_real_client_connects_to_the_fake_and_speaks_the_phd2_handshake() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_pixel_scale", JsonValue.Create(1.5));
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

            // Connect to the already-listening fake on localhost — no process launch. The
            // background connect opens the event-stream connection and runs the §63.4/.5 RPC
            // handshake (get_profile(s), set_*, get_pixel_scale, …) against the fake.
            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);

            // The real client drove the PHD2 JSON-RPC handshake through the fake.
            var spoke = await WaitUntilAsync(() => fake.ReceivedMethods.Count > 0).ConfigureAwait(false);
            Assert.That(spoke, Is.True, "the real guider client never opened/queried the fake guider");
            Assert.That(fake.ReceivedMethods, Does.Contain("get_profile").IgnoreCase
                .Or.Contain("get_profiles").IgnoreCase,
                "the connect handshake should query the guider's profiles");
            // svc is `using`-scoped — its Dispose tears the connection down; no explicit disconnect needed.
        }

        [Test]
        public async Task An_empty_connect_body_keeps_the_profiles_remote_host_and_port() {
            // A remote-PHD2 profile (SBC at :8080) must survive a bodyless POST /connect:
            // the old non-null DTO defaults overwrote the profile with localhost:4400 and
            // silently repointed every later reconnect/recovery at the wrong machine.
            var profiles = new HeadlessProfileService();
            profiles.ActiveProfile.GuiderSettings.PHD2ServerHost = "sbc.local";
            profiles.ActiveProfile.GuiderSettings.PHD2ServerPort = 8080;
            using var svc = new GuiderService(profiles, NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

            await svc.ConnectAsync(new GuiderConnectRequestDto(), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);

            Assert.That(profiles.ActiveProfile.GuiderSettings.PHD2ServerHost, Is.EqualTo("sbc.local"),
                "null request host must not clobber the profile's remote host");
            Assert.That(profiles.ActiveProfile.GuiderSettings.PHD2ServerPort, Is.EqualTo(8080));
        }

        [Test]
        public async Task A_connect_naming_a_DIFFERENT_target_reconnects_instead_of_no_op() {
            // The wizard's Test connection while the daemon is still linked to the OLD
            // profile's guider: an idempotent no-op accept here would false-positive
            // ('connected' status + the NEW host in the UI message). A different target
            // must tear down the live session and dial the new host:port.
            await using var fakeA = FakeGuider.Start();
            fakeA.SetOnConnectEvents(PhdEvents.Version(subver: "a"), PhdEvents.AppState("Stopped"));
            await using var fakeB = FakeGuider.Start();
            fakeB.SetOnConnectEvents(PhdEvents.Version(subver: "b"), PhdEvents.AppState("Stopped"));
            var profiles = new HeadlessProfileService();
            using var svc = new GuiderService(profiles, NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fakeA.Port),
                idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var spokeA = await WaitUntilAsync(() => fakeA.ReceivedMethods.Count > 0).ConfigureAwait(false);
            Assert.That(spokeA, Is.True, "precondition: the first target must be dialed");

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fakeB.Port),
                idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);

            var spokeB = await WaitUntilAsync(() => fakeB.ReceivedMethods.Count > 0).ConfigureAwait(false);
            Assert.That(spokeB, Is.True, "a NEW target must actually be dialed, not no-op'd");
            Assert.That(profiles.ActiveProfile.GuiderSettings.PHD2ServerPort, Is.EqualTo(fakeB.Port));
        }

        [Test]
        public async Task A_repeat_connect_to_the_SAME_target_stays_an_idempotent_no_op() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "x"), PhdEvents.AppState("Stopped"));
            var profiles = new HeadlessProfileService();
            using var svc = new GuiderService(profiles, NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port),
                idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            await WaitUntilAsync(() => fake.ReceivedMethods.Count > 0).ConfigureAwait(false);
            var methodsAfterFirst = fake.ReceivedMethods.Count;

            // Same explicit target + a blank-body repeat: both no-ops (§60.5).
            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port),
                idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            await svc.ConnectAsync(new GuiderConnectRequestDto(),
                idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);

            await Task.Delay(300).ConfigureAwait(false);
            Assert.That(fake.ConnectionCount, Is.EqualTo(1),
                "same-target repeats must not tear down the live session");
            Assert.That(fake.ReceivedMethods.Count, Is.GreaterThanOrEqualTo(methodsAfterFirst));
        }

        [Test]
        public async Task An_explicit_connect_body_still_rewrites_the_profiles_target() {
            var profiles = new HeadlessProfileService();
            using var svc = new GuiderService(profiles, NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

            await svc.ConnectAsync(new GuiderConnectRequestDto("sbc.local", 8080),
                idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(profiles.ActiveProfile.GuiderSettings.PHD2ServerHost, Is.EqualTo("sbc.local"));
            Assert.That(profiles.ActiveProfile.GuiderSettings.PHD2ServerPort, Is.EqualTo(8080));
        }

        [Test]
        public async Task The_connect_handshake_queries_get_version_for_fork_identification() {
            // §63.9: the real client must run the synchronous get_version handshake on connect so it can
            // tell openastro-guider from stock PHD2 (and read overlap_support). The fake serves the fork
            // result; here we assert the RPC is actually issued end-to-end through the real PHD2Guider.
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_version", _ => new JsonObject {
                ["version"] = "2.6.11dev5",
                ["phd_version"] = "2.6.11",
                ["phd_subver"] = "dev5",
                ["msg_version"] = 1,
                ["overlap_support"] = true,
                ["fork"] = "openastro-guider",
            });
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);

            var asked = await WaitUntilAsync(() => System.Linq.Enumerable.Contains(fake.ReceivedMethods, "get_version")).ConfigureAwait(false);
            Assert.That(asked, Is.True, "the §63.9 connect handshake must call get_version for fork identification");
        }

        [Test]
        public async Task Deleting_an_ara_profile_deletes_its_guider_twin_with_dark_files() {
            // §63.4 delete hook — the service maps the deleted ARA profile to its
            // ara-<slug>-<id8> twin and fires delete_profile with delete_dark_files=true.
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            string? deletedName = null;
            bool? darkFiles = null;
            // The factory receives the WHOLE JSON-RPC request; params sit under "params".
            fake.OnRpc("delete_profile", req => {
                var p = req["params"]?.AsObject();
                deletedName = p?["name"]?.GetValue<string>();
                darkFiles = p?["delete_dark_files"]?.GetValue<bool>();
                return JsonValue.Create(0);
            });
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false),
                Is.Not.Null, "the guider never reached Connected against the fake");

            var profileId = Guid.NewGuid();
            var ok = await svc.TryDeleteAraGuiderProfileAsync("Old Rig C8", profileId, CancellationToken.None)
                .ConfigureAwait(false);

            Assert.That(ok, Is.True, "the fake accepted the delete, so the hook reports success");
            Assert.That(deletedName, Is.EqualTo(PHD2Guider.AraGuiderProfileName("Old Rig C8", profileId)),
                "the twin's id-suffixed name — the same one the connect path creates");
            Assert.That(darkFiles, Is.True, "§63.4: the twin's dark library goes with it");
        }

        [Test]
        public void The_selected_twin_guard_blocks_only_an_exact_name_match() {
            // The selected twin tracks the last guider CONNECT; an ARA profile switch alone
            // doesn't re-map it, so a deletable (non-active) ARA profile's twin can still be
            // the daemon's selected profile — the hook must refuse that delete client-side
            // (round-2 review finding). The guard is Ordinal on the exact twin name; a
            // daemon with no selected profile never blocks.
            var id = Guid.NewGuid();
            var twin = PHD2Guider.AraGuiderProfileName("Switched Rig", id);
            Assert.That(GuiderService.IsTwinSelectedOnDaemon(twin, twin), Is.True,
                "the connect-time twin still selected on the daemon blocks the delete");
            Assert.That(GuiderService.IsTwinSelectedOnDaemon(twin, null), Is.False,
                "no selected profile → nothing to protect");
            Assert.That(GuiderService.IsTwinSelectedOnDaemon(twin, "ara-some-other-rig-12345678"), Is.False,
                "a different selected twin doesn't block");
            Assert.That(GuiderService.IsTwinSelectedOnDaemon(twin, twin.ToUpperInvariant()), Is.False,
                "PHD2 profile names are exact identifiers — the comparison is Ordinal");
        }

        [Test]
        public async Task Profile_delete_hook_is_a_quiet_noop_when_no_guider_is_connected() {
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            var ok = await svc.TryDeleteAraGuiderProfileAsync("Ghost rig", Guid.NewGuid(), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(ok, Is.False, "disconnected → false, never a throw (best-effort contract)");
        }

        [Test]
        public async Task Reaches_Connected_and_reflects_live_guiding_events() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);

            var connected = await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false);
            Assert.That(connected, Is.Not.Null, "the service never reached Connected against the fake guider");

            // Guiding state propagates from a live AppState event over the listener connection.
            // The fake sends the raw PHD2 token "Guiding"; GuiderService.MapGuidingState normalizes
            // it to the lowercase §63.2 DTO token "guiding", which is what we assert here.
            // Runtime is null-guarded: it can briefly lag the Connected transition before the
            // first status snapshot populates it, and the predicate should poll-on rather than throw.
            await fake.BroadcastAsync(PhdEvents.AppState("Guiding")).ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.Runtime?.State == "guiding").ConfigureAwait(false), Is.Not.Null,
                "an AppState=Guiding event did not reach the runtime state");

            // RMS accumulates from GuideStep events.
            for (var i = 0; i < 5; i++) {
                await fake.BroadcastAsync(PhdEvents.GuideStep(raDistanceRaw: 0.4, decDistanceRaw: -0.4)).ConfigureAwait(false);
            }
            Assert.That(await PollAsync(svc, d => d.Runtime?.RmsTotal is > 0).ConfigureAwait(false), Is.Not.Null,
                "GuideStep events did not accumulate into a non-zero RMS");

            await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            // Disconnect drops the guider session — GetAsync goes back to null (no device), the same
            // contract as before connect (see GuiderServiceTest.GetAsync_returns_null_before_connect).
            var after = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(after, Is.Null, "disconnect should drop the guider so GetAsync returns null");
        }

        [Test]
        public async Task Reflects_star_lost_when_the_guide_star_is_lost() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false), Is.Not.Null,
                "the service never reached Connected against the fake guider");

            await fake.BroadcastAsync(PhdEvents.AppState("Guiding")).ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.Runtime?.State == "guiding").ConfigureAwait(false), Is.Not.Null,
                "an AppState=Guiding event did not reach the runtime state");

            // §42.2 in-band fault: the guider loses the star mid-guiding. PHD2 emits a StarLost
            // event, which PHD2Guider folds into AppState=LostLock → the §63.2 "star_lost" token.
            // The session stays Connected (it's a guiding-quality fault, not a link drop).
            await fake.BroadcastAsync(PhdEvents.StarLost()).ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.Runtime?.State == "star_lost").ConfigureAwait(false), Is.Not.Null,
                "a StarLost event did not surface as the star_lost runtime state");
            var afterLost = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(afterLost?.State, Is.EqualTo(EquipmentConnectionState.Connected),
                "a lost star is a guiding fault, not a disconnect — the link should stay Connected");
        }

        [Test]
        public async Task Drops_to_Error_when_the_guider_link_dies_mid_session() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false), Is.Not.Null,
                "the service never reached Connected against the fake guider");
            // Connect leaves exactly the persistent event-stream connection open (per-call RPC
            // connections close after their reply); wait for it to settle before dropping. Assert
            // the wait so a slow CI box fails here ("nothing to drop") rather than later at the
            // drop-count assert with a misleading message.
            Assert.That(await WaitUntilAsync(() => fake.ConnectionCount >= 1).ConfigureAwait(false), Is.True,
                "the persistent event-stream connection never settled, so there was nothing to drop");

            // §42.2 link fault: the guider daemon drops the socket mid-session. PHD2Guider's
            // listener sees EOF and raises PHD2ConnectionLost; GuiderService.OnConnectionLost moves
            // the session to Error and kicks off §63.3 recovery (its outcome — Unsupervised off a
            // systemd host — is unit-covered by GuiderRecoveryCoordinatorTest, not re-asserted here).
            Assert.That(fake.DropConnections(), Is.GreaterThan(0), "expected at least one live connection to drop");
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Error).ConfigureAwait(false), Is.Not.Null,
                "a dropped guider link did not surface as the Error state");
        }

        [Test]
        public async Task A_mid_session_link_drop_pauses_the_running_sequence_per_policy() {
            // §42.2 end-to-end through the real client: the guider daemon drops the
            // socket mid-session and the fault flow executes the profile's
            // on_guider_lost policy (default pause_and_retry) against the sequencer —
            // the previously notify-only gap. Policy mapping details are unit-covered
            // by GuiderFaultReactionTest; this asserts the drop actually triggers it.
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            var profiles = new Mock<IProfileStore>();
            profiles.Setup(p => p.GetSafetyPolicies()).Returns(new SafetyPoliciesDto(
                OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                MeridianFlipAuto: true, MeridianPauseMin: 2, MeridianRecenter: true, MeridianRecalGuider: false,
                OnAltitudeLimit: "pause", ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
                GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: false));
            var sequencer = new Mock<ISequencerService>();
            var pauseRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .Callback(() => pauseRequested.TrySetResult())
                .ReturnsAsync(new List<Guid> { Guid.NewGuid() });
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(),
                ws: null, profileStore: profiles.Object, sequencerResolver: () => sequencer.Object,
                notifications: Mock.Of<INotificationService>());

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false), Is.Not.Null,
                "the service never reached Connected against the fake guider");
            Assert.That(await WaitUntilAsync(() => fake.ConnectionCount >= 1).ConfigureAwait(false), Is.True,
                "the persistent event-stream connection never settled, so there was nothing to drop");

            Assert.That(fake.DropConnections(), Is.GreaterThan(0), "expected at least one live connection to drop");
            Assert.That(await Task.WhenAny(pauseRequested.Task, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false),
                Is.SameAs(pauseRequested.Task),
                "the link drop never reached the §42.2 fault flow (PauseActiveRunsAsync was not called)");
        }

        [Test]
        public async Task A_mid_session_link_drop_lands_in_the_fault_log_and_a_reconnect_resolves() {
            // §42.5 — the guider is deliberately off the EquipmentFaultHub channel, so its fault
            // flow writes the persistent log DIRECTLY: connect (resolves any open rows — the
            // observed-reconnect semantics), drop the socket (records a Guider/disconnected row
            // and stamps the policy action onto it).
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            var profiles = new Mock<IProfileStore>();
            profiles.Setup(p => p.GetSafetyPolicies()).Returns(new SafetyPoliciesDto(
                OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
                MeridianFlipAuto: true, MeridianPauseMin: 2, MeridianRecenter: true, MeridianRecalGuider: false,
                OnAltitudeLimit: "pause", ParkIfNoMoreTargets: true, OnGuiderLost: "pause_and_retry",
                GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: false));
            var sequencer = new Mock<ISequencerService>();
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Guid> { Guid.NewGuid() });

            var faultLog = new Mock<IFaultLogService>();
            EquipmentFaultEvent? recorded = null;
            var recordedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            faultLog.Setup(f => f.RecordFaultAsync(It.IsAny<EquipmentFaultEvent>(), It.IsAny<CancellationToken>()))
                .Callback<EquipmentFaultEvent, CancellationToken>((f, _) => { recorded = f; recordedTcs.TrySetResult(); })
                .Returns(Task.CompletedTask);
            var actionTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string? stampedAction = null;
            faultLog.Setup(f => f.RecordActionAsync(It.IsAny<EquipmentFaultEvent>(), It.IsAny<string>(),
                    It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
                .Callback<EquipmentFaultEvent, string, DateTimeOffset?, CancellationToken>((_, a, _, _) => { stampedAction = a; actionTcs.TrySetResult(); })
                .Returns(Task.CompletedTask);
            var resolvedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            faultLog.Setup(f => f.ResolveOnReconnectAsync(DeviceType.Guider, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .Callback(() => resolvedTcs.TrySetResult())
                .ReturnsAsync(0);

            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(),
                ws: null, profileStore: profiles.Object, sequencerResolver: () => sequencer.Object,
                notifications: Mock.Of<INotificationService>(), faultLog: faultLog.Object);

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false), Is.Not.Null,
                "the service never reached Connected against the fake guider");
            Assert.That(await Task.WhenAny(resolvedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false),
                Is.SameAs(resolvedTcs.Task),
                "a successful connect must resolve the guider's open disconnect fault rows");
            Assert.That(await WaitUntilAsync(() => fake.ConnectionCount >= 1).ConfigureAwait(false), Is.True);

            Assert.That(fake.DropConnections(), Is.GreaterThan(0));
            Assert.That(await Task.WhenAny(recordedTcs.Task, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false),
                Is.SameAs(recordedTcs.Task), "the link drop never landed in the fault log");
            Assert.That(recorded!.DeviceType, Is.EqualTo(DeviceType.Guider));
            Assert.That(recorded.Kind, Is.EqualTo(EquipmentFaultKind.Disconnected));
            Assert.That(recorded.Details, Does.Contain("link down"));
            Assert.That(await Task.WhenAny(actionTcs.Task, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false),
                Is.SameAs(actionTcs.Task), "the reaction never stamped its action onto the fault row");
            Assert.That(stampedAction, Is.EqualTo("pause_and_retry"));
        }

        [Test]
        public async Task A_structured_equipment_fault_reacts_per_policy_but_stays_connected() {
            // §42.2 (openastro-guider #57): the daemon reports the guide camera dropped
            // (EquipmentDisconnected). The guider LINK is still up, so — unlike a socket drop — the
            // session runs the on_guider_lost policy (pauses the sequence) but must NOT go to Error or
            // start §63.3 recovery.
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            var profiles = new Mock<IProfileStore>();
            profiles.Setup(p => p.GetSafetyPolicies()).Returns(GuiderLostPolicy("pause_and_retry"));
            var sequencer = new Mock<ISequencerService>();
            var pauseRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .Callback(() => pauseRequested.TrySetResult())
                .ReturnsAsync(new List<Guid> { Guid.NewGuid() });
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(),
                ws: null, profileStore: profiles.Object, sequencerResolver: () => sequencer.Object,
                notifications: Mock.Of<INotificationService>());

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false), Is.Not.Null,
                "the service never reached Connected against the fake guider");

            await fake.BroadcastAsync(PhdEvents.EquipmentDisconnected()).ConfigureAwait(false);

            Assert.That(await Task.WhenAny(pauseRequested.Task, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false),
                Is.SameAs(pauseRequested.Task),
                "a structured EquipmentDisconnected did not run the §42.2 on_guider_lost policy");
            var after = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(after?.State, Is.EqualTo(EquipmentConnectionState.Connected),
                "a device fault is a guiding-degraded condition, not a link drop — the session must stay Connected");
        }

        [Test]
        public async Task A_non_camera_equipment_disconnect_does_not_pause_the_sequence() {
            // Only the guide CAMERA drives the guiding-lost policy. A future/other device_type
            // (rotator/aux) must not pause the sequence as "guiding lost".
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            var profiles = new Mock<IProfileStore>();
            profiles.Setup(p => p.GetSafetyPolicies()).Returns(GuiderLostPolicy("pause_and_retry"));
            var sequencer = new Mock<ISequencerService>();
            var pauseCalls = 0;
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .Callback(() => System.Threading.Interlocked.Increment(ref pauseCalls))
                .ReturnsAsync(new List<Guid> { Guid.NewGuid() });
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(),
                ws: null, profileStore: profiles.Object, sequencerResolver: () => sequencer.Object,
                notifications: Mock.Of<INotificationService>());

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false), Is.Not.Null,
                "the service never reached Connected against the fake guider");

            await fake.BroadcastAsync(PhdEvents.EquipmentDisconnected(deviceType: "rotator")).ConfigureAwait(false);
            // A camera drop afterwards SHOULD react — proving the rotator event was filtered, not that the
            // pipeline is simply dead.
            await fake.BroadcastAsync(PhdEvents.EquipmentDisconnected(deviceType: "camera")).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => System.Threading.Volatile.Read(ref pauseCalls) == 1).ConfigureAwait(false),
                Is.True, "the camera drop should have paused exactly once");
            // Give any erroneous rotator-driven reaction a moment to have fired, then confirm it did not.
            await Task.Delay(200).ConfigureAwait(false);
            Assert.That(System.Threading.Volatile.Read(ref pauseCalls), Is.EqualTo(1),
                "a non-camera device_type must not trigger the guiding-lost policy");
        }

        [Test]
        public async Task A_link_down_after_an_equipment_fault_still_reacts() {
            // Safety-critical: an EquipmentDisconnected stays Connected and so never clears the fault
            // latch. A genuine link death that follows must NOT be swallowed by that latch — the more
            // severe link-down policy has to fire, or the run keeps shooting unguided through a real drop.
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            var profiles = new Mock<IProfileStore>();
            profiles.Setup(p => p.GetSafetyPolicies()).Returns(GuiderLostPolicy("pause_and_retry"));
            var sequencer = new Mock<ISequencerService>();
            var pauseCalls = 0;
            sequencer.Setup(s => s.PauseActiveRunsAsync(It.IsAny<CancellationToken>()))
                .Callback(() => System.Threading.Interlocked.Increment(ref pauseCalls))
                .ReturnsAsync(new List<Guid> { Guid.NewGuid() });
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(),
                ws: null, profileStore: profiles.Object, sequencerResolver: () => sequencer.Object,
                notifications: Mock.Of<INotificationService>());

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false), Is.Not.Null,
                "the service never reached Connected against the fake guider");

            await fake.BroadcastAsync(PhdEvents.EquipmentDisconnected()).ConfigureAwait(false);
            Assert.That(await WaitUntilAsync(() => System.Threading.Volatile.Read(ref pauseCalls) == 1).ConfigureAwait(false),
                Is.True, "the camera fault should have paused once");
            Assert.That(await WaitUntilAsync(() => fake.ConnectionCount >= 1).ConfigureAwait(false), Is.True,
                "the persistent event-stream connection never settled");

            // Now the whole guider link dies — this must react despite the equipment latch already set.
            Assert.That(fake.DropConnections(), Is.GreaterThan(0), "expected a live connection to drop");
            Assert.That(await WaitUntilAsync(() => System.Threading.Volatile.Read(ref pauseCalls) == 2).ConfigureAwait(false),
                Is.True, "the link-down must fire its own reaction, not be swallowed by the equipment-fault latch");
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Error).ConfigureAwait(false), Is.Not.Null,
                "the link drop should surface as Error");
        }

        private static SafetyPoliciesDto GuiderLostPolicy(string onGuiderLost) => new(
            OnUnsafe: "pause_and_park", AutoResumeWhenSafe: true, ResumeDelayMin: 10,
            MeridianFlipAuto: true, MeridianPauseMin: 2, MeridianRecenter: true, MeridianRecalGuider: false,
            OnAltitudeLimit: "pause", ParkIfNoMoreTargets: true, OnGuiderLost: onGuiderLost,
            GuiderRetryTimeoutSec: 60, SkipTargetIfRecoveryFails: false);

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
                // Our own 15s deadline elapsed — fall through and let the caller's assertion report
                // the miss. A cancellation from a *different* token (e.g. a service-internal
                // disconnect race) is not swallowed here; it surfaces as the real failure cause.
            }
            return null;
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!cts.IsCancellationRequested) {
                if (condition()) {
                    return true;
                }
                try {
                    await Task.Delay(100, cts.Token).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    break;
                }
            }
            return condition();
        }
    }
}
