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
        public void A_writable_shared_memory_root_is_used() {
            var root = Path.Combine(Path.GetTempPath(), "ara-shm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                BaseSolver.SharedMemoryRoot = root;
                var dir = BaseSolver.ResolveWorkingDirectory();
                Assert.That(dir, Is.EqualTo(Path.Combine(root, "openastroara", "PlateSolver")));
                Assert.That(Directory.Exists(dir), Is.True);
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
