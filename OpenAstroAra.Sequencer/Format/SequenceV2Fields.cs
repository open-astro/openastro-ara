#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json.Serialization;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace OpenAstroAra.Sequencer.Format;

/// <summary>The JSON value kind a v2 field takes: a string, a whole number (which also fits its CLR type: <c>short</c>, <c>int</c>, <c>long</c>), a number, a boolean, an array or an object.</summary>
public enum SequenceV2ValueType {
    Text,
    WholeNumber,
    Number,
    Boolean,
    Array,
    Map,
}

/// <summary>
/// One field of a v2 node. <paramref name="Type"/> is null when the field is a name only (the provisional
/// <c>glover-exposure</c> set); <paramref name="ClrType"/> is the engine property's type for an integer field, else null.
/// </summary>
public sealed record SequenceV2Field(string Name, SequenceV2ValueType? Type, bool Nullable, Type? ClrType = null) {

    /// <summary>The JSON Schema <c>type</c> word for <see cref="Type"/>, or null for a name-only field.</summary>
    public string? SchemaType => Type switch {
        SequenceV2ValueType.Text => "string",
        SequenceV2ValueType.WholeNumber => "integer",
        SequenceV2ValueType.Number => "number",
        SequenceV2ValueType.Boolean => "boolean",
        SequenceV2ValueType.Array => "array",
        SequenceV2ValueType.Map => "object",
        _ => null,
    };
}

/// <summary>The fields a node may carry, by name. Enumerates the names.</summary>
public sealed class SequenceV2FieldSet : IReadOnlyCollection<string> {

    private readonly Dictionary<string, SequenceV2Field> fields;

    public SequenceV2FieldSet(IEnumerable<SequenceV2Field> fields) {
        this.fields = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
    }

    public int Count => fields.Count;

    public bool Contains(string name) => fields.ContainsKey(name);

    public SequenceV2Field? Get(string name) => fields.GetValueOrDefault(name);

    public IEnumerator<string> GetEnumerator() => fields.Keys.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// The exhaustive field table of every v2 node (#2025). The names of an engine kind are computed from the
/// engine class's Newtonsoft contract, not typed here: the set Newtonsoft writes (readable, not ignored),
/// minus <c>Parent</c> and <c>Strategy</c> (the tree and the kind decide them), renamed per the spec's
/// "Renamed fields", plus <c>kind</c>. The setter-only NINA shims on the altitude classes are not written,
/// so they are not fields. A field added to an engine class therefore appears here, in the checked-in schema
/// and in the spec-pinning tests without an edit to this file.
/// </summary>
public static class SequenceV2Fields {

    // The v2 names the spec's "Renamed fields" section gives; every other name is the lowerCamelCase of the property.
    private static readonly Dictionary<string, string> Renamed = new(StringComparer.Ordinal) {
        ["IsExpanded"] = "expanded", ["TriggerRunner"] = "run", ["SelectedProvider"] = "provider", ["Target"] = "targetId",
        ["USBLimit"] = "usbLimit",
    };

    private static readonly HashSet<string> NotStored = new(StringComparer.Ordinal) { "Parent", "Strategy" };

    // v2 types that differ from the CLR declaration: a provider is a name string, a target is its id, a trigger's run is a list of nodes.
    private static readonly Dictionary<string, SequenceV2Field> Overrides = new(StringComparer.Ordinal) {
        ["kind"] = new("kind", SequenceV2ValueType.Text, false),
        ["targetId"] = new("targetId", SequenceV2ValueType.Text, false),
        ["provider"] = new("provider", SequenceV2ValueType.Text, false),
        ["run"] = new("run", SequenceV2ValueType.Array, false),
    };

    private static SequenceV2Field Plain(string name, SequenceV2ValueType? type, bool nullable = false) => new(name, type, nullable);

    /// <summary>The members of the document object.</summary>
    public static SequenceV2FieldSet Document { get; } = new(new[] {
        Plain("schemaVersion", SequenceV2ValueType.Text), Plain("name", SequenceV2ValueType.Text),
        Plain("description", SequenceV2ValueType.Text), Plain("metadata", SequenceV2ValueType.Map),
        Plain("targets", SequenceV2ValueType.Array), Plain("root", SequenceV2ValueType.Map),
    });

    /// <summary>The members of an element of <c>targets</c>.</summary>
    public static SequenceV2FieldSet Target { get; } = new(new[] {
        Plain("id", SequenceV2ValueType.Text), Plain("name", SequenceV2ValueType.Text),
        Plain("coordinates", SequenceV2ValueType.Map), Plain("positionAngle", SequenceV2ValueType.Number),
    });

    /// <summary>The members of the <c>unknown</c> placeholder.</summary>
    public static SequenceV2FieldSet Unknown { get; } = new(new[] {
        Plain("kind", SequenceV2ValueType.Text), Plain("name", SequenceV2ValueType.Text),
        Plain("role", SequenceV2ValueType.Text), Plain("originalType", SequenceV2ValueType.Text),
        Plain("originalPayload", SequenceV2ValueType.Map), Plain("reason", SequenceV2ValueType.Text),
    });

    /// <summary>
    /// The spec's proposed <c>glover-exposure</c> names. Provisional until follow-up (d) adds the class: names only,
    /// so no value type is checked and none is written to the schema.
    /// </summary>
    public static SequenceV2FieldSet GloverExposure { get; } = new(new[] {
        "kind", "attempts", "errorBehavior", "startSubSeconds", "minSubSeconds", "maxSubSeconds", "measureFrames",
        "swampFactor", "frameCount", "gain", "offset", "binning", "imageType",
    }.Select(n => Plain(n, null)));

    private static readonly ConcurrentDictionary<Type, SequenceV2FieldSet> Cache = new();

    /// <summary>The fields of an engine kind.</summary>
    public static SequenceV2FieldSet For(SequenceKindEntry entry) => Cache.GetOrAdd(entry.EngineClass, Compute);

    private static SequenceV2FieldSet Compute(Type type) {
        var contract = (JsonObjectContract)new DefaultContractResolver().ResolveContract(type);
        var nullability = new NullabilityInfoContext();
        var fields = new Dictionary<string, SequenceV2Field>(StringComparer.Ordinal) { ["kind"] = Overrides["kind"] };
        foreach (var property in contract.Properties.Where(p => p.Readable && !p.Ignored)) {
            var engineName = property.PropertyName!;
            if (NotStored.Contains(engineName)) continue;
            var name = Renamed.TryGetValue(engineName, out var renamed) ? renamed : char.ToLowerInvariant(engineName[0]) + engineName[1..];
            fields[name] = Overrides.TryGetValue(name, out var fixedField) ? fixedField : FromMember(name, type, property.UnderlyingName!, nullability);
        }
        return new SequenceV2FieldSet(fields.Values);
    }

    private static SequenceV2Field FromMember(string name, Type type, string memberName, NullabilityInfoContext nullability) {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var t = type; t != null; t = t.BaseType) {
            var property = t.GetProperty(memberName, Flags);
            if (property != null) return FromClr(name, property.PropertyType, nullability.Create(property).ReadState == NullabilityState.Nullable);
            var field = t.GetField(memberName, Flags);
            if (field != null) return FromClr(name, field.FieldType, nullability.Create(field).ReadState == NullabilityState.Nullable);
        }
        throw new InvalidOperationException(type.Name + "." + memberName + " is a JSON member with no property or field");
    }

    private static SequenceV2Field FromClr(string name, Type clr, bool nullable) {
        var under = Nullable.GetUnderlyingType(clr) ?? clr;
        return under switch {
            _ when under == typeof(bool) => new(name, SequenceV2ValueType.Boolean, nullable),
            _ when IsInteger(under) => new(name, SequenceV2ValueType.WholeNumber, nullable, under),
            _ when under == typeof(double) || under == typeof(float) || under == typeof(decimal) => new(name, SequenceV2ValueType.Number, nullable),
            _ when under == typeof(string) || under.IsEnum || under == typeof(Guid) || under == typeof(DateTime)
                || under == typeof(DateTimeOffset) || under == typeof(TimeSpan) => new(name, SequenceV2ValueType.Text, nullable),
            _ when typeof(IDictionary).IsAssignableFrom(under) => new(name, SequenceV2ValueType.Map, nullable),
            _ when typeof(IEnumerable).IsAssignableFrom(under) => new(name, SequenceV2ValueType.Array, nullable),
            _ => new(name, SequenceV2ValueType.Map, nullable),
        };
    }

    private static bool IsInteger(Type t) => t == typeof(sbyte) || t == typeof(byte) || t == typeof(short) || t == typeof(ushort)
        || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong);
}