#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
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
    /// §38.7 disk-shipped sequence templates: drop a JSON file under
    /// <c>{profileDir}/sequences/templates/</c> and the
    /// <see cref="PlaceholderSequenceTemplateService"/> picks it up alongside
    /// the hardcoded built-ins, with disk-name collisions overriding built-ins.
    /// </summary>
    [TestFixture]
    public class DiskSequenceTemplateTest {

        private static readonly string[] ExpectedBuiltinTemplateNames = { "single-target-lrgb", "single-target-narrowband", "all-night-dso-roster" };

        private string _profileDir = string.Empty;
        private string _templatesDir = string.Empty;

        [SetUp]
        public void SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), $"oara-tpl-{Guid.NewGuid():N}");
            _templatesDir = Path.Combine(_profileDir, "sequences", "templates");
            Directory.CreateDirectory(_templatesDir);
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_profileDir, recursive: true); } catch (System.IO.IOException) { } catch (System.UnauthorizedAccessException) { }
        }

        [Test]
        public async Task ListAsync_returns_builtins_when_disk_dir_empty() {
            var svc = new PlaceholderSequenceTemplateService(Mock.Of<ISequenceService>(), _profileDir);
            var list = await svc.ListAsync(CancellationToken.None);
            Assert.That(list, Has.Count.EqualTo(3));
            Assert.That(list.Select(t => t.Name),
                Is.EquivalentTo(ExpectedBuiltinTemplateNames));
            Assert.That(list, Is.All.Matches<SequenceTemplateDto>(t => t.IsBuiltIn));
        }

        [Test]
        public async Task ListAsync_merges_disk_templates_with_builtins() {
            var diskJson = """
                {
                    "name": "comet-2026-x1",
                    "category": "comet",
                    "description": "Comet C/2026 X1 nightly schedule",
                    "isBuiltIn": false,
                    "body": { "schemaVersion": "openastroara-sequence-v1", "kind": "comet" }
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(_templatesDir, "comet-2026-x1.json"), diskJson);

            var svc = new PlaceholderSequenceTemplateService(Mock.Of<ISequenceService>(), _profileDir);
            var list = await svc.ListAsync(CancellationToken.None);

            Assert.That(list, Has.Count.EqualTo(4));
            Assert.That(list.Select(t => t.Name), Does.Contain("comet-2026-x1"));
            var disk = list.First(t => t.Name == "comet-2026-x1");
            Assert.That(disk.Category, Is.EqualTo("comet"));
            Assert.That(disk.IsBuiltIn, Is.False);
        }

        [Test]
        public async Task ListAsync_disk_template_overrides_builtin_with_same_name() {
            // .deb-shipped template can update a built-in without a code change.
            var diskJson = """
                {
                    "name": "single-target-lrgb",
                    "category": "single-target",
                    "description": "OVERRIDDEN: better LRGB defaults",
                    "isBuiltIn": false,
                    "body": { "schemaVersion": "openastroara-sequence-v1" }
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(_templatesDir, "single-target-lrgb.json"), diskJson);

            var svc = new PlaceholderSequenceTemplateService(Mock.Of<ISequenceService>(), _profileDir);
            var list = await svc.ListAsync(CancellationToken.None);

            // Still 3 entries (override doesn't duplicate)
            Assert.That(list, Has.Count.EqualTo(3));
            var lrgb = list.First(t => t.Name == "single-target-lrgb");
            Assert.That(lrgb.Description, Is.EqualTo("OVERRIDDEN: better LRGB defaults"));
            Assert.That(lrgb.IsBuiltIn, Is.False);
        }

        [Test]
        public async Task ListAsync_skips_invalid_disk_files() {
            await File.WriteAllTextAsync(Path.Combine(_templatesDir, "bad.json"), "{ not valid json");
            var svc = new PlaceholderSequenceTemplateService(Mock.Of<ISequenceService>(), _profileDir);
            var list = await svc.ListAsync(CancellationToken.None);
            Assert.That(list, Has.Count.EqualTo(3));  // built-ins only
        }

        [Test]
        public void TemplateExists_includes_disk_templates() {
            var diskJson = """
                {
                    "name": "lunar-mosaic",
                    "category": "moon",
                    "description": "",
                    "isBuiltIn": false,
                    "body": { "schemaVersion": "openastroara-sequence-v1" }
                }
                """;
            File.WriteAllText(Path.Combine(_templatesDir, "lunar-mosaic.json"), diskJson);

            var svc = new PlaceholderSequenceTemplateService(Mock.Of<ISequenceService>(), _profileDir);

            Assert.That(svc.TemplateExists("lunar-mosaic"), Is.True);
            Assert.That(svc.TemplateExists("nonexistent"), Is.False);
            Assert.That(svc.TemplateExists("single-target-lrgb"), Is.True);  // built-in
        }
    }
}