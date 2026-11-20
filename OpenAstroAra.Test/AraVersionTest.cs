#region "copyright"
/*
    Copyright © 2026 - present Open Astro contributors

    This file is part of the open-source OpenAstro Ara project.

    This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
    If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/
#endregion "copyright"
using NUnit.Framework;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Image.FileFormat.FITS;
using OpenAstroAra.Image.ImageData;
using OpenAstroAra.Server;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace OpenAstroAra.Test {

    /// <summary>#2093: the daemon's version comes from the root VERSION file.</summary>
    [TestFixture]
    public class AraVersionTest {

        private static string ReadVersionFile() {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir is not null) {
                var candidate = Path.Combine(dir.FullName, "VERSION");
                if (File.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) {
                    return File.ReadAllText(candidate);
                }
                dir = dir.Parent;
            }
            Assert.Fail("No VERSION file above " + TestContext.CurrentContext.TestDirectory);
            return string.Empty;
        }

        [Test]
        public void ServerVersionIsTheVersionFileInDisplaySpelling() {
            var expected = AraVersion.ToDisplay(ReadVersionFile());
            Assert.That(ServerIdentity.Version, Is.EqualTo(expected));
        }

        [Test]
        public void AssemblyVersionsComeFromTheVersionFile() {
            var display = AraVersion.ToDisplay(ReadVersionFile());
            var dash = display.IndexOf('-', StringComparison.Ordinal);
            var numeric = Version.Parse(dash >= 0 ? display[..dash] : display);
            var expected = new Version(numeric.Major, numeric.Minor, numeric.Build, 0);
            // The daemon and a NINA-forked library (CommonAssemblyInfo.cs, no longer 3.2.0.9001).
            foreach (var asm in new[] { typeof(ServerIdentity).Assembly, typeof(CoreUtil).Assembly }) {
                Assert.That(asm.GetName().Version, Is.EqualTo(expected), asm.GetName().Name);
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                Assert.That(AraVersion.FromInformationalVersion(info, null), Is.EqualTo(display), asm.GetName().Name);
            }
        }

        [Test]
        public void CoreUtilVersionIsTheDisplayVersion() {
            // Logs, the HTTP user agent and FITS SWCREATE read CoreUtil.Version: the display
            // spelling the daemon reports, not NINA's four-part FileVersion (0.0.1.0).
            var expected = AraVersion.ToDisplay(ReadVersionFile());
            Assert.That(CoreUtil.Version, Is.EqualTo(expected));
            Assert.That(CoreUtil.UserAgent, Does.StartWith("OpenAstroAra/" + expected + " ("));
        }

        [Test]
        public void FitsSwcreateNamesAraAndTheDisplayVersion() {
            // Saved frames say who made them in Ara's name and display version, as the XISF
            // writer does; it said "N.I.N.A. 0.0.1.0 (x64)", x64 even on a Pi.
            var fits = new FITS(new ushort[] { 1, 2 }, 1, 1);
            fits.PopulateHeaderCards(new ImageMetaData());
            var swcreate = fits.Header.HeaderCards.First(x => x.Key == "SWCREATE");
            Assert.That(swcreate.Value.Trim(), Is.EqualTo("'OpenAstro Ara " + AraVersion.ToDisplay(ReadVersionFile()) + "'"));
        }

        [TestCase("0.0.1", "0.0.1")]
        [TestCase("1.0.0\n", "1.0.0")]
        [TestCase("1.0.0~beta1", "1.0.0-beta.1")]
        [TestCase("10.20.30~beta12\r\n", "10.20.30-beta.12")]
        public void VersionFileMapsToDisplaySpelling(string file, string display) {
            Assert.That(AraVersion.ToDisplay(file), Is.EqualTo(display));
        }

        [TestCase("")]
        [TestCase("1.0")]
        [TestCase("1.0.0.0")]
        [TestCase("01.0.0")]
        [TestCase("1.0.0~beta0")]
        [TestCase("1.0.0~beta")]
        [TestCase("1.0.0~rc1")]
        [TestCase("1.0.0-beta.1")]
        [TestCase("v1.0.0")]
        [TestCase("0.0.1-ara.1")]
        [TestCase("1.0.0\n2.0.0")]
        public void MalformedVersionFileIsRejected(string file) {
            Assert.Throws<FormatException>(() => AraVersion.ToDisplay(file));
        }

        [TestCase("1.0.0-beta.1+0123abc", "1.0.0-beta.1")]
        [TestCase("1.2.3", "1.2.3")]
        public void InformationalVersionDropsBuildMetadata(string info, string display) {
            Assert.That(AraVersion.FromInformationalVersion(info, null), Is.EqualTo(display));
        }

        [Test]
        public void MissingInformationalVersionFallsBackToAssemblyVersion() {
            Assert.That(AraVersion.FromInformationalVersion(null, new Version(2, 3, 4, 0)), Is.EqualTo("2.3.4"));
        }
    }
}
