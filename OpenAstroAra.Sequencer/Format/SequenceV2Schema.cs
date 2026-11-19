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
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OpenAstroAra.Sequencer.Format;

/// <summary>
/// Generates <c>docs/specs/sequence-v2.schema.json</c> (JSON Schema draft 2020-12) from the kind table and the
/// field tables, so the file cannot drift from the validator. One <c>$defs</c> object per kind, each with
/// <c>properties.kind.const</c> and <c>additionalProperties: false</c>. The file is the output of
/// <see cref="Generate"/>, LF line ends and a final newline; a test compares them, so a table change fails
/// until the file is regenerated. It states names and value kinds only: the node roles, the cross-references
/// (<c>targetId</c>) and the capturable rule are the validator's.
/// </summary>
public static class SequenceV2Schema {

    private const string Draft = "https://json-schema.org/draft/2020-12/schema";

    private static readonly string[] DocumentRequired = { "schemaVersion", "name", "targets", "root" };
    private static readonly string[] TargetRequired = { "id", "coordinates" };
    private static readonly string[] KindRequired = { "kind" };
    private static readonly string[] TargetNodeRequired = { "kind", "targetId" };
    private static readonly string[] UnknownRequired = { "kind", "role", "originalType", "originalPayload", "reason" };

    public static string Generate() {
        using var stream = new MemoryStream();
        var options = new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using (var w = new Utf8JsonWriter(stream, options)) {
            w.WriteStartObject();
            w.WriteString("$schema", Draft);
            w.WriteString("title", "OpenAstro Ara sequence v2");
            w.WriteString("type", "object");
            w.WriteBoolean("additionalProperties", false);
            w.WriteStartArray("required");
            foreach (var name in DocumentRequired) w.WriteStringValue(name);
            w.WriteEndArray();
            w.WriteStartObject("properties");
            WriteFields(w, SequenceV2Fields.Document, null);
            w.WriteEndObject();
            w.WriteStartObject("$defs");
            WriteDef(w, "target-element", null, SequenceV2Fields.Target, TargetRequired, withEngineClass: false);
            foreach (var entry in SequenceKinds.All) {
                WriteDef(w, entry.Kind, entry.Kind, SequenceV2Fields.For(entry), entry.Kind == "target" ? TargetNodeRequired : KindRequired,
                    withEngineClass: true, engineClass: entry.EngineClass.Name);
            }
            WriteDef(w, SequenceKinds.Unknown, SequenceKinds.Unknown, SequenceV2Fields.Unknown,
                UnknownRequired, withEngineClass: false);
            WriteDef(w, SequenceKinds.GloverExposure, SequenceKinds.GloverExposure, SequenceV2Fields.GloverExposure, KindRequired,
                withEngineClass: true, engineClass: null);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void WriteDef(Utf8JsonWriter w, string defName, string? kind, SequenceV2FieldSet fields, string[] required,
            bool withEngineClass, string? engineClass = null) {
        w.WriteStartObject(defName);
        w.WriteString("type", "object");
        if (withEngineClass) w.WriteString("x-ara-engine-class", engineClass);
        w.WriteBoolean("additionalProperties", false);
        w.WriteStartArray("required");
        foreach (var name in required) w.WriteStringValue(name);
        w.WriteEndArray();
        w.WriteStartObject("properties");
        WriteFields(w, fields, kind);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    // `kind` first, then the other names in ordinal order, so the file does not depend on declaration order.
    private static void WriteFields(Utf8JsonWriter w, SequenceV2FieldSet fields, string? kind) {
        foreach (var name in fields.OrderBy(n => n == "kind" ? "" : n, StringComparer.Ordinal)) {
            var field = fields.Get(name)!;
            w.WriteStartObject(name);
            if (name == "kind" && kind != null) {
                w.WriteString("const", kind);
            } else if (field.Type != null) {
                WriteType(w, field);
            }
            w.WriteEndObject();
        }
    }

    private static void WriteType(Utf8JsonWriter w, SequenceV2Field field) {
        var type = field.SchemaType!;
        if (!field.Nullable) {
            w.WriteString("type", type);
            return;
        }
        w.WriteStartArray("type");
        w.WriteStringValue(type);
        w.WriteStringValue("null");
        w.WriteEndArray();
    }
}