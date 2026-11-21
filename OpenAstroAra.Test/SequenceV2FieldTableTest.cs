#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NUnit.Framework;
using OpenAstroAra.Sequencer.Format;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

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

        // The write set: what Newtonsoft's contract writes for the class (readable, not ignored). The
        // setter-only NINA shims (`Comparator`, `UserSunAltitude`, ... on the altitude base classes) are
        // readable-only-by-old-files members that the engine never writes, so they are not v2 fields.
        private static HashSet<string> EngineFields(Type type) {
            var contract = (JsonObjectContract)new DefaultContractResolver().ResolveContract(type);
            var names = new HashSet<string> { "kind" };
            foreach (var property in contract.Properties.Where(p => p.Readable && !p.Ignored)) {
                var engineName = property.PropertyName!;
                if (NotStored.Contains(engineName)) continue;
                names.Add(Renamed.TryGetValue(engineName, out var v2) ? v2 : LowerCamel(engineName));
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
        public void The_setter_only_NINA_shims_are_not_fields_of_the_altitude_kinds() {
            // WaitForAltitudeBase / LoopForAltitudeBase carry write-only [JsonProperty] shims so old NINA
            // files still read; the engine never writes them. The part B importer folds them into `data`.
            foreach (var kind in new[] { "wait-for-sun-altitude", "loop-while-above-horizon", "loop-while-altitude" }) {
                var fields = SequenceV2Fields.For(SequenceKinds.ForKind(kind)!);
                Assert.That(fields, Does.Contain("data"), kind);
                Assert.That(fields, Does.Not.Contain("comparator").And.Not.Contain("userSunAltitude").And.Not.Contain("altitudeOffset")
                    .And.Not.Contain("userMoonAltitude").And.Not.Contain("coordinates").And.Not.Contain("altitude"), kind);
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

        // The ExposureInfo row keys the `target` cell describes; they are nested keys, not node fields.
        private static readonly HashSet<string> ExposureInfoKeys = new() {
            "count", "filter", "exposureTime", "gain", "offset", "imageType", "binningX", "binningY", "roi",
        };

        [Test]
        public void Every_field_the_spec_row_names_is_in_the_table_of_its_kind() {
            // A backticked lowerCamel token directly followed by " (" in a field cell names a field
            // (`exposureTime` (double, ...)); values such as `Sidereal`, `LIGHT` and `-1` do not match.
            var token = new System.Text.RegularExpressions.Regex("`([a-z][A-Za-z0-9]*)` \\(");
            var stray = new List<string>();
            Assert.That(SequenceKinds.All, Has.Count.EqualTo(67));
            foreach (var row in SequenceV2Spec.KindRows()) {
                var fields = SequenceV2Fields.For(SequenceKinds.ForKind(row.Kind)!);
                foreach (System.Text.RegularExpressions.Match m in token.Matches(row.FieldsCell)) {
                    var name = m.Groups[1].Value;
                    if (row.Kind == "target" && ExposureInfoKeys.Contains(name)) continue;
                    if (!fields.Contains(name)) stray.Add(row.Kind + "." + name);
                }
            }
            Assert.That(stray, Is.Empty, "the spec promises a field the engine does not store");
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

        [Test]
        public void The_schema_carries_each_fields_type_and_null_from_the_same_mapping_as_the_validator() {
            using var schema = JsonDocument.Parse(SequenceV2Schema.Generate());
            var defs = schema.RootElement.GetProperty("$defs");
            static string Types(JsonElement prop) => string.Join(",", prop.GetProperty("type").ValueKind == JsonValueKind.Array
                ? prop.GetProperty("type").EnumerateArray().Select(t => t.GetString()!).OrderBy(t => t, StringComparer.Ordinal)
                : new[] { prop.GetProperty("type").GetString()! });
            JsonElement Prop(string kind, string field) {
                foreach (var def in defs.EnumerateObject()) {
                    if (def.Value.TryGetProperty("properties", out var props) && props.TryGetProperty("kind", out var k)
                        && k.GetProperty("const").GetString() == kind) return props.GetProperty(field);
                }
                throw new KeyNotFoundException(kind);
            }
            Assert.That(Types(Prop("capture", "exposureTime")), Is.EqualTo("number"));
            Assert.That(Types(Prop("capture", "gain")), Is.EqualTo("integer"));
            Assert.That(Types(Prop("capture", "calibrationTarget")), Is.EqualTo("null,string"));
            Assert.That(Types(Prop("slew-radec", "center")), Is.EqualTo("boolean,null"));
            Assert.That(Types(Prop("slew-radec", "coordinates")), Is.EqualTo("object"));
            Assert.That(Types(Prop("target", "targetId")), Is.EqualTo("string"));
            Assert.That(Types(Prop("dither-after-exposures", "run")), Is.EqualTo("array"));
            Assert.That(Types(Prop("sequential", "items")), Is.EqualTo("array"));
            // Every field of an engine kind carries a type (the glover fields are names only until follow-up (d)).
            foreach (var entry in SequenceKinds.All) {
                foreach (var field in SequenceV2Fields.For(entry).Where(f => f != "kind")) {
                    Assert.That(Prop(entry.Kind, field).TryGetProperty("type", out _), Is.True, entry.Kind + "." + field);
                }
            }
        }
    }
}