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
    public void FreshDir_seeds_exactly_one_active_profile() {
        var list = _repo.List();
        list.Profiles.Should().HaveCount(1);
        list.ActiveId.Should().Be(list.Profiles[0].Id);
        list.Profiles[0].Name.Should().Be("Default");
    }

    [Test]
    public void Seed_captures_the_live_store_settings() {
        // Re-seed scenario: a repo built over a store that already has edited settings
        // should capture them into the initial profile (the legacy-migration path).
        SetLatitude(42);
        using var repo2 = new FileProfileRepository(_dir, _store);
        // _dir already has the SetUp repo's "Default"; repo2 just re-reads it. Its active
        // profile must reflect the latitude we wrote (mirrored from the live store).
        var active = repo2.GetProfile(repo2.ActiveId!.Value)!;
        active.Settings.Site.LatitudeDeg.Should().Be(42);
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
    public void Delete_refuses_active_and_last_but_removes_an_extra() {
        var activeId = _repo.ActiveId!.Value;
        _repo.Delete(activeId).Should().Be(ProfileDeleteResult.RefusedActive);
        _repo.Delete(Guid.NewGuid()).Should().Be(ProfileDeleteResult.NotFound);

        var b = _repo.Create("B", settings: null, makeActive: false);
        _repo.Delete(b.Id).Should().Be(ProfileDeleteResult.Deleted);
        _repo.List().Profiles.Should().ContainSingle().Which.Id.Should().Be(activeId);

        // The last remaining profile is also the active one, so the active guard fires first;
        // RefusedLastRemaining is the defensive fallback for the (normally impossible) case of
        // a populated set with no active profile.
        _repo.Delete(activeId).Should().Be(ProfileDeleteResult.RefusedActive);
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
    public void Live_put_mirrors_into_the_active_profile_file() {
        SetLatitude(33);
        var id = _repo.ActiveId!.Value;
        _repo.GetProfile(id)!.Settings.Site.LatitudeDeg.Should().Be(33);
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
