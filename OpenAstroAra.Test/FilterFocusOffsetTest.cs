#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Equipment.MyFocuser;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1667 — a filter change moves the focuser by offset(new) − offset(old), once, on every path.</summary>
    [TestFixture]
    [Category("IO")]
    public class FilterFocusOffsetTest {
        private InMemoryProfileStore _store = null!;
        private Mock<IFocuserMediator> _focuser = null!;
        private ConcurrentQueue<int> _moves = null!;
        private CapturingLog _log = null!;
        private string _dir = null!;

        private sealed class CapturingLog : ILogger<FilterFocusOffset> {
            public readonly ConcurrentQueue<string> Warnings = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                if (logLevel == LogLevel.Warning) {
                    Warnings.Enqueue(formatter(state, exception));
                }
            }
        }

        [SetUp]
        public void SetUp() {
            _store = new InMemoryProfileStore();
            _store.PutFilterWheelPolicy(new FilterWheelPolicyDto(UseFilterWheelOffsets: true));
            _store.PutFilterWheelLabels(new FilterWheelLabelsDto(["L", "R", "Ha", "", "", "", ""]));
            _moves = new ConcurrentQueue<int>();
            _focuser = new Mock<IFocuserMediator>();
            _focuser.Setup(f => f.GetInfo()).Returns(new FocuserInfo { Connected = true });
            _focuser.Setup(f => f.MoveFocuserRelative(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Callback<int, CancellationToken>((d, _) => _moves.Enqueue(d))
                .ReturnsAsync(0);
            _log = new CapturingLog();
            _dir = Directory.CreateTempSubdirectory("focus-offset-").FullName;
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        private FilterFocusOffset Offsets(Func<IFocuserMediator?>? focuser = null) =>
            new(focuser ?? (() => _focuser.Object), _store, _log);

        private void OffsetsOff() => _store.PutFilterWheelPolicy(new FilterWheelPolicyDto(UseFilterWheelOffsets: false));

        private static async Task WaitForAsync(Func<bool> condition, string failure) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline) {
                if (condition()) {
                    return;
                }
                await Task.Delay(20);
            }
            Assert.Fail(failure);
        }

        // ── Alpaca wheel ─────────────────────────────────────────────────────────────────

        private sealed class Wheel : IAsyncDisposable {
            public readonly ScriptedAlpacaDevice Box;
            public volatile string Position;
            public readonly FilterWheelService Service;

            public Wheel(string position, string offsets, FilterFocusOffset? focus) {
                Position = position;
                Box = ScriptedAlpacaDevice.Start(path =>
                    path.EndsWith("/connected", StringComparison.Ordinal) ? "true"
                    : path.EndsWith("/position", StringComparison.Ordinal) ? Position
                    : path.EndsWith("/names", StringComparison.Ordinal) ? "[\"L\",\"R\",\"Ha\"]"
                    : path.EndsWith("/focusoffsets", StringComparison.Ordinal) ? offsets
                    : null);
                Service = new FilterWheelService(focusOffset: focus) { RefreshPeriod = TimeSpan.FromMilliseconds(50) };
            }

            public DiscoveredDeviceDto Device => new(UniqueId: "efw", Name: "EFW", Type: DeviceType.FilterWheel,
                HostName: Box.BaseUri.Host, IpAddress: Box.BaseUri.Host, IpPort: Box.BaseUri.Port, AlpacaDeviceNumber: 0, UseHttps: false);

            public async ValueTask DisposeAsync() {
                Service.Dispose();
                await Box.DisposeAsync();
            }
        }

        private static async Task ConnectedAsync(Wheel wheel) {
            await wheel.Service.ConnectAsync(new ConnectRequestDto(wheel.Device), null, CancellationToken.None);
            await WaitForAsync(() => wheel.Service.GetAsync(CancellationToken.None).Result?.Runtime.CurrentSlot == int.Parse(wheel.Position, System.Globalization.CultureInfo.InvariantCulture)
                && wheel.Service.GetAsync(CancellationToken.None).Result?.Slots.Count > 0, "the wheel never reported");
        }

        [Test]
        public async Task A_REST_change_on_an_Alpaca_wheel_moves_the_focuser_once_after_it_lands() {
            await using var wheel = new Wheel("0", "[100,40,-20]", Offsets());
            await ConnectedAsync(wheel);

            await wheel.Service.ChangeFilterAsync(new FilterChangeRequestDto(2), null, CancellationToken.None);
            wheel.Position = "-1";
            await Task.Delay(300);
            Assert.That(_moves, Is.Empty, "the wheel has not landed");
            wheel.Position = "2";
            await WaitForAsync(() => !_moves.IsEmpty, "no focuser move");
            await Task.Delay(300);

            Assert.That(_moves, Is.EqualTo(new[] { -120 }));
        }

        [Test]
        public async Task A_sequencer_change_on_an_Alpaca_wheel_moves_the_focuser_once_before_returning() {
            await using var wheel = new Wheel("0", "[100,40,-20]", Offsets());
            await ConnectedAsync(wheel);

            var change = wheel.Service.ChangeFilter(new FilterInfo("Ha", 0, 2), token: CancellationToken.None);
            wheel.Position = "2";
            var done = await change;

            Assert.That(done.Position, Is.EqualTo(2));
            Assert.That(_moves, Is.EqualTo(new[] { -120 }));
            Assert.That(wheel.Service.GetInfo().SelectedFilter?.Position, Is.EqualTo(2), "what autofocus records its result against");
        }

        [Test]
        public async Task With_offsets_off_an_Alpaca_wheel_never_moves_the_focuser() {
            OffsetsOff();
            await using var wheel = new Wheel("0", "[100,40,-20]", Offsets());
            await ConnectedAsync(wheel);

            await wheel.Service.ChangeFilterAsync(new FilterChangeRequestDto(2), null, CancellationToken.None);
            wheel.Position = "2";
            await wheel.Service.ChangeFilter(new FilterInfo("R", 0, 1), token: CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
            await Task.Delay(300);

            Assert.That(_moves, Is.Empty);
        }

        [Test]
        public async Task A_wheel_whose_previous_position_is_unknown_makes_no_move() {
            await using var wheel = new Wheel("-1", "[100,40,-20]", Offsets());
            await wheel.Service.ConnectAsync(new ConnectRequestDto(wheel.Device), null, CancellationToken.None);
            await WaitForAsync(() => wheel.Service.GetAsync(CancellationToken.None).Result?.Slots.Count > 0, "no slots");

            var change = wheel.Service.ChangeFilter(new FilterInfo("Ha", 0, 2), token: CancellationToken.None);
            wheel.Position = "2";
            await change;

            Assert.That(_moves, Is.Empty);
        }

        [Test]
        public async Task Two_filters_with_the_same_offset_make_no_move() {
            await using var wheel = new Wheel("0", "[100,100,100]", Offsets());
            await ConnectedAsync(wheel);

            var change = wheel.Service.ChangeFilter(new FilterInfo("Ha", 0, 2), token: CancellationToken.None);
            wheel.Position = "2";
            await change;

            Assert.That(_moves, Is.Empty);
        }

        [Test]
        public async Task A_retry_after_an_unconfirmed_change_still_moves_from_the_filter_the_focuser_was_on() {
            await using var wheel = new Wheel("0", "[100,40,-20]", Offsets());
            await ConnectedAsync(wheel);
            wheel.Service.FilterChangeSettleMaxPolls = 1; // the first change ends unconfirmed

            await wheel.Service.ChangeFilter(new FilterInfo("Ha", 0, 2), token: CancellationToken.None)
                .ContinueWith(t => Assert.That(t.IsFaulted, Is.True), TaskScheduler.Default);
            wheel.Position = "2"; // it did land after all
            wheel.Service.FilterChangeSettleMaxPolls = 150;
            await wheel.Service.ChangeFilter(new FilterInfo("Ha", 0, 2), token: CancellationToken.None);

            Assert.That(_moves, Is.EqualTo(new[] { -120 }));
        }

        [Test]
        public async Task A_missing_focuser_leaves_the_change_successful_and_warns() {
            await using var wheel = new Wheel("0", "[100,40,-20]", Offsets(() => null));
            await ConnectedAsync(wheel);

            var change = wheel.Service.ChangeFilter(new FilterInfo("Ha", 0, 2), token: CancellationToken.None);
            wheel.Position = "2";
            var done = await change;

            Assert.That(done.Position, Is.EqualTo(2));
            Assert.That(_log.Warnings, Has.Exactly(1).Items);
        }

        [Test]
        public async Task A_failed_focuser_move_leaves_the_change_successful_and_warns() {
            _focuser.Setup(f => f.MoveFocuserRelative(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("focuser stalled"));
            await using var wheel = new Wheel("0", "[100,40,-20]", Offsets());
            await ConnectedAsync(wheel);

            var change = wheel.Service.ChangeFilter(new FilterInfo("Ha", 0, 2), token: CancellationToken.None);
            wheel.Position = "2";
            await change;

            Assert.That(_log.Warnings.Single(), Does.Contain("focuser").IgnoreCase);
        }

        // ── Manual wheel ─────────────────────────────────────────────────────────────────

        private ManualFilterWheelService Manual() {
            var svc = new ManualFilterWheelService(profileStore: _store, focusOffset: Offsets(), profileDir: _dir);
            return svc;
        }

        private static async Task<ManualFilterWheelService> OnFilterAsync(ManualFilterWheelService wheel, int position) {
            await wheel.ConnectAsync(new ConnectRequestDto(ManualFilterWheelService.Descriptor), null, CancellationToken.None);
            await wheel.ReportInstalledAsync(position, CancellationToken.None);
            return wheel;
        }

        private void ProfileOffsets(HeadlessProfileService profile) {
            var filters = profile.ActiveProfile.FilterWheelSettings.FilterWheelFilters;
            filters.Clear();
            filters.Add(new FilterInfo("L", 100, 0));
            filters.Add(new FilterInfo("R", 40, 1));
            filters.Add(new FilterInfo("Ha", -20, 2));
        }

        private ManualFilterWheelService ManualWithOffsets() {
            var profile = new HeadlessProfileService();
            ProfileOffsets(profile);
            return new ManualFilterWheelService(profileStore: _store, profileService: profile, focusOffset: Offsets(), profileDir: _dir);
        }

        [Test]
        public async Task A_REST_change_on_a_manual_wheel_moves_the_focuser_once_when_the_swap_is_confirmed() {
            using var wheel = ManualWithOffsets();
            await OnFilterAsync(wheel, 0);
            _moves.Clear();

            await wheel.ChangeFilterAsync(new FilterChangeRequestDto(2), null, CancellationToken.None);
            Assert.That(_moves, Is.Empty, "not until the user confirms");
            await wheel.ReportInstalledAsync(2, CancellationToken.None);

            Assert.That(_moves, Is.EqualTo(new[] { -120 }));
        }

        [Test]
        public async Task A_sequencer_change_on_a_manual_wheel_moves_the_focuser_once_before_the_run_continues() {
            using var wheel = ManualWithOffsets();
            await OnFilterAsync(wheel, 0);
            _moves.Clear();
            var moveGate = new TaskCompletionSource();
            _focuser.Setup(f => f.MoveFocuserRelative(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<int, CancellationToken>(async (d, _) => {
                    _moves.Enqueue(d);
                    await moveGate.Task;
                    return 0;
                });

            var change = wheel.ChangeFilter(new FilterInfo("Ha", 0, 2), token: CancellationToken.None);
            var report = wheel.ReportInstalledAsync(2, CancellationToken.None);
            await WaitForAsync(() => !_moves.IsEmpty, "no focuser move");
            await Task.Delay(100);
            Assert.That(change.IsCompleted, Is.False, "the sequence must wait for the focuser");
            moveGate.SetResult();
            await report;
            await change;

            Assert.That(_moves, Is.EqualTo(new[] { -120 }));
        }

        [Test]
        public async Task With_offsets_off_a_manual_wheel_never_moves_the_focuser() {
            OffsetsOff();
            using var wheel = ManualWithOffsets();
            await OnFilterAsync(wheel, 0);
            await wheel.ReportInstalledAsync(2, CancellationToken.None);

            Assert.That(_moves, Is.Empty);
        }

        [Test]
        public async Task A_manual_wheel_with_no_known_filter_makes_no_move_on_its_first_confirm() {
            using var wheel = ManualWithOffsets();
            await OnFilterAsync(wheel, 2);

            Assert.That(_moves, Is.Empty);
        }
    }
}
