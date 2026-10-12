#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Data.Sqlite;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1194 — the three API hardening gaps: stale-session REST guard, WebSocket connection cap,
    /// frame-file confinement to the capture roots.</summary>
    [TestFixture]
    [NonParallelizable] // WsConnectionGate is process-wide: no other fixture may open or close sockets meanwhile
    public class ApiHardeningTest {

        // ── §27 stale-session guard ──

        private static readonly Guid Holder = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid Other = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

        private static bool Reject(string method, string path, string header, bool stale = true) =>
            SessionGuard.ShouldReject(method, path, header, _ => stale);

        [Test]
        public void A_mutating_request_with_a_stale_session_is_refused() {
            Assert.That(Reject("POST", "/api/v1/equipment/telescope/park", Other.ToString()), Is.True);
            Assert.That(Reject("PUT", "/api/v1/profile/storage", Other.ToString()), Is.True);
            Assert.That(Reject("DELETE", "/api/v1/frames/123", Other.ToString()), Is.True);
            Assert.That(Reject("PATCH", "/api/v1/sequences/1", Other.ToString()), Is.True);
        }

        [Test]
        public void Reads_are_never_refused() {
            Assert.That(Reject("GET", "/api/v1/equipment/telescope", Other.ToString()), Is.False);
            Assert.That(Reject("HEAD", "/api/v1/frames/1/download", Other.ToString()), Is.False);
            Assert.That(Reject("OPTIONS", "/api/v1/frames", Other.ToString()), Is.False);
        }

        [Test]
        public void Header_less_and_garbage_requests_stay_on_the_advisory_path() {
            Assert.That(Reject("POST", "/api/v1/equipment/telescope/park", ""), Is.False, "pre-§27 client: advisory");
            Assert.That(Reject("POST", "/api/v1/equipment/telescope/park", "not-a-guid"), Is.False, "never issued by the daemon → nothing to be stale");
        }

        [Test]
        public void Connect_and_disconnect_are_exempt_and_so_is_everything_outside_the_api() {
            Assert.That(Reject("POST", "/api/v1/server/connect", Other.ToString()), Is.False, "that is how a session becomes current");
            Assert.That(Reject("POST", "/api/v1/server/disconnect", Other.ToString()), Is.False);
            Assert.That(Reject("POST", "/api/v1/server/emergency-stop", Other.ToString()), Is.False, "§57: a panic stop never waits on a session");
            Assert.That(Reject("POST", "/healthz", Other.ToString()), Is.False);
            Assert.That(Reject("POST", "/api/v2/anything", Other.ToString()), Is.False);
        }

        [Test]
        public void A_current_session_is_not_refused() {
            Assert.That(Reject("POST", "/api/v1/equipment/telescope/park", Holder.ToString(), stale: false), Is.False);
        }

        [Test]
        public async Task The_session_service_calls_only_a_non_holder_stale() {
            var svc = new ClientSessionService();
            Assert.That(svc.IsStaleSession(Other), Is.False, "no holder: the slot is free, nothing is stale");
            var granted = await svc.ConnectAsync("mac.local", null, CancellationToken.None);
            Assert.That(granted.Kind, Is.EqualTo(ConnectOutcomeKind.Granted));
            Assert.That(svc.IsStaleSession(granted.SessionId), Is.False);
            Assert.That(svc.IsStaleSession(Other), Is.True);
        }

        // ── WebSocket cap ──

        [Test]
        [NonParallelizable]
        public void The_gate_admits_the_cap_and_refuses_the_next() {
            WsConnectionGate.ResetForTests();
            try {
                for (var i = 0; i < WsConnectionGate.MaxConcurrentConnections; i++) {
                    Assert.That(WsConnectionGate.TryEnter(), Is.True, $"slot {i}");
                }
                Assert.That(WsConnectionGate.TryEnter(), Is.False, "one past the cap");
                Assert.That(WsConnectionGate.Open, Is.EqualTo(WsConnectionGate.MaxConcurrentConnections), "a refused attempt claims nothing");
                WsConnectionGate.Exit();
                Assert.That(WsConnectionGate.TryEnter(), Is.True, "a released slot is reusable");
            } finally {
                WsConnectionGate.ResetForTests();
            }
        }

        [Test]
        [NonParallelizable]
        public void Exit_never_goes_below_zero() {
            WsConnectionGate.ResetForTests();
            WsConnectionGate.Exit();
            Assert.That(WsConnectionGate.Open, Is.EqualTo(0));
        }

    }

    // ── frame-file confinement ──

    /// <summary>#1194 — frame files are served and deleted only from under the capture roots.</summary>
[TestFixture]
[Category("IO")] // #1265 — real disk
public class FrameFileConfinementTest {
        private string _root = string.Empty;
        private string _outside = string.Empty;
        private SqliteAraDatabase _db = null!;
        private SqliteFrameRepository _repo = null!;
        private static readonly Guid Session = Guid.Parse("66666666-6666-6666-6666-666666666661");

        [SetUp]
        public async Task SetUp() {
            var dir = Path.Combine(Path.GetTempPath(), $"oara-confine-{Guid.NewGuid():N}");
            _root = Path.Combine(dir, "captures");
            _outside = Path.Combine(dir, "elsewhere");
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_outside);
            _db = new SqliteAraDatabase(Path.Combine(dir, "db"), logger: null);
            await _db.InitializeAsync(CancellationToken.None);
            _repo = new SqliteFrameRepository(_db, new InMemoryProfileStore(), captureRoots: () => new[] { _root });
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO sessions (id, started_at) VALUES ($id, $t);";
            cmd.Parameters.AddWithValue("$id", Session.ToString());
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        [TearDown]
        public void TearDown() {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private async Task<Guid> InsertAsync(string fitsPath) {
            var id = Guid.NewGuid();
            await File.WriteAllTextAsync(fitsPath, "FITS");
            await _repo.InsertAsync(new FrameDto(
                Id: id, SessionId: Session, TargetName: "M31", FrameType: FrameType.Light,
                FilterName: null, ExposureSeconds: 60, Gain: 100, Offset: 10,
                TemperatureC: null, CapturedUtc: DateTimeOffset.UtcNow, FilePath: fitsPath,
                FileSizeBytes: 4, Width: 10, Height: 10, BitDepth: 16, Hfr: null,
                StarCount: null, Eccentricity: null, GuidingRmsArcsec: null, SnrEstimate: null,
                QualityScore: null, Rating: 0, Tags: []), CancellationToken.None);
            return id;
        }

        [Test]
        public void Paths_under_a_root_pass_and_everything_else_is_refused() {
            var inside = Path.Combine(_root, "night", "a.fits");
            Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
            File.WriteAllText(inside, "x");
            Assert.That(_repo.ConfinedExistingPath(inside), Is.EqualTo(Path.GetFullPath(inside)));
            Assert.That(_repo.ConfinedExistingPath(Path.Combine(_root, "night", "..", "night", "a.fits")), Is.EqualTo(Path.GetFullPath(inside)), "normalized before the check");
            Assert.That(_repo.ConfinedExistingPath(Path.Combine(_root, "..", "elsewhere", "a.fits")), Is.Null, "traversal out of the root");
            Assert.That(_repo.ConfinedExistingPath(_root + "-sibling/a.fits"), Is.Null, "a prefix match on the name is not containment");
            Assert.That(_repo.ConfinedExistingPath("/etc/hostname"), Is.Null);
            Assert.That(_repo.ConfinedExistingPath(""), Is.Null);
            Assert.That(_repo.ConfinedExistingPath(null), Is.Null);
            Assert.That(_repo.ConfinedExistingPath(Path.Combine(_root, "missing.fits")), Is.Null, "inside but absent reads as missing");
        }

        [Test]
        public void A_link_inside_the_root_that_leaves_it_is_refused() {
            var target = Path.Combine(_outside, "secret.fits");
            File.WriteAllText(target, "s");
            var link = Path.Combine(_root, "link.fits");
            try {
                File.CreateSymbolicLink(link, target);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) {
                Assert.Ignore("symlinks not available here: " + ex.Message);
            }
            Assert.That(_repo.ConfinedExistingPath(link), Is.Null);
        }

        [Test]
        public async Task Download_and_preview_refuse_a_row_outside_the_roots() {
            var inside = await InsertAsync(Path.Combine(_root, "in.fits"));
            var outside = await InsertAsync(Path.Combine(_outside, "out.fits"));

            var ok = await _repo.OpenDownloadAsync(inside, CancellationToken.None);
            Assert.That(ok, Is.Not.Null);
            await ok!.Value.FitsStream.DisposeAsync();
            Assert.That(await _repo.OpenDownloadAsync(outside, CancellationToken.None), Is.Null, "the file exists, but it is not ours to serve");

            var preview = await _repo.GetPreviewAsync(outside, new FramePreviewRequestDto(null!, null, null, null, null, ApplyDebayer: true), CancellationToken.None);
            Assert.That(preview!.Value.Bytes.Length, Is.LessThan(2000), "placeholder, not a render of the outside file");
            Assert.That(await _repo.GetHistogramAsync(outside, CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task Delete_from_disk_never_touches_a_file_outside_the_roots() {
            var insidePath = Path.Combine(_root, "in.fits");
            var outsidePath = Path.Combine(_outside, "out.fits");
            var inside = await InsertAsync(insidePath);
            var outside = await InsertAsync(outsidePath);

            await _repo.BulkDeleteAsync(new BulkDeleteRequestDto(FrameIds: [inside, outside], DeleteFromDisk: true), idempotencyKey: null, CancellationToken.None);

            Assert.That(File.Exists(insidePath), Is.False, "our file goes");
            Assert.That(File.Exists(outsidePath), Is.True, "a foreign file is left alone");
            Assert.That(await _repo.GetAsync(outside, CancellationToken.None), Is.Null, "the catalog row still goes — it was wrong");
        }

        [Test]
        public async Task Delete_from_disk_sweeps_the_sidecars_of_a_fits_the_user_already_removed() {
            var insidePath = Path.Combine(_root, "gone.fits");
            var inside = await InsertAsync(insidePath);
            var stem = Path.ChangeExtension(insidePath, null);
            await File.WriteAllTextAsync(stem + ".preview.jpg", "p");
            await File.WriteAllTextAsync(stem + ".thumb.jpg", "t");
            File.Delete(insidePath); // removed over SMB / in Finder

            var outsidePath = Path.Combine(_outside, "gone2.fits");
            var outside = await InsertAsync(outsidePath);
            var outsideStem = Path.ChangeExtension(outsidePath, null);
            await File.WriteAllTextAsync(outsideStem + ".preview.jpg", "p");
            File.Delete(outsidePath);

            await _repo.BulkDeleteAsync(new BulkDeleteRequestDto(FrameIds: [inside, outside], DeleteFromDisk: true), idempotencyKey: null, CancellationToken.None);

            Assert.That(File.Exists(stem + ".preview.jpg"), Is.False, "sidecar under a root is swept even though the FITS was already gone");
            Assert.That(File.Exists(stem + ".thumb.jpg"), Is.False);
            Assert.That(File.Exists(outsideStem + ".preview.jpg"), Is.True, "a sidecar outside the roots is never touched");
        }

        [Test]
        public async Task Deleting_preview_variants_never_touches_a_directory_outside_the_roots() {
            var insidePath = Path.Combine(_root, "v.fits");
            var outsidePath = Path.Combine(_outside, "v.fits");
            var inside = await InsertAsync(insidePath);
            var outside = await InsertAsync(outsidePath);
            var insideVariant = Path.ChangeExtension(insidePath, null) + ".preview.foo.jpg";
            var outsideVariant = Path.ChangeExtension(outsidePath, null) + ".preview.foo.jpg";
            await File.WriteAllTextAsync(insideVariant, "v");
            await File.WriteAllTextAsync(outsideVariant, "v");

            Assert.That(await _repo.DeletePreviewVariantsAsync(inside, CancellationToken.None), Is.True);
            Assert.That(await _repo.DeletePreviewVariantsAsync(outside, CancellationToken.None), Is.True, "the row exists; there is just nothing of ours to delete");

            Assert.That(File.Exists(insideVariant), Is.False, "our variant goes");
            Assert.That(File.Exists(outsideVariant), Is.True, "a foreign directory is never enumerated or deleted from");
        }

        [Test]
        public void The_default_roots_follow_the_profile_save_directory() {
            var store = new InMemoryProfileStore();
            var repo = new SqliteFrameRepository(_db, store);
            var saveDir = Path.Combine(_outside, "newstore");
            Directory.CreateDirectory(saveDir);
            var fits = Path.Combine(saveDir, "f.fits");
            File.WriteAllText(fits, "x");
            Assert.That(repo.ConfinedExistingPath(fits), Is.Null, "not under the configured save directory yet");
            store.PutStorageSettings(store.GetStorageSettings() with { SaveDirectory = saveDir });
            Assert.That(repo.ConfinedExistingPath(fits), Is.EqualTo(Path.GetFullPath(fits)), "read live: a changed setting is honoured");
        }
    }
}
