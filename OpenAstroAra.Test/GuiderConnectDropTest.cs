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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Guider;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1850 — the guider's event socket dropped while the service was still Connecting (during the
    /// post-connect init). The service must end that attempt in Error and start §63.3 recovery, never
    /// report Connected over a dead PHD2Guider.
    /// </summary>
    [TestFixture]
    [Category("IO")]
    [Category("bench")]
    public class GuiderConnectDropTest {

        private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 20000) {
            var sw = Stopwatch.StartNew();
            while (!condition() && sw.ElapsedMilliseconds < timeoutMs) {
                await Task.Delay(50).ConfigureAwait(false);
            }
            return condition();
        }

        [Test]
        public async Task A_socket_drop_during_Connecting_ends_in_Error_and_starts_recovery() {
            await using var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            // The event socket dies while the post-connect init is still asking get_version.
            fake.OnRpc("get_version", _ => { fake.DropConnections(); return 0; });

            var recoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var supervisor = new Mock<IGuiderProcessSupervisor>();
            supervisor.Setup(s => s.QueryStatusAsync(It.IsAny<CancellationToken>()))
                .Callback(() => recoveryStarted.TrySetResult())
                .ReturnsAsync(GuiderProcessStatus.Active);
            var recovery = new GuiderRecoveryCoordinator(supervisor.Object, Mock.Of<INotificationService>(),
                Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance,
                new[] { TimeSpan.Zero }, (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1));
            using var svc = new GuiderService(new HeadlessProfileService(), recovery, NullLogger<GuiderService>.Instance,
                supervisor.Object, ws: null, profileStore: null, sequencerResolver: () => Mock.Of<ISequencerService>(),
                notifications: Mock.Of<INotificationService>(), faultLog: null, activeProfileIdResolver: null) {
                ReconnectAttemptInterval = TimeSpan.FromMilliseconds(200),
                ReconnectGraceFromSeconds = _ => TimeSpan.FromSeconds(20),
            };

            await svc.ConnectAsync(new GuiderConnectRequestDto("127.0.0.1", fake.Port), null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(await WaitUntilAsync(() => fake.ReceivedMethods.Contains("get_version")), Is.True, "init never reached get_version");
            var sw = Stopwatch.StartNew();
            while ((await svc.GetAsync(CancellationToken.None).ConfigureAwait(false))?.State == EquipmentConnectionState.Connecting && sw.ElapsedMilliseconds < 20000) {
                await Task.Delay(50).ConfigureAwait(false);
            }
            Assert.That((await svc.GetAsync(CancellationToken.None).ConfigureAwait(false))?.State, Is.EqualTo(EquipmentConnectionState.Error),
                "a drop during Connecting must not leave the service Connected over a dead link");
            Assert.That(await WaitUntilAsync(() => recoveryStarted.Task.IsCompleted), Is.True, "the drop never reached §63.3 recovery");
        }
    }
}
