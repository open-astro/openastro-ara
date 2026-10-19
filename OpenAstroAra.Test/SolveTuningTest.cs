using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.PlateSolving;
using System;

namespace OpenAstroAra.Test {

    /// <summary>#1136 — the per-solve tuning rules derived from the rig geometry.</summary>
    [TestFixture]
    public class SolveTuningTest {

        [TestCase(3.1, 3000, 1, TestName = "RedCat at 3.1\"/px solves at full resolution")]
        [TestCase(1.73, 4176, 2, TestName = "RedCat on a 26 MP sensor (CM4 rig) bins 2x for the tall frame")]
        [TestCase(3.1, 4176, 2, TestName = "a tall frame bins at least 2x whatever its scale")]
        [TestCase(1.0, 3000, 2, TestName = "1\"/px downsamples 2x")]
        [TestCase(0.78, 3000, 2, TestName = "1000 mm refractor at 0.78\"/px downsamples 2x")]
        [TestCase(0.5, 3000, 4, TestName = "0.5\"/px downsamples 4x")]
        [TestCase(0.3, 6000, 4, TestName = "C11 at 0.3\"/px is capped at 4x")]
        [TestCase(0.3, 1500, 2, TestName = "a short frame keeps at least 600 px of height")]
        [TestCase(0.3, 800, 1, TestName = "a tiny frame is never downsampled")]
        [TestCase(0, 3000, 1, TestName = "no scale means no downsample")]
        [TestCase(0, 4000, 2, TestName = "no scale on a tall frame still bins 2x")]
        [TestCase(double.NaN, 3000, 1, TestName = "NaN scale means no downsample")]
        public void Downsample_follows_the_pixel_scale(double arcsecPerPixel, double heightPx, int expected) {
            Assert.That(SolveTuning.DownsampleFor(arcsecPerPixel, heightPx), Is.EqualTo(expected));
        }

        [TestCase(10, 1)]
        [TestCase(30, 1)]
        [TestCase(31, 2)]
        [TestCase(90, 2)]
        [TestCase(180, 5)]
        public void Timeout_scales_with_the_search_radius(double radiusDeg, int factor) {
            Assert.That(SolveTuning.TimeoutFor(TimeSpan.FromSeconds(60), radiusDeg), Is.EqualTo(TimeSpan.FromSeconds(60 * factor)));
        }

        [Test]
        public void Focal_length_from_scale_inverts_arcsec_per_pixel() {
            var scale = AstroUtil.ArcsecPerPixel(3.76, 530);
            Assert.That(SolveTuning.FocalLengthFromScale(3.76, scale), Is.EqualTo(530).Within(0.01));
            Assert.That(SolveTuning.FocalLengthFromScale(0, scale), Is.NaN);
            Assert.That(SolveTuning.FocalLengthFromScale(3.76, 0), Is.NaN);
        }

        [Test]
        public void Scale_mismatch_is_more_than_ten_percent() {
            Assert.Multiple(() => {
                Assert.That(SolveTuning.ScaleMismatch(1.0, 1.05), Is.False);
                Assert.That(SolveTuning.ScaleMismatch(1.0, 1.11), Is.True);
                Assert.That(SolveTuning.ScaleMismatch(1.0, 0.8), Is.True, "a 0.8x reducer the profile doesn't know about");
                Assert.That(SolveTuning.ScaleMismatch(1.0, double.NaN), Is.False, "an unsolved scale is not a mismatch");
                Assert.That(SolveTuning.ScaleMismatch(0, 1.0), Is.False);
            });
        }

        [Test]
        public void Separation_is_the_angular_distance() {
            var a = new Coordinates(Angle.ByDegree(10), Angle.ByDegree(0), Epoch.J2000);
            var b = new Coordinates(Angle.ByDegree(40), Angle.ByDegree(0), Epoch.J2000);
            Assert.That(SolveTuning.SeparationDeg(a, b), Is.EqualTo(30).Within(1e-6));
            Assert.That(SolveTuning.SeparationDeg(a, null), Is.NaN);
        }
    }
}
