#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §63.6 (guider-e-4b-2) — the dark-library build endpoint's error-to-status mapping: a valid request is
    /// 202-Accepted, a bad request (validation) is 400, and a disconnected guider is 409. The service is mocked
    /// so the mapping is covered without an ASP.NET host or a live guider.
    /// </summary>
    [TestFixture]
    public class EquipmentEndpointsDarkLibraryTest {

        private static readonly BuildDarkLibraryRequestDto Request = new(FrameCount: 5);

        [Test]
        public async Task Build_returns_202_accepted_on_success() {
            var accepted = new OperationAcceptedDto(Guid.NewGuid(), "guider.dark_library.build", DateTimeOffset.UtcNow, "idem-1");
            var svc = new Mock<IGuiderService>();
            svc.Setup(s => s.BuildDarkLibraryAsync(Request, "idem-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(accepted);

            var result = await EquipmentEndpoints.BuildDarkLibraryAsync(Request, "idem-1", svc.Object, CancellationToken.None);

            var typed = result as Accepted<OperationAcceptedDto>;
            Assert.That(typed, Is.Not.Null);
            Assert.That(typed!.Value, Is.SameAs(accepted));
        }

        [Test]
        public async Task Build_maps_validation_ArgumentException_to_400() {
            var svc = new Mock<IGuiderService>();
            svc.Setup(s => s.BuildDarkLibraryAsync(It.IsAny<BuildDarkLibraryRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ArgumentOutOfRangeException("frameCount"));

            var result = await EquipmentEndpoints.BuildDarkLibraryAsync(Request, null, svc.Object, CancellationToken.None);

            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status400BadRequest));
        }

        [Test]
        public async Task Build_maps_not_connected_InvalidOperation_to_409() {
            var svc = new Mock<IGuiderService>();
            svc.Setup(s => s.BuildDarkLibraryAsync(It.IsAny<BuildDarkLibraryRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("guider is not connected"));

            var result = await EquipmentEndpoints.BuildDarkLibraryAsync(Request, null, svc.Object, CancellationToken.None);

            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status409Conflict));
        }

        // ── §63.6 guider-e-4c-b-2: defect-map build endpoint mapping (mirrors the dark-library handler) ──

        private static readonly BuildDefectMapDarksRequestDto DefectRequest = new(ExposureMs: 3000, FrameCount: 10);

        [Test]
        public async Task DefectMap_build_returns_202_accepted_on_success() {
            var accepted = new OperationAcceptedDto(Guid.NewGuid(), "guider.defect_map.build", DateTimeOffset.UtcNow, "idem-2");
            var svc = new Mock<IGuiderService>();
            svc.Setup(s => s.BuildDefectMapDarksAsync(DefectRequest, "idem-2", It.IsAny<CancellationToken>()))
                .ReturnsAsync(accepted);

            var result = await EquipmentEndpoints.BuildDefectMapDarksAsync(DefectRequest, "idem-2", svc.Object, CancellationToken.None);

            var typed = result as Accepted<OperationAcceptedDto>;
            Assert.That(typed, Is.Not.Null);
            Assert.That(typed!.Value, Is.SameAs(accepted));
        }

        [Test]
        public async Task DefectMap_build_maps_validation_ArgumentException_to_400() {
            var svc = new Mock<IGuiderService>();
            svc.Setup(s => s.BuildDefectMapDarksAsync(It.IsAny<BuildDefectMapDarksRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ArgumentOutOfRangeException("exposureMs"));

            var result = await EquipmentEndpoints.BuildDefectMapDarksAsync(DefectRequest, null, svc.Object, CancellationToken.None);

            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status400BadRequest));
        }

        [Test]
        public async Task DefectMap_build_maps_not_connected_or_busy_InvalidOperation_to_409() {
            var svc = new Mock<IGuiderService>();
            svc.Setup(s => s.BuildDefectMapDarksAsync(It.IsAny<BuildDefectMapDarksRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("a calibration build is already in progress"));

            var result = await EquipmentEndpoints.BuildDefectMapDarksAsync(DefectRequest, null, svc.Object, CancellationToken.None);

            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status409Conflict));
        }

        // ── §63.6 guider-e-4c-c: calibration enable/disable toggle endpoint mapping ──

        private static readonly CalibrationFilesStatusDto SampleStatus = new(
            ProfileId: 3, DarkLibraryPath: "/d.fits", DefectMapPath: null,
            DarkLibraryExists: true, DefectMapExists: false, DarkLibraryCompatible: true, DefectMapCompatible: false,
            DarkLibraryLoaded: true, DefectMapLoaded: false, AutoLoadDarks: true, AutoLoadDefectMap: false,
            DarkCountLoaded: 8, DarkMinExposureSecondsLoaded: 1.0, DarkMaxExposureSecondsLoaded: 5.0);

        [Test]
        public async Task Toggle_returns_200_with_updated_status_on_success() {
            var svc = new Mock<IGuiderService>();
            svc.Setup(s => s.SetDarkLibraryEnabledAsync(true, It.IsAny<CancellationToken>())).ReturnsAsync(SampleStatus);

            var result = await EquipmentEndpoints.SetDarkLibraryEnabledAsync(new SetCalibrationEnabledRequestDto(true), svc.Object, CancellationToken.None);

            var ok = result as Ok<CalibrationFilesStatusDto>;
            Assert.That(ok, Is.Not.Null);
            Assert.That(ok!.Value, Is.SameAs(SampleStatus));
        }

        [Test]
        public async Task Toggle_maps_not_connected_InvalidOperation_to_409() {
            var svc = new Mock<IGuiderService>();
            svc.Setup(s => s.SetDefectMapEnabledAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("guider is not connected"));

            var result = await EquipmentEndpoints.SetDefectMapEnabledAsync(new SetCalibrationEnabledRequestDto(true), svc.Object, CancellationToken.None);

            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status409Conflict));
        }

        [Test]
        public async Task Toggle_maps_daemon_GuiderRpcException_to_422() {
            var svc = new Mock<IGuiderService>();
            svc.Setup(s => s.SetDarkLibraryEnabledAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.GuiderRpcException(
                    "set_dark_library_enabled", 1, "camera not connected"));

            var result = await EquipmentEndpoints.SetDarkLibraryEnabledAsync(new SetCalibrationEnabledRequestDto(true), svc.Object, CancellationToken.None);

            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status422UnprocessableEntity));
        }

        private static int? StatusOf(IResult result) => (result as ProblemHttpResult)?.StatusCode;
    }
}
