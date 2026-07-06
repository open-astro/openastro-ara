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
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§40/§50 — per-run capture sessions: the AsyncLocal scope semantics
    /// and the sessions-table rows behind them.</summary>
    [TestFixture]
    public class RunCaptureSessionTest {

        // ── CaptureSessionScope semantics ───────────────────────────────────

        [Test]
        public async Task Scope_flows_into_child_tasks_and_is_isolated_between_flows() {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();

            var runA = Task.Run(async () => {
                CaptureSessionScope.Enter(a);
                // A Task.Run hop captures the ExecutionContext — the value must
                // survive it (this is the TakeExposure → CameraService path).
                var seen = await Task.Run(() => CaptureSessionScope.Current);
                await Task.Delay(20);
                return (seen, CaptureSessionScope.Current);
            });
            var runB = Task.Run(async () => {
                CaptureSessionScope.Enter(b);
                var seen = await Task.Run(() => CaptureSessionScope.Current);
                await Task.Delay(20);
                return (seen, CaptureSessionScope.Current);
            });

            var (aChild, aAfter) = await runA;
            var (bChild, bAfter) = await runB;

            Assert.That(aChild, Is.EqualTo(a), "flows into child work");
            Assert.That(aAfter, Is.EqualTo(a), "stable across awaits");
            Assert.That(bChild, Is.EqualTo(b), "concurrent run sees only its own");
            Assert.That(bAfter, Is.EqualTo(b));
            Assert.That(CaptureSessionScope.Current, Is.Null,
                "a child flow's Enter never leaks up to the caller");
        }

        [Test]
        public async Task Exit_clears_only_the_current_flow() {
            var a = Guid.NewGuid();
            await Task.Run(() => {
                CaptureSessionScope.Enter(a);
                Assert.That(CaptureSessionScope.Current, Is.EqualTo(a));
                CaptureSessionScope.Exit();
                Assert.That(CaptureSessionScope.Current, Is.Null);
            });
        }

        // ── sessions-table rows ─────────────────────────────────────────────

        private string _dir = string.Empty;

        [SetUp]
        public void SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), $"oara-runsess-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Test]
        public async Task Run_session_rows_open_and_end_idempotently() {
            var db = new SqliteAraDatabase(_dir, logger: null);
            await db.InitializeAsync(CancellationToken.None);
            var repo = new SqliteFrameRepository(db, new InMemoryProfileStore());

            var sid = await repo.CreateRunSessionAsync(CancellationToken.None);
            var manual = await repo.EnsureManualCaptureSessionAsync(CancellationToken.None);
            Assert.That(sid, Is.Not.EqualTo(manual), "a run session is its own row, not the manual bucket");

            string? EndedAt() {
                using var conn = db.OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT ended_at FROM sessions WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", sid.ToString());
                return cmd.ExecuteScalar() as string;
            }

            Assert.That(EndedAt(), Is.Null, "open while the run executes");

            await repo.EndSessionAsync(sid, CancellationToken.None);
            var firstEnd = EndedAt();
            Assert.That(firstEnd, Is.Not.Null, "terminal path stamps the end");

            // Idempotent: a second end (retry/double teardown) can't move the time.
            await Task.Delay(20);
            await repo.EndSessionAsync(sid, CancellationToken.None);
            Assert.That(EndedAt(), Is.EqualTo(firstEnd));
        }
    }
}
