#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1298 — the driverless manual filter wheel, the router in front of it and the
    /// Alpaca wheel, and the discovery row that offers it.</summary>
    [TestFixture]
    public class ManualFilterWheelServiceTest {

        // CA1861: the expected lists, shared across assertions.
        private static readonly string[] TrimmedNames = ["L", "Filter 2", "Ha"];
        private static readonly string[] FiveFilters = ["L", "R", "G", "B", "Ha"];
        private static readonly string[] EfwThenManual = ["efw-1", ManualFilterWheelService.DeviceUniqueId];
        private static readonly string[] EfwOnly = ["efw-1"];

        private string _dir = null!;
        private InMemoryProfileStore _store = null!;
        private HeadlessProfileService _profile = null!;
        private CapturingBroadcaster _ws = null!;

        [SetUp]
        public void SetUp() {
            _dir = Directory.CreateTempSubdirectory("manual-fw-").FullName;
            _store = new InMemoryProfileStore();
            _store.PutFilterWheelLabels(new FilterWheelLabelsDto(["L", "R", "G", "B", "Ha", "", ""]));
            _profile = new HeadlessProfileService();
            _ws = new CapturingBroadcaster();
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        private ManualFilterWheelService NewWheel() =>
            new(profileStore: _store, profileService: _profile, events: new EquipmentEventPublisher(_ws), profileDir: _dir);

        private static ConnectRequestDto Manual() => new(ManualFilterWheelService.Descriptor);

        private static FilterInfo Filter(string name, short position) => new(name, 0, position);

        [Test]
        public void SlotNames_trims_trailing_blanks_and_names_inner_ones() {
            Assert.That(ManualFilterWheelService.SlotNames(["L", "", "Ha", " ", ""]),
                Is.EqualTo(TrimmedNames));
            Assert.That(ManualFilterWheelService.SlotNames(["", ""]), Is.Empty);
            Assert.That(ManualFilterWheelService.SlotNames(null), Is.Empty);
        }

        [Test]
        public void SyncProfileFilters_renames_adds_and_drops_but_keeps_offsets() {
            var filters = new List<FilterInfo> { new("Lum", 120, 0), new("Red", 40, 1), new("Old", 9, 7) };
            var changed = ManualFilterWheelService.SyncProfileFilters(filters, ["L", "R", "Ha"]);
            Assert.That(changed, Is.True);
            Assert.That(filters.Select(f => (f.Name, f.Position, f.FocusOffset)), Is.EquivalentTo(new[] {
                ("L", (short)0, 120), ("R", (short)1, 40), ("Ha", (short)2, 0),
            }));
            Assert.That(ManualFilterWheelService.SyncProfileFilters(filters, ["L", "R", "Ha"]), Is.False);
        }

        [Test]
        public async Task Connect_serves_the_labels_as_slots_with_no_filter_known_yet() {
            using var wheel = NewWheel();
            Assert.That(await wheel.GetAsync(CancellationToken.None), Is.Null, "nothing selected yet");
            await wheel.ConnectAsync(Manual(), null, CancellationToken.None);
            Assert.That(_profile.ActiveProfile.FilterWheelSettings.FilterWheelFilters.Select(f => f.Name),
                Is.EqualTo(FiveFilters), "synced at connect, before any status read");
            var dto = await wheel.GetAsync(CancellationToken.None);
            Assert.That(dto!.State, Is.EqualTo(EquipmentConnectionState.Connected));
            Assert.That(dto.Manual, Is.True);
            Assert.That(dto.Slots.Select(s => s.Name), Is.EqualTo(FiveFilters));
            Assert.That(dto.Runtime, Is.EqualTo(new FilterWheelStateDto("idle", null)));
            Assert.That(_profile.ActiveProfile.FilterWheelSettings.FilterWheelFilters.Select(f => f.Name),
                Is.EqualTo(FiveFilters), "SwitchFilter resolves against this list");
        }

        [Test]
        public async Task A_REST_change_raises_the_prompt_and_installed_resolves_and_persists_it() {
            using (var wheel = NewWheel()) {
                await wheel.ConnectAsync(Manual(), null, CancellationToken.None);
                await wheel.ChangeFilterAsync(new FilterChangeRequestDto(4), null, CancellationToken.None);
                var waiting = (await wheel.GetAsync(CancellationToken.None))!.Runtime;
                Assert.That(waiting, Is.EqualTo(new FilterWheelStateDto("awaiting_user", null, 4)));
                var raised = _ws.Events.Where(e => e.EventType == WsEventCatalog.FilterWheelManualSwap).ToList();
                Assert.That(raised, Has.Count.EqualTo(1));
                Assert.That(raised[0].Payload.GetProperty("pending_name").GetString(), Is.EqualTo("Ha"));

                await wheel.ReportInstalledAsync(4, CancellationToken.None);
                Assert.That((await wheel.GetAsync(CancellationToken.None))!.Runtime,
                    Is.EqualTo(new FilterWheelStateDto("idle", 4)));
                Assert.That(wheel.GetInfo().SelectedFilter?.Name, Is.EqualTo("Ha"));
            }
            // A new daemon session remembers what is in the train.
            using var next = NewWheel();
            await next.ConnectAsync(Manual(), null, CancellationToken.None);
            Assert.That((await next.GetAsync(CancellationToken.None))!.Runtime.CurrentSlot, Is.EqualTo(4));
        }

        [Test]
        public async Task A_remembered_slot_beyond_the_labels_is_not_trusted() {
            await File.WriteAllTextAsync(Path.Combine(_dir, ManualFilterWheelService.StateFileName), "{\"current_slot\":6}");
            using var wheel = NewWheel();
            await wheel.ConnectAsync(Manual(), null, CancellationToken.None);
            Assert.That((await wheel.GetAsync(CancellationToken.None))!.Runtime.CurrentSlot, Is.Null);
        }

        [Test]
        public async Task Asking_for_the_installed_filter_raises_nothing() {
            using var wheel = NewWheel();
            await wheel.ConnectAsync(Manual(), null, CancellationToken.None);
            await wheel.ReportInstalledAsync(0, CancellationToken.None);
            _ws.Events.Clear();
            await wheel.ChangeFilterAsync(new FilterChangeRequestDto(0), null, CancellationToken.None);
            Assert.That((await wheel.GetAsync(CancellationToken.None))!.Runtime.State, Is.EqualTo("idle"));
            Assert.That(_ws.Events.Any(e => e.EventType == WsEventCatalog.FilterWheelManualSwap), Is.False);
            var reached = await wheel.ChangeFilter(Filter("L", 0)).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(reached.Name, Is.EqualTo("L"));
        }

        [Test]
        public async Task A_sequencer_change_waits_for_the_user_then_returns_the_filter() {
            using var wheel = NewWheel();
            await wheel.ConnectAsync(Manual(), null, CancellationToken.None);
            var statuses = new List<string>();
            var change = wheel.ChangeFilter(Filter("R", 1), new Progress<ApplicationStatus>(s => statuses.Add(s.Status)));
            await Task.Delay(200);
            Assert.That(change.IsCompleted, Is.False, "no time limit: the run waits for the person");
            Assert.That(wheel.GetInfo().IsMoving, Is.True);

            await wheel.ReportInstalledAsync(3, CancellationToken.None); // wrong filter: still waiting
            await Task.Delay(100);
            Assert.That(change.IsCompleted, Is.False);

            await wheel.ReportInstalledAsync(1, CancellationToken.None);
            var reached = await change.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(reached.Name, Is.EqualTo("R"));
            Assert.That(wheel.GetInfo().IsMoving, Is.False);
            Assert.That(statuses, Has.Some.Contains("install the R filter"));
        }

        [Test]
        public async Task A_cancelled_prompt_fails_the_waiting_change() {
            using var wheel = NewWheel();
            await wheel.ConnectAsync(Manual(), null, CancellationToken.None);
            var change = wheel.ChangeFilter(Filter("G", 2));
            await Task.Delay(100);
            await wheel.CancelPendingAsync(CancellationToken.None);
            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => change.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.That((await wheel.GetAsync(CancellationToken.None))!.Runtime.State, Is.EqualTo("idle"));
        }

        [Test]
        public async Task A_disconnect_fails_the_waiting_change_and_a_cancel_token_propagates() {
            using var wheel = NewWheel();
            await wheel.ConnectAsync(Manual(), null, CancellationToken.None);
            var change = wheel.ChangeFilter(Filter("G", 2));
            await Task.Delay(100);
            await wheel.DisconnectAsync(null, CancellationToken.None);
            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => change.WaitAsync(TimeSpan.FromSeconds(2)));

            await wheel.ConnectAsync(Manual(), null, CancellationToken.None);
            using var cts = new CancellationTokenSource();
            var cancelled = wheel.ChangeFilter(Filter("B", 3), token: cts.Token);
            await cts.CancelAsync();
            await Assert.CatchAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.That((await wheel.GetAsync(CancellationToken.None))!.Runtime,
                Is.EqualTo(new FilterWheelStateDto("idle", null)),
                "a stopped run withdraws the prompt it raised");
        }

        [Test]
        public async Task Out_of_range_and_unconnected_requests_are_refused_or_skipped() {
            using var wheel = NewWheel();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                wheel.ChangeFilterAsync(new FilterChangeRequestDto(0), null, CancellationToken.None));
            var skipped = await wheel.ChangeFilter(Filter("L", 0));
            Assert.That(skipped.Name, Is.EqualTo("L"), "not connected: a logged no-op, like the Alpaca wheel");

            await wheel.ConnectAsync(Manual(), null, CancellationToken.None);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                wheel.ChangeFilterAsync(new FilterChangeRequestDto(5), null, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => wheel.ReportInstalledAsync(-1, CancellationToken.None));

            _store.PutFilterWheelLabels(new FilterWheelLabelsDto(["", ""]));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                wheel.ChangeFilterAsync(new FilterChangeRequestDto(0), null, CancellationToken.None),
                "no filters configured");
        }

        [Test]
        public async Task Router_switching_kinds_forgets_the_other_wheel() {
            using var alpaca = new FilterWheelService();
            using var manual = NewWheel();
            using var router = new FilterWheelRouter(alpaca, manual);

            await router.ConnectAsync(Manual(), null, CancellationToken.None);
            Assert.That(router.ManualSelected, Is.True);
            Assert.That((await router.GetAsync(CancellationToken.None))!.Manual, Is.True);
            Assert.That(router.RetainedDevice?.UniqueId, Is.EqualTo(ManualFilterWheelService.DeviceUniqueId));

            // An Alpaca wheel nobody answers for: the connect is accepted (it fails in the
            // background), and the manual wheel is disconnected and forgotten first.
            var dead = new DiscoveredDeviceDto("efw-1", "EFW", DeviceType.FilterWheel, "", "127.0.0.1", 9, 0, false);
            await router.ConnectAsync(new ConnectRequestDto(dead), null, CancellationToken.None);
            Assert.That(router.ManualSelected, Is.False);
            Assert.That(manual.RetainedDevice, Is.Null);
            Assert.That((await router.GetAsync(CancellationToken.None))!.DeviceId, Is.EqualTo("efw-1"));
            Assert.That(router.GetInfo().DeviceId, Is.EqualTo("efw-1"));
        }

        [Test]
        public async Task Router_switching_away_fails_a_waiting_manual_change() {
            using var alpaca = new FilterWheelService();
            using var manual = NewWheel();
            using var router = new FilterWheelRouter(alpaca, manual);
            await router.ConnectAsync(Manual(), null, CancellationToken.None);
            var change = router.ChangeFilter(Filter("Ha", 4));
            await Task.Delay(100);
            Assert.That(change.IsCompleted, Is.False);
            var efw = new DiscoveredDeviceDto("efw-1", "EFW", DeviceType.FilterWheel, "", "127.0.0.1", 9, 0, false);
            await router.ConnectAsync(new ConnectRequestDto(efw), null, CancellationToken.None);
            await Assert.ThrowsAsync<SequenceEntityFailedException>(() => change.WaitAsync(TimeSpan.FromSeconds(2)));
        }

        [Test]
        public async Task Discovery_offers_the_manual_wheel_for_filter_wheels_only() {
            var inner = new FixedDiscovery();
            var discovery = new ManualDeviceDiscoveryService(inner);
            var wheels = await discovery.DiscoverAsync(DeviceType.FilterWheel, false, CancellationToken.None);
            Assert.That(wheels.Select(d => d.UniqueId), Is.EqualTo(EfwThenManual));
            var cameras = await discovery.DiscoverAsync(DeviceType.Camera, false, CancellationToken.None);
            Assert.That(cameras.Select(d => d.UniqueId), Is.EqualTo(EfwOnly));
        }

        private sealed class FixedDiscovery : IEquipmentDiscoveryService {
            public Task<IReadOnlyList<DiscoveredDeviceDto>> DiscoverAsync(DeviceType type, bool forceRefresh, CancellationToken ct) =>
                Task.FromResult<IReadOnlyList<DiscoveredDeviceDto>>(
                    [new DiscoveredDeviceDto("efw-1", "EFW", type, "", "127.0.0.1", 11111, 0, false)]);
        }
    }
}
