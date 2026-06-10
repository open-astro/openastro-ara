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
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Sim-free unit coverage for the §14e <see cref="IFilterWheelMediator"/> surface that
    /// <see cref="FilterWheelService"/> serves alongside its REST surface (one singleton backs both,
    /// so the <c>SwitchFilter</c> instruction drives the live wheel). The live change path is
    /// exercised by the <c>[Category("Integration")]</c> companion test; here we cover the
    /// not-connected / disposed contracts plus the pure profile-import and filter-resolution helpers
    /// against fabricated slots.
    /// </summary>
    [TestFixture]
    public class FilterWheelMediatorTest {

        private static List<FilterSlotDto> SampleSlots() => new() {
            new FilterSlotDto(Position: 0, Name: "L", FocusOffset: 0),
            new FilterSlotDto(Position: 1, Name: "R", FocusOffset: 10),
            new FilterSlotDto(Position: 2, Name: "G", FocusOffset: 20),
        };

        [Test]
        public void GetInfo_before_connect_reports_not_connected() {
            using var svc = new FilterWheelService();
            var info = ((IFilterWheelMediator)svc).GetInfo();
            Assert.That(info.Connected, Is.False);
            Assert.That(info.IsMoving, Is.False);
        }

        [Test]
        public void GetInfo_after_Dispose_reports_not_connected_without_throwing() {
            var svc = new FilterWheelService();
            svc.Dispose();
            var info = ((IFilterWheelMediator)svc).GetInfo();
            Assert.That(info.Connected, Is.False);
        }

        [Test]
        public async Task ChangeFilter_when_not_connected_returns_input_filter_without_throwing() {
            using var svc = new FilterWheelService();
            var input = new FilterInfo("Ha", 0, 1);
            var result = await ((IFilterWheelMediator)svc).ChangeFilter(input, progress: null, CancellationToken.None);
            Assert.That(result, Is.SameAs(input));
        }

        [Test]
        public async Task ChangeFilter_after_Dispose_returns_input_filter_without_throwing() {
            var svc = new FilterWheelService();
            svc.Dispose();
            var input = new FilterInfo("OIII", 0, 2);
            var result = await ((IFilterWheelMediator)svc).ChangeFilter(input, progress: null, CancellationToken.None);
            Assert.That(result, Is.SameAs(input));
        }

        [Test]
        public void ChangeFilter_null_filter_throws() {
            using var svc = new FilterWheelService();
            // ChangeFilter is async, so the guard surfaces on the returned Task.
            Assert.ThrowsAsync<System.ArgumentNullException>(
                () => ((IFilterWheelMediator)svc).ChangeFilter(null!, progress: null, CancellationToken.None));
        }

        [Test]
        public void SyncProfileFilters_populates_an_empty_profile_from_the_device() {
            var profile = new List<FilterInfo>();
            FilterWheelService.SyncProfileFilters(profile, SampleSlots());

            Assert.That(profile, Has.Count.EqualTo(3));
            Assert.That(profile[0].Name, Is.EqualTo("L"));
            Assert.That(profile[1].FocusOffset, Is.EqualTo(10));
            Assert.That(profile[2].Position, Is.EqualTo((short)2));
        }

        [Test]
        public void SyncProfileFilters_preserves_user_edited_entries_at_valid_positions() {
            var userEdited = new FilterInfo("Luminance (custom)", 5, 0) { AutoFocusExposureTime = 3.5 };
            var profile = new List<FilterInfo> { userEdited };
            FilterWheelService.SyncProfileFilters(profile, SampleSlots());

            Assert.That(profile, Has.Count.EqualTo(3));
            Assert.That(profile, Does.Contain(userEdited),
                "an existing profile entry at a still-valid position must be preserved verbatim");
            Assert.That(userEdited.AutoFocusExposureTime, Is.EqualTo(3.5));
        }

        [Test]
        public void SyncProfileFilters_removes_entries_beyond_the_device_slot_count() {
            var profile = new List<FilterInfo> {
                new FilterInfo("L", 0, 0),
                new FilterInfo("Stale slot 7", 0, 7),
            };
            FilterWheelService.SyncProfileFilters(profile, SampleSlots());

            Assert.That(profile, Has.Count.EqualTo(3));
            Assert.That(profile.ConvertAll(f => f.Name), Does.Not.Contain("Stale slot 7"));
        }

        [Test]
        public void ResolveFilter_prefers_the_profile_entry_over_the_device_slot() {
            var profileFilter = new FilterInfo("Lum (custom)", 5, 0);
            var resolved = FilterWheelService.ResolveFilter(
                new List<FilterInfo> { profileFilter }, SampleSlots(), position: 0);
            Assert.That(resolved, Is.SameAs(profileFilter));
        }

        [Test]
        public void ResolveFilter_falls_back_to_the_device_slot_then_null() {
            var fromSlot = FilterWheelService.ResolveFilter(null, SampleSlots(), position: 1);
            Assert.That(fromSlot, Is.Not.Null);
            Assert.That(fromSlot!.Name, Is.EqualTo("R"));
            Assert.That(fromSlot.Position, Is.EqualTo((short)1));

            Assert.That(FilterWheelService.ResolveFilter(null, SampleSlots(), position: 9), Is.Null);
            Assert.That(FilterWheelService.ResolveFilter(null, null, position: 0), Is.Null);
        }
    }
}
