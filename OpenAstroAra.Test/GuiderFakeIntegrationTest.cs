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
using System.Collections.Generic;
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
        public async Task Twin_profile_is_named_after_the_repository_profile_not_the_legacy_store() {
            // §63.4 — the Equipment layer's legacy store is always named "Default"; the twin must
            // carry the MULTI-PROFILE repository's active name (what the user called their rig).
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            string? createdName = null;
            fake.OnRpc("get_profile", _ => new JsonObject { ["id"] = 1, ["name"] = "My Equipment" });
            fake.OnRpc("get_profiles", _ => new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "My Equipment" }));
            fake.OnRpc("create_profile", req => {
                createdName = req["params"]?["name"]?.GetValue<string>();
                return new JsonObject { ["id"] = 5, ["name"] = createdName, ["selected"] = true };
            });
            var araId = Guid.NewGuid();
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(),
                araProfileResolver: () => (araId, "RC91 - Backyard"));
            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false),
                Is.Not.Null, "the guider never reached Connected against the fake");
            Assert.That(await WaitUntilAsync(() => createdName != null).ConfigureAwait(false), Is.True,
                "connect never created the twin profile");
            Assert.That(createdName, Is.EqualTo("RC91 - Backyard"),
                "the twin carries the repository profile's display name, not the legacy 'Default'");
        }

        [Test]
        public async Task A_profile_switch_push_flips_the_daemon_to_the_new_profiles_twin() {
            // §63.4 — POST /profiles/{id}/select fires a guider push; the push's ensure step
            // must land the daemon on the NEW profile's twin (create it here), not keep
            // pushing into the old one.
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_connected", JsonValue.Create(true));
            var created = new List<string?>();
            var selected = new List<string?>();
            var daemonProfiles = new List<(int Id, string Name)> { (1, "Rig A") };
            fake.OnRpc("get_profile", _ => new JsonObject { ["id"] = 1, ["name"] = "Rig A" });
            fake.OnRpc("get_profiles", _ => {
                var arr = new JsonArray();
                foreach (var (pid, name) in daemonProfiles) {
                    arr.Add(new JsonObject { ["id"] = pid, ["name"] = name });
                }
                return arr;
            });
            fake.OnRpc("create_profile", req => {
                var name = req["params"]?["name"]?.GetValue<string>();
                created.Add(name);
                daemonProfiles.Add((daemonProfiles.Count + 1, name ?? ""));
                return new JsonObject { ["id"] = daemonProfiles.Count, ["name"] = name, ["selected"] = true };
            });
            fake.OnRpc("set_profile_by_name", req => {
                selected.Add(req["params"]?["name"]?.GetValue<string>());
                return JsonValue.Create(0);
            });

            var activeAra = (Id: Guid.NewGuid(), Name: "Rig A");
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(),
                araProfileResolver: () => (activeAra.Id, activeAra.Name));
            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false),
                Is.Not.Null);
            await WaitUntilAsync(() => selected.Count > 0 || created.Count > 0).ConfigureAwait(false);
            created.Clear();
            selected.Clear();

            // The ARA-side profile switch (what /profiles/{id}/select performs) then a push.
            activeAra = (Guid.NewGuid(), "Rig B");
            await svc.PushGuiderProfileAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(created, Does.Contain("Rig B"),
                "the push's ensure step must create the NEW profile's twin on the daemon");
        }

        [Test]
        public async Task Deleting_an_ara_profile_deletes_its_guider_twin_with_dark_files() {
            // §63.4 delete hook — the service tries the twin under BOTH the display name
            // (current scheme) and the legacy ara-<slug>-<id8> name, dark files included.
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            var deletedNames = new List<string?>();
            bool? darkFiles = null;
            // The factory receives the WHOLE JSON-RPC request; params sit under "params".
            fake.OnRpc("delete_profile", req => {
                var p = req["params"]?.AsObject();
                deletedNames.Add(p?["name"]?.GetValue<string>());
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
            Assert.That(deletedNames, Does.Contain("Old Rig C8"),
                "the display-name twin — the same one the connect path now creates");
            Assert.That(deletedNames, Does.Contain(PHD2Guider.AraGuiderProfileName("Old Rig C8", profileId)),
                "the legacy id-suffixed twin is swept too (pre-migration daemons)");
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

        private static readonly string[] ExpectedChoiceCameras = ["Alpaca Camera", "Simulator"];
        private static readonly string[] ExpectedChoiceMounts = ["On-camera"];
        private static readonly string[] ExpectedDiscoveredServers = ["192.168.1.154:6800"];

        [Test]
        public async Task Equipment_choices_and_alpaca_discovery_round_trip_through_the_fake() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            // §63.17 — per-slot choices as the daemon serializes them, including the caps "AO" key and an
            // omitted rotator slot (a build without rotator support) which must map to an empty list.
            fake.OnRpc("get_equipment_choices", _ => new JsonObject {
                ["camera"] = new JsonArray("Alpaca Camera", "Simulator"),
                ["mount"] = new JsonArray("On-camera"),
                ["aux_mount"] = new JsonArray(),
                ["AO"] = new JsonArray(),
            });
            fake.OnRpc("discover_alpaca_servers", _ => new JsonArray("192.168.1.154:6800"));
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false), Is.Not.Null,
                "the service never reached Connected against the fake guider");

            var choices = await svc.GetEquipmentChoicesAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.That(choices, Is.Not.Null, "a connected guider should answer the choices read");
            Assert.That(choices!.Cameras, Is.EqualTo(ExpectedChoiceCameras));
            Assert.That(choices.Mounts, Is.EqualTo(ExpectedChoiceMounts));
            Assert.That(choices.Rotators, Is.Empty, "an omitted slot maps to an empty list, not null");

            var discovery = await svc.DiscoverAlpacaServersAsync(new DiscoverAlpacaServersRequestDto(), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(discovery.Servers, Is.EqualTo(ExpectedDiscoveredServers));
            Assert.That(fake.ReceivedMethods, Does.Contain("discover_alpaca_servers"));
        }

        [Test]
        public async Task Alpaca_camera_pixel_size_reads_through_the_fake_with_params() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            JsonNode? seenParams = null;
            // §63.20 — the daemon reads pixelsizex/y from the Alpaca driver and answers {pixel_size, ...}.
            fake.OnRpc("get_alpaca_camera_pixelsize", req => {
                seenParams = req["params"]?.DeepClone();
                return new JsonObject { ["pixel_size"] = 2.9 };
            });
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false), Is.Not.Null);

            var dto = await svc.GetAlpacaCameraPixelSizeAsync("rc91.lan", 6800, 1, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(dto.Connected, Is.True);
            Assert.That(dto.PixelSize, Is.EqualTo(2.9).Within(1e-9));
            Assert.That(seenParams?["host"]?.GetValue<string>(), Is.EqualTo("rc91.lan"));
            Assert.That(seenParams?["port"]?.GetValue<int>(), Is.EqualTo(6800));
            Assert.That(seenParams?["device_number"]?.GetValue<int>(), Is.EqualTo(1));
        }

        [Test]
        public async Task Alpaca_camera_pixel_size_read_failure_is_a_null_not_an_error() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_alpaca_camera_pixelsize", _ => throw new InvalidOperationException("simulated daemon rejection"));
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());
            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false), Is.Not.Null);

            var dto = await svc.GetAlpacaCameraPixelSizeAsync(null, null, null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(dto.Connected, Is.True, "the guider link is up even though the read failed");
            Assert.That(dto.PixelSize, Is.Null, "best-effort assist: failure maps to null, never a throw");
        }

        [Test]
        public async Task Profile_push_sends_the_equipment_selections_to_the_fake() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            // The real daemon answers get_connected with a boolean; FakeGuider's generic default (result 0)
            // would make EnsurePHD2EquipmentConnected's bool cast throw.
            fake.OnRpc("get_connected", JsonValue.Create(true));
            var profiles = new HeadlessProfileService();
            var settings = profiles.ActiveProfile.GuiderSettings;
            settings.GuiderCamera = "Alpaca Camera";
            settings.GuiderMount = "On-camera";
            settings.GuiderRotator = "Alpaca Rotator";
            settings.GuiderAlpacaHost = "192.168.1.20";
            settings.GuiderAlpacaPort = 11111;
            using var svc = new GuiderService(profiles, NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false), Is.Not.Null,
                "the service never reached Connected against the fake guider");

            // §63.17 — the on-demand push drives the selection setters over the wire (the connect path
            // already pushed once; the explicit push must send them again).
            var before = fake.ReceivedMethods.Count(m => m == "set_selected_camera");
            var accepted = await svc.PushGuiderProfileAsync("push-1", CancellationToken.None).ConfigureAwait(false);
            Assert.That(accepted.OperationType, Is.EqualTo("guider.profile.push"));
            Assert.That(fake.ReceivedMethods.Count(m => m == "set_selected_camera"), Is.GreaterThan(before),
                "the push should re-send set_selected_camera");
            Assert.That(fake.ReceivedMethods, Does.Contain("set_selected_mount"));
            Assert.That(fake.ReceivedMethods, Does.Contain("set_selected_rotator"));
            Assert.That(fake.ReceivedMethods, Does.Contain("set_alpaca_server"));
            Assert.That(fake.ReceivedMethods, Does.Not.Contain("set_selected_aux_mount"),
                "an unset selection must not be pushed");
        }

        [Test]
        public async Task Profile_push_surfaces_a_failed_equipment_reconnect_as_a_typed_rpc_error() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            // The realistic §63.17 failure: a just-pushed selection the daemon can't connect. get_connected
            // answers false, then set_connected fails → EnsurePHD2EquipmentConnected returns false → the push
            // must NOT read as success (reviewer-caught bug on #879: 202 while equipment was left off).
            fake.OnRpc("get_connected", JsonValue.Create(false));
            fake.OnRpc("set_connected", _ => throw new InvalidOperationException("simulated: equipment failed to connect"));
            var profiles = new HeadlessProfileService();
            profiles.ActiveProfile.GuiderSettings.GuiderCamera = "Bogus Camera";
            using var svc = new GuiderService(profiles, NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false), Is.Not.Null,
                "the service never reached Connected against the fake guider");

            Assert.ThrowsAsync<GuiderRpcException>(
                () => svc.PushGuiderProfileAsync("push-fail", CancellationToken.None),
                "a push whose equipment reconnect fails must surface, not return 202");
        }

        [Test]
        public async Task Camera_change_push_emits_the_dark_library_invalidated_event() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            fake.OnRpc("get_connected", JsonValue.Create(true));
            // A dark library exists → an actual camera change should invalidate it.
            fake.OnRpc("get_calibration_files_status", _ => new JsonObject { ["profile_id"] = 1, ["dark_library_exists"] = true });
            var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(w => w.PublishAsync(It.IsAny<string>(), It.IsAny<System.Text.Json.JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback<string, System.Text.Json.JsonElement, CancellationToken>((t, _, _) => events.Enqueue(t))
                .Returns(Task.CompletedTask);
            var profiles = new HeadlessProfileService();
            profiles.ActiveProfile.GuiderSettings.GuiderCamera = "Cam A";
            using var svc = new GuiderService(profiles, NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>(), ws.Object);

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false), Is.Not.Null,
                "the service never reached Connected against the fake guider");

            // First push establishes the baseline — no invalidation (no prior push to differ from).
            await svc.PushGuiderProfileAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(events, Does.Not.Contain("guider.dark_library.invalidated"),
                "the baseline-establishing first push must not invalidate");

            // Change the camera and push again — NOW the darks belong to the old camera.
            profiles.ActiveProfile.GuiderSettings.GuiderCamera = "Cam B";
            await svc.PushGuiderProfileAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(events, Does.Contain("guider.profile_pushed"));
            Assert.That(events, Does.Contain("guider.dark_library.invalidated"),
                "a camera-changing push with an existing dark library must emit the invalidation");
        }

        [Test]
        public async Task Delete_calibration_files_round_trips_through_the_fake() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            // The RPC answers the calibration-files status object (same as get_calibration_files_status).
            fake.OnRpc("delete_calibration_files", req => {
                // §63.17 — flags arrive as explicit named booleans.
                Assert.That((bool?)req["params"]?["delete_dark_library"], Is.True);
                Assert.That((bool?)req["params"]?["delete_defect_map"], Is.False);
                return new JsonObject { ["profile_id"] = 1, ["dark_library_exists"] = false, ["defect_map_exists"] = true };
            });
            using var svc = new GuiderService(new HeadlessProfileService(), NewRecovery(),
                NullLogger<GuiderService>.Instance, Mock.Of<IGuiderProcessSupervisor>());

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), idempotencyKey: null, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.That(await PollAsync(svc, d => d.State == EquipmentConnectionState.Connected).ConfigureAwait(false), Is.Not.Null,
                "the service never reached Connected against the fake guider");

            var status = await svc.DeleteCalibrationFilesAsync(true, false, CancellationToken.None).ConfigureAwait(false);
            Assert.That(status.DarkLibraryExists, Is.False);
            Assert.That(status.DefectMapExists, Is.True, "the kept defect map must survive a darks-only delete");
            Assert.That(fake.ReceivedMethods, Does.Contain("delete_calibration_files"));
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
