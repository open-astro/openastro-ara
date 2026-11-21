#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenAstroAra.Server;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1955 — builds the daemon's real composition root (<see cref="Program.BuildApp"/>) on a
    /// profile dir holding an unreadable profile file and checks the boot wiring posts one Critical
    /// inbox notice for it, and that a second start on the same dir does not post another.</summary>
    [TestFixture]
    [Category("IO")] // real disk and SQLite, like CompositionRootSmokeTest
    // Same isolation as CompositionRootSmokeTest: process-wide profile-dir variable, global Serilog
    // logger and Notifier sink.
    [NonParallelizable]
    public class ProfileSetAsideBootTest {

        private string profileDir = null!;
        private string? previousProfileDir;

        [SetUp]
        public void PointTheDaemonAtATempDir() {
            profileDir = Path.Combine(Path.GetTempPath(), $"oara-setaside-boot-{Guid.NewGuid():N}");
            Directory.CreateDirectory(profileDir);
            previousProfileDir = Environment.GetEnvironmentVariable("OPENASTROARA_PROFILE_DIR");
            Environment.SetEnvironmentVariable("OPENASTROARA_PROFILE_DIR", profileDir);
        }

        [TearDown]
        public void RestoreTheEnvironment() {
            Environment.SetEnvironmentVariable("OPENASTROARA_PROFILE_DIR", previousProfileDir);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try {
                Directory.Delete(profileDir, recursive: true);
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            }
        }

        [Test]
        public async Task Boot_posts_one_Critical_notice_for_a_set_aside_profile_and_none_on_the_next_start() {
            var id = Guid.NewGuid();
            var profilesDir = Path.Combine(profileDir, "profiles");
            Directory.CreateDirectory(profilesDir);
            await File.WriteAllTextAsync(Path.Combine(profilesDir, $"{id:N}.json"), "not json");

            var first = await BootAndListProfileNoticesAsync();
            Assert.That(first, Has.Count.EqualTo(1), "one notice for the one set-aside file");
            Assert.That(first[0].Severity, Is.EqualTo(NotificationSeverity.Critical));
            Assert.That(first[0].RelatedEntityId, Is.EqualTo(id.ToString()));
            Assert.That(first[0].Message, Does.Contain(".json.corrupt."));

            var second = await BootAndListProfileNoticesAsync();
            Assert.That(second, Has.Count.EqualTo(1), "the second start must not post the notice again");
            Assert.That(second[0].Id, Is.EqualTo(first[0].Id));
        }

        private static async Task<List<NotificationDto>> BootAndListProfileNoticesAsync() {
            var app = Program.BuildApp([], TestHostSeams.Replace);
            try {
                var page = await app.Services.GetRequiredService<INotificationService>()
                    .ListAsync(50, null, null, CancellationToken.None);
                return page.Items.Where(n => n.RelatedEntityType == "profile").ToList();
            } finally {
                OpenAstroAra.Core.Utility.Notification.Notifier.Use(null);
                await app.DisposeAsync();
                await Serilog.Log.CloseAndFlushAsync();
            }
        }
    }
}