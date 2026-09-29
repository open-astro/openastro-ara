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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Alpaca;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeviceType = OpenAstroAra.Server.Contracts.DeviceType;

namespace OpenAstroAra.Test {

    /// <summary>#1187 — the sequencer's <c>CoolCamera</c>/<c>WarmCamera</c> mediator calls on the real
    /// <see cref="CameraService"/>, against a loopback simulated camera whose sensor follows the
    /// set-point it was sent (or a scripted temperature). The ramp's waits go through the
    /// <see cref="CameraService.CoolingDelay"/> seam, so the minutes-long ramps run instantly and
    /// every wait they asked for is recorded.</summary>
    [TestFixture]
    [Category("bench")]
    public class CameraServiceCoolingTest {

        /// <summary>A scripted Alpaca camera: cooler state and set-point are whatever was last PUT;
        /// the sensor follows the set-point instantly unless <see cref="FixedTemperature"/> pins it.</summary>
        private sealed class SimCamera : IAsyncDisposable {
            public ScriptedAlpacaDevice Box { get; }
            public double Ambient { get; init; } = 20;
            public double? FixedTemperature { get; init; }
            public double CoolerPower { get; init; } = 50;
            public bool CoolerPowerUnreadable { get; init; }
            public bool CanSetTemperature { get; init; } = true;
            public bool InitialCoolerOn { get; init; }
            public double? InitialSetpoint { get; init; }

            public SimCamera() {
                Box = ScriptedAlpacaDevice.Start(Respond);
            }

            private string? Respond(string path) {
                if (path.EndsWith("/ccdtemperature", StringComparison.Ordinal)) {
                    return Num(Temperature);
                }
                if (path.EndsWith("/setccdtemperature", StringComparison.Ordinal)) {
                    return Num(Setpoint ?? Ambient);
                }
                if (path.EndsWith("/cooleron", StringComparison.Ordinal)) {
                    return CoolerOn ? "true" : "false";
                }
                if (path.EndsWith("/coolerpower", StringComparison.Ordinal)) {
                    return CoolerPowerUnreadable ? "\"n/a\"" : Num(CoolerPower);
                }
                if (path.EndsWith("/cansetccdtemperature", StringComparison.Ordinal)) {
                    return CanSetTemperature ? "true" : "false";
                }
                return null;
            }

            private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

            public double Temperature => FixedTemperature ?? (CoolerOn && Setpoint is double sp ? sp : Ambient);

            public bool CoolerOn {
                get {
                    var last = Box.Puts.LastOrDefault(p => p.Path.EndsWith("/cooleron", StringComparison.Ordinal));
                    return last.Path is null ? InitialCoolerOn : bool.Parse(Field(last.Body, "CoolerOn"));
                }
            }

            public double? Setpoint => Setpoints.Count > 0 ? Setpoints[^1] : InitialSetpoint;

            /// <summary>Every set-point the camera was sent, in order.</summary>
            public List<double> Setpoints => Box.Puts
                .Where(p => p.Path.EndsWith("/setccdtemperature", StringComparison.Ordinal))
                .Select(p => double.Parse(Field(p.Body, "SetCCDTemperature"), CultureInfo.InvariantCulture))
                .ToList();

            /// <summary>Every cooler on/off write, in order.</summary>
            public List<bool> CoolerWrites => Box.Puts
                .Where(p => p.Path.EndsWith("/cooleron", StringComparison.Ordinal))
                .Select(p => bool.Parse(Field(p.Body, "CoolerOn")))
                .ToList();

            private static string Field(string body, string name) {
                foreach (var pair in body.Split('&')) {
                    var eq = pair.IndexOf('=', StringComparison.Ordinal);
                    if (eq > 0 && string.Equals(Uri.UnescapeDataString(pair[..eq]), name, StringComparison.OrdinalIgnoreCase)) {
                        return Uri.UnescapeDataString(pair[(eq + 1)..]);
                    }
                }
                throw new InvalidOperationException($"PUT body '{body}' has no {name}");
            }

            public ValueTask DisposeAsync() => Box.DisposeAsync();
        }

        /// <summary>Synchronous progress sink (<see cref="Progress{T}"/> posts asynchronously).</summary>
        private sealed class StatusLog : IProgress<ApplicationStatus> {
            public ConcurrentQueue<ApplicationStatus> Reports { get; } = new();
            public void Report(ApplicationStatus value) => Reports.Enqueue(value);
            public IEnumerable<string> Statuses => Reports.Select(r => r.Status ?? string.Empty);
        }

        private static DiscoveredDeviceDto Device(ScriptedAlpacaDevice box) => new(
            UniqueId: "Camera-under-test", Name: "Sim Camera", Type: DeviceType.Camera,
            HostName: box.BaseUri.Host, IpAddress: box.BaseUri.Host, IpPort: box.BaseUri.Port,
            AlpacaDeviceNumber: 0, UseHttps: false);

        private static async Task WaitForAsync(Func<Task<bool>> condition, string failure) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline) {
                if (await condition()) {
                    return;
                }
                await Task.Delay(100);
            }
            Assert.Fail(failure);
        }

        /// <summary>A connected service (capabilities read) whose cooling waits are recorded and
        /// return at once.</summary>
        private static async Task<(CameraService Svc, ConcurrentQueue<TimeSpan> Delays)> ConnectedAsync(
                SimCamera cam, Func<ICoolingFanActuator?>? fan = null, Action<int>? onDelay = null) {
            var svc = new CameraService(fan: fan);
            var delays = new ConcurrentQueue<TimeSpan>();
            svc.CoolingDelay = (d, ct) => {
                delays.Enqueue(d);
                onDelay?.Invoke(delays.Count);
                return ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;
            };
            await svc.ConnectAsync(new ConnectRequestDto(Device(cam.Box)), null, CancellationToken.None);
            await WaitForAsync(async () => (await svc.GetAsync(CancellationToken.None)) is { State: EquipmentConnectionState.Connected, Capabilities: not null },
                "camera never connected with its capabilities read");
            return (svc, delays);
        }

        [Test]
        public async Task An_immediate_cool_sets_the_target_turns_the_cooler_on_and_returns_true() {
            await using var cam = new SimCamera();
            var (svc, delays) = await ConnectedAsync(cam);
            using var _ = svc;

            var ok = await svc.CoolCamera(-10, TimeSpan.Zero, new StatusLog(), CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(cam.Setpoints, Is.EqualTo(new[] { -10.0 }), "duration 0 writes the target once, no ramp");
            Assert.That(cam.CoolerWrites, Does.Contain(true));
            Assert.That(delays, Is.Empty, "already at target after the write: no waiting");
        }

        [Test]
        public async Task A_ramped_cool_steps_the_setpoint_every_15_seconds_over_the_duration() {
            await using var cam = new SimCamera();
            var (svc, delays) = await ConnectedAsync(cam);
            using var _ = svc;

            var ok = await svc.CoolCamera(-10, TimeSpan.FromMinutes(1), new StatusLog(), CancellationToken.None);

            Assert.That(ok, Is.True);
            // 20 °C → -10 °C over 60 s: the line at 15/30/45/60 s is 12.5/5/-2.5/-10, rounded to
            // whole degrees away from zero (NINA's integer steps); the exact target ends the ramp.
            Assert.That(cam.Setpoints, Is.EqualTo(new[] { 13.0, 5.0, -3.0, -10.0 }));
            Assert.That(delays.Take(4), Is.All.EqualTo(TimeSpan.FromSeconds(15)));
        }

        [Test]
        public async Task A_non_integer_target_is_written_exactly_at_the_end_of_the_ramp() {
            await using var cam = new SimCamera();
            var (svc, _) = await ConnectedAsync(cam);
            using var __ = svc;

            Assert.That(await svc.CoolCamera(-7.5, TimeSpan.FromSeconds(30), new StatusLog(), CancellationToken.None), Is.True);
            Assert.That(cam.Setpoints[^1], Is.EqualTo(-7.5));
        }

        [Test]
        public async Task A_cool_that_stalls_short_of_the_target_at_full_power_returns_false() {
            // Sensor stuck at 5 °C with the TEC at 100 %: it will never get to -10.
            await using var cam = new SimCamera { FixedTemperature = 5, CoolerPower = 100 };
            var (svc, delays) = await ConnectedAsync(cam);
            using var _ = svc;
            var log = new StatusLog();

            var ok = await svc.CoolCamera(-10, TimeSpan.Zero, log, CancellationToken.None);

            Assert.That(ok, Is.False);
            var waited = TimeSpan.FromTicks(delays.Sum(d => d.Ticks));
            Assert.That(waited, Is.GreaterThanOrEqualTo(TimeSpan.FromMinutes(2)).And.LessThan(TimeSpan.FromMinutes(3)),
                "gives up after 2 minutes without progress at saturated cooler power");
            Assert.That(log.Statuses, Has.Some.Contains("could not reach"));
        }

        [Test]
        public async Task A_cool_that_stalls_while_the_cooler_has_headroom_waits_the_longer_timeout() {
            // TEC at 50 %: still regulating, so the 10-minute bound applies, not the 2-minute one.
            await using var cam = new SimCamera { FixedTemperature = 5, CoolerPower = 50 };
            var (svc, delays) = await ConnectedAsync(cam);
            using var _ = svc;

            var ok = await svc.CoolCamera(-10, TimeSpan.Zero, new StatusLog(), CancellationToken.None);

            Assert.That(ok, Is.False);
            var waited = TimeSpan.FromTicks(delays.Sum(d => d.Ticks));
            Assert.That(waited, Is.GreaterThanOrEqualTo(TimeSpan.FromMinutes(10)).And.LessThan(TimeSpan.FromMinutes(11)),
                "gives up after 10 minutes without progress while the cooler has headroom");
        }

        [Test]
        public async Task A_cool_whose_sensor_never_moves_with_unknown_cooler_power_still_gives_up() {
            // No readable CoolerPower (unparseable) — must not hang waiting for a power reading.
            await using var cam = new SimCamera { FixedTemperature = 5, CoolerPowerUnreadable = true };
            var (svc, _) = await ConnectedAsync(cam);
            using var __ = svc;

            Assert.That(await svc.CoolCamera(-10, TimeSpan.Zero, new StatusLog(), CancellationToken.None), Is.False);
        }

        [Test]
        public async Task Cancelling_mid_ramp_throws_and_holds_the_setpoint_at_the_current_temperature() {
            await using var cam = new SimCamera { FixedTemperature = 18 };
            using var cts = new CancellationTokenSource();
            var (svc, _) = await ConnectedAsync(cam, onDelay: n => { if (n == 2) { cts.Cancel(); } });
            using var __ = svc;

            Assert.CatchAsync<OperationCanceledException>(
                () => svc.CoolCamera(-10, TimeSpan.FromMinutes(1), new StatusLog(), cts.Token));

            Assert.That(cam.Setpoints, Has.Count.EqualTo(3), "two ramp steps, then the hold");
            Assert.That(cam.Setpoints[^1], Is.EqualTo(18.0), "the TEC is left holding where the sensor is, like NINA");
            await WaitForAsync(() => Task.FromResult(svc.TargetTemp == 18.0), "a stale ramp target outlived the cancel");
        }

        [Test]
        public async Task A_camera_without_a_settable_cooler_fails_at_once_with_a_clear_status() {
            await using var cam = new SimCamera { CanSetTemperature = false };
            var (svc, delays) = await ConnectedAsync(cam);
            using var _ = svc;
            var log = new StatusLog();

            Assert.That(await svc.CoolCamera(-10, TimeSpan.FromMinutes(5), log, CancellationToken.None), Is.False);
            Assert.That(log.Statuses, Has.Some.Contains("does not support"));
            Assert.That(cam.Setpoints, Is.Empty, "nothing written to the camera");
            Assert.That(delays, Is.Empty, "no waiting");
            Assert.That(svc.GetInfo().CanSetTemperature, Is.False);
        }

        [Test]
        public async Task A_disconnected_camera_cools_and_warms_to_false_without_waiting() {
            using var svc = new CameraService();
            var log = new StatusLog();
            Assert.That(await svc.CoolCamera(-10, TimeSpan.Zero, log, CancellationToken.None), Is.False);
            Assert.That(await svc.WarmCamera(TimeSpan.Zero, log, CancellationToken.None), Is.False);
            Assert.That(log.Statuses, Has.Some.Contains("not connected"));
        }

        [Test]
        public async Task A_warm_ramps_toward_ambient_then_turns_the_cooler_off() {
            await using var cam = new SimCamera { InitialCoolerOn = true, InitialSetpoint = -10 };
            var (svc, delays) = await ConnectedAsync(cam);
            using var _ = svc;

            var ok = await svc.WarmCamera(TimeSpan.FromMinutes(1), new StatusLog(), CancellationToken.None);

            Assert.That(ok, Is.True);
            Assert.That(cam.Setpoints, Is.Ordered.Ascending);
            Assert.That(cam.Setpoints[^1], Is.EqualTo(20.0), "warms to NINA's 20 °C");
            Assert.That(cam.CoolerWrites[^1], Is.False, "the cooler is switched off last");
            Assert.That(delays, Does.Contain(TimeSpan.FromSeconds(20)), "settles before the cooler-off");
        }

        [Test]
        public async Task A_warm_that_cannot_reach_ambient_still_turns_the_cooler_off() {
            // A cold night: the sensor sits at 3 °C with the TEC idle — 20 °C is out of reach.
            await using var cam = new SimCamera { InitialCoolerOn = true, InitialSetpoint = -10, FixedTemperature = 3, CoolerPower = 0 };
            var (svc, _) = await ConnectedAsync(cam);
            using var __ = svc;

            Assert.That(await svc.WarmCamera(TimeSpan.Zero, new StatusLog(), CancellationToken.None), Is.True);
            Assert.That(cam.CoolerWrites[^1], Is.False);
        }

        [Test]
        public async Task A_warm_with_the_cooler_already_off_never_turns_it_on() {
            await using var cam = new SimCamera { InitialCoolerOn = false };
            var (svc, _) = await ConnectedAsync(cam);
            using var __ = svc;

            Assert.That(await svc.WarmCamera(TimeSpan.FromMinutes(1), new StatusLog(), CancellationToken.None), Is.True);
            Assert.That(cam.Setpoints, Is.Empty);
            Assert.That(cam.CoolerWrites, Has.None.True);
        }

        [Test]
        public async Task Cooling_goes_through_the_fan_interlock_fan_on_first_and_fan_off_after_the_warm() {
            await using var cam = new SimCamera();
            var fanValue = 0.0;
            var actuator = new Mock<ICoolingFanActuator>();
            actuator.Setup(a => a.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new List<SwitchDto> {
                new("sw-5", 0, "ToupTek Thermal Switch", EquipmentConnectionState.Connected,
                    [new SwitchPortDto(1, "Fan", Value: fanValue, Min: 0, Max: 1, CanWrite: true)]),
            });
            var setpointsWhenFanStarted = -1;
            actuator.Setup(a => a.SetFanValueAsync("sw-5", It.IsAny<SwitchValueRequestDto>(), It.IsAny<CancellationToken>()))
                .Callback<string, SwitchValueRequestDto, CancellationToken>((_, r, _) => {
                    if (r.Value == 1 && setpointsWhenFanStarted < 0) {
                        setpointsWhenFanStarted = cam.Setpoints.Count;
                    }
                    fanValue = r.Value;
                })
                .Returns(Task.CompletedTask);
            var (svc, _) = await ConnectedAsync(cam, fan: () => actuator.Object);
            using var __ = svc;

            Assert.That(await svc.CoolCamera(-10, TimeSpan.Zero, new StatusLog(), CancellationToken.None), Is.True);
            Assert.That(setpointsWhenFanStarted, Is.Zero, "the fan starts before the first set-point write");
            Assert.That(fanValue, Is.EqualTo(1));

            Assert.That(await svc.WarmCamera(TimeSpan.Zero, new StatusLog(), CancellationToken.None), Is.True);
            Assert.That(fanValue, Is.Zero, "the warm's cooler-off stops the fan");
        }

        [Test]
        public async Task A_fan_that_cannot_start_refuses_the_cool_before_anything_reaches_the_camera() {
            await using var cam = new SimCamera();
            var actuator = new Mock<ICoolingFanActuator>();
            actuator.Setup(a => a.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SwitchDto> {
                new("sw-5", 0, "ToupTek Thermal Switch", EquipmentConnectionState.Connected,
                    [new SwitchPortDto(1, "Fan", Value: 0, Min: 0, Max: 1, CanWrite: true)]),
            });
            actuator.Setup(a => a.SetFanValueAsync(It.IsAny<string>(), It.IsAny<SwitchValueRequestDto>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TimeoutException("bridge did not answer the fan write"));
            var (svc, _) = await ConnectedAsync(cam, fan: () => actuator.Object);
            using var __ = svc;
            var log = new StatusLog();

            Assert.That(await svc.CoolCamera(-10, TimeSpan.Zero, log, CancellationToken.None), Is.False);
            Assert.That(log.Statuses, Has.Some.Contains("cooling fan could not be started"));
            Assert.That(cam.Setpoints, Is.Empty);
            Assert.That(cam.CoolerWrites, Has.None.True);
        }

        [Test]
        public async Task TargetTemp_and_AtTargetTemp_follow_the_cooler_state() {
            await using var cam = new SimCamera();
            var (svc, _) = await ConnectedAsync(cam);
            using var __ = svc;
            Assert.That(svc.TargetTemp, Is.NaN, "cooler off: no target");
            Assert.That(svc.AtTargetTemp, Is.False);

            Assert.That(await svc.CoolCamera(-10, TimeSpan.Zero, new StatusLog(), CancellationToken.None), Is.True);

            await WaitForAsync(() => Task.FromResult(svc.TargetTemp == -10 && svc.AtTargetTemp),
                "TargetTemp/AtTargetTemp never reflected the reached set-point");
            Assert.That(svc.GetInfo().TemperatureSetPoint, Is.EqualTo(-10));

            Assert.That(await svc.WarmCamera(TimeSpan.Zero, new StatusLog(), CancellationToken.None), Is.True);
            await WaitForAsync(() => Task.FromResult(double.IsNaN(svc.TargetTemp) && !svc.AtTargetTemp),
                "TargetTemp stayed set after the cooler was switched off");
        }

        [Test]
        public async Task The_sequencer_CoolCamera_instruction_validates_and_runs_past_its_step() {
            // The step every temperature-set dark library starts with (CalibrationSequenceBuilder).
            await using var cam = new SimCamera();
            var (svc, _) = await ConnectedAsync(cam);
            using var __ = svc;
            var item = new OpenAstroAra.Sequencer.SequenceItem.Camera.CoolCamera(svc) { Temperature = -10, Duration = 0 };

            Assert.That(item.Validate(), Is.True, string.Join(", ", item.Issues));
            Assert.DoesNotThrowAsync(() => item.Execute(new StatusLog(), CancellationToken.None));
            Assert.That(cam.Setpoints[^1], Is.EqualTo(-10.0));
        }
    }
}
