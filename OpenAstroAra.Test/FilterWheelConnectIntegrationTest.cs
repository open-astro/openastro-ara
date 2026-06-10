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
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeviceType = OpenAstroAra.Server.Contracts.DeviceType;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §14e — live integration test for <see cref="FilterWheelService"/>. Discovers a FilterWheel
    /// from a running ASCOM OmniSim, connects, reads its slots, changes to a target slot via
    /// <see cref="FilterWheelService.ChangeFilterAsync"/>, and verifies the cached current slot
    /// reflects the change, then disconnects. Runs in the <c>alpaca-sim-integration</c> CI job.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    public class FilterWheelConnectIntegrationTest {

        private static readonly Uri ManagementProbeUri = new("http://127.0.0.1:32323/management/apiversions");
        private const int MaxDiscoveryAttempts = 6;

        [OneTimeSetUp]
        public async Task OneTimeSetUp() {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            try {
                using var resp = await http.GetAsync(ManagementProbeUri).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) {
                    Assert.Ignore($"OmniSim management API returned {(int)resp.StatusCode} on :32323 — skipping live FilterWheel test.");
                }
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
                Assert.Ignore("No ASCOM OmniSim answering on :32323 — start one (or run the alpaca-sim-integration CI job) to exercise this test.");
            }
        }

        [Test]
        public async Task Connect_reads_slots_changes_filter_then_disconnects() {
            var device = await DiscoverAsync().ConfigureAwait(false);
            Assert.That(device, Is.Not.Null, "no FilterWheel device discovered from the running OmniSim");

            using var svc = new FilterWheelService();

            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var connected = await PollUntilAsync(svc, s => s != EquipmentConnectionState.Connecting).ConfigureAwait(false);
            Assert.That(connected, Is.Not.Null, "connection never left the Connecting state");
            Assert.That(connected!.State, Is.EqualTo(EquipmentConnectionState.Connected));

            var withSlots = await PollUntilSlotsAsync(svc).ConfigureAwait(false);
            Assert.That(withSlots, Is.Not.Null, "slots were never seeded after connect");
            Assert.That(withSlots!.Slots.Count, Is.GreaterThan(1), "the simulated wheel should expose multiple slots");

            // Change to a slot other than the current one and confirm the read-back.
            var currentSlot = withSlots.Runtime.CurrentSlot ?? 0;
            var target = (currentSlot + 1) % withSlots.Slots.Count;

            await svc.ChangeFilterAsync(new FilterChangeRequestDto(target), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);

            var changed = await PollUntilSlotAsync(svc, target).ConfigureAwait(false);
            Assert.That(changed, Is.Not.Null, "filter wheel never reported the target slot");
            Assert.That(changed!.Runtime.CurrentSlot, Is.EqualTo(target), "the cached current slot should reflect the change");

            await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var disconnected = await PollUntilAsync(svc, s => s == EquipmentConnectionState.Disconnected).ConfigureAwait(false);
            Assert.That(disconnected!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        /// <summary>
        /// §14e — the same <see cref="FilterWheelService"/> also serves
        /// <see cref="IFilterWheelMediator"/>: on connect the wheel's filter list imports into the
        /// active profile (so <c>SwitchFilter</c> resolves filters by name/position), and
        /// <c>ChangeFilter</c> drives the wheel + blocks until the target slot is reported. This
        /// exercises that path end-to-end against the live OmniSim wheel.
        /// </summary>
        [Test]
        public async Task Mediator_imports_profile_filters_and_ChangeFilter_drives_the_live_device() {
            var device = await DiscoverAsync().ConfigureAwait(false);
            Assert.That(device, Is.Not.Null, "no FilterWheel device discovered from the running OmniSim");

            var profileService = new HeadlessProfileService();
            using var svc = new FilterWheelService(logger: null, profileService);
            await svc.ConnectAsync(new ConnectRequestDto(device!), idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            var connected = await PollUntilAsync(svc, s => s != EquipmentConnectionState.Connecting).ConfigureAwait(false);
            Assert.That(connected!.State, Is.EqualTo(EquipmentConnectionState.Connected));

            try {
                var withSlots = await PollUntilSlotsAsync(svc).ConfigureAwait(false);
                Assert.That(withSlots!.Slots.Count, Is.GreaterThan(1));

                // Import-on-connect: the device's filter list must be in the active profile now.
                var profileFilters = profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters;
                Assert.That(profileFilters, Has.Count.EqualTo(withSlots.Slots.Count),
                    "the connected wheel's slots should be imported into the active profile");

                var info = ((IFilterWheelMediator)svc).GetInfo();
                Assert.That(info.Connected, Is.True);

                // Change to a different slot through the mediator, like SwitchFilter.Execute does.
                var currentSlot = withSlots.Runtime.CurrentSlot ?? 0;
                var targetPosition = (currentSlot + 1) % withSlots.Slots.Count;
                var targetFilter = profileFilters[targetPosition];
                var reached = await ((IFilterWheelMediator)svc).ChangeFilter(targetFilter, progress: null, CancellationToken.None).ConfigureAwait(false);

                Assert.That(reached.Position, Is.EqualTo((short)targetPosition),
                    "ChangeFilter should confirm arrival at the requested slot");
                var after = ((IFilterWheelMediator)svc).GetInfo();
                Assert.That(after.SelectedFilter?.Position, Is.EqualTo((short)targetPosition),
                    "GetInfo should resolve the new position as the selected filter");
            } finally {
                await svc.DisconnectAsync(idempotencyKey: null, CancellationToken.None).ConfigureAwait(false);
            }
            var disconnected = await PollUntilAsync(svc, s => s == EquipmentConnectionState.Disconnected).ConfigureAwait(false);
            Assert.That(disconnected!.State, Is.EqualTo(EquipmentConnectionState.Disconnected));
        }

        private static async Task<DiscoveredDeviceDto?> DiscoverAsync() {
            var discovery = new AlpacaEquipmentDiscoveryService();
            for (var attempt = 1; attempt <= MaxDiscoveryAttempts; attempt++) {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var found = await discovery.DiscoverAsync(DeviceType.FilterWheel, forceRefresh: true, cts.Token).ConfigureAwait(false);
                if (found.Count > 0) {
                    return found[0];
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            return null;
        }

        private static async Task<FilterWheelDto?> PollUntilAsync(FilterWheelService svc, Func<EquipmentConnectionState, bool> predicate) {
            for (var i = 0; i < 50; i++) {
                var dto = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
                if (dto is not null && predicate(dto.State)) {
                    return dto;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
            return await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private static async Task<FilterWheelDto?> PollUntilSlotsAsync(FilterWheelService svc) {
            for (var i = 0; i < 50; i++) {
                var dto = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
                if (dto is not null && dto.Slots.Count > 0) {
                    return dto;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
            return null;
        }

        // Up to ~16s for the wheel to settle on the slot + the 2s cache to reflect it. Null on timeout.
        private static async Task<FilterWheelDto?> PollUntilSlotAsync(FilterWheelService svc, int target) {
            for (var i = 0; i < 80; i++) {
                var dto = await svc.GetAsync(CancellationToken.None).ConfigureAwait(false);
                if (dto?.Runtime.CurrentSlot == target) {
                    return dto;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }
            return null;
        }
    }
}
