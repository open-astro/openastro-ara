#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    // #1298 — status mapping of the filter-wheel change and the manual wheel's /installed, on the
    // extracted handlers (the repo's endpoint-test pattern). Both used to answer 500 for a bad slot.
    [TestFixture]
    public class EquipmentEndpointsFilterWheelTest {

        private static int StatusOf(IResult r) => r switch {
            ProblemHttpResult p => p.StatusCode,
            NoContent => StatusCodes.Status204NoContent,
            Accepted<OperationAcceptedDto> => StatusCodes.Status202Accepted,
            _ => throw new InvalidOperationException(r.GetType().Name),
        };

        private static (FilterWheelRouter Router, FilterWheelService Alpaca, ManualFilterWheelService Manual) NewRouter() {
            var store = new InMemoryProfileStore();
            store.PutFilterWheelLabels(new FilterWheelLabelsDto(["L", "Ha"]));
            var alpaca = new FilterWheelService();
            var manual = new ManualFilterWheelService(profileStore: store);
            return (new FilterWheelRouter(alpaca, manual), alpaca, manual);
        }

        [Test]
        public async Task Change_maps_bad_slot_to_400_and_not_connected_to_409() {
            var (router, alpaca, manual) = NewRouter();
            using (router) using (alpaca) using (manual) {
                Assert.That(StatusOf(await EquipmentEndpoints.ChangeFilterAsync(new FilterChangeRequestDto(0), null, router, CancellationToken.None)),
                    Is.EqualTo(StatusCodes.Status409Conflict), "Alpaca wheel not connected");
                Assert.That(StatusOf(await EquipmentEndpoints.ChangeFilterAsync(new FilterChangeRequestDto(-1), null, router, CancellationToken.None)),
                    Is.EqualTo(StatusCodes.Status400BadRequest));

                await router.ConnectAsync(new ConnectRequestDto(ManualFilterWheelService.Descriptor), null, CancellationToken.None);
                Assert.That(StatusOf(await EquipmentEndpoints.ChangeFilterAsync(new FilterChangeRequestDto(5), null, router, CancellationToken.None)),
                    Is.EqualTo(StatusCodes.Status400BadRequest));
                Assert.That(StatusOf(await EquipmentEndpoints.ChangeFilterAsync(new FilterChangeRequestDto(1), null, router, CancellationToken.None)),
                    Is.EqualTo(StatusCodes.Status202Accepted));
            }
        }

        [Test]
        public async Task Installed_is_409_off_the_manual_wheel_400_for_a_bad_slot_204_otherwise() {
            var (router, alpaca, manual) = NewRouter();
            using (router) using (alpaca) using (manual) {
                Assert.That(StatusOf(await EquipmentEndpoints.ReportFilterInstalledAsync(new FilterInstalledRequestDto(0), router, CancellationToken.None)),
                    Is.EqualTo(StatusCodes.Status409Conflict), "the selected wheel is the Alpaca one");

                await router.ConnectAsync(new ConnectRequestDto(ManualFilterWheelService.Descriptor), null, CancellationToken.None);
                Assert.That(StatusOf(await EquipmentEndpoints.ReportFilterInstalledAsync(new FilterInstalledRequestDto(9), router, CancellationToken.None)),
                    Is.EqualTo(StatusCodes.Status400BadRequest));
                Assert.That(StatusOf(await EquipmentEndpoints.ReportFilterInstalledAsync(new FilterInstalledRequestDto(1), router, CancellationToken.None)),
                    Is.EqualTo(StatusCodes.Status204NoContent));

                await router.DisconnectAsync(null, CancellationToken.None);
                Assert.That(StatusOf(await EquipmentEndpoints.ReportFilterInstalledAsync(new FilterInstalledRequestDto(0), router, CancellationToken.None)),
                    Is.EqualTo(StatusCodes.Status409Conflict), "manual wheel disconnected");
            }
        }

        [Test]
        public async Task Swap_cancel_is_409_off_the_manual_wheel_and_204_on_it() {
            var (router, alpaca, manual) = NewRouter();
            using (router) using (alpaca) using (manual) {
                Assert.That(StatusOf(await EquipmentEndpoints.CancelManualSwapAsync(router, CancellationToken.None)),
                    Is.EqualTo(StatusCodes.Status409Conflict));
                await router.ConnectAsync(new ConnectRequestDto(ManualFilterWheelService.Descriptor), null, CancellationToken.None);
                await router.ChangeFilterAsync(new FilterChangeRequestDto(1), null, CancellationToken.None);
                Assert.That(StatusOf(await EquipmentEndpoints.CancelManualSwapAsync(router, CancellationToken.None)),
                    Is.EqualTo(StatusCodes.Status204NoContent));
                Assert.That((await router.GetAsync(CancellationToken.None))!.Runtime.PendingSlot, Is.Null);
            }
        }
    }
}
