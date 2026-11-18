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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Newtonsoft.Json;
using NUnit.Framework;
using OpenAstroAra.Sequencer.Format;

namespace OpenAstroAra.Test {

    /// <summary>
    /// The exhaustive per-kind field table (#2025 part A) is pinned against the engine's
    /// `[JsonProperty]` set, against the spec's field cells and against the checked-in JSON Schema.
    /// A field added to an engine class without a table row fails the first test.
    /// </summary>
    [TestFixture]
    public class SequenceV2FieldTableTest {

        // The v2 names the spec's "Renamed fields" section gives, and the engine properties no v2
        // field carries (the tree gives Parent; the kind decides the strategy).
        private static readonly Dictionary<string, string> Renamed = new() {
            ["IsExpanded"] = "expanded", ["TriggerRunner"] = "run", ["SelectedProvider"] = "provider", ["Target"] = "targetId",
            ["USBLimit"] = "usbLimit",
        };

        private static readonly string[] UnknownFields = { "kind", "name", "role", "originalType", "originalPayload", "reason" };

        private static readonly string[] GloverFields = {
            "kind", "attempts", "errorBehavior", "startSubSeconds", "minSubSeconds", "maxSubSeconds", "measureFrames", "swampFactor",
            "frameCount", "gain", "offset", "binning", "imageType",
        };

        private static readonly string[] ExtraKinds = { "unknown", "glover-exposure" };

        private static readonly HashSet<string> NotStored = new() { "Parent", "Strategy" };

        private static string LowerCamel(string name) => char.ToLowerInvariant(name[0]) + name.Substring(1);

        private static HashSet<string> EngineFields(Type type) {
            var names = new HashSet<string> { "kind" };
            for (var t = type; t != null && t != typeof(object); t = t.BaseType) {
                foreach (var member in t.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                    var attribute = member.GetCustomAttribute<JsonPropertyAttribute>(true);
                    if (attribute == null) continue;
                    var engineName = attribute.PropertyName ?? member.Name;
                    if (NotStored.Contains(engineName)) continue;
                    names.Add(Renamed.TryGetValue(engineName, out var v2) ? v2 : LowerCamel(engineName));
                }
            }
            return names;
        }

        [Test]
        public void The_field_table_equals_the_engine_JsonProperty_set_for_every_kind() {
            Assert.That(SequenceKinds.All, Has.Count.EqualTo(67), "the table covers every class WithDefaults registers");
            foreach (var entry in SequenceKinds.All) {
                Assert.That(SequenceV2Fields.For(entry).OrderBy(f => f, StringComparer.Ordinal),
                    Is.EqualTo(EngineFields(entry.EngineClass).OrderBy(f => f, StringComparer.Ordinal)),
                    entry.Kind + " (" + entry.EngineClass.Name + ")");
            }
        }

        [Test]
        public void The_placeholder_and_glover_field_sets_are_the_spec_ones() {
            Assert.That(SequenceV2Fields.Unknown, Is.EquivalentTo(UnknownFields));
            Assert.That(SequenceV2Fields.GloverExposure, Is.EquivalentTo(GloverFields));
        }

        [Test]
        public void Every_field_in_the_table_is_named_in_the_spec_row_of_its_kind() {
            var common = new HashSet<string> { "kind", "name", "errorBehavior", "attempts", "items", "conditions", "triggers", "expanded", "run" };
            var rows = SequenceV2Spec.KindRows().ToDictionary(r => r.Kind);
            var missing = new List<string>();
            Assert.That(SequenceKinds.All, Has.Count.EqualTo(67));
            foreach (var entry in SequenceKinds.All) {
                foreach (var field in SequenceV2Fields.For(entry).Where(f => !common.Contains(f))) {
                    if (!rows[entry.Kind].FieldsCell.Contains("`" + field + "`", StringComparison.Ordinal)) {
                        missing.Add(entry.Kind + "." + field);
                    }
                }
            }
            Assert.That(missing, Is.Empty, "a field the spec does not list goes into the spec in the same PR (Known limits)");
        }

        [Test]
        public void The_spec_promises_no_pause_gate_field_the_engine_does_not_store() {
            var root = SequenceV2Spec.KindRows().Single(r => r.Kind == "root");
            Assert.That(root.FieldsCell, Does.Not.Contain("pauseGate"));
            Assert.That(SequenceV2Fields.For(SequenceKinds.ForKind("root")!), Does.Not.Contain("pauseGate"));
        }

        // --- The machine-readable schema ---

        [Test]
        public void The_checked_in_schema_is_the_one_generated_from_the_tables() {
            var path = SequenceV2Spec.RepoFile("docs/specs/sequence-v2.schema.json");
            Assert.That(File.Exists(path), Is.True, "docs/specs/sequence-v2.schema.json is checked in");
            Assert.That(File.ReadAllText(path).Replace("\r\n", "\n", System.StringComparison.Ordinal), Is.EqualTo(SequenceV2Schema.Generate()),
                "regenerate with the generator in SequenceV2Schema when a table changes");
        }

        [Test]
        public void The_schema_names_every_kind_with_exactly_its_fields_and_refuses_the_rest() {
            using var schema = JsonDocument.Parse(SequenceV2Schema.Generate());
            var defs = schema.RootElement.GetProperty("$defs");
            var seen = new Dictionary<string, HashSet<string>>();
            foreach (var def in defs.EnumerateObject()) {
                if (!def.Value.TryGetProperty("properties", out var props) || !props.TryGetProperty("kind", out var kind)) continue;
                Assert.That(def.Value.GetProperty("additionalProperties").GetBoolean(), Is.False, def.Name + " refuses unknown fields");
                seen[kind.GetProperty("const").GetString()!] = props.EnumerateObject().Select(p => p.Name).ToHashSet();
            }
            foreach (var entry in SequenceKinds.All) {
                Assert.That(seen.ContainsKey(entry.Kind), Is.True, entry.Kind + " is in the schema");
                Assert.That(seen[entry.Kind], Is.EquivalentTo(SequenceV2Fields.For(entry)), entry.Kind);
            }
            Assert.That(seen.Keys, Is.EquivalentTo(SequenceKinds.All.Select(e => e.Kind).Concat(ExtraKinds)));
            Assert.That(schema.RootElement.GetProperty("$schema").GetString(), Is.EqualTo("https://json-schema.org/draft/2020-12/schema"));
        }
    }
}