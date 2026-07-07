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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §51 reconnect-replay gap — the per-connection <c>diagnostics.snapshot</c>
    /// payload the WS endpoint sends after every accept. Per-issue fields must be
    /// spelled exactly like <c>diagnostics.issue_detected</c> so the client folds
    /// both with one parser.
    /// </summary>
    [TestFixture]
    public class WebSocketDiagnosticsSnapshotTest {

        private static DiagnosticsStateDto State(params DiagnosticIssueDto[] open) => new(
            Health: open.Length > 0 ? DiagnosticHealth.Red : DiagnosticHealth.Green,
            Mode: DiagnosticsMode.Observe,
            OpenIssueCount: open.Length,
            LastHourIssueCount: open.Length,
            LastEvaluationUtc: new DateTimeOffset(2026, 7, 6, 21, 0, 0, TimeSpan.Zero),
            OpenIssues: open);

        [Test]
        public void EmptyOpenSet_IsAnExplicitEmptyList() {
            var payload = WebSocketEndpoints.BuildDiagnosticsSnapshotPayload(State());
            Assert.That(payload["health"]!.GetValue<string>(), Is.EqualTo("green"));
            var issues = payload["open_issues"] as JsonArray;
            Assert.That(issues, Is.Not.Null, "an all-clear snapshot must still carry the list — the client treats a missing list as malformed, not as empty");
            Assert.That(issues, Is.Empty);
        }

        [Test]
        public void OpenIssues_MirrorTheIssueDetectedFieldSpelling() {
            var detected = new DateTimeOffset(2026, 7, 6, 20, 30, 0, TimeSpan.Zero);
            var id = Guid.NewGuid();
            var payload = WebSocketEndpoints.BuildDiagnosticsSnapshotPayload(State(
                new DiagnosticIssueDto(id, "guider.lost", DiagnosticHealth.Red,
                    "Guide star lost", detected, "check clouds", AutoCorrectible: false)));

            var issue = (JsonObject)((JsonArray)payload["open_issues"]!)[0]!;
            Assert.Multiple(() => {
                Assert.That(issue["id"]!.GetValue<string>(), Is.EqualTo(id.ToString()));
                Assert.That(issue["event_type"]!.GetValue<string>(), Is.EqualTo("guider.lost"));
                Assert.That(issue["severity"]!.GetValue<string>(), Is.EqualTo("red"));
                Assert.That(issue["description"]!.GetValue<string>(), Is.EqualTo("Guide star lost"));
                Assert.That(issue["detected_utc"]!.GetValue<string>(), Is.EqualTo(detected.ToString("O")));
                Assert.That(issue["recommended_action"]!.GetValue<string>(), Is.EqualTo("check clouds"));
                Assert.That(issue["auto_correctible"]!.GetValue<bool>(), Is.False);
            });
        }

        [Test]
        public void NullRecommendedAction_SerializesAsJsonNull() {
            var payload = WebSocketEndpoints.BuildDiagnosticsSnapshotPayload(State(
                new DiagnosticIssueDto(Guid.NewGuid(), "disk.low", DiagnosticHealth.Yellow,
                    "Low disk", DateTimeOffset.UtcNow, RecommendedAction: null, AutoCorrectible: true)));
            var issue = (JsonObject)((JsonArray)payload["open_issues"]!)[0]!;
            Assert.That(issue.ContainsKey("recommended_action"), Is.True);
            Assert.That(issue["recommended_action"], Is.Null);
        }

        private static readonly string[] AllSeverityTokens = { "green", "yellow", "red", "unknown" };

        [Test]
        public void SeverityTokens_AreTotalOverTheEnum() {
            var seen = new HashSet<string>();
            foreach (DiagnosticHealth health in Enum.GetValues<DiagnosticHealth>()) {
                // Must not throw for any current member (Unknown included) —
                // one odd row must never suppress the whole resync frame.
                seen.Add(DiagnosticHealthWire.Token(health));
            }
            Assert.That(seen, Is.EquivalentTo(AllSeverityTokens));
        }
    }
}
