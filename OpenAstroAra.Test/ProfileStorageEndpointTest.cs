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
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// <c>PUT /profile/storage</c> write-boundary validation (§29 disk pair, §43-2b backup count,
    /// §42.5 fault-log retention #1145): a bad body is a 400 and never reaches the store; a valid one
    /// is stored and echoed back.
    /// </summary>
    [TestFixture]
    public class ProfileStorageEndpointTest {

        private static StorageSettingsDto Body(int warnGb = 10, int criticalGb = 2, int backupKeep = 20, int faultDays = 90) =>
            new("/media/openastroara", "fits", "zstd", "t", warnGb, criticalGb, backupKeep, faultDays);

        [TestCase(-1)]
        [TestCase(-365)]
        public void Negative_fault_log_retention_is_400_and_not_stored(int days) {
            var store = new Mock<IProfileStore>();

            var result = ProfileEndpoints.PutStorageSettings(Body(faultDays: days), store.Object);

            var problem = result as ProblemHttpResult;
            Assert.That(problem?.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            Assert.That(problem?.ProblemDetails.Detail, Does.Contain("fault_log_retention_days"));
            store.Verify(s => s.PutStorageSettings(It.IsAny<StorageSettingsDto>()), Times.Never);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(90)]
        [TestCase(3650)]
        public void Zero_or_positive_fault_log_retention_is_stored_and_echoed(int days) {
            var store = new Mock<IProfileStore>();
            var body = Body(faultDays: days);

            var result = ProfileEndpoints.PutStorageSettings(body, store.Object);

            Assert.That((result as Ok<StorageSettingsDto>)?.Value, Is.EqualTo(body));
            store.Verify(s => s.PutStorageSettings(body), Times.Once);
        }

        [Test]
        public void Negative_backup_retention_is_400_and_not_stored() {
            var store = new Mock<IProfileStore>();

            var result = ProfileEndpoints.PutStorageSettings(Body(backupKeep: -1), store.Object);

            Assert.That((result as ProblemHttpResult)?.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            store.Verify(s => s.PutStorageSettings(It.IsAny<StorageSettingsDto>()), Times.Never);
        }

        [TestCase(10, 0)]
        [TestCase(2, 10)]
        [TestCase(5, 5)]
        public void Invalid_disk_threshold_pair_is_400_and_not_stored(int warnGb, int criticalGb) {
            var store = new Mock<IProfileStore>();

            var result = ProfileEndpoints.PutStorageSettings(Body(warnGb: warnGb, criticalGb: criticalGb), store.Object);

            Assert.That((result as ProblemHttpResult)?.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            store.Verify(s => s.PutStorageSettings(It.IsAny<StorageSettingsDto>()), Times.Never);
        }

        // ADR 0015 — If-Match: match writes, mismatch is 412 and writes nothing, absent writes as before.
        private static (Mock<IProfileStore> store, StorageSettingsDto current) StoreHolding() {
            var current = Body(warnGb: 20);
            var store = new Mock<IProfileStore>();
            store.Setup(s => s.GetStorageSettings()).Returns(current);
            return (store, current);
        }

        [Test]
        public void Get_returns_an_etag_of_the_section() {
            var (store, current) = StoreHolding();
            var http = new DefaultHttpContext();

            ProfileEndpoints.GetStorageSettings(store.Object, http);

            Assert.That(http.Response.Headers.ETag.ToString(), Is.EqualTo(ProfileEndpoints.StorageETag(current)));
            Assert.That(ProfileEndpoints.StorageETag(current), Is.Not.EqualTo(ProfileEndpoints.StorageETag(Body())));
        }

        [Test]
        public void Put_with_matching_if_match_writes_and_returns_the_new_etag() {
            var (store, current) = StoreHolding();
            var http = new DefaultHttpContext();
            var body = Body(warnGb: 30);

            var result = ProfileEndpoints.PutStorageSettings(body, store.Object, ProfileEndpoints.StorageETag(current), http);

            Assert.That((result as Ok<StorageSettingsDto>)?.Value, Is.EqualTo(body));
            store.Verify(s => s.PutStorageSettings(body), Times.Once);
            Assert.That(http.Response.Headers.ETag.ToString(), Is.EqualTo(ProfileEndpoints.StorageETag(body)));
        }

        [Test]
        public void Put_with_stale_if_match_is_412_and_writes_nothing() {
            var (store, _) = StoreHolding();

            var result = ProfileEndpoints.PutStorageSettings(Body(warnGb: 30), store.Object, ProfileEndpoints.StorageETag(Body()), new DefaultHttpContext());

            Assert.That((result as ProblemHttpResult)?.StatusCode, Is.EqualTo(StatusCodes.Status412PreconditionFailed));
            store.Verify(s => s.PutStorageSettings(It.IsAny<StorageSettingsDto>()), Times.Never);
        }

        [Test]
        public void Put_without_if_match_is_accepted() {
            var (store, _) = StoreHolding();
            var body = Body(warnGb: 30);

            var result = ProfileEndpoints.PutStorageSettings(body, store.Object);

            Assert.That(result, Is.InstanceOf<Ok<StorageSettingsDto>>());
            store.Verify(s => s.PutStorageSettings(body), Times.Once);
        }
    }
}
