#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test;

[TestFixture]
public class FileProfileRepositoryTest {
    private string _dir = null!;
    private FileProfileStore _store = null!;
    private FileProfileRepository _repo = null!;

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "ara-profiles-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
        _store = new FileProfileStore(_dir);
        _repo = new FileProfileRepository(_dir, _store);
        // The repository no longer auto-seeds (zero profiles is a first-class
        // state) — create the baseline profile these tests operate on.
        _repo.Create("Default", settings: null, makeActive: true);
    }

    [TearDown]
    public void TearDown() {
        _repo.Dispose();
        try {
            Directory.Delete(_dir, recursive: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // best-effort temp cleanup
        }
    }

    private void SetLatitude(double lat) {
        var site = _store.GetSiteSettings() with { LatitudeDeg = lat };
        _store.PutSiteSettings(site);
    }

    private double Latitude() => _store.GetSiteSettings().LatitudeDeg;

    [Test]
    public void FreshDir_starts_with_zero_profiles_and_no_active_id() {
        // A fresh install must land the user in profile setup, not on an
        // unconfigured auto-seeded profile that can't run a rig.
        var dir = Path.Combine(Path.GetTempPath(), "ara-profiles-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try {
            var store = new FileProfileStore(dir);
            using var repo = new FileProfileRepository(dir, store);
            repo.List().Profiles.Should().BeEmpty();
            repo.ActiveId.Should().BeNull();
        } finally {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Test]
    public void First_create_with_null_settings_captures_the_live_store() {
        // The legacy single-profile migration path: pre-§37 settings live in
        // profile.json (the live store); the first profile the user creates
        // captures them instead of starting from factory defaults.
        var dir = Path.Combine(Path.GetTempPath(), "ara-profiles-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try {
            var store = new FileProfileStore(dir);
            store.PutSiteSettings(store.GetSiteSettings() with { LatitudeDeg = 42 });
            using var repo = new FileProfileRepository(dir, store);
            var meta = repo.Create("My Rig", settings: null, makeActive: true);
            repo.ActiveId.Should().Be(meta.Id, "the first profile becomes active");
            repo.GetProfile(meta.Id)!.Settings.Site.LatitudeDeg.Should().Be(42);
        } finally {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Test]
    public void Create_inactive_keeps_current_active() {
        var before = _repo.ActiveId;
        var meta = _repo.Create("Second", settings: null, makeActive: false);
        _repo.List().Profiles.Should().HaveCount(2);
        _repo.ActiveId.Should().Be(before, "creating with makeActive:false must not switch");
        meta.Name.Should().Be("Second");
    }

    [Test]
    public void Create_active_switches_and_loads_into_live_store() {
        SetLatitude(10);                       // active profile A now at lat 10
        var b = _repo.Create("B", settings: null, makeActive: true); // clones A (lat 10)
        SetLatitude(20);                       // edit B → lat 20
        _repo.ActiveId.Should().Be(b.Id);
        _repo.GetProfile(b.Id)!.Settings.Site.LatitudeDeg.Should().Be(20);
    }

    [Test]
    public void Select_loads_the_target_profiles_settings_into_the_live_store() {
        SetLatitude(11);                                   // A at 11
        var b = _repo.Create("B", settings: null, makeActive: true);
        SetLatitude(22);                                   // B at 22
        var aId = _repo.List().Profiles.Single(p => p.Id != b.Id).Id;

        _repo.SelectProfile(aId).Should().BeTrue();

        _repo.ActiveId.Should().Be(aId);
        Latitude().Should().Be(11, "selecting A must reload its settings into the live store");
    }

    [Test]
    public void Delete_removes_a_non_active_profile_and_404s_an_unknown_id() {
        var activeId = _repo.ActiveId!.Value;
        _repo.Delete(Guid.NewGuid()).Should().Be(ProfileDeleteResult.NotFound);

        var b = _repo.Create("B", settings: null, makeActive: false);
        _repo.Delete(b.Id).Should().Be(ProfileDeleteResult.Deleted);
        _repo.List().Profiles.Should().ContainSingle().Which.Id.Should().Be(activeId);
        _repo.ActiveId.Should().Be(activeId);
    }

    [Test]
    public void Delete_active_falls_back_to_the_newest_remaining_profile() {
        SetLatitude(11);                                   // A at 11
        var b = _repo.Create("B", settings: null, makeActive: true);
        SetLatitude(22);                                   // B (active, newest) at 22
        var aId = _repo.List().Profiles.Single(p => p.Id != b.Id).Id;

        _repo.Delete(b.Id).Should().Be(ProfileDeleteResult.Deleted);

        // Mirrors the boot-time stale-pointer recovery: newest remaining wins,
        // and its settings load into the live store.
        _repo.ActiveId.Should().Be(aId);
        Latitude().Should().Be(11, "the fallback profile's settings must be applied");
        _repo.List().Profiles.Should().ContainSingle().Which.Id.Should().Be(aId);
    }

    [Test]
    public void Delete_last_profile_returns_to_the_zero_profile_state() {
        // Deleting the last profile is "start over": the fresh-install state,
        // where the client routes the user into profile setup — NOT an
        // unconfigured auto-seeded profile.
        var activeId = _repo.ActiveId!.Value;

        _repo.Delete(activeId).Should().Be(ProfileDeleteResult.Deleted);

        _repo.List().Profiles.Should().BeEmpty();
        _repo.ActiveId.Should().BeNull();

        // And the state survives a daemon restart: the active pointer was
        // cleared, and boot doesn't resurrect a profile out of nothing.
        _repo.Dispose();
        var store2 = new FileProfileStore(_dir);
        _repo = new FileProfileRepository(_dir, store2);
        _repo.List().Profiles.Should().BeEmpty();
        _repo.ActiveId.Should().BeNull();
    }

    [Test]
    public void Rename_updates_the_meta() {
        var id = _repo.ActiveId!.Value;
        _repo.Rename(id, "  Backyard  ").Should().BeTrue();
        _repo.List().Profiles.Single(p => p.Id == id).Name.Should().Be("Backyard");
    }

    [Test]
    public void Create_caps_an_overlong_name() {
        var meta = _repo.Create(new string('x', 500), settings: null, makeActive: false);
        meta.Name.Length.Should().BeLessThanOrEqualTo(120);
    }

    [Test]
    public void Load_ignores_foreign_and_misnamed_json_files() {
        var profilesDir = Path.Combine(_dir, "profiles");
        // A non-GUID-named JSON a future feature might drop alongside the profiles.
        File.WriteAllText(Path.Combine(profilesDir, "index.json"), "{\"some\":\"thing\"}");
        // A GUID-named file whose contents don't parse as a StoredProfileDto.
        File.WriteAllText(
            Path.Combine(profilesDir, Guid.NewGuid().ToString("N") + ".json"), "not json");

        using var repo2 = new FileProfileRepository(_dir, _store);
        // Only the seeded "Default" profile is recognized; the stray files are ignored.
        repo2.List().Profiles.Should().ContainSingle().Which.Name.Should().Be("Default");
    }

    [Test]
    public void Load_ignores_a_file_whose_name_mismatches_its_meta_id() {
        // Create a real profile, then rename its file to a different GUID so the filename
        // no longer matches the stored Meta.Id — Load must skip it (the guard against a
        // foreign file that happens to deserialize).
        var extra = _repo.Create("Extra", settings: null, makeActive: false);
        var profilesDir = Path.Combine(_dir, "profiles");
        File.Move(
            Path.Combine(profilesDir, extra.Id.ToString("N") + ".json"),
            Path.Combine(profilesDir, Guid.NewGuid().ToString("N") + ".json"));

        using var repo2 = new FileProfileRepository(_dir, _store);
        repo2.List().Profiles.Should().ContainSingle().Which.Name.Should().Be("Default");
    }

    [Test]
    public void Live_put_mirrors_into_the_active_profile_file() {
        SetLatitude(33);
        var id = _repo.ActiveId!.Value;
        _repo.GetProfile(id)!.Settings.Site.LatitudeDeg.Should().Be(33);
    }

    [Test]
    public void ReadFile_backfills_null_sections_from_an_older_saved_snapshot() {
        // A profile saved before newer sections existed: its JSON simply lacks
        // those keys, so they deserialize to null. Simulate by rewriting the
        // seeded profile's file with a snapshot that carries ONLY a site section.
        var id = _repo.ActiveId!.Value;
        var path = Path.Combine(_dir, "profiles", id.ToString("N") + ".json");
        File.Exists(path).Should().BeTrue("the seeded profile persists to disk");
        var meta = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path))
            .RootElement.GetProperty("meta").GetRawText();
        File.WriteAllText(path, $$"""
            {
              "meta": {{meta}},
              "settings": {
                "site": { "site_name": "Old Yard", "latitude_deg": 12.5 }
              }
            }
            """);

        var stored = _repo.GetProfile(id)!;

        // The section that WAS present survives...
        stored.Settings.Site.SiteName.Should().Be("Old Yard");
        stored.Settings.Site.LatitudeDeg.Should().Be(12.5);
        // ...and every missing section is back-filled instead of shipping null on
        // the wire (GET /profiles/{id}, profile-share export) or NRE'ing consumers.
        stored.Settings.CameraElectronics.Should().NotBeNull();
        stored.Settings.Optics.Should().NotBeNull();
        stored.Settings.FilterSet.Should().NotBeNull();
        stored.Settings.FilterSet.Filters.Should().NotBeNull();
        stored.Settings.FilterWheelLabels.Should().NotBeNull();
        stored.Settings.Notifications.Should().NotBeNull();
        stored.Settings.SafetyPolicies.Should().NotBeNull();
        stored.Settings.StretchDefaults.Should().NotBeNull();
        stored.Settings.StretchDefaults.ManualDefaultParams.Should().NotBeNull();
        stored.Settings.ImagingDefaults.Should().NotBeNull();
        stored.Settings.Storage.Should().NotBeNull();
        stored.Settings.Filenames.Should().NotBeNull();
        stored.Settings.Autofocus.Should().NotBeNull();
        stored.Settings.PlateSolve.Should().NotBeNull();
        stored.Settings.DiagnosticsMode.Should().NotBeNull();
        stored.Settings.Phd2.Should().NotBeNull();
        stored.Settings.EquipmentConnection.Should().NotBeNull();
    }

    [Test]
    public void ReadFile_backfills_a_snapshot_that_is_entirely_missing() {
        // Pathological hand-edited file: no settings object at all.
        var id = _repo.ActiveId!.Value;
        var path = Path.Combine(_dir, "profiles", id.ToString("N") + ".json");
        var meta = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path))
            .RootElement.GetProperty("meta").GetRawText();
        File.WriteAllText(path, $$"""{ "meta": {{meta}} }""");

        var stored = _repo.GetProfile(id)!;

        stored.Settings.Should().NotBeNull();
        stored.Settings.Site.Should().NotBeNull();
        stored.Settings.CameraElectronics.Should().NotBeNull();
    }

    [Test]
    public void Reopen_restores_the_active_profile_into_the_live_store() {
        var b = _repo.Create("B", settings: null, makeActive: true);
        SetLatitude(77);
        _repo.Dispose();

        // A fresh store+repo over the same dir must come back up on profile B at lat 77.
        var store2 = new FileProfileStore(_dir);
        using var repo2 = new FileProfileRepository(_dir, store2);
        repo2.ActiveId.Should().Be(b.Id);
        store2.GetSiteSettings().LatitudeDeg.Should().Be(77);

        // Re-init the SetUp fields so TearDown disposes the live objects.
        _repo = new FileProfileRepository(_dir, store2);
    }
}
