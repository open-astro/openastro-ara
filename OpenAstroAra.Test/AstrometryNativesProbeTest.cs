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
using OpenAstroAra.Astrometry;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §14e — the boot-time natives probe never throws and agrees with whether the real
    /// transform works. CI stages the natives for this job, so both are true there; on a dev box
    /// without them both are false and the transform test self-ignores for the same reason.
    /// </summary>
    [TestFixture]
    public class AstrometryNativesProbeTest {

        [Test]
        public void Probe_never_throws_and_names_platform_files() {
            var (sofa, novas) = AstrometryNatives.Probe();
            var (sofaName, novasName) = AstrometryNatives.ExpectedFileNames;
            Assert.Multiple(() => {
                Assert.That(sofaName, Does.Contain("sofa").IgnoreCase);
                Assert.That(novasName, Does.Contain("novas").IgnoreCase);
                Assert.That(sofa, Is.TypeOf<bool>());
                Assert.That(novas, Is.TypeOf<bool>());
            });
        }

        [Test]
        public void Probe_agrees_with_a_real_transform() {
            var (sofa, _) = AstrometryNatives.Probe();
            var coords = new Coordinates(Angle.ByHours(1), Angle.ByDegree(10), Epoch.J2000);
            bool transformWorks;
            try {
                _ = coords.Transform(Epoch.JNOW);
                transformWorks = true;
            } catch (System.TypeInitializationException) {
                transformWorks = false;
            } catch (System.DllNotFoundException) {
                transformWorks = false;
            }
            Assert.That(sofa, Is.EqualTo(transformWorks));
        }
    }
}
