#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Model;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Solvers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1188 — <see cref="CLISolver"/> redirected the solver's stdout but never started reading it,
    /// so the progress/log handlers were dead and a solver writing more than the pipe buffer
    /// (64 KB) blocked on write until the solve timeout killed it. A <c>/bin/sh</c> script stands in
    /// for the solver.
    /// </summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    public class CLISolverOutputTest {

        private string dir = string.Empty;

        [SetUp]
        public void SetUp() {
            Assume.That(!OperatingSystem.IsWindows(), "the fake solver is a POSIX shell script");
            dir = Path.Combine(Path.GetTempPath(), "ara-clisolver-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }

        [TearDown]
        public void TearDown() {
            if (Directory.Exists(dir)) {
                Directory.Delete(dir, recursive: true);
            }
        }

        private string Script(string body) {
            var path = Path.Combine(dir, "fake-solver.sh");
            File.WriteAllText(path, body.Replace("\r\n", "\n", StringComparison.Ordinal));
            return path;
        }

        // ~200 KB on stdout and ~130 KB on stderr, each past the 64 KB pipe buffer, then an end marker.
        private const string ChattySolver = """
            awk 'BEGIN { for (i = 0; i < 2000; i++) printf "out %05d xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n", i }'
            echo STDOUT-END
            awk 'BEGIN { for (i = 0; i < 1300; i++) printf "err %05d yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy\n", i }' >&2
            echo STDERR-END >&2
            exit 0
            """;

        [Test]
        public async Task Solver_writing_past_the_pipe_buffer_completes_and_its_output_is_read() {
            var solver = new FakeSolver(Script(ChattySolver));
            var progress = new RecordingProgress();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var sw = Stopwatch.StartNew();

            // Before the fix this blocked on the full pipe until the token cancelled (OperationCanceledException).
            await solver.Run(progress, cts.Token);
            sw.Stop();

            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)), "solve should not wait for the timeout");
            // Checked straight after the await: the streams are drained before StartCLI returns.
            var statuses = progress.Statuses.ToList();
            Assert.That(statuses, Does.Contain("STDOUT-END"));
            Assert.That(statuses, Does.Contain("STDERR-END"));
            Assert.That(statuses.Count(s => s.StartsWith("out ", StringComparison.Ordinal)), Is.EqualTo(2000));
            Assert.That(statuses.Count(s => s.StartsWith("err ", StringComparison.Ordinal)), Is.EqualTo(1300));
            Assert.That(statuses, Has.None.Empty, "blank or end-of-stream lines must not clear the status");

            var lines = solver.Lines.ToList();
            Assert.That(lines.Count(l => !l.StdErr), Is.EqualTo(2001));
            Assert.That(lines.Count(l => l.StdErr), Is.EqualTo(1301));
            Assert.That(lines, Does.Contain(("STDERR-END", true)));
        }

        [Test]
        public async Task Cancellation_still_kills_a_hung_solver() {
            var solver = new FakeSolver(Script("echo started\nsleep 30\n"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var sw = Stopwatch.StartNew();

            await Assert.CatchAsync<OperationCanceledException>(() => solver.Run(null, cts.Token));

            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
        }

        // #1219 — a progress sink that throws must not take the process down (the reader callback
        // runs on a thread-pool thread, where an escaping exception is fatal).
        [Test]
        public async Task A_throwing_progress_sink_does_not_stop_the_solve() {
            var solver = new FakeSolver(Script("printf 'one\\ntwo\\nthree\\n'; exit 0"));
            var sink = new ThrowingProgress();

            await solver.Run(sink, CancellationToken.None);

            Assert.That(sink.Reports, Is.EqualTo(3), "every line still reached the sink");
            Assert.That(solver.Lines.Count, Is.EqualTo(3), "and the solver kept reading");
        }

        // #1219 — the non-zero-exit warning carries the last 20 lines, stderr tagged.
        [Test]
        public async Task A_non_zero_exit_keeps_the_last_twenty_lines_with_stderr_tagged() {
            var solver = new FakeSolver(Script("for i in $(seq 1 30); do echo \"line $i\"; done; echo oops >&2; exit 3"));

            await solver.Run(new RecordingProgress(), CancellationToken.None);

            var tail = solver.Tail!;
            Assert.That(tail, Has.Count.EqualTo(20));
            // stdout and stderr arrive on two reader threads, so the stderr line's position in the
            // tail is not fixed; its presence and tag are.
            Assert.That(tail, Does.Contain("[stderr] oops"));
            Assert.That(tail, Does.Contain("line 30").And.Not.Contain("line 10"), "only the last twenty");
        }

        [Test]
        public async Task A_clean_exit_records_no_tail() {
            var solver = new FakeSolver(Script("echo fine; exit 0"));
            await solver.Run(new RecordingProgress(), CancellationToken.None);
            Assert.That(solver.Tail, Is.Null);
        }

        // #1219 — the solver exits but a grandchild still holds the pipe: cancellation must still
        // return promptly (the tree kill is unconditional and the readers are cancelled).
        [Test]
        public async Task Cancellation_returns_promptly_when_a_grandchild_holds_the_pipe() {
            var solver = new FakeSolver(Script("(sleep 30) & echo started; exit 0"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var sw = System.Diagnostics.Stopwatch.StartNew();

            await Assert.ThatAsync(() => solver.Run(new RecordingProgress(), cts.Token), Throws.InstanceOf<OperationCanceledException>());

            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(15)), "must not wait out the grandchild's sleep");
        }

        private sealed class ThrowingProgress : IProgress<ApplicationStatus> {
            public int Reports;
            public void Report(ApplicationStatus value) {
                Interlocked.Increment(ref Reports);
                throw new InvalidOperationException("sink is broken");
            }
        }

        private sealed class RecordingProgress : IProgress<ApplicationStatus> {
            public ConcurrentQueue<string> Statuses { get; } = new();
            public void Report(ApplicationStatus value) => Statuses.Enqueue(value.Status);
        }

        private sealed class FakeSolver : CLISolver {
            private readonly string script;

            public FakeSolver(string script) : base("/bin/sh") {
                this.script = script;
            }

            public ConcurrentQueue<(string Line, bool StdErr)> Lines { get; } = new();

            public IReadOnlyList<string>? Tail => LastExitTail;

            public Task Run(IProgress<ApplicationStatus>? progress, CancellationToken ct) =>
                StartCLI("unused.fits", "unused.ini", new PlateSolveParameter(), null!, progress, ct);

            protected override void OnSolverOutput(string line, bool stdErr, IProgress<ApplicationStatus>? progress) {
                Lines.Enqueue((line, stdErr));
                base.OnSolverOutput(line, stdErr, progress);
            }

            protected override string GetArguments(string imageFilePath, string outputFilePath, PlateSolveParameter parameter,
                    PlateSolveImageProperties imageProperties) => $"\"{script}\"";

            protected override string GetLocalizedPlateSolverName() => "fake";

            protected override string GetOutputPath(string imageFilePath) => imageFilePath + ".ini";

            protected override PlateSolveResult ReadResult(string outputFilePath, PlateSolveParameter parameter,
                    PlateSolveImageProperties imageProperties) => new PlateSolveResult { Success = false };
        }
    }
}
