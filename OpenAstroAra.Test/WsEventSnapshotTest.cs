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
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace OpenAstroAra.Test {

    /// <summary>
    /// ADR 0014 — <c>OpenAstroAra.Server/ws-events.json</c> is the committed snapshot of the declared WebSocket
    /// events (catalog field name and wire token, plus the payload shape of an event that has a typed record, in <see cref="WsEventCatalog.All"/> order). The client's
    /// generated event names (scripts/generate-ws-events-dart.py) read it. Same pattern as openapi.yaml: this
    /// test fails when the file drifts from the declaration; refresh it locally with
    /// <c>OPENASTROARA_UPDATE_WS_EVENTS=1 dotnet test OpenAstroAra.Test --filter WsEventSnapshotTest</c>.
    /// </summary>
    [TestFixture]
    public class WsEventSnapshotTest {

        private const string UpdateEnvVar = "OPENASTROARA_UPDATE_WS_EVENTS";

        private static string SnapshotPath() {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent) {
                var candidate = Path.Combine(dir.FullName, "OpenAstroAra.Server", "ws-events.json");
                if (File.Exists(candidate) || File.Exists(Path.Combine(dir.FullName, "OpenAstroAra.sln"))) { return candidate; }
            }
            throw new FileNotFoundException("repo root (OpenAstroAra.sln) not found above " + AppContext.BaseDirectory);
        }

        // Field name (snake_case wire name), JSON type and nullability of an event's typed payload record, in
        // declaration order; empty for an event with no record (its payload stays hand-built).
        private static string PayloadShape(string token) {
            if (!WsEventCatalog.Payloads.TryGetValue(token, out var type)) { return ""; }
            var nullability = new NullabilityInfoContext();
            var fields = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => {
                var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                var json = t == typeof(string) ? "string"
                    : t == typeof(bool) ? "boolean"
                    : t == typeof(double) || t == typeof(float) || t == typeof(decimal) ? "number"
                    : t == typeof(int) || t == typeof(long) || t == typeof(short) ? "integer"
                    : throw new NotSupportedException($"{type.Name}.{p.Name}: no JSON type for {t}");
                var nullable = Nullable.GetUnderlyingType(p.PropertyType) != null
                    || nullability.Create(p).ReadState == NullabilityState.Nullable;
                return $"{{ \"field\": \"{JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name)}\", \"type\": \"{json}\", \"nullable\": {(nullable ? "true" : "false")} }}";
            });
            return ", \"payload\": [ " + string.Join(", ", fields) + " ]";
        }

        internal static string Render() {
            var names = typeof(WsEventCatalog).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(WsEvent))
                .ToDictionary(f => ((WsEvent)f.GetValue(null)!).Token, f => f.Name, StringComparer.Ordinal);
            var sb = new StringBuilder("{\n  \"events\": [\n");
            sb.AppendJoin(",\n", WsEventCatalog.All.Select(e => $"    {{ \"name\": \"{names[e.Token]}\", \"type\": \"{e.Token}\"{PayloadShape(e.Token)} }}"));
            return sb.Append("\n  ]\n}\n").ToString();
        }

        [Test]
        public void The_committed_snapshot_matches_the_declared_events() {
            var path = SnapshotPath();
            var generated = Render();
            if (Environment.GetEnvironmentVariable(UpdateEnvVar) == "1") {
                Assert.That(Environment.GetEnvironmentVariable("CI"), Is.Null.Or.Empty,
                    $"{UpdateEnvVar}=1 is for local refreshes only; CI must compare, not rewrite");
                File.WriteAllText(path, generated);
                Assert.Pass($"rewrote {path}");
            }
            Assert.That(File.Exists(path), "ws-events.json is missing; refresh with " + UpdateEnvVar + "=1");
            var committed = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.That(committed, Is.EqualTo(generated),
                "OpenAstroAra.Server/ws-events.json drifted from WsEventCatalog; refresh with "
                + UpdateEnvVar + "=1 dotnet test OpenAstroAra.Test --filter WsEventSnapshotTest, then rerun scripts/generate-ws-events-dart.py");
        }
    }
}