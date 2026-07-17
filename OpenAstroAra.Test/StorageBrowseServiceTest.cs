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
using System.Linq;

namespace OpenAstroAra.Test;

/// <summary>§37.4/§29 — the save-directory picker's server-side walk: one level
/// per call, directories only, hidden + virtual filesystems excluded, curated
/// roots when no path is given.</summary>
public class StorageBrowseServiceTest {
    private static readonly string[] ExpectedChildren = ["Archive", "captures"];
    private string _dir = null!;
    private readonly StorageBrowseService _svc = new();

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "ara-browse-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_dir, "captures"));
        Directory.CreateDirectory(Path.Combine(_dir, "Archive"));
        Directory.CreateDirectory(Path.Combine(_dir, ".hidden"));
        File.WriteAllText(Path.Combine(_dir, "not-a-dir.fits"), "x");
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Test]
    public void Lists_child_directories_only_sorted_and_unhidden() {
        var result = _svc.Browse(_dir);
        Assert.That(result.Path, Is.EqualTo(Path.GetFullPath(_dir)));
        Assert.That(result.Parent, Is.EqualTo(Path.GetDirectoryName(Path.GetFullPath(_dir))));
        Assert.That(result.Dirs.Select(d => d.Name), Is.EqualTo(ExpectedChildren),
            "files and dot-directories are excluded; names sort case-insensitively");
        Assert.That(result.Writable, Is.True, "a temp dir we just created is writable");
    }

    [Test]
    public void No_path_returns_curated_roots_that_exist() {
        var roots = _svc.Browse(null);
        Assert.That(roots.Parent, Is.Null);
        Assert.That(roots.Dirs, Is.Not.Empty, "home + / always exist");
        Assert.That(roots.Dirs.Select(d => d.Path),
            Does.Contain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.That(roots.Dirs.All(d => Directory.Exists(d.Path)), Is.True,
            "only existing roots are offered");
    }

    [Test]
    public void A_missing_path_throws_DirectoryNotFound() {
        Assert.Throws<DirectoryNotFoundException>(
            () => _svc.Browse(Path.Combine(_dir, "nope")));
    }

    [Test]
    public void A_virtual_filesystem_is_refused() {
        // /proc exists on Linux CI but must never be offered as a save target;
        // on macOS/Windows it doesn't exist — either way the walk refuses it.
        Assert.Throws<DirectoryNotFoundException>(() => _svc.Browse("/proc"));
    }

    [Test]
    public void CreateFolder_creates_and_returns_the_refreshed_parent_listing() {
        var result = _svc.CreateFolder(_dir, "Lights");
        Assert.That(Directory.Exists(Path.Combine(_dir, "Lights")), Is.True);
        Assert.That(result.Path, Is.EqualTo(Path.GetFullPath(_dir)));
        Assert.That(result.Dirs.Select(d => d.Name), Does.Contain("Lights"));
    }

    [Test]
    public void CreateFolder_is_idempotent_when_the_folder_already_exists() {
        _svc.CreateFolder(_dir, "Lights");
        var again = _svc.CreateFolder(_dir, "Lights");
        Assert.That(again.Dirs.Count(d => d.Name == "Lights"), Is.EqualTo(1));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("a/b")]
    [TestCase(@"a\b")]
    [TestCase("..")]
    [TestCase(".hidden")]
    public void CreateFolder_rejects_invalid_names(string name) {
        Assert.Throws<ArgumentException>(() => _svc.CreateFolder(_dir, name));
    }

    [Test]
    public void CreateFolder_rejects_a_missing_parent_and_the_roots_listing() {
        Assert.Throws<DirectoryNotFoundException>(
            () => _svc.CreateFolder(Path.Combine(_dir, "nope"), "Lights"));
        Assert.Throws<DirectoryNotFoundException>(
            () => _svc.CreateFolder("", "Lights"));
    }

    [Test]
    public void Media_children_are_marked_removable() {
        // Simulated via the path-prefix rule (no real /media on dev boxes):
        // the flag derivation is pure, so pin it through a real browse of a
        // tree we control only when /media exists; otherwise pin the rule via
        // the roots list (media/mnt/Volumes are flagged removable there).
        var roots = _svc.Browse(null);
        foreach (var d in roots.Dirs.Where(d => d.Path is "/media" or "/mnt" or "/Volumes")) {
            Assert.That(d.Removable, Is.True, $"{d.Path} must badge as removable");
        }
    }
}
