using NUnit.Framework;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Solvers;
using System.Text.RegularExpressions;

namespace OpenAstroAra.Test {

    /// <summary>#1136 — the ASTAP argument builder's per-solve tuning: downsample from the pixel
    /// scale when the profile says 0, and <c>-fov 0</c> for the scale-search stage.</summary>
    [TestFixture]
    public class ASTAPSolverArgumentsTest {

        private static string Args(PlateSolveParameter parameter, int height = 3000) {
            var image = new OpenAstroAra.Image.ImageData.BaseImageData(new ushort[8 * 8], 8, 8, bitDepth: 16, isBayered: false,
                new OpenAstroAra.Image.ImageData.ImageMetaData(), new OpenAstroAra.Server.Services.HeadlessProfileService(), null!, null!);
            // Height drives the FOV and the downsample floor; the pixel buffer is a stub.
            var props = PlateSolveImageProperties.Create(parameter, image);
            typeof(PlateSolveImageProperties).GetProperty("ImageHeight")!.SetValue(props, (double)height);
            typeof(PlateSolveImageProperties).GetProperty("ImageWidth")!.SetValue(props, (double)height * 3 / 2);
            return new ASTAPSolver("/usr/bin/astap_cli").ArgumentsFor("/tmp/x.fits", "/tmp/x.ini", parameter, props);
        }

        private static string Flag(string args, string flag) => Regex.Match(args, $@"{flag} (\S+)").Groups[1].Value;

        [Test]
        public void Fixed_downsample_is_passed_through() {
            var args = Args(new PlateSolveParameter { FocalLength = 530, PixelSize = 3.76, DownSampleFactor = 3, MaxObjects = 500 });
            Assert.That(Flag(args, "-z"), Is.EqualTo("3"));
        }

        [Test]
        public void Zero_downsample_is_derived_from_the_pixel_scale() {
            // 530 mm / 3.76 µm = 1.46"/px → 1; 2000 mm / 2.4 µm = 0.25"/px → 4.
            Assert.That(Flag(Args(new PlateSolveParameter { FocalLength = 530, PixelSize = 3.76, DownSampleFactor = 0, MaxObjects = 500 }), "-z"), Is.EqualTo("1"));
            Assert.That(Flag(Args(new PlateSolveParameter { FocalLength = 2000, PixelSize = 2.4, DownSampleFactor = 0, MaxObjects = 500 }), "-z"), Is.EqualTo("4"));
        }

        [Test]
        public void Scale_search_passes_fov_zero_and_a_normal_solve_the_field_height() {
            var p = new PlateSolveParameter { FocalLength = 530, PixelSize = 3.76, DownSampleFactor = 2, MaxObjects = 500 };
            Assert.That(Flag(Args(p), "-fov"), Is.Not.EqualTo("0"));
            p.SearchScale = true;
            Assert.That(Flag(Args(p), "-fov"), Is.EqualTo("0"));
        }
    }
}
