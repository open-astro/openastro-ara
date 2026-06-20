#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §29.9 real <see cref="LogService"/> over the rolling CLEF (Compact-JSON)
    /// log files written by the Serilog file sink. Covers tail parse/filter/order,
    /// download streaming + path-traversal rejection, the newest-file selection,
    /// and the rotate acknowledgement.
    /// </summary>
    [TestFixture]
    public class LogServiceTest {

        private static readonly string[] WarningThenError = { "an error", "a warning" };
        private static readonly string[] CameraMatches = { "CAMERA exposing", "Camera connected" };
        private static readonly string[] NewestThree = { "line9", "line8", "line7" };
        private static readonly string[] TraversalNames = {
            "../secret.log",
            "sub/openastroara-20260619.log",
            "/etc/passwd",
            "openastroara-20260619.txt", // wrong extension
        };

        private string _logsDir = null!;
        private LogService _svc = null!;

        [SetUp]
        public void SetUp() {
            _logsDir = Path.Combine(Path.GetTempPath(), "ara-logs-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_logsDir);
            _svc = new LogService(_logsDir, NullLogger<LogService>.Instance);
        }

        [TearDown]
        public void TearDown() {
            if (Directory.Exists(_logsDir)) {
                Directory.Delete(_logsDir, recursive: true);
            }
        }

        // Build a CLEF line as RenderedCompactJsonFormatter writes it: @t timestamp,
        // @m rendered message, @l level (omitted for Information), optional SourceContext.
        private static string Clef(string timestamp, string message, string? level = null,
            string? source = null, string? exception = null) {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"@t\":\"").Append(timestamp).Append("\",\"@m\":\"").Append(message).Append('"');
            if (level is not null) {
                sb.Append(",\"@l\":\"").Append(level).Append('"');
            }
            if (exception is not null) {
                sb.Append(",\"@x\":\"").Append(exception).Append('"');
            }
            if (source is not null) {
                sb.Append(",\"SourceContext\":\"").Append(source).Append('"');
            }
            sb.Append('}');
            return sb.ToString();
        }

        private string WriteLog(string name, params string[] lines) {
            var path = Path.Combine(_logsDir, name);
            File.WriteAllLines(path, lines);
            return path;
        }

        [Test]
        public async Task TailAsync_parses_orders_newest_first_and_surfaces_level() {
            WriteLog("openastroara-20260619.log",
                Clef("2026-06-19T10:00:00.0000000Z", "first", source: "Foo"),
                Clef("2026-06-19T10:00:01.0000000Z", "second warning", "Warning", "Bar"),
                Clef("2026-06-19T10:00:02.0000000Z", "third"));

            var entries = await _svc.TailAsync(new LogTailRequestDto(null, null, null), CancellationToken.None);

            Assert.That(entries.Count, Is.EqualTo(3));
            // Newest-first.
            Assert.That(entries[0].Message, Is.EqualTo("third"));
            Assert.That(entries[2].Message, Is.EqualTo("first"));
            // Information default when @l is absent; @l honoured when present.
            Assert.That(entries[2].Level, Is.EqualTo("Information"));
            Assert.That(entries[1].Level, Is.EqualTo("Warning"));
            Assert.That(entries[2].Source, Is.EqualTo("Foo"));
        }

        [Test]
        public async Task TailAsync_filters_by_min_level() {
            WriteLog("openastroara-20260619.log",
                Clef("2026-06-19T10:00:00.0000000Z", "debug noise", "Debug"),
                Clef("2026-06-19T10:00:01.0000000Z", "info hum"),
                Clef("2026-06-19T10:00:02.0000000Z", "a warning", "Warning"),
                Clef("2026-06-19T10:00:03.0000000Z", "an error", "Error"));

            var entries = await _svc.TailAsync(new LogTailRequestDto(null, "Warning", null), CancellationToken.None);

            Assert.That(entries.Select(e => e.Message), Is.EqualTo(WarningThenError));
        }

        [Test]
        public async Task TailAsync_filters_by_substring_case_insensitively() {
            WriteLog("openastroara-20260619.log",
                Clef("2026-06-19T10:00:00.0000000Z", "Camera connected"),
                Clef("2026-06-19T10:00:01.0000000Z", "mount slewing"),
                Clef("2026-06-19T10:00:02.0000000Z", "CAMERA exposing"));

            var entries = await _svc.TailAsync(new LogTailRequestDto(null, null, "camera"), CancellationToken.None);

            Assert.That(entries.Select(e => e.Message), Is.EqualTo(CameraMatches));
        }

        [Test]
        public async Task TailAsync_caps_to_max_lines_keeping_newest() {
            var lines = Enumerable.Range(0, 10)
                .Select(i => Clef($"2026-06-19T10:00:{i:00}.0000000Z", $"line{i}"))
                .ToArray();
            WriteLog("openastroara-20260619.log", lines);

            var entries = await _svc.TailAsync(new LogTailRequestDto(3, null, null), CancellationToken.None);

            Assert.That(entries.Select(e => e.Message), Is.EqualTo(NewestThree));
        }

        [Test]
        public async Task TailAsync_skips_torn_and_non_clef_lines() {
            WriteLog("openastroara-20260619.log",
                Clef("2026-06-19T10:00:00.0000000Z", "valid"),
                "{ this is not json",
                "",
                "plain text line");

            var entries = await _svc.TailAsync(new LogTailRequestDto(null, null, null), CancellationToken.None);

            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[0].Message, Is.EqualTo("valid"));
        }

        [Test]
        public async Task TailAsync_surfaces_exception_inline() {
            WriteLog("openastroara-20260619.log",
                Clef("2026-06-19T10:00:00.0000000Z", "boom", "Error", exception: "System.Exception: kaboom"));

            var entries = await _svc.TailAsync(new LogTailRequestDto(null, null, null), CancellationToken.None);

            Assert.That(entries[0].Message, Does.Contain("boom"));
            Assert.That(entries[0].Message, Does.Contain("kaboom"));
        }

        [Test]
        public async Task TailAsync_reads_the_newest_file_only() {
            var older = WriteLog("openastroara-20260618.log",
                Clef("2026-06-18T10:00:00.0000000Z", "yesterday"));
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-1));
            WriteLog("openastroara-20260619.log",
                Clef("2026-06-19T10:00:00.0000000Z", "today"));

            var entries = await _svc.TailAsync(new LogTailRequestDto(null, null, null), CancellationToken.None);

            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[0].Message, Is.EqualTo("today"));
        }

        [Test]
        public async Task TailAsync_returns_empty_when_no_log_files() {
            var entries = await _svc.TailAsync(new LogTailRequestDto(null, null, null), CancellationToken.None);
            Assert.That(entries, Is.Empty);
        }

        [Test]
        public async Task OpenDownloadAsync_null_name_streams_newest_file() {
            WriteLog("openastroara-20260619.log",
                Clef("2026-06-19T10:00:00.0000000Z", "hello"));

            var result = await _svc.OpenDownloadAsync(null, CancellationToken.None);

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Value.FileName, Is.EqualTo("openastroara-20260619.log"));
            using var reader = new StreamReader(result.Value.Stream);
            var text = await reader.ReadToEndAsync();
            Assert.That(text, Does.Contain("hello"));
        }

        [Test]
        public async Task OpenDownloadAsync_resolves_bare_name() {
            WriteLog("openastroara-20260619.log", Clef("2026-06-19T10:00:00.0000000Z", "x"));

            var result = await _svc.OpenDownloadAsync("openastroara-20260619.log", CancellationToken.None);

            Assert.That(result, Is.Not.Null);
            await result!.Value.Stream.DisposeAsync();
        }

        [Test]
        public async Task OpenDownloadAsync_rejects_path_traversal() {
            WriteLog("openastroara-20260619.log", Clef("2026-06-19T10:00:00.0000000Z", "x"));

            foreach (var bad in TraversalNames) {
                var result = await _svc.OpenDownloadAsync(bad, CancellationToken.None);
                Assert.That(result, Is.Null, $"expected reject for '{bad}'");
            }
        }

        [Test]
        public async Task OpenDownloadAsync_returns_null_for_missing_file() {
            var result = await _svc.OpenDownloadAsync("openastroara-29991231.log", CancellationToken.None);
            Assert.That(result, Is.Null);
        }

        [Test]
        public async Task RotateAsync_acknowledges() {
            var dto = await _svc.RotateAsync("idem-1", CancellationToken.None);

            Assert.That(dto.OperationType, Is.EqualTo("log.rotate"));
            Assert.That(dto.IdempotencyKey, Is.EqualTo("idem-1"));
            Assert.That(dto.OperationId, Is.Not.EqualTo(Guid.Empty));
        }
    }
}
