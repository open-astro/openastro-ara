#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using NUnit.Framework;
using OpenAstroAra.Server;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace OpenAstroAra.Test;

/// <summary>Builds a snapshot whose every section differs from the default, by section name.</summary>
internal static class ProfileSectionSamples {
    public static IEnumerable<PropertyInfo> Sections =>
        typeof(ProfileSnapshotDto).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract");

    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    // Every scalar constructor parameter of the other sections is nudged, so a strip of any one field shows.
    // Sections with no scalar constructor parameter to nudge, or a null default: built by hand.
    private static readonly Dictionary<string, object> Hand = new() {
        // Format and compression are token-normalized on write, so only the free-text fields are nudged.
        ["Storage"] = ProfileSnapshotNormalizer.Defaults.Storage with { SaveDirectory = "/sample/dir", FilenameTemplate = "$$FILTER$$" },
        ["FilterSet"] = new FilterSetDto(Filters: [new PlanningFilterDto("Sample L", FilterKind.Duo, 7)]),
        ["FilterWheelLabels"] = new FilterWheelLabelsDto(["L", "Ha"]),
        ["CustomHorizon"] = new CustomHorizonDto([new CustomHorizonPointDto(123.75, 41.5)]),
        ["FocusCalibration"] = new FocusCalibrationDto([], Now, 3.5, "L"),
        ["TrackingCalibration"] = new TrackingCalibrationDto(CeilingSeconds: 45, MeasuredUtc: Now, PolarAlignErrorArcmin: 3,
            PixelScaleArcsec: 1.2, SeeingFwhmArcsec: 2.5, PriorSeconds: null, Source: "measured", Rungs: []),
        ["SkyModelCalibration"] = new SkyModelCalibrationDto(Now, 5, []),
        ["CalibrationState"] = new CalibrationStateDto(new GuiderCalibrationEntryDto(true, Now), new GuiderCalibrationEntryDto(true, Now)),
        ["EquipmentIdentity"] = new EquipmentIdentityDto(new Dictionary<string, DeviceIdentityDto> { ["camera"] = new("uid-1", "Sample cam", Now) }),
        ["SavedTargets"] = new SavedTargetsDto([new SavedTargetDto("M31", ["Andromeda"], 10.5, 41.25, "typed", Now)]),
        ["FilterFocusOffsets"] = new FilterFocusOffsetsDto([new FilterFocusOffsetDto(2, 14)]),
        ["CoolingFan"] = new CoolingFanDto("sample-switch", 2),
    };

    public static object Sample(PropertyInfo section, object defaultValue) {
        if (Hand.TryGetValue(section.Name, out var hand)) {
            return hand;
        }
        var type = section.PropertyType;
        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters().Select(p => type.GetProperty(p.Name!, BindingFlags.Public | BindingFlags.Instance)!.GetValue(defaultValue)).ToArray();
        var ps = ctor.GetParameters();
        var nudged = false;
        for (var i = 0; i < ps.Length; i++) {
            var t = Nullable.GetUnderlyingType(ps[i].ParameterType) ?? ps[i].ParameterType;
            if (t == typeof(string)) { args[i] = args[i] + "-x"; nudged = true; } else if (t == typeof(int)) { args[i] = args[i] is int n ? n + 1 : 7; nudged = true; } else if (t == typeof(double)) { args[i] = args[i] is double d ? d + 1.5 : 7.5; nudged = true; } else if (t == typeof(bool)) { args[i] = args[i] is bool b ? !b : true; nudged = true; }
        }
        if (nudged) { return ctor.Invoke(args); }
        throw new InvalidOperationException($"No sample for section {section.Name}: add one to ProfileSectionSamples.Hand.");
    }

    /// <summary>The normalizer's defaults, with every section replaced by a sample that differs from them.</summary>
    public static ProfileSnapshotDto AllSampled() {
        var snap = ProfileSnapshotNormalizer.Normalize(ProfileSnapshotNormalizer.Defaults);
        foreach (var p in Sections) {
            var def = p.GetValue(snap);
            snap = Set(snap, p, Sample(p, def!));
        }
        return snap;
    }

    public static ProfileSnapshotDto Set(ProfileSnapshotDto snap, PropertyInfo p, object? value) {
        // Records expose init-only setters; copy through the compiler-generated clone.
        var clone = (ProfileSnapshotDto)typeof(ProfileSnapshotDto).GetMethod("<Clone>$")!.Invoke(snap, null)!;
        p.SetValue(clone, value);
        return clone;
    }

    public static string Json(PropertyInfo section, object? value) =>
        value is null ? "null" : JsonSerializer.Serialize(value, AraJsonSerializerContext.Default.GetTypeInfo(section.PropertyType)!);
}

[TestFixture]
public class ProfileSectionTableTest {

    [Test]
    public void The_table_declares_every_section_of_the_snapshot_once() {
        var declared = ProfileSections.All.Select(x => x.Name).ToList();
        declared.Should().OnlyHaveUniqueItems();
        declared.Should().BeEquivalentTo(ProfileSectionSamples.Sections.Select(p => p.Name),
            "a snapshot section missing from the table is skipped by Capture, Apply, the normalizer and the share strip");
    }

    [Test]
    public void Every_field_of_every_section_is_classified_kept_or_stripped_exactly_once() {
        foreach (var section in ProfileSections.All) {
            var fields = section.DtoType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First()
                .GetParameters().Select(p => p.Name!).ToList();
            var classified = section.Kept.Concat(section.Stripped).Select(f => f.ToUpperInvariant()).ToList();
            classified.Should().OnlyHaveUniqueItems($"{section.Name} lists a field twice");
            classified.Should().BeEquivalentTo(fields.Select(f => f.ToUpperInvariant()),
                $"{section.Name} must classify each DTO field as kept or stripped");
        }
    }

    [Test]
    public void The_share_strip_changes_exactly_the_fields_the_table_says_it_strips() {
        var snap = ProfileSectionSamples.AllSampled();
        var shared = ProfileShareService.StripForShare(snap);
        foreach (var section in ProfileSections.All) {
            var before = section.ValueIn(snap);
            var after = section.ValueIn(shared);
            foreach (var field in section.Kept.Concat(section.Stripped)) {
                var prop = section.DtoType.GetProperty(field)!;
                var was = before is null ? null : prop.GetValue(before);
                var now = after is null ? null : prop.GetValue(after);
                if (was is null && section.Stripped.Contains(field)) { continue; } // the sample left it empty: a strip shows nothing
                var changed = JsonSerializer.Serialize(was) != JsonSerializer.Serialize(now);
                changed.Should().Be(section.Stripped.Contains(field), $"{section.Name}.{field} is {(section.Stripped.Contains(field) ? "stripped" : "kept")}");
            }
        }
    }

    [Test]
    public void Saved_targets_are_emptied_in_a_share() {
        var snap = ProfileSectionSamples.AllSampled();
        snap.SavedTargets!.Targets.Should().NotBeEmpty();
        ProfileShareService.StripForShare(snap).SavedTargets!.Targets.Should().BeEmpty();
    }

    [TestCase("memory")]
    [TestCase("file")]
    public void A_stores_defaults_equal_the_normalizers_for_every_section(string kind) {
        var dir = Path.Combine(Path.GetTempPath(), "ara-defaults-" + Path.GetRandomFileName());
        try {
            IProfileStore store = kind == "memory" ? new InMemoryProfileStore() : new FileProfileStore(dir);
            var fromStore = ProfileStoreSnapshot.Capture(store);
            var expected = ProfileSnapshotNormalizer.Normalize(ProfileSnapshotNormalizer.Defaults);
            var differing = ProfileSectionSamples.Sections
                .Select(p => (p.Name, Store: ProfileSectionSamples.Json(p, p.GetValue(fromStore)), Norm: ProfileSectionSamples.Json(p, p.GetValue(expected))))
                .Where(x => x.Store != x.Norm)
                .Select(x => $"DEFAULTS {kind} {x.Name}: store {x.Store} vs normalizer {x.Norm}")
                .ToList();
            differing.Should().BeEmpty(string.Join("\n", differing));
        } finally {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }

    [TestCase("memory")]
    [TestCase("file")]
    public void Every_section_survives_Capture_then_Apply(string kind) {
        var dir = Path.Combine(Path.GetTempPath(), "ara-roundtrip-" + Path.GetRandomFileName());
        try {
            var defaults = ProfileSnapshotNormalizer.Normalize(ProfileSnapshotNormalizer.Defaults);
            foreach (var section in ProfileSectionSamples.Sections) {
                IProfileStore store = kind == "memory" ? new InMemoryProfileStore() : new FileProfileStore(dir);
                var sample = ProfileSectionSamples.Sample(section, section.GetValue(defaults)!);
                ProfileStoreSnapshot.Apply(store, ProfileSectionSamples.Set(defaults, section, sample));
                var back = ProfileStoreSnapshot.Capture(store);
                ProfileSectionSamples.Json(section, section.GetValue(back))
                    .Should().Be(ProfileSectionSamples.Json(section, sample), $"section {section.Name} must survive Apply then Capture");
                if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
            }
        } finally {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }
}