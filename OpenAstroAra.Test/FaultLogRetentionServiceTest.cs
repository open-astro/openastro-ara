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
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§42.5 fault-log retention sweep (#1145): the knob is read live, 0 keeps everything,
    /// the cutoff is now − days, and the hosted loop sweeps after its startup delay.</summary>
    [TestFixture]
    public class FaultLogRetentionServiceTest {

        private static readonly DateTimeOffset Now = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

        private static IProfileStore StoreWith(int days) {
            var store = new Mock<IProfileStore>();
            store.Setup(s => s.GetStorageSettings())
                .Returns(new StorageSettingsDto("/tmp", "fits", "rice", "t", FaultLogRetentionDays: days));
            return store.Object;
        }

        [Test]
        public void CutoffFor_is_now_minus_days_and_null_when_off() {
            Assert.That(FaultLogRetentionService.CutoffFor(90, Now), Is.EqualTo(Now.AddDays(-90)));
            Assert.That(FaultLogRetentionService.CutoffFor(0, Now), Is.Null, "0 keeps everything");
            Assert.That(FaultLogRetentionService.CutoffFor(-5, Now), Is.Null, "negative is treated as off, defensively");
        }

        [Test]
        public async Task SweepOnce_prunes_at_the_configured_cutoff() {
            var log = new Mock<IFaultLogService>();
            log.Setup(l => l.PruneBeforeAsync(Now.AddDays(-30), It.IsAny<CancellationToken>())).ReturnsAsync(3);
            using var svc = new FaultLogRetentionService(StoreWith(30), log.Object, clock: () => Now);

            Assert.That(await svc.SweepOnceAsync(CancellationToken.None), Is.EqualTo(3));
            log.Verify(l => l.PruneBeforeAsync(Now.AddDays(-30), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task SweepOnce_with_retention_off_never_touches_the_log() {
            var log = new Mock<IFaultLogService>(MockBehavior.Strict);
            using var svc = new FaultLogRetentionService(StoreWith(0), log.Object, clock: () => Now);

            Assert.That(await svc.SweepOnceAsync(CancellationToken.None), Is.EqualTo(0));
            log.VerifyNoOtherCalls();
        }

        [Test]
        public async Task The_hosted_loop_sweeps_after_the_startup_delay() {
            var log = new Mock<IFaultLogService>();
            var swept = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
            log.Setup(l => l.PruneBeforeAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .Callback<DateTimeOffset, CancellationToken>((c, _) => swept.TrySetResult(c))
                .ReturnsAsync(0);
            using var svc = new FaultLogRetentionService(StoreWith(7), log.Object,
                interval: TimeSpan.FromHours(1), startupDelay: TimeSpan.FromMilliseconds(10), clock: () => Now);

            await svc.StartAsync(CancellationToken.None);
            try {
                var cutoff = await swept.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(cutoff, Is.EqualTo(Now.AddDays(-7)));
            } finally {
                await svc.StopAsync(CancellationToken.None);
            }
        }
    }
}
