#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using OpenAstroAra.Sequencer.Format;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #2025 part A: the v2 validator holds every rule in the "Validation" section of
    /// `docs/specs/sequence-v2.md`. One test per rule, each failing when its rule is removed. Nothing
    /// on the upload route calls the validator in this part; `SequenceSchemaValidator` stays on v1.
    /// </summary>
    [TestFixture]
    public class SequenceV2ValidatorTest {

        private const string Capture = """{ "kind": "capture", "exposureTime": 60 }""";
        private const string Coordinates = """{ "raHours": 5, "raMinutes": 35, "raSeconds": 17.3, "negativeDec": true, "decDegrees": 5, "decMinutes": 23, "decSeconds": 28 }""";
        private const string T1 = """{ "id": "t1", "name": "M42", "coordinates": """ + Coordinates + """, "positionAngle": 0 }""";

        private static readonly string[] GloverOnly = { "glover-exposure" };

        private static void CollectKinds(JsonElement node, HashSet<string> kinds) {
            if (node.ValueKind == JsonValueKind.Object) {
                foreach (var p in node.EnumerateObject()) {
                    if (p.Name == "kind" && p.Value.ValueKind == JsonValueKind.String) kinds.Add(p.Value.GetString()!);
                    CollectKinds(p.Value, kinds);
                }
            } else if (node.ValueKind == JsonValueKind.Array) {
                foreach (var e in node.EnumerateArray()) CollectKinds(e, kinds);
            }
        }

        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

        private static string Document(string items, string targets = "") =>
            """{ "schemaVersion": "openastroara-sequence-v2", "name": "t", "targets": [ TARGETS ], "root": { "kind": "root", "items": [ ITEMS ] } }"""
                .Replace("TARGETS", targets, System.StringComparison.Ordinal).Replace("ITEMS", items, System.StringComparison.Ordinal);

        private static IReadOnlyList<SequenceV2Error> Check(string document, SequenceV2ValidationOptions? options = null) =>
            SequenceV2Validator.Validate(Parse(document), options);

        private static IReadOnlyList<SequenceV2Error> CheckItems(string items, string targets = "") => Check(Document(items, targets));

        private static List<(string Code, string Path)> Pairs(IEnumerable<SequenceV2Error> errors) => errors.Select(e => (e.Code, e.Path)).ToList();

        [Test]
        public void A_minimal_document_with_a_capture_is_valid() {
            Assert.That(CheckItems(Capture), Is.Empty);
            Assert.That(SequenceV2Validator.SchemaVersion, Is.EqualTo("openastroara-sequence-v2"));
        }

        [Test]
        public void The_spec_Example_validates_when_the_daemon_has_the_glover_class() {
            using var example = JsonDocument.Parse(SequenceV2Spec.Example());
            var kinds = new HashSet<string>();
            CollectKinds(example.RootElement, kinds);
            Assert.That(kinds.Where(k => SequenceKinds.ForKind(k) == null), Is.EquivalentTo(GloverOnly), "the Example uses only table kinds and glover-exposure");
            var errors = Check(SequenceV2Spec.Example(), new SequenceV2ValidationOptions { GloverExposureAvailable = true });
            Assert.That(errors, Is.Empty, string.Join("; ", errors.Select(e => e.Path + " " + e.Message)));
        }

        [Test]
        public void The_spec_Example_is_refused_as_an_unknown_kind_today_never_as_a_placeholder() {
            // glover-exposure has no engine class yet (follow-up (d)); a body that names it is a 422.
            var errors = Check(SequenceV2Spec.Example());
            Assert.That(Pairs(errors), Is.EqualTo(new[] { ("unknown_kind", "$.root.items[2].items[2].items[3]") }));
            Assert.That(errors[0].Message, Does.Contain("glover-exposure"));
        }

        // --- Validation rule: today's rules (object body, schema version) ---

        [TestCase("[]")]
        [TestCase("5")]
        [TestCase("null")]
        public void A_body_that_is_not_an_object_is_refused(string body) {
            Assert.That(Pairs(Check(body)), Is.EqualTo(new[] { ("body_not_object", "$") }));
        }

        [Test]
        public void A_missing_schemaVersion_is_refused() {
            var errors = Check("""{ "name": "t", "targets": [], "root": { "kind": "root", "items": [ """ + Capture + """ ] } }""");
            Assert.That(Pairs(errors), Is.EqualTo(new[] { ("missing_field", "$") }));
            Assert.That(errors[0].Message, Does.Contain("schemaVersion"));
        }

        [TestCase("openastroara-sequence-v1")]
        [TestCase("openastroara-sequence-v3")]
        public void Another_schemaVersion_is_refused(string version) {
            var doc = Document(Capture).Replace("openastroara-sequence-v2", version, System.StringComparison.Ordinal);
            Assert.That(Pairs(Check(doc)), Is.EqualTo(new[] { ("schema_version", "$.schemaVersion") }));
        }

        // --- Validation rule: every kind is in the tables ---

        [Test]
        public void A_kind_that_is_in_no_table_is_refused_with_its_node_path() {
            var errors = CheckItems(Capture + """, { "kind": "frobnicate" }""");
            Assert.That(Pairs(errors), Is.EqualTo(new[] { ("unknown_kind", "$.root.items[1]") }));
            Assert.That(errors[0].Message, Does.Contain("frobnicate"));
        }

        [Test]
        public void A_NET_class_name_is_not_a_kind() {
            Assert.That(Pairs(CheckItems(Capture + """, { "kind": "TakeExposure" }""")), Is.EqualTo(new[] { ("unknown_kind", "$.root.items[1]") }));
        }

        [Test]
        public void A_node_without_a_kind_is_refused() {
            Assert.That(Pairs(CheckItems(Capture + """, { "name": "x" }""")), Is.EqualTo(new[] { ("missing_field", "$.root.items[1]") }));
        }

        [TestCase("""{ "kind": "loop-iterations", "iterations": 3 }""", "$.root.items[0]")]
        [TestCase("""{ "kind": "meridian-flip" }""", "$.root.items[0]")]
        public void A_kind_in_the_wrong_list_for_its_role_is_refused(string node, string path) {
            Assert.That(Pairs(CheckItems(node + ", " + Capture)), Is.EqualTo(new[] { ("wrong_role", path) }));
        }

        [Test]
        public void A_instruction_in_conditions_or_triggers_and_a_container_in_run_are_wrong_role() {
            var doc = Document("""
                { "kind": "sequential",
                  "conditions": [ { "kind": "annotation" } ],
                  "triggers": [ { "kind": "dither-after-exposures", "run": [ { "kind": "sequential" } ] }, { "kind": "dither" } ],
                  "items": [ CAPTURE ] }
                """.Replace("CAPTURE", Capture, System.StringComparison.Ordinal));
            Assert.That(Pairs(Check(doc)), Is.EqualTo(new[] {
                ("wrong_role", "$.root.items[0].conditions[0]"),
                ("wrong_role", "$.root.items[0].triggers[0].run[0]"),
                ("wrong_role", "$.root.items[0].triggers[1]"),
            }));
        }

        [Test]
        public void An_items_value_that_is_not_an_array_of_objects_is_wrong_type_not_an_exception() {
            var doc = """{ "schemaVersion": "openastroara-sequence-v2", "name": "t", "targets": [], "root": { "kind": "root", "items": 5 } }""";
            Assert.That(Pairs(Check(doc, new SequenceV2ValidationOptions { RequireCapturableInstruction = false })), Is.EqualTo(new[] { ("wrong_type", "$.root.items") }));
            Assert.That(Pairs(CheckItems(Capture + ", 7")), Is.EqualTo(new[] { ("wrong_type", "$.root.items[1]") }));
        }

        // --- Validation rule: root is the only root ---

        [Test]
        public void A_second_root_inside_the_tree_is_refused() {
            Assert.That(Pairs(CheckItems(Capture + """, { "kind": "root" }""")), Is.EqualTo(new[] { ("misplaced_root", "$.root.items[1]") }));
        }

        [Test]
        public void The_document_root_must_be_kind_root() {
            var doc = Document(Capture).Replace("""{ "kind": "root", "items""", """{ "kind": "sequential", "items""", System.StringComparison.Ordinal);
            Assert.That(Pairs(Check(doc)), Is.EqualTo(new[] { ("root_kind", "$.root") }));
        }

        [Test]
        public void A_document_without_a_root_is_refused() {
            var errors = Check("""{ "schemaVersion": "openastroara-sequence-v2", "name": "t", "targets": [] }""");
            Assert.That(Pairs(errors), Is.EqualTo(new[] { ("missing_field", "$") }));
            Assert.That(errors[0].Message, Does.Contain("root"));
        }

        // --- Validation rule: a target's targetId is an id in targets ---

        [Test]
        public void A_target_container_with_a_resolving_targetId_is_valid() {
            Assert.That(CheckItems("""{ "kind": "target", "targetId": "t1", "items": [ """ + Capture + """ ] }""", T1), Is.Empty);
        }

        [Test]
        public void A_targetId_that_is_not_in_targets_is_refused() {
            var errors = CheckItems("""{ "kind": "target", "targetId": "t9", "items": [ """ + Capture + """ ] }""", T1);
            Assert.That(Pairs(errors), Is.EqualTo(new[] { ("unknown_target", "$.root.items[0]") }));
            Assert.That(errors[0].Message, Does.Contain("t9"));
        }

        [Test]
        public void A_target_container_without_a_targetId_is_refused() {
            Assert.That(Pairs(CheckItems("""{ "kind": "target", "items": [ """ + Capture + """ ] }""", T1)), Is.EqualTo(new[] { ("missing_field", "$.root.items[0]") }));
        }

        [Test]
        public void Two_targets_with_one_id_are_refused() {
            var errors = CheckItems(Capture, T1 + ", " + T1);
            Assert.That(Pairs(errors), Is.EqualTo(new[] { ("duplicate_target_id", "$.targets[1]") }));
        }

        [Test]
        public void A_target_without_an_id_or_coordinates_is_refused() {
            Assert.That(Pairs(CheckItems(Capture, """{ "name": "M42", "coordinates": """ + Coordinates + " }")), Is.EqualTo(new[] { ("missing_field", "$.targets[0]") }));
            Assert.That(Pairs(CheckItems(Capture, """{ "id": "t1" }""")), Is.EqualTo(new[] { ("missing_field", "$.targets[0]") }));
        }

        // --- Validation rule: capturable ---

        [TestCase("""{ "kind": "capture", "exposureTime": 60 }""")]
        [TestCase("""{ "kind": "flat-panel-flats" }""")]
        [TestCase("""{ "kind": "sky-flats" }""")]
        public void A_capture_flat_panel_flats_or_sky_flats_node_is_capturable(string node) {
            Assert.That(CheckItems("""{ "kind": "annotation" }, { "kind": "sequential", "items": [ """ + node + " ] }"), Is.Empty);
        }

        [Test]
        public void A_glover_exposure_node_is_capturable_when_the_class_exists() {
            var doc = Document("""{ "kind": "glover-exposure", "startSubSeconds": 180 }""");
            Assert.That(Check(doc, new SequenceV2ValidationOptions { GloverExposureAvailable = true }), Is.Empty);
        }

        [Test]
        public void A_tree_with_no_capturable_instruction_is_refused() {
            var errors = CheckItems("""{ "kind": "annotation", "text": "x" }, { "kind": "slew-radec" }""");
            Assert.That(Pairs(errors), Is.EqualTo(new[] { ("no_capturable_instruction", "$.root") }));
        }

        [Test]
        public void A_capture_only_inside_a_trigger_run_or_a_placeholder_is_not_capturable() {
            var trigger = """{ "kind": "sequential", "items": [ { "kind": "annotation" } ], "triggers": [ { "kind": "dither-after-exposures", "run": [ """ + Capture + " ] } ] }";
            Assert.That(Pairs(CheckItems(trigger)), Is.EqualTo(new[] { ("no_capturable_instruction", "$.root") }));
            var placeholder = """{ "kind": "unknown", "role": "instruction", "originalType": "X", "originalPayload": {}, "reason": "r" }""";
            Assert.That(Pairs(CheckItems(placeholder)), Is.EqualTo(new[] { ("no_capturable_instruction", "$.root") }));
        }

        [Test]
        public void The_capturable_rule_can_be_waived_for_a_template_body() {
            Assert.That(CheckItems("""{ "kind": "annotation" }"""), Is.Not.Empty);
            Assert.That(Check(Document("""{ "kind": "annotation" }"""), new SequenceV2ValidationOptions { RequireCapturableInstruction = false }), Is.Empty);
        }

        // --- Validation rule: no unknown fields ---

        [Test]
        public void An_unknown_field_on_a_node_is_refused_with_the_node_path_and_the_field_name() {
            var errors = CheckItems("""{ "kind": "capture", "exposureTim": 60 }""");
            Assert.That(Pairs(errors), Is.EqualTo(new[] { ("unknown_field", "$.root.items[0]") }));
            Assert.That(errors[0].Message, Does.Contain("exposureTim"));
        }

        [Test]
        public void A_field_of_another_kind_is_unknown_on_this_one() {
            Assert.That(Pairs(CheckItems("""{ "kind": "annotation", "exposureTime": 60 }, """ + Capture)), Is.EqualTo(new[] { ("unknown_field", "$.root.items[0]") }));
        }

        [Test]
        public void An_unknown_field_is_refused_on_conditions_triggers_and_run_nodes_too() {
            var doc = Document("""
                { "kind": "sequential",
                  "conditions": [ { "kind": "loop-iterations", "iteratons": 3 } ],
                  "triggers": [ { "kind": "dither-after-exposures", "afterExposure": 3, "run": [ { "kind": "dither", "x": 1 } ] } ],
                  "items": [ CAPTURE ] }
                """.Replace("CAPTURE", Capture, System.StringComparison.Ordinal));
            Assert.That(Pairs(Check(doc)), Is.EqualTo(new[] {
                ("unknown_field", "$.root.items[0].conditions[0]"),
                ("unknown_field", "$.root.items[0].triggers[0]"),
                ("unknown_field", "$.root.items[0].triggers[0].run[0]"),
            }));
        }

        [Test]
        public void An_unknown_top_level_or_target_field_is_refused() {
            var top = Document(Capture).Replace("\"name\": \"t\",", "\"name\": \"t\", \"nmae\": 1,", System.StringComparison.Ordinal);
            Assert.That(Pairs(Check(top)), Is.EqualTo(new[] { ("unknown_field", "$") }));
            var target = T1.Replace("\"positionAngle\"", "\"positonAngle\"", System.StringComparison.Ordinal);
            Assert.That(Pairs(CheckItems(Capture, target)), Is.EqualTo(new[] { ("unknown_field", "$.targets[0]") }));
        }

        [Test]
        public void The_engine_never_stores_a_pause_gate_or_an_item_name_so_v2_refuses_them() {
            // Spec fix (same PR): SequenceRootContainer.PauseGate carries no [JsonProperty], and only
            // containers persist a Name; the spec table said otherwise.
            var root = Document(Capture).Replace("\"kind\": \"root\",", "\"kind\": \"root\", \"pauseGate\": {},", System.StringComparison.Ordinal);
            Assert.That(Pairs(Check(root)), Is.EqualTo(new[] { ("unknown_field", "$.root") }));
            Assert.That(Pairs(CheckItems("""{ "kind": "capture", "name": "L" }""")), Is.EqualTo(new[] { ("unknown_field", "$.root.items[0]") }));
            Assert.That(CheckItems("""{ "kind": "sequential", "name": "Filter loop", "items": [ """ + Capture + " ] }"), Is.Empty);
        }

        // --- Validation rule: the unknown placeholder ---

        [Test]
        public void A_complete_placeholder_is_valid_in_every_list_its_role_names() {
            var doc = Document("""
                CAPTURE
                , { "kind": "unknown", "role": "container", "name": "X", "originalType": "X", "originalPayload": { "a": 1 }, "reason": "no kind" }
                , { "kind": "sequential",
                    "conditions": [ { "kind": "unknown", "role": "condition", "originalType": "C", "originalPayload": {}, "reason": "r" } ],
                    "triggers": [ { "kind": "unknown", "role": "trigger", "originalType": "T", "originalPayload": {}, "reason": "r" } ],
                    "items": [ { "kind": "unknown", "role": "instruction", "originalType": "", "originalPayload": {}, "reason": "r" } ] }
                """.Replace("CAPTURE", Capture, System.StringComparison.Ordinal));
            Assert.That(Check(doc), Is.Empty);
        }

        [TestCase("originalType")]
        [TestCase("originalPayload")]
        [TestCase("reason")]
        [TestCase("role")]
        public void A_placeholder_without_a_required_field_is_refused(string missing) {
            var fields = new Dictionary<string, string> {
                ["role"] = "\"instruction\"", ["originalType"] = "\"X\"", ["originalPayload"] = "{}", ["reason"] = "\"r\"",
            };
            fields.Remove(missing);
            var node = "{ \"kind\": \"unknown\", " + string.Join(", ", fields.Select(f => "\"" + f.Key + "\": " + f.Value)) + " }";
            var errors = CheckItems(Capture + ", " + node);
            Assert.That(Pairs(errors), Is.EqualTo(new[] { ("missing_field", "$.root.items[1]") }));
            Assert.That(errors[0].Message, Does.Contain(missing));
        }

        [Test]
        public void A_placeholder_whose_role_does_not_match_its_list_is_refused() {
            var node = """{ "kind": "unknown", "role": "condition", "originalType": "X", "originalPayload": {}, "reason": "r" }""";
            Assert.That(Pairs(CheckItems(Capture + ", " + node)), Is.EqualTo(new[] { ("wrong_role", "$.root.items[1]") }));
        }

        [Test]
        public void A_placeholder_refuses_unknown_fields_too() {
            var node = """{ "kind": "unknown", "role": "instruction", "originalType": "X", "originalPayload": {}, "reason": "r", "extra": 1 }""";
            Assert.That(Pairs(CheckItems(Capture + ", " + node)), Is.EqualTo(new[] { ("unknown_field", "$.root.items[1]") }));
        }

        // --- What the validator returns ---

        [Test]
        public void Every_error_is_reported_in_document_order_not_only_the_first() {
            var errors = CheckItems("""{ "kind": "capture", "exposureTim": 1 }, { "kind": "frobnicate" }, { "kind": "target", "targetId": "t9" }""");
            Assert.That(Pairs(errors), Is.EqualTo(new[] {
                ("unknown_field", "$.root.items[0]"), ("unknown_kind", "$.root.items[1]"), ("unknown_target", "$.root.items[2]"),
            }));
            Assert.That(errors.All(e => !string.IsNullOrEmpty(e.Message)), Is.True);
        }

        [Test]
        public void Part_A_changes_no_behaviour_the_upload_validator_is_still_v1() {
            // Characterization, green before and after: the route's validator keeps v1 and refuses a v2 body.
            Assert.That(SequenceSchemaValidator.SchemaVersion, Is.EqualTo("openastroara-sequence-v1"));
            var (valid, reason) = SequenceSchemaValidator.Validate(Parse(Document(Capture)));
            Assert.That(valid, Is.False);
            Assert.That(reason, Does.Contain("unrecognized schema version"));
        }
    }
}