#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using NUnit.Framework;
using OpenAstroAra.Fits;
using OpenAstroAra.Server;
using System;
using System.Collections.Generic;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1120: the daemon's boot-time CFITSIO probe logs what loaded, or an install hint when nothing
    /// did, and never throws (log-and-continue, like the astrometry natives probe).
    /// </summary>
    [TestFixture]
    public class CfitsioBootProbeTest {

        private sealed class RecordingLogger : ILogger {
            public List<(LogLevel Level, string Message)> Entries { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
        }

        [Test]
        public void Loaded_library_logs_its_source_at_Information() {
            var log = new RecordingLogger();
            var result = new FitsLibraryProbeResult(true,
                new CFitsIOResolution(null, ["cfitsio (runtime default probing)", "libcfitsio.so.10"], "libcfitsio.so.10"), null);

            Program.LogCfitsioProbe(log, result, "sudo apt install libcfitsio10");

            Assert.That(log.Entries, Has.Count.EqualTo(1));
            Assert.That(log.Entries[0].Level, Is.EqualTo(LogLevel.Information));
            Assert.That(log.Entries[0].Message, Does.Contain("libcfitsio.so.10"));
        }

        [Test]
        public void Explicit_path_that_did_not_load_is_a_Warning_even_when_a_fallback_did() {
            var log = new RecordingLogger();
            var result = new FitsLibraryProbeResult(true,
                new CFitsIOResolution("/nope/libcfitsio.so", ["/nope/libcfitsio.so", "cfitsio (runtime default probing)"],
                    "cfitsio (runtime default probing)"), null);

            Program.LogCfitsioProbe(log, result, "sudo apt install libcfitsio10");

            Assert.That(log.Entries, Has.Count.EqualTo(1));
            Assert.That(log.Entries[0].Level, Is.EqualTo(LogLevel.Warning));
            Assert.That(log.Entries[0].Message, Does.Contain("OPENASTROARA_CFITSIO_PATH").And.Contain("/nope/libcfitsio.so"));
        }

        [Test]
        public void Missing_library_logs_an_Error_with_the_install_hint_and_every_candidate() {
            var log = new RecordingLogger();
            var result = new FitsLibraryProbeResult(false,
                new CFitsIOResolution(null, ["cfitsio (runtime default probing)", "libcfitsio.so.10"], null),
                "Unable to load shared library 'cfitsio'");

            Program.LogCfitsioProbe(log, result, "sudo apt install libcfitsio10");

            Assert.That(log.Entries, Has.Count.EqualTo(1));
            Assert.That(log.Entries[0].Level, Is.EqualTo(LogLevel.Error));
            Assert.That(log.Entries[0].Message, Does.Contain("sudo apt install libcfitsio10")
                .And.Contain("libcfitsio.so.10")
                .And.Contain("cfitsio (runtime default probing)")
                .And.Contain("Unable to load shared library"));
        }

        [Test]
        public void Linux_install_hint_names_the_runtime_package() {
            if (!OperatingSystem.IsLinux()) Assert.Ignore("Linux-only hint");
            Assert.That(Program.CfitsioInstallHint(), Does.Contain("apt install libcfitsio10"));
        }
    }
}
