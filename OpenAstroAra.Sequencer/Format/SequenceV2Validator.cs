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
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace OpenAstroAra.Sequencer.Format;

/// <summary>One refusal: a stable <paramref name="Code"/>, the JSON path of the node or member (<c>$.root.items[2]</c>) and a message for a person.</summary>
public sealed record SequenceV2Error(string Code, string Path, string Message);

/// <summary>What the validator should hold a document to.</summary>
public sealed class SequenceV2ValidationOptions {

    /// <summary>True when the daemon has the <c>glover-exposure</c> class (follow-up (d)); false answers <c>unknown_kind</c> for it.</summary>
    public bool GloverExposureAvailable { get; init; }

    /// <summary>False waives the "one capturable instruction" rule, as v1's flag does for a template body.</summary>
    public bool RequireCapturableInstruction { get; init; } = true;
}

/// <summary>
/// The v2 validator (#2025 part A): every rule in the "Validation" section of <c>docs/specs/sequence-v2.md</c>.
/// It reads the kind table and the field table, writes nothing and logs nothing; a caller that logs a
/// client-supplied <c>Message</c> or <c>Path</c> escapes it (CWE-117). Nothing calls it on the upload route yet.
/// A hostile shape (a member of the wrong JSON kind) is one <c>wrong_type</c>, never an exception.
/// </summary>
public static class SequenceV2Validator {

    public const string SchemaVersion = "openastroara-sequence-v2";

    public const string BodyNotObject = "body_not_object";
    public const string SchemaVersionMismatch = "schema_version";
    public const string MissingField = "missing_field";
    public const string WrongType = "wrong_type";
    public const string UnknownField = "unknown_field";
    public const string UnknownKind = "unknown_kind";
    public const string WrongRole = "wrong_role";
    public const string RootKind = "root_kind";
    public const string MisplacedRoot = "misplaced_root";
    public const string DuplicateTargetId = "duplicate_target_id";
    public const string UnknownTarget = "unknown_target";
    public const string NoCapturableInstruction = "no_capturable_instruction";

    private const int MaxErrors = 100;
    private const int MaxQuoted = 64;

    private enum ListKind { Items, Conditions, Triggers, Run }

    private static readonly string[] PlaceholderRequired = { "role", "originalType", "originalPayload", "reason" };
    private static readonly string[] RunOnly = { "run" };

    private static readonly string[] CapturableKinds = { "capture", "flat-panel-flats", "sky-flats", SequenceKinds.GloverExposure };

    public static IReadOnlyList<SequenceV2Error> Validate(JsonElement body, SequenceV2ValidationOptions? options = null) =>
        new Walk(options ?? new SequenceV2ValidationOptions()).Run(body);

    private static bool Accepts(ListKind list, SequenceNodeRole role) => list switch {
        ListKind.Items => role is SequenceNodeRole.Container or SequenceNodeRole.Instruction,
        ListKind.Conditions => role == SequenceNodeRole.Condition,
        ListKind.Triggers => role == SequenceNodeRole.Trigger,
        _ => role == SequenceNodeRole.Instruction,
    };

    // The `role` tokens of an `unknown` placeholder.
    private static readonly Dictionary<string, SequenceNodeRole> Roles = new(StringComparer.Ordinal) {
        ["container"] = SequenceNodeRole.Container, ["instruction"] = SequenceNodeRole.Instruction,
        ["condition"] = SequenceNodeRole.Condition, ["trigger"] = SequenceNodeRole.Trigger,
    };

    private static string RoleName(SequenceNodeRole role) => Roles.First(r => r.Value == role).Key;

    private static string Quote(string clientText) => clientText.Length <= MaxQuoted ? clientText : clientText[..MaxQuoted];

    private static string Index(string path, int i) => path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";

    private sealed class Walk(SequenceV2ValidationOptions options) {

        private readonly List<SequenceV2Error> errors = new();
        private readonly HashSet<string> targetIds = new(StringComparer.Ordinal);
        private bool capturable;

        private void Add(string code, string path, string message) {
            if (errors.Count < MaxErrors) errors.Add(new SequenceV2Error(code, path, message));
        }

        public List<SequenceV2Error> Run(JsonElement body) {
            if (body.ValueKind != JsonValueKind.Object) {
                Add(BodyNotObject, "$", "the sequence body must be a JSON object");
                return errors;
            }
            Document(body);
            return errors;
        }

        private void Document(JsonElement doc) {
            CheckMembers(doc, SequenceV2Fields.Document, "$", "the document", "schemaVersion", "targets", "root");
            if (!doc.TryGetProperty("schemaVersion", out var version)) {
                Add(MissingField, "$", "missing field 'schemaVersion'");
            } else if (version.ValueKind != JsonValueKind.String) {
                Add(WrongType, "$.schemaVersion", "'schemaVersion' must be a string");
            } else if (version.GetString() != SchemaVersion) {
                Add(SchemaVersionMismatch, "$.schemaVersion", "unrecognized schema version '" + Quote(version.GetString()!) + "', expected '" + SchemaVersion + "'");
            }
            Require(doc, "name", "$");
            if (!doc.TryGetProperty("targets", out var targets)) {
                Add(MissingField, "$", "missing field 'targets'");
            } else if (targets.ValueKind != JsonValueKind.Array) {
                Add(WrongType, "$.targets", "'targets' must be an array");
            } else {
                var i = 0;
                foreach (var target in targets.EnumerateArray()) Target(target, Index("$.targets", i++));
            }
            if (!doc.TryGetProperty("root", out var root)) {
                Add(MissingField, "$", "missing field 'root'");
            } else {
                Root(root);
            }
        }

        private void Require(JsonElement obj, string name, string path) {
            if (!obj.TryGetProperty(name, out _)) Add(MissingField, path, "missing field '" + name + "'");
        }

        // The members this node's table knows: unknown_field for the others, wrong_type for a value of the wrong JSON kind.
        // Members named in `skip` are walked by the caller.
        private void CheckMembers(JsonElement node, SequenceV2FieldSet fields, string path, string what, params string[] skip) {
            foreach (var member in node.EnumerateObject()) {
                if (skip.Contains(member.Name, StringComparer.Ordinal)) continue;
                var field = fields.Get(member.Name);
                if (field == null) {
                    Add(UnknownField, path, "unknown field '" + Quote(member.Name) + "' on " + what);
                    continue;
                }
                if (!Fits(member.Value, field)) Add(WrongType, path + "." + member.Name, "'" + Quote(member.Name) + "' must be " + Describe(field));
            }
        }

        private static string Describe(SequenceV2Field field) =>
            "of JSON type " + field.SchemaType + (field.Nullable ? " or null" : "");

        private static bool Fits(JsonElement value, SequenceV2Field field) {
            if (value.ValueKind == JsonValueKind.Null) return field.Nullable || field.Type == null;
            return field.Type switch {
                null => true,
                SequenceV2ValueType.Text => value.ValueKind == JsonValueKind.String,
                SequenceV2ValueType.Number => value.ValueKind == JsonValueKind.Number,
                SequenceV2ValueType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                SequenceV2ValueType.Array => value.ValueKind == JsonValueKind.Array,
                SequenceV2ValueType.Map => value.ValueKind == JsonValueKind.Object,
                _ => value.ValueKind == JsonValueKind.Number && FitsInteger(value, field.ClrType),
            };
        }

        private static bool FitsInteger(JsonElement value, Type? clr) {
            if (clr == typeof(sbyte)) return value.TryGetSByte(out _);
            if (clr == typeof(byte)) return value.TryGetByte(out _);
            if (clr == typeof(short)) return value.TryGetInt16(out _);
            if (clr == typeof(ushort)) return value.TryGetUInt16(out _);
            if (clr == typeof(int)) return value.TryGetInt32(out _);
            if (clr == typeof(uint)) return value.TryGetUInt32(out _);
            if (clr == typeof(ulong)) return value.TryGetUInt64(out _);
            return value.TryGetInt64(out _);
        }

        private void Target(JsonElement target, string path) {
            if (target.ValueKind != JsonValueKind.Object) {
                Add(WrongType, path, "a target must be an object");
                return;
            }
            CheckMembers(target, SequenceV2Fields.Target, path, "a target");
            if (!target.TryGetProperty("id", out var id)) {
                Add(MissingField, path, "missing field 'id'");
            } else if (id.ValueKind == JsonValueKind.String && !targetIds.Add(id.GetString()!)) {
                Add(DuplicateTargetId, path, "duplicate target id '" + Quote(id.GetString()!) + "'");
            }
            Require(target, "coordinates", path);
        }

        private void Root(JsonElement root) {
            const string path = "$.root";
            if (root.ValueKind != JsonValueKind.Object) {
                Add(WrongType, path, "'root' must be an object");
                return;
            }
            if (!root.TryGetProperty("kind", out var kind)) {
                Add(MissingField, path, "missing field 'kind'");
                return;
            }
            if (kind.ValueKind != JsonValueKind.String) {
                Add(WrongType, path + ".kind", "'kind' must be a string");
                return;
            }
            if (kind.GetString() != SequenceKinds.Root) {
                Add(RootKind, path, "the document's root must have kind 'root', not '" + Quote(kind.GetString()!) + "'");
                return;
            }
            Container(root, SequenceKinds.ForKind(SequenceKinds.Root)!, path, reachable: true);
            if (options.RequireCapturableInstruction && !capturable) {
                Add(NoCapturableInstruction, path, "the sequence needs at least one capturable instruction (capture, flat-panel-flats or sky-flats) reachable through items");
            }
        }

        // `reachable`: this node is reached from the root through `items` only, so a capture in it counts as the sequence's imaging.
        private void Node(JsonElement node, string path, ListKind list, bool reachable) {
            if (node.ValueKind != JsonValueKind.Object) {
                Add(WrongType, path, "a node must be an object");
                return;
            }
            if (!node.TryGetProperty("kind", out var kindValue)) {
                Add(MissingField, path, "missing field 'kind'");
                return;
            }
            if (kindValue.ValueKind != JsonValueKind.String) {
                Add(WrongType, path + ".kind", "'kind' must be a string");
                return;
            }
            var kind = kindValue.GetString()!;
            if (kind == SequenceKinds.Root) {
                Add(MisplacedRoot, path, "'root' is only valid as the document's root");
                return;
            }
            if (kind == SequenceKinds.Unknown) {
                Placeholder(node, path, list);
                return;
            }
            if (kind == SequenceKinds.GloverExposure) {
                if (!options.GloverExposureAvailable) {
                    Add(UnknownKind, path, "unknown kind '" + Quote(kind) + "': this daemon has no such instruction");
                    return;
                }
                if (!Accepts(list, SequenceNodeRole.Instruction)) {
                    Add(WrongRole, path, "kind '" + kind + "' is an instruction and cannot sit in " + ListName(list));
                    return;
                }
                CheckMembers(node, SequenceV2Fields.GloverExposure, path, "kind '" + kind + "'");
                capturable |= reachable;
                return;
            }
            var entry = SequenceKinds.ForKind(kind);
            if (entry == null) {
                Add(UnknownKind, path, "unknown kind '" + Quote(kind) + "'");
                return;
            }
            if (!Accepts(list, entry.Role)) {
                Add(WrongRole, path, "kind '" + kind + "' is a " + RoleName(entry.Role) + " and cannot sit in " + ListName(list));
                return;
            }
            if (entry.Role == SequenceNodeRole.Container) {
                Container(node, entry, path, reachable);
                return;
            }
            if (reachable && CapturableKinds.Contains(kind, StringComparer.Ordinal)) capturable = true;
            var fields = SequenceV2Fields.For(entry);
            var isTrigger = entry.Role == SequenceNodeRole.Trigger;
            CheckMembers(node, fields, path, "kind '" + kind + "'", isTrigger ? RunOnly : Array.Empty<string>());
            if (isTrigger && node.TryGetProperty("run", out var run)) Items(run, path + ".run", ListKind.Run, false);
        }

        private void Container(JsonElement node, SequenceKindEntry entry, string path, bool reachable) {
            var kind = entry.Kind;
            var fields = SequenceV2Fields.For(entry);
            CheckMembers(node, fields, path, "kind '" + kind + "'", "items", "conditions", "triggers");
            if (kind == "target") Resolve(node, path);
            if (node.TryGetProperty("conditions", out var conditions)) Items(conditions, path + ".conditions", ListKind.Conditions, false);
            if (node.TryGetProperty("triggers", out var triggers)) Items(triggers, path + ".triggers", ListKind.Triggers, false);
            if (node.TryGetProperty("items", out var items)) Items(items, path + ".items", ListKind.Items, reachable);
        }

        // A target container names a target of the document by id.
        private void Resolve(JsonElement node, string path) {
            if (!node.TryGetProperty("targetId", out var id)) {
                Add(MissingField, path, "missing field 'targetId'");
            } else if (id.ValueKind == JsonValueKind.String && !targetIds.Contains(id.GetString()!)) {
                Add(UnknownTarget, path, "targetId '" + Quote(id.GetString()!) + "' is not the id of any target");
            }
        }

        private void Items(JsonElement list, string path, ListKind kind, bool reachable) {
            if (list.ValueKind != JsonValueKind.Array) {
                Add(WrongType, path, "a node list must be an array");
                return;
            }
            var i = 0;
            foreach (var node in list.EnumerateArray()) Node(node, Index(path, i++), kind, reachable);
        }

        // The payload is opaque: it is not walked, so a capture inside it is neither counted nor checked.
        private void Placeholder(JsonElement node, string path, ListKind list) {
            CheckMembers(node, SequenceV2Fields.Unknown, path, "kind 'unknown'");
            foreach (var required in PlaceholderRequired) Require(node, required, path);
            if (!node.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String) return;
            if (!Roles.TryGetValue(role.GetString()!, out var parsed)) {
                Add(WrongRole, path, "unknown role '" + Quote(role.GetString()!) + "'");
            } else if (!Accepts(list, parsed)) {
                Add(WrongRole, path, "a " + role.GetString() + " placeholder cannot sit in " + ListName(list));
            }
        }

        private static string ListName(ListKind list) => list switch {
            ListKind.Items => "items",
            ListKind.Conditions => "conditions",
            ListKind.Triggers => "triggers",
            _ => "a trigger's run",
        };
    }
}