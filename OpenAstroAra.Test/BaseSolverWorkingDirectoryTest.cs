using NUnit.Framework;
using OpenAstroAra.PlateSolving.Solvers;
using System;
using System.IO;

namespace OpenAstroAra.Test {

    /// <summary>#1136 — the solver's working directory prefers a RAM-backed root when one is writable.</summary>
    [TestFixture]
    [Category("IO")]
    public class BaseSolverWorkingDirectoryTest {
        private string? saved;

        [SetUp]
        public void Save() => saved = BaseSolver.SharedMemoryRoot;

        [TearDown]
        public void Restore() => BaseSolver.SharedMemoryRoot = saved;

        [Test]
        public void The_static_working_directory_was_resolved_with_the_default_root() {
            // CM4 rig 2026-10-19: with SharedMemoryRoot declared after WORKING_DIRECTORY the static
            // initializer ran with a null root and nothing was ever written to /dev/shm. On Linux both
            // sides land under /dev/shm; on macOS/Windows both are the disk directory.
            Assert.That(BaseSolver.WorkingDirectory, Is.EqualTo(BaseSolver.ResolveWorkingDirectory()));
            if (OperatingSystem.IsLinux() && Directory.Exists("/dev/shm")) {
                Assert.That(BaseSolver.WorkingDirectory, Does.StartWith("/dev/shm/"));
            }
        }

        [Test]
        public void A_writable_shared_memory_root_is_used() {
            var root = Path.Combine(Path.GetTempPath(), "ara-shm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                BaseSolver.SharedMemoryRoot = root;
                var dir = BaseSolver.ResolveWorkingDirectory();
                Assert.That(dir, Is.EqualTo(Path.Combine(root, "openastroara", "PlateSolver")));
                Assert.That(Directory.Exists(dir), Is.True);
                if (!OperatingSystem.IsWindows()) {
                    // Bot round 1 — /dev/shm is world-readable by default; the frames stay the service's.
                    Assert.That(File.GetUnixFileMode(dir) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead), Is.EqualTo(UnixFileMode.None));
                }
            } finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void A_missing_or_disabled_root_falls_back_to_the_disk_directory() {
            BaseSolver.SharedMemoryRoot = Path.Combine(Path.GetTempPath(), "ara-shm-missing-" + Guid.NewGuid().ToString("N"));
            Assert.That(BaseSolver.ResolveWorkingDirectory(), Does.EndWith("PlateSolver").And.Not.Contain("openastroara"));
            BaseSolver.SharedMemoryRoot = null;
            Assert.That(BaseSolver.ResolveWorkingDirectory(), Does.Not.Contain("openastroara"));
        }
    }
}
