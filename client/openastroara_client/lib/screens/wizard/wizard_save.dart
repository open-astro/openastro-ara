import 'package:flutter/foundation.dart';

// The draft declares its own `ImagingDefaults` / `PlateSolveSettings` /
// `AutofocusSettings` bags; we map onto the profile *section* types from the
// settings-state files, so hide the draft's to disambiguate (the draft
// sub-objects are still reached via `d.<field>`).
import '../../models/profile_draft.dart'
    hide ImagingDefaults, PlateSolveSettings, AutofocusSettings, SafetyPolicies;
import '../../services/profile_api.dart';
import '../../util/guide_optics.dart';
import '../../util/host_port.dart';
import '../../widgets/guider/guider_setup_wizard.dart'
    show parseAlpacaChoiceEndpoint;
import '../../state/imaging/exposure_state.dart' show FrameKind;
import '../../state/settings/autofocus_settings_state.dart';
import '../../state/settings/camera_electronics_state.dart';
import '../../state/settings/filter_set_state.dart';
import '../../state/settings/imaging_defaults_state.dart';
import '../../state/settings/optics_settings_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../state/settings/plate_solve_settings_state.dart';
import '../../state/settings/safety_policies_state.dart';
import '../../state/settings/site_settings_state.dart';
import '../../state/settings/storage_settings_state.dart';

/// §37 wizard Save. Maps the wizard's [ProfileDraft] onto the daemon's profile
/// sections and persists it as a new active profile.
///
/// The draft → section mappers are pure (base section + draft → new section)
/// so they're unit-testable without a server: each only overrides the fields
/// the wizard actually collected, leaving the freshly-created profile's
/// defaults for everything else (and for the screens 11–18 not built yet).

SiteSettings applyDraftToSite(SiteSettings base, ProfileDraft d) {
  // copyWith uses `?? base.field`, so a null override (an unset draft field, or a
  // blanked string) preserves the base value. The string fields get an explicit
  // trim/empty→null guard; the numeric fields are already nullable so a null is
  // passed straight through to the same preserve-on-null behavior.
  return base.copyWith(
    siteName: (d.siteName?.trim().isNotEmpty ?? false) ? d.siteName!.trim() : null,
    latitudeDeg: d.latitudeDeg,
    longitudeDeg: d.longitudeDeg,
    elevationM: d.altitudeMeters,
    timeZone: (d.timezone?.trim().isNotEmpty ?? false) ? d.timezone!.trim() : null,
    // Screen 16 — horizon floor + twilight + runtime cap (null keeps the base).
    defaultHorizonAltitudeDeg: d.site.hardMinAltitudeDeg,
    maxSequenceRuntimeMin: d.site.maxSequenceRuntimeMin,
    softWarningAltitudeDeg: d.site.softWarningAltitudeDeg,
    twilightDefinition: switch (d.site.twilight) {
      TwilightOption.civil => TwilightDefinition.civil,
      TwilightOption.nautical => TwilightDefinition.nautical,
      TwilightOption.astronomical => TwilightDefinition.astronomical,
      null => null,
    },
  );
}

/// NEXTGEN §4 — the wizard's two user-owned electronics fields. Preserve-on-null
/// like every mapper here, which also keeps the ASCOM-auto-captured fields
/// (sensor name, full well, e-/ADU, gain) exactly as the camera connect wrote
/// them; QE converts from the entered percent to the stored fraction.
CameraElectronics applyDraftToCameraElectronics(CameraElectronics base, ProfileDraft d) {
  final rn = d.camera.readNoiseE;
  final qe = d.camera.qePeakPct;
  return base.copyWith(
    readNoiseE: (rn != null && rn > 0) ? rn : null,
    quantumEfficiencyPeak: (qe != null && qe > 0 && qe <= 100) ? qe / 100.0 : null,
  );
}

/// NEXTGEN §4 — the wizard's screen-6 filters become the planning filter set
/// (feeds the emission-aware Tonight's Sky advice + the per-filter Optimal Sub
/// advisor). Each named filter's kind is guessed from its label via the SAME
/// inference the Settings panel's "seed from wheel labels" button uses, so the
/// two entry paths agree; the user can refine kinds later in Settings →
/// Imaging → Filter set. An empty/unnamed draft list preserves the base —
/// until this mapper, the wizard's filter entries were saved nowhere at all.
FilterSetSettings applyDraftToFilterSet(FilterSetSettings base, ProfileDraft d) {
  // Dedupe case-insensitively (keep-first), mirroring the daemon's PUT
  // validation and the Settings notifier's addFilter/seedFromWheelLabels —
  // duplicate wheel labels (multiple unset slots, case variants) must degrade
  // to one planning filter, not 400 the ENTIRE wizard save.
  final seen = <String>{};
  final named = <(String, FilterDef)>[
    for (final f in d.filterWheel.filters)
      if ((f.name?.trim().isNotEmpty ?? false) &&
          seen.add(f.name!.trim().toLowerCase()))
        (f.name!.trim(), f),
  ];
  if (named.isEmpty) return base;
  return FilterSetSettings(filters: [
    for (final (name, def) in named)
      PlanningFilter(
        // Kind by the same name heuristic as the Settings seed ("Ha 6nm" → Hα,
        // "Red" → r); an oddly-named filter's kind is directly editable in
        // Settings → Imaging → Filter set.
        name: name,
        kind: FilterSetNotifier.guessKind(name),
        // The screen's explicit bandwidth entry (the "3nm/6nm/12nm" printed
        // on the filter) — the number the Optimal-Sub math runs on. 0 (unset)
        // falls back to the kind's default passband.
        bandwidthNm: (def.bandwidthNm != null && def.bandwidthNm! > 0)
            ? def.bandwidthNm!
            : 0,
      ),
  ]);
}


OpticsSettings applyDraftToOptics(OpticsSettings base, ProfileDraft d) {
  final fl = d.telescope.focalLengthMm;
  final ap = d.telescope.apertureMm;
  final px = d.camera.pixelSizeMicrons;
  return base.copyWith(
    focalLengthMm: (fl != null && fl > 0) ? fl : null,
    // NEXTGEN §4: the telescope screen has always asked for the aperture; the
    // optics section can now store it (it feeds the Optimal-Sub sky-flux term).
    apertureMm: (ap != null && ap > 0) ? ap : null,
    pixelSizeUm: (px != null && px > 0) ? px : null,
  );
}

ImagingDefaults applyDraftToImaging(ImagingDefaults base, ProfileDraft d) {
  final c = d.camera;
  final i = d.imagingDefaults; // screen 14
  return base.copyWith(
    coolerTargetC: c.coolingTargetC,
    coolerRampRatePerMin: c.coolerRampRateCPerMin,
    // The draft models warmup as an enum; the section flag is a bool.
    warmupAtSessionEnd: c.warmupMode != CoolerWarmupMode.off,
    defaultGain: c.defaultGain,
    defaultOffset: c.defaultOffset,
    defaultBin: c.defaultBin,
    // Screen 14 — default exposure + frame kind (null keeps the base).
    defaultExposure: i.exposure,
    defaultFrameKind: switch (i.frameType) {
      FrameType.light => FrameKind.light,
      FrameType.dark => FrameKind.dark,
      FrameType.bias => FrameKind.bias,
      FrameType.flat => FrameKind.flat,
      null => null,
    },
  );
}

/// §63.19 — the guide-setup type the wizard should ACT on: the draft's pick
/// when the user touched the dropdown, else the base profile's value (new
/// wizard profiles clone the active profile, so an untouched dropdown means
/// "keep what the base says", not "guide scope"). Shared by ScreenGuider's
/// display branch and [applyDraftToPhd2] so the UI and the save can never
/// resolve to different modes.
String resolveGuiderSetupType(String? draftSetupType, String baseSetupType) =>
    Phd2SettingsNotifier.normalizeGuiderSetupType(
        draftSetupType ?? baseSetupType);

Phd2Settings applyDraftToPhd2(Phd2Settings base, ProfileDraft d,
    {OpticsSettings? baseOptics}) {
  final g = d.guider;
  // §63.19 — with an OAG the guide focal length is derived from the main
  // optics: the telescope screen's focal length × the base profile's optics
  // reducer factor (the wizard collects no reducer of its own; the base
  // section is the source, falling back to 1.0 when it's unset/invalid).
  // 0/unset telescope focal length maps to null = keep the base value.
  final setupType = g.setupType == null
      ? null
      : Phd2SettingsNotifier.normalizeGuiderSetupType(g.setupType!);
  final effectiveType = resolveGuiderSetupType(g.setupType, base.guiderSetupType);
  int? focalLength;
  if (effectiveType == 'oag') {
    final reducer = effectiveOagReducerFactor(baseOptics?.reducerFactor);
    final derived =
        derivedOagGuideFocalLength(d.telescope.focalLengthMm ?? 0, reducer);
    focalLength = derived == 0 ? null : derived;
  } else {
    focalLength = g.guideFocalLengthMm;
  }
  // parseHostPort handles IPv6 literals/bare hosts/`:port`; null parts keep the
  // base (profile) values so a blank field never clobbers a configured target.
  final parsed = parseHostPort(g.hostPort);
  final host = parsed.host ?? base.host;
  final port = parsed.port ?? base.port;
  return base.copyWith(
    host: host,
    port: port,
    ditherPixels: g.ditherPixels,
    settlePixels: g.settleThresholdPx,
    settleTimeSec: g.settleDuration.inSeconds,
    forceCalibrationEachSession:
        g.calibrationCadence == CalibrationCadence.eachSession,
    // §63.17 — null (untouched picker) keeps the base profile's selection.
    guiderCamera: g.guiderCamera,
    guiderMount: g.guiderMount,
    guiderAuxMount: g.guiderAuxMount,
    guiderRotator: g.guiderRotator,
    // A camera picked on another Alpaca server retargets the daemon's Alpaca
    // config (same rule as the §63.17 setup wizard's Apply) — without this the
    // pick would silently not take effect.
    guiderAlpacaHost: parseAlpacaChoiceEndpoint(g.guiderCamera ?? '')?.host,
    guiderAlpacaPort: parseAlpacaChoiceEndpoint(g.guiderCamera ?? '')?.port,
    // §63.19 — null keeps the base for anything the user left untouched.
    guiderSetupType: setupType,
    guideFocalLength: focalLength,
    guidePixelSize: g.guidePixelSizeUm,
    // §76.2 — the wizard's chosen exposure range persists in the profile so a
    // later darks rebuild covers the same range guiding runs in.
    guideExposureMinMs: g.darkMinExposureMs,
    guideExposureMaxMs: g.darkMaxExposureMs,
  );
}

/// §37.4 screen 11 — map the draft's ASTAP paths + search tuning onto the
/// plate-solve section. The screen stores trimmed-or-null values, so every field
/// passes straight through: copyWith treats null as "keep", so a blank/untouched
/// field preserves the base profile's value. A path the user explicitly RESET
/// ([ClearableField] in [ProfileDraft.clearedFields]) writes the section
/// default instead — the new profile clones the active one, so keep-on-blank
/// alone would trap an old rig's path with no way to shed it.
PlateSolveSettings applyDraftToPlateSolve(PlateSolveSettings base, ProfileDraft d) {
  final ps = d.plateSolve;
  const defaults = PlateSolveSettings();
  return base.copyWith(
    pathOrEndpoint: d.clearedFields.contains(ClearableField.astapBinaryPath)
        ? defaults.pathOrEndpoint
        : ps.astapBinaryPath,
    indexDownloadPath: d.clearedFields.contains(ClearableField.starDatabasePath)
        ? defaults.indexDownloadPath
        : ps.starDatabasePath,
    // Search radius is deliberately NOT derived (§76.1): it reflects mount
    // pointing uncertainty, not optics — image scale says nothing about how
    // far off a goto lands, so the §37.8 default stands.
    searchRadiusDeg: ps.searchRadiusDeg,
    // §76.1 derived-never-asked: the wizard no longer has a plate-solve
    // screen, so an unset downsample is seeded from the rig's image scale —
    // but only when the base is still at the section default: a cloned
    // profile where the user hand-tuned the value keeps that tuning (null →
    // copyWith keeps base).
    downsampleFactor: ps.downsampleFactor ??
        (base.downsampleFactor == defaults.downsampleFactor
            ? derivedAstapDownsample(
                d.camera.pixelSizeMicrons, d.telescope.focalLengthMm)
            : null),
  );
}

/// §76 S5 — seed ASTAP's downsample from the imaging train's pixel scale
/// (206.265 × µm ÷ mm arcsec/px): solvers want ~1.5–3″/px, so oversampled
/// rigs bin down (scale < 0.75″ → 4, < 1.5″ → 2) and everything else runs
/// full-res. Null when the scale isn't computable (either fact missing) —
/// copyWith then keeps the base value, same as every unset field.
int? derivedAstapDownsample(double? pixelSizeUm, double? focalLengthMm) {
  if (pixelSizeUm == null || pixelSizeUm <= 0) return null;
  if (focalLengthMm == null || focalLengthMm <= 0) return null;
  final scale = 206.265 * pixelSizeUm / focalLengthMm;
  if (scale >= 1.5) return 1;
  if (scale >= 0.75) return 2;
  return 4;
}

/// §37.4 screen 12 — map the draft's autofocus subset onto the profile's
/// autofocus section. Every field is nullable, so copyWith's `??` keeps the base
/// for anything the user left blank/untouched.
AutofocusSettings applyDraftToAutofocus(AutofocusSettings base, ProfileDraft d) {
  final af = d.autofocus;
  return base.copyWith(
    exposureSeconds: af.exposureSeconds,
    steps: af.steps,
    // §76 S5 — with no autofocus screen, an unset step size is seeded from
    // the focuser's read step size + the optics' focal ratio when both are
    // known — but only when the base is still at the section default: a
    // cloned profile's hand-tuned step size survives (null keeps the base).
    stepSize: af.stepSize ??
        (base.stepSize == const AutofocusSettings().stepSize
            ? derivedAutofocusStepSize(
                focuserStepUm: d.focuser.stepSizeMicrons,
                focalLengthMm: d.telescope.focalLengthMm,
                apertureMm: d.telescope.apertureMm)
            : null),
    runAfterFilterChange: af.runAfterFilterChange,
    // §59.4 — the draft carries the wire string (null = untouched, keep base).
    telescopeType: af.telescopeType == null
        ? null
        : telescopeTypeFromWire(af.telescopeType),
  );
}

/// §76 S5 — seed the autofocus step size (focuser steps per AF point) from
/// the critical-focus zone: CFZ ≈ 2.2 µm × f-ratio² (the standard visual
/// approximation), one AF step ≈ half the CFZ so the V-curve samples it.
/// Needs the focuser's µm/step (often unreported — e.g. ZWO EAF) and the
/// optics; null (keep base default) when either is missing. Clamped to
/// [5, 500] so a degenerate train can't produce a useless 1-step or a
/// hardware-grinding multi-thousand-step seed.
int? derivedAutofocusStepSize({
  double? focuserStepUm,
  double? focalLengthMm,
  double? apertureMm,
}) {
  if (focuserStepUm == null || focuserStepUm <= 0) return null;
  if (focalLengthMm == null || focalLengthMm <= 0) return null;
  if (apertureMm == null || apertureMm <= 0) return null;
  final fRatio = focalLengthMm / apertureMm;
  final cfzUm = 2.2 * fRatio * fRatio;
  final steps = (cfzUm / 2 / focuserStepUm).round();
  // .toInt() for explicitness: int.clamp(int, int) IS statically int since
  // Dart 2.17 (the SDK special-cases it), but spelling it out spares every
  // future reader the num-vs-int double-take (review r3).
  return steps.clamp(5, 500).toInt();
}

/// §37.4 screen 13 — map the draft's file-saving choices onto the profile's
/// storage section. The screen stores trimmed-or-null strings (null keeps base);
/// the format/compression enums translate from the draft's wizard model
/// (compress true → Rice, false → Off; gzip stays a Settings-only choice).
StorageSettings applyDraftToStorage(StorageSettings base, ProfileDraft d) {
  final f = d.fileSaving;
  // Explicitly-reset fields take the section default (see applyDraftToPlateSolve).
  const defaults = StorageSettings();
  return base.copyWith(
    saveDirectory: d.clearedFields.contains(ClearableField.saveDirectory)
        ? defaults.saveDirectory
        : f.saveDirectory,
    filenameTemplate: d.clearedFields.contains(ClearableField.filenameTemplate)
        ? defaults.filenameTemplate
        : f.filenameTemplate,
    fileFormat: switch (f.format) {
      ImageFormat.fits => StorageFileFormat.fits,
      ImageFormat.xisf => StorageFileFormat.xisf,
      null => null,
    },
    compression: switch (f.compress) {
      true => StorageCompression.rice,
      false => StorageCompression.off,
      null => null,
    },
  );
}

/// §37.5 screen 15 — map the draft's compact safety choices onto the safety
/// section. Every field is nullable (null keeps base). The §35.1 weather
/// thresholds map through since their enforcement landed; per-trigger actions
/// stay deferred (PORT_DECISIONS 2026-07-07) and alarm knobs are device-local.
SafetyPolicies applyDraftToSafety(SafetyPolicies base, ProfileDraft d) {
  final s = d.safety;
  return base.copyWith(
    onUnsafe: switch (s.onUnsafe) {
      UnsafeConditionAction.pauseAndPark => UnsafeAction.pauseAndPark,
      UnsafeConditionAction.parkOnly => UnsafeAction.parkOnly,
      UnsafeConditionAction.abortAndPark => UnsafeAction.abortAndPark,
      UnsafeConditionAction.ignore => UnsafeAction.ignore,
      null => null,
    },
    autoResumeWhenSafe: s.autoResumeWhenSafe,
    resumeDelayMin: s.resumeDelayMin,
    weatherTriggersEnabled: s.weatherTriggersEnabled,
    maxWindKmh: s.maxWindKmh,
    maxHumidityPct: s.maxHumidityPct,
    minDewDeltaC: s.minDewDeltaC,
    unattendedShutdownEnabled: s.unattendedShutdownEnabled,
    unattendedShutdownWaitMinutes: s.unattendedShutdownWaitMin,
  );
}

/// Create a new active profile from the wizard draft and layer the configured
/// sections on top. Throws on transport failure — the caller surfaces it.
///
/// Pass the **live** draft (not a copy): a successful create stamps its id onto
/// `d.savedProfileId`, which a retry after a mid-save failure reads to re-use the
/// same profile instead of orphaning a new one.
Future<void> saveWizardProfile(ProfileApi api, ProfileDraft d) async {
  final name = (d.profileName?.trim().isNotEmpty ?? false)
      ? d.profileName!.trim()
      : 'Untitled profile';
  // Create the profile exactly once. If a prior Save got as far as creating it
  // but failed applying a section, the profile already exists (and is active);
  // a retry re-applies the sections onto it rather than orphaning a new profile
  // each attempt.
  if (d.savedProfileId == null) {
    // createProfile throws on an empty/garbled id, so savedProfileId is always a
    // real id here — a retry then re-uses it rather than orphaning a new profile.
    final meta = await api.createProfile(name);
    d.savedProfileId = meta.id;
  }

  // Each section is an independent GET-overlay-PUT against the active profile, so
  // run them concurrently rather than serially (4 round-trips → ~1 round-trip of
  // latency). Collect every failure (rather than letting Future.wait drop all but
  // the first) so a partial save reports exactly which sections didn't apply.
  // The sections are disjoint (no section's GET reads another's PUT target),
  // and the caller (_saveAndExit) runs this under a non-dismissible spinner that
  // blocks a second Save, so there is no GET-after-PUT overlap to worry about.
  final errors = (await Future.wait([
    _trySave(() async => api.putSiteSettings(applyDraftToSite(await api.getSiteSettings(), d))),
    _trySave(() async => api.putOptics(applyDraftToOptics(await api.getOptics(), d))),
    _trySave(() async =>
        api.putImagingDefaults(applyDraftToImaging(await api.getImagingDefaults(), d))),
    // §63.19 — the OAG derivation needs the base optics reducer factor; the
    // draft never writes the reducer, so reading it concurrently with the
    // optics-section save above is race-free.
    _trySave(() async => api.putPhd2Settings(applyDraftToPhd2(
        await api.getPhd2Settings(), d,
        baseOptics: await api.getOptics()))),
    _trySave(() async => api.putPlateSolveSettings(
        applyDraftToPlateSolve(await api.getPlateSolveSettings(), d))),
    _trySave(() async => api.putAutofocusSettings(
        applyDraftToAutofocus(await api.getAutofocusSettings(), d))),
    _trySave(() async => api.putCameraElectronics(
        applyDraftToCameraElectronics(await api.getCameraElectronics(), d))),
    _trySave(() async =>
        api.putFilterSet(applyDraftToFilterSet(await api.getFilterSet(), d))),
    _trySave(() async => api.putStorageSettings(
        applyDraftToStorage(await api.getStorageSettings(), d))),
    _trySave(() async => api.putSafetyPolicies(
        applyDraftToSafety(await api.getSafetyPolicies(), d))),
  ]))
      .whereType<Object>()
      .toList();
  if (errors.isNotEmpty) {
    throw Exception('Failed to save ${errors.length} profile section(s): '
        '${errors.map((e) => e.toString()).join('; ')}');
  }
}

/// Runs a section save and captures its error (null = success) so a concurrent
/// batch can report every failure instead of only the first.
Future<Object?> _trySave(Future<void> Function() body) async {
  try {
    await body();
    return null;
  } catch (e, st) {
    // Log the full error + stack now; the aggregated Exception the caller throws
    // only carries the joined messages, so this keeps each section's trace.
    debugPrint('[wizard] profile section save failed: $e\n$st');
    return e;
  }
}
