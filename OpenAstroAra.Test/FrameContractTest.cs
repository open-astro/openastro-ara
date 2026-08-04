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
using OpenAstroAra.Server.Contracts.WsEvents;

namespace OpenAstroAra.Test;

[TestFixture]
public sealed class FrameContractTest {
    private static readonly string[] RequiredFrameEvents = [
        "frame.persist_started",
        "frame.persist_progress",
        "frame.complete",
        "frame.analysis_started",
        "frame.analyzed",
        "frame.preview_started",
        "frame.preview_ready",
        "frame.failed",
        "frame.quarantined",
    ];

    [Test]
    public void Frame_event_catalog_is_complete_and_unique() {
        Assert.Multiple(() => {
            foreach (var eventType in RequiredFrameEvents) {
                Assert.That(WsEventCatalog.All, Does.Contain(eventType));
            }
            Assert.That(WsEventCatalog.All.Distinct(StringComparer.Ordinal).Count(),
                Is.EqualTo(WsEventCatalog.All.Count));
        });
    }

    [Test]
    public void Static_openapi_documents_every_frame_operation_and_payload() {
        var contract = File.ReadAllText(FindRepositoryFile(
            Path.Combine("OpenAstroAra.Server", "openapi.yaml")));
        var required = new[] {
            "/frames:",
            "/frames/{id}:",
            "/frames/{id}/metadata:",
            "/frames/{id}/preview:",
            "/frames/{id}/download:",
            "/frames/{id}/reanalyze:",
            "/frames/{id}/rebuild-preview:",
            "/frames/bulk/tag:",
            "/frames/bulk/rate:",
            "/frames/bulk/quarantine:",
            "WsFrameLifecyclePayload:",
            "WsFrameAnalyzedPayload:",
            "WsFrameFailurePayload:",
        };
        Assert.Multiple(() => {
            foreach (var value in required) Assert.That(contract, Does.Contain(value));
            Assert.That(contract, Does.Contain("frame.preview_ready"));
            Assert.That(contract, Does.Contain("safe display message").IgnoreCase);
        });
    }

    private static string FindRepositoryFile(string relativePath) {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null) {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(relativePath);
    }
}