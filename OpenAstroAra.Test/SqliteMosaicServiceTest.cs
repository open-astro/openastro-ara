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
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§47 / #1146 — mosaic projects persist in the catalog and their per-panel completion is
    /// derived from the light frames filed under each panel's "&lt;name&gt; i/N" target block.</summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk
    public class SqliteMosaicServiceTest {

        private string _dir = string.Empty;
        private SqliteAraDatabase _db = null!;
        private SqliteFrameRepository _frames = null!;
        private InMemoryProfileStore _store = null!;
        private SqliteMosaicService _svc = null!;
        private static readonly Guid Session = Guid.Parse("77777777-7777-7777-7777-777777777771");
        private static readonly DateTimeOffset T0 = new(2026, 10, 12, 22, 0, 0, TimeSpan.Zero);
        private static readonly string[] ExpectedStatuses = { "done", "done", "in_progress", "pending", "pending", "pending" };
        private static readonly int[] ExpectedCounts = { 3, 3, 1, 0, 0, 0 };
        private static readonly string[] NewestTwo = { "M2", "M1" };
        private static readonly string[] Oldest = { "M0" };

        [SetUp]
        public async Task SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), $"oara-mosaic-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _db = new SqliteAraDatabase(_dir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
            _store = new InMemoryProfileStore();
            _frames = new SqliteFrameRepository(_db, _store, captureRoots: () => new[] { _dir });
            _svc = new SqliteMosaicService(_db, _store, () => T0);
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO sessions (id, started_at) VALUES ($id, $t);";
            cmd.Parameters.AddWithValue("$id", Session.ToString());
            cmd.Parameters.AddWithValue("$t", T0.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        [TearDown]
        public void TearDown() {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static MosaicCreateRequestDto Veil(IReadOnlyList<MosaicPanelCenterDto>? panels = null, int? framesPerPanel = 3) =>
            new("Veil", 312.75, 30.5, PanelCountX: 3, PanelCountY: 2, OverlapPercent: 15, PositionAngleDegrees: 20,
                SequenceTemplateName: null, Panels: panels, FramesPerPanel: framesPerPanel);

        private static IReadOnlyList<MosaicPanelCenterDto> SixPanels() => [
            new(311.5, 31.0), new(312.75, 31.0), new(314.0, 31.0),
            new(311.5, 30.0), new(312.75, 30.0), new(314.0, 30.0),
        ];

        private async Task LightAsync(string target, int minutesAfterT0) {
            var id = Guid.NewGuid();
            await _frames.InsertAsync(new FrameDto(
                Id: id, SessionId: Session, TargetName: target, FrameType: FrameType.Light,
                FilterName: "Ha", ExposureSeconds: 300, Gain: 100, Offset: 10, TemperatureC: null,
                CapturedUtc: T0.AddMinutes(minutesAfterT0), FilePath: Path.Combine(_dir, $"{id:N}.fits"),
                FileSizeBytes: 4, Width: 10, Height: 10, BitDepth: 16, Hfr: null, StarCount: null,
                Eccentricity: null, GuidingRmsArcsec: null, SnrEstimate: null, QualityScore: null,
                Rating: 0, Tags: []), CancellationToken.None);
        }

        [Test]
        public async Task Create_stores_the_plan_and_the_supplied_panel_centres() {
            var dto = await _svc.CreateAsync(Veil(SixPanels()), null, CancellationToken.None);

            Assert.That(dto.Name, Is.EqualTo("Veil"));
            Assert.That(dto.TotalPanels, Is.EqualTo(6));
            Assert.That(dto.PositionAngleDegrees, Is.EqualTo(20));
            Assert.That(dto.CreatedUtc, Is.EqualTo(T0));

            var again = await _svc.GetAsync(dto.Id, CancellationToken.None);
            Assert.That(again, Is.EqualTo(dto), "read back from the catalog, not a cache");

            var panels = await _svc.GetPanelsAsync(dto.Id, CancellationToken.None);
            Assert.That(panels.Select(p => p.PanelIndex), Is.EqualTo(Enumerable.Range(0, 6)));
            Assert.That(panels.Select(p => (p.PanelX, p.PanelY)), Is.EqualTo(new[] { (0, 0), (1, 0), (2, 0), (0, 1), (1, 1), (2, 1) }), "row-major like the planetarium grid");
            Assert.That(panels[2].CenterRaDegrees, Is.EqualTo(314.0));
            Assert.That(panels.Select(p => p.Status), Is.All.EqualTo("pending"));
        }

        [Test]
        public async Task A_second_service_instance_sees_the_project_after_a_restart() {
            var dto = await _svc.CreateAsync(Veil(SixPanels()), null, CancellationToken.None);
            var restarted = new SqliteMosaicService(_db, _store);
            var page = await restarted.ListAsync(10, null, CancellationToken.None);
            Assert.That(page.Items.Select(m => m.Id), Is.EqualTo(new[] { dto.Id }));
            Assert.That((await restarted.GetPanelsAsync(dto.Id, CancellationToken.None)).Count, Is.EqualTo(6));
        }

        [Test]
        public async Task Progress_is_derived_from_the_light_frames_filed_under_each_panel() {
            var dto = await _svc.CreateAsync(Veil(SixPanels(), framesPerPanel: 3), null, CancellationToken.None);
            // Panels 1 and 2 done (3 lights each), panel 3 started (1 light), 4-6 untouched.
            for (var i = 0; i < 3; i++) { await LightAsync("Veil 1/6", i); await LightAsync("Veil 2/6", 10 + i); }
            await LightAsync("Veil 3/6", 30);
            await LightAsync("Veil 7/6", 31);          // not a panel of this mosaic
            await LightAsync("Veil 1/4", 32);          // a different mosaic of the same name
            await LightAsync("Veil", 33);              // a plain single-target run
            await _frames.InsertAsync(new FrameDto(Guid.NewGuid(), Session, "Veil 4/6", FrameType.Dark, null, 300, 100, 10, null,
                T0.AddMinutes(40), Path.Combine(_dir, "dark.fits"), 4, 10, 10, 16, null, null, null, null, null, null, 0, []), CancellationToken.None);

            var progress = await _svc.GetProgressAsync(dto.Id, CancellationToken.None);
            Assert.That(progress, Is.Not.Null);
            Assert.That(progress!.CompletedPanels, Is.EqualTo(2));
            Assert.That(progress.TotalPanels, Is.EqualTo(6));
            Assert.That(progress.FramesCapturedTotal, Is.EqualTo(7), "only lights of panels 1..6 count — not the dark, not 7/6, not 1/4, not the bare name");
            Assert.That(progress.FramesPerPanelTarget, Is.EqualTo(3));
            Assert.That(progress.CurrentPanelIndex, Is.EqualTo(2), "panel 3 (0-based 2) is started and not done");
            Assert.That(progress.CurrentPanelStartedUtc, Is.EqualTo(T0.AddMinutes(30)));

            var panels = await _svc.GetPanelsAsync(dto.Id, CancellationToken.None);
            Assert.That(panels.Select(p => p.Status), Is.EqualTo(ExpectedStatuses));
            Assert.That(panels.Select(p => p.FramesCaptured), Is.EqualTo(ExpectedCounts));
            Assert.That(panels[0].TargetFrameId, Is.Not.Null, "the newest light of a panel is its representative frame");
            Assert.That(panels[3].TargetFrameId, Is.Null, "a dark is not a light");
        }

        [Test]
        public async Task Progress_survives_a_restart_and_follows_frame_deletion() {
            var dto = await _svc.CreateAsync(Veil(SixPanels(), framesPerPanel: 1), null, CancellationToken.None);
            await LightAsync("Veil 5/6", 0);
            var restarted = new SqliteMosaicService(_db, _store);
            Assert.That((await restarted.GetProgressAsync(dto.Id, CancellationToken.None))!.CompletedPanels, Is.EqualTo(1));

            var frameId = (await _frames.ListAsync(10, null, null, null, CancellationToken.None)).Items.Single().Id;
            await _frames.BulkDeleteAsync(new BulkDeleteRequestDto([frameId]), null, CancellationToken.None);
            Assert.That((await restarted.GetProgressAsync(dto.Id, CancellationToken.None))!.CompletedPanels, Is.EqualTo(0), "derived, so a deleted frame un-does the panel");
        }

        [Test]
        public async Task Delete_removes_the_project_and_its_panels() {
            var dto = await _svc.CreateAsync(Veil(SixPanels()), null, CancellationToken.None);
            Assert.That(await _svc.DeleteAsync(dto.Id, CancellationToken.None), Is.True);
            Assert.That(await _svc.GetAsync(dto.Id, CancellationToken.None), Is.Null);
            Assert.That(await _svc.GetPanelsAsync(dto.Id, CancellationToken.None), Is.Empty);
            Assert.That(await _svc.GetProgressAsync(dto.Id, CancellationToken.None), Is.Null);
            Assert.That(await _svc.DeleteAsync(dto.Id, CancellationToken.None), Is.False, "second delete: 404");
        }

        [Test]
        public async Task List_pages_newest_first_with_an_offset_cursor() {
            var clock = T0;
            var svc = new SqliteMosaicService(_db, _store, () => clock);
            for (var i = 0; i < 3; i++) {
                clock = T0.AddMinutes(i);
                await svc.CreateAsync(Veil(SixPanels()) with { Name = $"M{i}" }, null, CancellationToken.None);
            }
            var first = await svc.ListAsync(2, null, CancellationToken.None);
            Assert.That(first.Items.Select(m => m.Name), Is.EqualTo(NewestTwo));
            Assert.That(first.HasMore, Is.True);
            var second = await svc.ListAsync(2, first.NextCursor, CancellationToken.None);
            Assert.That(second.Items.Select(m => m.Name), Is.EqualTo(Oldest));
            Assert.That(second.HasMore, Is.False);
        }

        [Test]
        public async Task Without_supplied_centres_the_grid_is_spread_from_the_profile_optics() {
            // RedCat 51 + APS-C-ish sensor: ~5.4° × 3.6° field; 2×1 at 15 % overlap → panels ~4.6° apart in RA at dec 0.
            _store.PutOpticsSettings(new OpticsSettingsDto(250, 1.0, 6248, 4176, 3.76));
            var dto = await _svc.CreateAsync(new MosaicCreateRequestDto("Grid", 180, 0, 2, 1, 15, 0, null), null, CancellationToken.None);
            var panels = await _svc.GetPanelsAsync(dto.Id, CancellationToken.None);
            Assert.That(panels, Has.Count.EqualTo(2));
            var separation = panels[1].CenterRaDegrees - panels[0].CenterRaDegrees;
            Assert.That(separation, Is.EqualTo(4.57).Within(0.1), "FOV width × (1 − overlap)");
            Assert.That(panels.Average(p => p.CenterRaDegrees), Is.EqualTo(180).Within(1e-6), "centred on the request");
        }

        [Test]
        public async Task Without_optics_every_panel_sits_at_the_centre_and_nothing_crosses_the_wrap() {
            _store.PutOpticsSettings(new OpticsSettingsDto(0, 1.0, 0, 0, 0));
            var dto = await _svc.CreateAsync(new MosaicCreateRequestDto("Blind", 359.9, 10, 2, 2, 10, 0, null), null, CancellationToken.None);
            var panels = await _svc.GetPanelsAsync(dto.Id, CancellationToken.None);
            Assert.That(panels.Select(p => p.CenterRaDegrees), Is.All.EqualTo(359.9));
            Assert.That(panels.Select(p => p.CrossesRaWrap), Is.All.False, "unknown field width: the §47.3 flag cannot be asserted");
        }

        [Test]
        public async Task An_absurd_grid_is_clamped_not_inserted_row_by_row() {
            var dto = await _svc.CreateAsync(new MosaicCreateRequestDto("Huge", 10, 10, 1000, 1000, 10, 0, null), null, CancellationToken.None);
            Assert.That(dto.PanelCountX, Is.EqualTo(SqliteMosaicService.MaxPanelsPerAxis));
            Assert.That(dto.PanelCountY, Is.EqualTo(SqliteMosaicService.MaxPanelsPerAxis));
            Assert.That(dto.TotalPanels, Is.EqualTo(SqliteMosaicService.MaxPanelsPerAxis * SqliteMosaicService.MaxPanelsPerAxis));
            Assert.That((await _svc.GetPanelsAsync(dto.Id, CancellationToken.None)).Count, Is.EqualTo(dto.TotalPanels));
        }

        [Test]
        public void Ra_wrap_and_panel_name_helpers() {
            Assert.That(SqliteMosaicService.CrossesRaWrap(359.5, 0, 2.0), Is.True);
            Assert.That(SqliteMosaicService.CrossesRaWrap(0.5, 0, 2.0), Is.True);
            Assert.That(SqliteMosaicService.CrossesRaWrap(180, 0, 2.0), Is.False);
            Assert.That(SqliteMosaicService.CrossesRaWrap(357, 80, 2.0), Is.True, "RA extent widens by 1/cos(dec)");
            Assert.That(SqliteMosaicService.CrossesRaWrap(180, 0, 0), Is.False, "no field width → unknown → false");

            Assert.That(SqliteMosaicService.PanelTargetName("Veil", 0, 6), Is.EqualTo("Veil 1/6"));
            Assert.That(SqliteMosaicService.TryPanelIndex("Veil 6/6", "Veil", 6, out var i) && i == 5, Is.True);
            Assert.That(SqliteMosaicService.TryPanelIndex("Veil 0/6", "Veil", 6, out _), Is.False);
            Assert.That(SqliteMosaicService.TryPanelIndex("Veil 7/6", "Veil", 6, out _), Is.False);
            Assert.That(SqliteMosaicService.TryPanelIndex("Veil 1/4", "Veil", 6, out _), Is.False);
            Assert.That(SqliteMosaicService.TryPanelIndex("Veil nebula 1/6", "Veil", 6, out _), Is.False);
            Assert.That(SqliteMosaicService.StatusFor(0, 3), Is.EqualTo("pending"));
            Assert.That(SqliteMosaicService.StatusFor(2, 3), Is.EqualTo("in_progress"));
            Assert.That(SqliteMosaicService.StatusFor(3, 3), Is.EqualTo("done"));
        }

        [Test]
        public void Tangent_plane_deprojection_round_trips_the_centre_and_steps_east() {
            Assert.That(SqliteMosaicService.TanToRaDec(0, 0, 10, 20), Is.EqualTo((10.0, 20.0)));
            var (ra, dec) = SqliteMosaicService.TanToRaDec(1.0, 0, 10, 0);
            Assert.That(ra, Is.EqualTo(11.0).Within(1e-3), "1° east at the equator is 1° of RA");
            Assert.That(dec, Is.EqualTo(0).Within(1e-6));
            var (raHigh, _) = SqliteMosaicService.TanToRaDec(1.0, 0, 10, 60);
            Assert.That(raHigh - 10, Is.EqualTo(2.0).Within(0.05), "at dec 60 a 1° offset is ~2° of RA");
        }
    }
}
