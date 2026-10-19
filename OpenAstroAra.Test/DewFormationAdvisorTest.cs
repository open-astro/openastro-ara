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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§42.2 row 18 — dew formation advisory: the pure rule and the episode.</summary>
    [TestFixture]
    public class DewFormationAdvisorTest {

        private static double[] Rising() => [2.0, 2.0, 2.1, 2.2, 2.4, 2.5, 2.6, 2.7, 2.8];
        private static double[] Flat() => [2.0, 2.1, 2.0, 2.1, 2.0, 2.1, 2.0, 2.1, 2.0];
        private static readonly double[] HaFlat = [3.0, 3.1, 3.0, 3.1, 3.0, 3.1, 3.0];
        private static readonly double[] HaConstant = [3.0, 3.0, 3.0, 3.0, 3.0, 3.0, 3.0];
        private static readonly double[] LateRise = [2.2, 2.4, 2.6, 2.8, 3.0, 3.2, 3.4, 3.6, 3.8, 4.0];

        [Test]
        public void The_pattern_needs_saturated_air_at_the_dew_point_and_a_rising_hfr() {
            Assert.That(DewFormationRule.Evaluate(97, 8.0, 7.6, Rising()).Suspected, Is.True);
            Assert.That(DewFormationRule.Evaluate(97, 8.0, 7.6, Rising()).Detail, Does.Contain("HFR up"));
            Assert.That(DewFormationRule.Evaluate(80, 8.0, 7.6, Rising()).Suspected, Is.False, "dry air");
            Assert.That(DewFormationRule.Evaluate(97, 10.0, 7.6, Rising()).Suspected, Is.False, "2.4 °C above the dew point");
            Assert.That(DewFormationRule.Evaluate(97, 8.0, 7.6, Flat()).Suspected, Is.False, "steady HFR: just humid air");
            Assert.That(DewFormationRule.Evaluate(97, 8.0, 7.6, Rising().Take(4).ToArray()).Suspected, Is.False, "too few frames");
            Assert.That(DewFormationRule.Evaluate(null, 8.0, 7.6, Rising()).Suspected, Is.False, "no humidity reading");
            Assert.That(DewFormationRule.Evaluate(97, 8.0, 7.6, [2, 2, double.NaN, 0, -1, 2, 2.1, 2.2, 2.8, 2.9, 3.0]).Suspected, Is.True, "junk points are ignored");
        }

        [Test]
        public async Task Stop_cancels_a_hung_weather_read_and_returns_inside_the_hosts_budget() {
            // 2026-10-18 19:21: systemctl restart took the full 30 s, then the old process died with
            // SIGABRT — StopAsync's wait for the tick's gate was cancelled and the exception escaped.
            var weather = new Mock<IObservingConditionsService>();
            CancellationToken seen = default;
            weather.Setup(w => w.GetAsync(It.IsAny<CancellationToken>()))
                .Returns<CancellationToken>(async ct => { seen = ct; await Task.Delay(Timeout.Infinite, ct); return null; });
            var history = new ImageHistoryService();
            using var advisor = new DewFormationAdvisor(weather.Object, history, Mock.Of<IDiagnosticsService>(), Mock.Of<INotificationService>()) {
                PollInterval = TimeSpan.FromMilliseconds(20),
            };
            await advisor.StartAsync(CancellationToken.None);
            for (var i = 0; i < 100 && !seen.CanBeCanceled; i++) {
                await Task.Delay(10);
            }
            Assert.That(seen.CanBeCanceled, Is.True, "a tick is in flight, hung on the weather read");

            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stop = advisor.StopAsync(budget.Token);
            Assert.That(await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(4))), Is.SameAs(stop), "Stop returned well inside the budget");
            await stop;
            Assert.That(seen.IsCancellationRequested, Is.True, "the hung read was told to stop");
        }

        [Test]
        public async Task Stop_whose_budget_runs_out_returns_instead_of_throwing() {
            var weather = new Mock<IObservingConditionsService>();
            var hang = new TaskCompletionSource<ObservingConditionsDto?>();
            // Ignores its token on purpose: a read that cannot be cancelled.
            weather.Setup(w => w.GetAsync(It.IsAny<CancellationToken>())).Returns(hang.Task);
            using var advisor = new DewFormationAdvisor(weather.Object, new ImageHistoryService(), Mock.Of<IDiagnosticsService>(), Mock.Of<INotificationService>());
            var tick = advisor.CheckOnceAsync(CancellationToken.None);
            await Task.Delay(50);

            using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await advisor.StopAsync(budget.Token); // returns instead of throwing TaskCanceledException
            hang.SetResult(null);
            await tick;
        }

        [Test]
        public async Task One_episode_opens_the_diagnostic_and_posts_once_then_clears_when_the_pattern_breaks() {
            var weather = new Mock<IObservingConditionsService>();
            var dto = new ObservingConditionsDto("w", "Station", EquipmentConnectionState.Connected, 8.0, 97, 7.6, null, null, null, null, null, null, true, "now");
            weather.Setup(w => w.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => dto);
            var history = new ImageHistoryService();
            foreach (var h in Rising()) history.RecordImage("LIGHT", h, "L");
            history.RecordImage("FLAT", 9.9, null); // not a light: ignored
            var diagnostics = new Mock<IDiagnosticsService>();
            var notifications = new Mock<INotificationService>();
            using var advisor = new DewFormationAdvisor(weather.Object, history, diagnostics.Object, notifications.Object);

            await advisor.CheckOnceAsync(CancellationToken.None);
            await advisor.CheckOnceAsync(CancellationToken.None); // sustained: no second post
            diagnostics.Verify(d => d.CreateEventAsync(It.Is<DiagnosticEventDto>(e => e.EventType == "dew_formation" && e.Severity == DiagnosticHealth.Yellow),
                It.IsAny<string?>(), false, It.IsAny<CancellationToken>()), Times.Once);
            notifications.Verify(n => n.CreateAsync(It.Is<NotificationDto>(x => x.Title == "Dew formation suspected" && x.Severity == NotificationSeverity.Warning
                && x.Category == NotificationCategory.Equipment && x.Message.Contains("Wipe the optics")), It.IsAny<CancellationToken>()), Times.Once);
            diagnostics.Verify(d => d.ClearOpenEventsByTypeAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);

            dto = dto with { HumidityPct = 70 }; // the air dried out
            await advisor.CheckOnceAsync(CancellationToken.None);
            await advisor.CheckOnceAsync(CancellationToken.None);
            diagnostics.Verify(d => d.ClearOpenEventsByTypeAsync("dew_formation", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
            notifications.Verify(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()), Times.Once, "clearing is quiet");
        }

        [Test]
        public async Task A_failed_diagnostic_write_retries_next_tick_but_a_failed_notification_does_not_reopen() {
            var weather = new Mock<IObservingConditionsService>();
            weather.Setup(w => w.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
                new ObservingConditionsDto("w", "Station", EquipmentConnectionState.Connected, 8.0, 97, 7.6, null, null, null, null, null, null, true, "now"));
            var history = new ImageHistoryService();
            foreach (var h in Rising()) history.RecordImage("LIGHT", h, "L");
            var diagnostics = new Mock<IDiagnosticsService>();
            var writes = 0;
            diagnostics.Setup(d => d.CreateEventAsync(It.IsAny<DiagnosticEventDto>(), It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
                .Returns(() => ++writes == 1 ? Task.FromException(new InvalidOperationException("sqlite locked")) : Task.CompletedTask);
            var notifications = new Mock<INotificationService>();
            notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("inbox down"));
            using var advisor = new DewFormationAdvisor(weather.Object, history, diagnostics.Object, notifications.Object);

            await advisor.CheckOnceAsync(CancellationToken.None); // write fails → no episode yet
            await advisor.CheckOnceAsync(CancellationToken.None); // write lands, notification fails → episode open
            await advisor.CheckOnceAsync(CancellationToken.None); // sustained: nothing more
            diagnostics.Verify(d => d.CreateEventAsync(It.IsAny<DiagnosticEventDto>(), It.IsAny<string?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Exactly(2),
                "retried once after the failed write, then never duplicated");
            notifications.Verify(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()), Times.Once,
                "a failed post is best effort: it does not re-open the episode every tick");
        }

        [Test]
        public async Task Startup_clears_an_event_a_previous_daemon_left_open() {
            // The episode is in memory, the diagnostic is in SQLite: without this, autofocus would stay
            // deferred for good after a restart during dew.
            var diagnostics = new Mock<IDiagnosticsService>();
            using var advisor = new DewFormationAdvisor(null, null, diagnostics.Object, null);
            await advisor.StartAsync(CancellationToken.None);
            diagnostics.Verify(d => d.ClearOpenEventsByTypeAsync("dew_formation", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
            await advisor.StopAsync(CancellationToken.None);
        }

        [Test]
        public void The_hfr_window_is_the_current_filter_since_the_last_autofocus_newest_first() {
            var history = new ImageHistoryService();
            // Flat L lights, then a refocus, then Hα lights that are naturally broader: not a dew trend.
            foreach (var h in Flat()) history.RecordImage("LIGHT", h, "L");
            history.RecordAutofocus(5.0, "L");
            foreach (var h in HaFlat) history.RecordImage("LIGHT", h, "Ha");
            var window = DewFormationAdvisor.RecentLightHfr(history.ImagePoints, history.AutofocusPoints);
            Assert.That(window, Is.EqualTo(HaFlat), "only the Hα lights after the autofocus");
            Assert.That(DewFormationRule.Evaluate(97, 8.0, 7.6, window).Suspected, Is.False, "a filter change is not dew");

            // Mixed filters with no autofocus between: the current filter's lights only.
            var mixed = new ImageHistoryService();
            foreach (var h in Flat()) mixed.RecordImage("LIGHT", h, "L");
            foreach (var h in HaConstant) mixed.RecordImage("LIGHT", h, "Ha");
            Assert.That(DewFormationAdvisor.RecentLightHfr(mixed.ImagePoints, mixed.AutofocusPoints), Has.All.EqualTo(3.0));

            // A long run: only the newest WindowFrames count, so dew in the last subs moves the late third.
            var long_ = new ImageHistoryService();
            for (var i = 0; i < 300; i++) long_.RecordImage("LIGHT", 2.0, "L");
            foreach (var h in LateRise) long_.RecordImage("LIGHT", h, "L");
            var recent = DewFormationAdvisor.RecentLightHfr(long_.ImagePoints, long_.AutofocusPoints);
            Assert.That(recent, Has.Count.EqualTo(DewFormationAdvisor.WindowFrames));
            Assert.That(DewFormationRule.Evaluate(97, 8.0, 7.6, recent).Suspected, Is.True, "dew in the last ten subs is seen");
        }

        [Test]
        public async Task A_station_that_is_not_connected_keeps_the_last_verdict() {
            var weather = new Mock<IObservingConditionsService>();
            var dto = new ObservingConditionsDto("w", "Station", EquipmentConnectionState.Connected, 8.0, 97, 7.6, null, null, null, null, null, null, true, "now");
            weather.Setup(w => w.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => dto);
            var history = new ImageHistoryService();
            foreach (var h in Rising()) history.RecordImage("LIGHT", h, "L");
            var diagnostics = new Mock<IDiagnosticsService>();
            using var advisor = new DewFormationAdvisor(weather.Object, history, diagnostics.Object, null);
            await advisor.CheckOnceAsync(CancellationToken.None);
            dto = dto with { State = EquipmentConnectionState.Disconnected, HumidityPct = null, TemperatureC = null, DewPointC = null };
            await advisor.CheckOnceAsync(CancellationToken.None);
            diagnostics.Verify(d => d.ClearOpenEventsByTypeAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never,
                "a station going away is not 'the dew cleared'");
        }

        [Test]
        public async Task No_weather_station_or_a_failed_read_means_no_verdict_and_no_noise() {
            var history = new ImageHistoryService();
            foreach (var h in Rising()) history.RecordImage("LIGHT", h, "L");
            var diagnostics = new Mock<IDiagnosticsService>();
            var notifications = new Mock<INotificationService>();
            using var none = new DewFormationAdvisor(null, history, diagnostics.Object, notifications.Object);
            await none.CheckOnceAsync(CancellationToken.None);

            var weather = new Mock<IObservingConditionsService>();
            weather.Setup(w => w.GetAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("station offline"));
            using var failing = new DewFormationAdvisor(weather.Object, history, diagnostics.Object, notifications.Object);
            await failing.CheckOnceAsync(CancellationToken.None);

            diagnostics.VerifyNoOtherCalls();
            notifications.VerifyNoOtherCalls();
        }
    }
}
