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
