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
        ["CalibrationState"] = new CalibrationStateDto(new GuiderCalibrationEntryDto(true, Now), new GuiderCalibrationEntryDto()),
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
            var t = ps[i].ParameterType;
            if (t == typeof(string)) { args[i] = args[i] + "-x"; nudged = true; }
            else if (t == typeof(int)) { args[i] = (int)args[i]! + 1; nudged = true; }
            else if (t == typeof(double)) { args[i] = (double)args[i]! + 1.5; nudged = true; }
            else if (t == typeof(bool)) { args[i] = !(bool)args[i]!; nudged = true; }
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

    // The sections that ship in a share file unchanged, by the CTO's decision on #1697: tuning judgement only.
    private static readonly string[] KeptWhole = [
        "ImagingDefaults", "Filenames", "SafetyPolicies", "Autofocus", "DiagnosticsMode", "EquipmentConnection",
        "StretchDefaults", "FilterWheelLabels", "PolarAlign", "Unguided", "GuiderAdvanced", "FilterWheelPolicy",
    ];

    [Test]
    public void Every_section_the_share_strip_leaves_whole_is_on_the_approved_keep_list() {
        var snap = ProfileSectionSamples.AllSampled();
        var shared = ProfileShareService.StripForShare(snap);
        var shippedWhole = ProfileSectionSamples.Sections
            .Where(p => ProfileSectionSamples.Json(p, p.GetValue(shared)) == ProfileSectionSamples.Json(p, p.GetValue(snap)))
            .Select(p => p.Name)
            .ToList();
        shippedWhole.Except(KeptWhole).Should().BeEmpty(
            "a section the share file carries verbatim must be a deliberate keep, not a default (the donor's saved targets shipped before this)");
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
