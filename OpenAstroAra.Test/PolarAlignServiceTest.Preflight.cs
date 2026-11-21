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
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TelescopeAxes = OpenAstroAra.Core.Enums.TelescopeAxes;

namespace OpenAstroAra.Test {

    /// <summary>#2068 (ARA-18) — the pre-flight list the Start card shows is the one a start refuses on.</summary>
    public partial class PolarAlignServiceTest {

        [Test]
        public async Task Preflight_lists_every_check_and_Start_refuses_on_a_fail() {
            var fake = NewFake();
            var mount = NewMount();
            var store = NewStore();
            // A profile from before #2068: no mount's clearance is confirmed.
            store.PutPolarAlignSettings(store.GetPolarAlignSettings() with { MountClearance = null });
            using var svc = NewService(fake, new ScriptedSolver(), mount, store: store);

            var preflight = await svc.GetPreflightAsync(null, CancellationToken.None).ConfigureAwait(false);

            Assert.That(preflight.Checks.Select(c => c.Id), Is.EqualTo(PolarAlignPreflightChecks.All), "every check, in the order a start makes them");
            string StatusOf(PolarAlignPreflightDto dto, string id) => dto.Checks.Single(c => c.Id == id).Status;
            Assert.Multiple(() => {
                Assert.That(preflight.Ready, Is.False);
                Assert.That(StatusOf(preflight, PolarAlignPreflightChecks.ClearanceConfirmed), Is.EqualTo(PolarAlignPreflightStatuses.Fail));
                Assert.That(StatusOf(preflight, PolarAlignPreflightChecks.MountConnected), Is.EqualTo(PolarAlignPreflightStatuses.Ok));
                Assert.That(StatusOf(preflight, PolarAlignPreflightChecks.StartPointing), Is.EqualTo(PolarAlignPreflightStatuses.Ok));
                Assert.That(StatusOf(preflight, PolarAlignPreflightChecks.MountLimits), Is.EqualTo(PolarAlignPreflightStatuses.Warn));
                Assert.That(StatusOf(preflight, PolarAlignPreflightChecks.StartCwAngle), Is.EqualTo(PolarAlignPreflightStatuses.Unknown));
                Assert.That(preflight.MountKey, Is.EqualTo(PolarAlignPreflightEvaluator.UnidentifiedMountKey));
                Assert.That(preflight.Planned.Direction, Is.Not.Null, "the plan the next Start would confirm");
                Assert.That(preflight.Planned.MaxCwAngleDeg, Is.EqualTo(PolarAlignSettingsClamp.DefaultMaxCwAngleDeg));
            });

            var planRefusal = await Assert.ThrowsAsync<RefusalException>(() => svc.GetStartPlanAsync(null, CancellationToken.None));
            Assert.That(planRefusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignClearanceNotConfirmed), "start-plan refuses on the same check");
            var refusal = await Assert.ThrowsAsync<RefusalException>(() => svc.StartAsync(PolarAlignStages.Fine,
                new PolarAlignStartConfirmation(preflight.Planned.Direction!, preflight.Planned.NewOrigin ?? true), null, CancellationToken.None));
            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignClearanceNotConfirmed));
            Assert.That(fake.LeaseCalls, Is.Empty, "a refused start takes no lease");
            mount.Verify(m => m.MoveAxis(TelescopeAxes.Primary, It.IsNotIn(0.0)), Times.Never, "nothing moved");

            // The user confirms the clearance (the app's dialog writes the mount's entry): the list is ready and Start goes.
            store.PutPolarAlignSettings(store.GetPolarAlignSettings().Confirmed(preflight.MountKey!));
            var ready = await svc.GetPreflightAsync(null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(ready.Ready, Is.True, string.Join("; ", ready.Checks.Where(c => c.Status == PolarAlignPreflightStatuses.Fail).Select(c => c.Detail)));
            Assert.That(StatusOf(ready, PolarAlignPreflightChecks.ClearanceConfirmed), Is.EqualTo(PolarAlignPreflightStatuses.Ok));
            await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
            Assert.That(PaCalls(fake), Is.Not.Empty, "the start went ahead and took the lease");
            await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
