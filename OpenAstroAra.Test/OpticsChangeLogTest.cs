#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace OpenAstroAra.Test {

    /// <summary>An optics PUT that changes the optics logs the client that sent it (event 3730): the rig's
    /// pixel size went to 0 mid-session on 11-13 and nothing said which device wrote it.</summary>
    [TestFixture]
    public class OpticsChangeLogTest {

        private sealed class CapturingLogger : ILogger {
            public readonly List<(EventId Id, string Text)> Entries = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                Entries.Add((eventId, formatter(state, exception)));
        }

        private static readonly OpticsSettingsDto Rig = new(FocalLengthMm: 375, ReducerFactor: 1,
            SensorWidthPx: 3008, SensorHeightPx: 3008, PixelSizeUm: 3.76);

        private static DefaultHttpContext Http() {
            var http = new DefaultHttpContext();
            http.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.7");
            http.Request.Headers.UserAgent = "Dart/3.9 (dart:io)";
            return http;
        }

        [Test]
        public void A_put_that_changes_the_optics_names_the_client_and_both_values() {
            var log = new CapturingLogger();
            ProfileEndpoints.LogOpticsChange(Rig, Rig with { PixelSizeUm = 0 }, Http(), log);
            Assert.That(log.Entries, Has.Count.EqualTo(1));
            var (id, text) = log.Entries.Single();
            Assert.Multiple(() => {
                Assert.That(id.Id, Is.EqualTo(3730));
                Assert.That(text, Does.Contain("192.0.2.7"));
                Assert.That(text, Does.Contain("Dart/3.9 (dart:io)"));
                Assert.That(text, Does.Contain("PixelSizeUm = 3.76"));
                Assert.That(text, Does.Contain("PixelSizeUm = 0"));
            });
        }

        [Test]
        public void An_identical_put_logs_nothing() {
            var log = new CapturingLogger();
            ProfileEndpoints.LogOpticsChange(Rig, Rig with { }, Http(), log);
            Assert.That(log.Entries, Is.Empty);
        }
    }
}