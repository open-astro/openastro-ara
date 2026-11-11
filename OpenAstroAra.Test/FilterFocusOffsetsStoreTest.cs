#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1675 — the saved per-filter focus offsets persist with the profile and win over the driver's
    /// offsets on wheel connect, for the slots that have one.</summary>
    [TestFixture]
    [Category("IO")] // real disk and a loopback Alpaca wheel
    public class FilterFocusOffsetsStoreTest {

        private static readonly FilterFocusOffsetsDto Saved = new([new FilterFocusOffsetDto(1, 77)]);
        private static readonly int[] DriverWithSavedSlot1 = [100, 77, -20];
        private static readonly int[] DriverWithNewSlot1 = [100, 55, -20];

        private string _dir = null!;

        [SetUp]
        public void SetUp() => _dir = Directory.CreateTempSubdirectory("ara-focus-offsets-").FullName;

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        [Test]
        public void Saved_offsets_survive_a_store_reopen_and_ride_the_profile_snapshot() {
            var store = new FileProfileStore(_dir);
            Assert.That(store.GetFilterFocusOffsets().Offsets, Is.Empty, "default: nothing saved");
            store.PutFilterFocusOffsets(Saved);

            var reopened = new FileProfileStore(_dir);
            Assert.That(reopened.GetFilterFocusOffsets().Offsets, Is.EqualTo(Saved.Offsets), "persisted");

            var captured = ProfileStoreSnapshot.Capture(reopened);
            Assert.That(captured.FilterFocusOffsets!.Offsets, Is.EqualTo(Saved.Offsets), "Capture carries the section");
            var live = new InMemoryProfileStore();
            live.PutFilterFocusOffsets(Saved);
            ProfileStoreSnapshot.Apply(live, ProfileStoreSnapshot.Capture(new InMemoryProfileStore()) with { FilterFocusOffsets = null });
            Assert.That(live.GetFilterFocusOffsets().Offsets, Is.Empty, "selecting a profile without offsets clears them");
            Assert.That(ProfileShareService.StripForShare(captured).FilterFocusOffsets!.Offsets, Is.Empty, "the donor's offsets stay home");
        }

        [Test]
        public async Task On_connect_a_saved_offset_wins_over_the_drivers_and_other_slots_keep_the_drivers() {
            new FileProfileStore(_dir).PutFilterFocusOffsets(Saved);
            var store = new FileProfileStore(_dir); // the daemon restarted
            using var profile = new OpenAstroAra.Profile.Profile();
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile).Returns(profile);
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/connected", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/position", StringComparison.Ordinal) ? "0"
                : path.EndsWith("/names", StringComparison.Ordinal) ? "[\"L\",\"R\",\"Ha\"]"
                : path.EndsWith("/focusoffsets", StringComparison.Ordinal) ? "[100,40,-20]"
                : null);
            using var wheel = new FilterWheelService(profileService: profileService.Object, profileStore: store) { RefreshPeriod = TimeSpan.FromMilliseconds(50) };
            var device = new DiscoveredDeviceDto(UniqueId: "efw", Name: "EFW", Type: DeviceType.FilterWheel,
                HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port, AlpacaDeviceNumber: 0, UseHttps: false);
            await wheel.ConnectAsync(new ConnectRequestDto(device), null, CancellationToken.None);
            for (var i = 0; i < 500 && profile.FilterWheelSettings.FilterWheelFilters.Count < 3; i++) {
                await Task.Delay(20);
            }

            var slots = (await wheel.GetAsync(CancellationToken.None))!.Slots;
            Assert.That(slots.Select(s => s.FocusOffset), Is.EqualTo(DriverWithSavedSlot1), "the GET reports the effective offset");
            Assert.That(profile.FilterWheelSettings.FilterWheelFilters.Single(f => f.Position == 1).FocusOffset, Is.EqualTo(77),
                "a filter change applies the saved offset");

            store.PutFilterFocusOffsets(new FilterFocusOffsetsDto([new FilterFocusOffsetDto(1, 55)]));
            slots = (await wheel.GetAsync(CancellationToken.None))!.Slots;
            Assert.That(slots.Select(s => s.FocusOffset), Is.EqualTo(DriverWithNewSlot1), "a Save while connected shows at once");
        }

        [Test]
        public async Task The_status_poll_never_reads_the_profile_store_and_a_Save_still_shows_at_once() {
            // The store's getter takes the lock its writes hold across the disk write, so a poll that read
            // it would wait out every profile save; the wheel answers from what Changed last told it.
            var saved = Saved;
            var reads = 0;
            var store = new Mock<IProfileStore>();
            store.Setup(s => s.GetFilterFocusOffsets()).Returns(() => { Interlocked.Increment(ref reads); return saved; });
            using var profile = new OpenAstroAra.Profile.Profile();
            var profileService = new Mock<IProfileService>();
            profileService.SetupGet(p => p.ActiveProfile).Returns(profile);
            await using var box = ScriptedAlpacaDevice.Start(path =>
                path.EndsWith("/connected", StringComparison.Ordinal) ? "true"
                : path.EndsWith("/position", StringComparison.Ordinal) ? "0"
                : path.EndsWith("/names", StringComparison.Ordinal) ? "[\"L\",\"R\",\"Ha\"]"
                : path.EndsWith("/focusoffsets", StringComparison.Ordinal) ? "[100,40,-20]"
                : null);
            using var wheel = new FilterWheelService(profileService: profileService.Object, profileStore: store.Object) { RefreshPeriod = TimeSpan.FromMilliseconds(50) };
            var device = new DiscoveredDeviceDto(UniqueId: "efw", Name: "EFW", Type: DeviceType.FilterWheel,
                HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port, AlpacaDeviceNumber: 0, UseHttps: false);
            await wheel.ConnectAsync(new ConnectRequestDto(device), null, CancellationToken.None);
            for (var i = 0; i < 500 && profile.FilterWheelSettings.FilterWheelFilters.Count < 3; i++) {
                await Task.Delay(20);
            }

            Interlocked.Exchange(ref reads, 0);
            for (var i = 0; i < 3; i++) {
                Assert.That((await wheel.GetAsync(CancellationToken.None))!.Slots.Select(s => s.FocusOffset), Is.EqualTo(DriverWithSavedSlot1));
            }
            Assert.That(reads, Is.Zero, "a poll reads no profile store");

            saved = new FilterFocusOffsetsDto([new FilterFocusOffsetDto(1, 55)]);
            store.Raise(s => s.Changed += null, EventArgs.Empty);
            Assert.That((await wheel.GetAsync(CancellationToken.None))!.Slots.Select(s => s.FocusOffset), Is.EqualTo(DriverWithNewSlot1),
                "a Save while connected shows at once");
        }
    }
}
