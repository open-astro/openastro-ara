#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §29.1.4 — the result file that openastroara-storage@.service leaves for
    /// the daemon: line 1 is configure-storage.sh's exit code, the rest its
    /// output. The daemon must read the helper's real outcome from it and never
    /// mistake a malformed file for success.
    /// </summary>
    [TestFixture]
    public class StorageHelperResultTest {

        [Test]
        public void SuccessCarriesMountPointLine() {
            var (code, output) = StorageDeviceService.ParseHelperResult("0\nOK /media/openastroara\n");
            Assert.That(code, Is.EqualTo(0));
            Assert.That(output.Trim(), Is.EqualTo("OK /media/openastroara"));
        }

        [Test]
        public void HelperErrorCodeAndTextSurvive() {
            var (code, output) = StorageDeviceService.ParseHelperResult("4\nERROR: label_mismatch ARA-DISK\n");
            Assert.That(code, Is.EqualTo(4));
            Assert.That(output.Trim(), Is.EqualTo("ERROR: label_mismatch ARA-DISK"));
        }

        [Test]
        public void MultiLineOutputIsKeptWhole() {
            var (code, output) = StorageDeviceService.ParseHelperResult("7\nmkfs: something\nERROR: mkfs_failed\n");
            Assert.That(code, Is.EqualTo(7));
            Assert.That(output, Is.EqualTo("mkfs: something\nERROR: mkfs_failed\n"));
        }

        [Test]
        public void MissingOutputIsEmptyNotNull() {
            var (code, output) = StorageDeviceService.ParseHelperResult("0");
            Assert.That(code, Is.EqualTo(0));
            Assert.That(output, Is.EqualTo(string.Empty));
        }

        [TestCase("")]
        [TestCase("OK /media/openastroara\n")]
        [TestCase("garbage\n0\n")]
        public void MalformedFirstLineIsNeverSuccess(string text) {
            var (code, output) = StorageDeviceService.ParseHelperResult(text);
            Assert.That(code, Is.EqualTo(-1));
            Assert.That(output, Is.EqualTo(text));
        }

        // The request file is one argument per line, so a confirm label with a
        // line break would change the helper's argv shape; ConfigureAsync
        // refuses it before anything is written.
        [TestCase(null, true)]
        [TestCase("", true)]
        [TestCase("ARA-DISK", true)]
        [TestCase("ARA DISK 2", true)]
        [TestCase("ARA-DISK\n", false)]
        [TestCase("ARA-DISK\r\n", false)]
        [TestCase("ARA\nDISK", false)]
        [TestCase("\n\n\n\n\n\n\n\n\n", false)]
        public void ConfirmLabelMustBeOneRequestLine(string? label, bool accepted) {
            Assert.That(StorageDeviceService.IsSingleLine(label), Is.EqualTo(accepted));
        }
    }
}
