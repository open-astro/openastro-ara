import 'dart:math' as math;

import '../../util/input_coordinates.dart';
import 'condition_catalog.dart';
import 'instruction_catalog.dart';
import 'rig_capabilities.dart';
import 'nina_dom.dart';
import 'slew_target_body.dart';
import 'trigger_catalog.dart';

/// #1264 — Imaging → Unguided as the run builder consumes it: what goes on the
/// Tracking Probe, the Unguided Exposure Guard and Center After Drift nodes of a
/// rig without a guider. Defaults ARE the shipped behaviour (the daemon's
/// `UnguidedSettingsDto` defaults), so a caller without settings builds the
/// same body as before.
class UnguidedOptions {
  final bool guardEnabled;
  final double eccentricityTolerance;
  final double goodEccentricity;
  final int stepDownAfter;
  final int stepUpAfter;
  final List<double> probeRungsSeconds;
  final double recentreDriftArcmin;
  final int recentreSolveEveryN;

  const UnguidedOptions({
    this.guardEnabled = true,
    this.eccentricityTolerance = 0.5,
    this.goodEccentricity = 0.3,
    this.stepDownAfter = 2,
    this.stepUpAfter = 10,
    this.probeRungsSeconds = const [10, 30, 60, 120],
    this.recentreDriftArcmin = 10,
    this.recentreSolveEveryN = 1,
  });
}

/// §36 Planning → "Create Run": build a COMPLETE, runnable imaging session for
/// a target, not just a slew. The tree (all `$type`s are registered prototypes
/// in the daemon's `HeadlessSequencerFactory`, so every step executes for real
/// against connected equipment — simulators included):
///
/// ```
/// SequentialContainer  (named after the target)
/// ├─ Cool Camera        (when a cooler target is configured; starts the cooler, doesn't wait)
/// ├─ Unpark Scope
/// ├─ Set Tracking       (sidereal)
/// ├─ Target SequentialContainer  (named after the target — see [buildTargetBlock])
/// │   ├─ Slew to RA/Dec     (the target's J2000 coordinates; blind when autofocus follows)
/// │   ├─ Switch Filter      (when a filter is chosen — matched by name)
/// │   ├─ Run Autofocus      (when enabled)
/// │   ├─ Slew to RA/Dec     (after autofocus only: the centring go-to)
/// │   ├─ Cool Camera        (when a cooler target is configured; waits for the sensor)
/// │   └─ "Imaging" SequentialContainer
/// │        Conditions: Loop (× frame count)
/// │        Triggers:  Autofocus After Exposures (when a cadence is set)
/// │        └─ Take Exposure (Imaging Defaults: time / gain / offset / binning, LIGHT)
/// ├─ Park Scope         (when [parkAtEnd]: Simple Mode's Sessions)
/// └─ Warm Camera        (when "warm up at session end" is on)
/// ```
///
/// The per-target steps live in their own container (not flat at the root) so
/// a session can hold SEVERAL targets: "Add to Sequence" with a sequence
/// already open appends another target block via [appendTargetToRunBody]
/// instead of creating a parallel run. Session-wide steps (cool / unpark /
/// tracking / warm) stay at the root and are emitted once.
///
/// With a [FilterPlanStep] plan (the §Run "smart target" flow), the target
/// block becomes filter-aware: slew → switch to the FIRST plan filter →
/// autofocus (through that filter) → go-to → optional Start Guiding → one
/// `<Filter> Imaging` loop per plan step (each preceded by its Switch
/// Filter), every loop carrying the dither / autofocus triggers. §63 made
/// the guider mediator real (GuiderService drives OpenAstro Guider), so guiding steps
/// execute for real now.
///
/// A dialed framing position angle ([positionAngleDeg]) upgrades the slew to
/// a `CenterAndRotate` carrying it (§36/§38 — the rotate half executes now).
/// With no PA the step stays a plain slew; the daemon centres it (solve,
/// sync, re-slew) when Settings → Plate Solving → "Center target after slew"
/// is on (#1446), and slews blind when it is off.
///
/// The body is editable afterwards in the Run tab like any hand-built
/// sequence — this is a starting point that actually images, not a template
/// lock-in.
Map<String, dynamic> buildImagingRunBody({
  required double raDeg,
  required double decDeg,
  String? targetName,
  required double exposureSeconds,
  int gain = -1,
  int offset = -1,
  int binning = 1,
  required int frameCount,
  String? filterName,
  double? coolToC,
  bool warmAtEnd = false,
  bool autofocusAtStart = true,
  int? autofocusEveryNExposures,
  // The profile's "refocus when it drifts N °C"; null or 0 = off.
  double? autofocusAfterTempChangeC,
  double? positionAngleDeg,
  List<FilterPlanStep>? filterPlan,
  bool startGuiding = false,
  int? ditherEveryNExposures,
  bool manualFilterSwap = false,
  // What the rig has — every device-bound step below is gated on it (see
  // [RigCapabilities]). Callers without a rig to read keep the old
  // "assume everything" shape.
  RigCapabilities rig = RigCapabilities.everything,
  // #1264 — Imaging → Unguided: the guard / probe / re-centre knobs.
  UnguidedOptions unguidedOptions = const UnguidedOptions(),
  // #1264 — the mount's SPEC-SHEET prior for the probe (AlpacaBridge #815),
  // never the stored ceiling: the daemon already starts from last night's
  // measurement on its own, and a body prior would override a Recalibrate.
  // Null = no sheet. Ignored on a guided rig.
  double? trackingPriorSeconds,
  bool alwaysCenter = false,
  // #1560 — a [timeProviders] dawn label ('Civil Dawn' …): the target and each
  // of its imaging loops end at that dawn. Null = no dawn stop.
  String? stopAtDawn,
  // #1560 — park the mount once the last target is done (before the warm-up).
  bool parkAtEnd = false,
}) {
  // #1577 — nothing regulates a set-point on this camera: no Cool Camera step.
  if (!rig.coolerRegulates) coolToC = null;
  final target = buildTargetBlock(
    raDeg: raDeg,
    decDeg: decDeg,
    targetName: targetName,
    exposureSeconds: exposureSeconds,
    gain: gain,
    offset: offset,
    binning: binning,
    frameCount: frameCount,
    filterName: filterName,
    autofocusAtStart: autofocusAtStart,
    autofocusEveryNExposures: autofocusEveryNExposures,
    autofocusAfterTempChangeC: autofocusAfterTempChangeC,
    positionAngleDeg: positionAngleDeg,
    filterPlan: filterPlan,
    startGuiding: startGuiding,
    ditherEveryNExposures: ditherEveryNExposures,
    manualFilterSwap: manualFilterSwap,
    rig: rig,
    unguidedOptions: unguidedOptions,
    trackingPriorSeconds: trackingPriorSeconds,
    coolToC: coolToC,
    alwaysCenter: alwaysCenter,
    stopAtDawn: stopAtDawn,
  );

  // ── The session around it, in run order ──────────────────────────────────
  final children = <Map<String, dynamic>>[
    if (coolToC != null)
      _item(coolCameraType)
        ..['Temperature'] = coolToC
        // 0 = regulate as fast as the driver allows; a fixed multi-minute ramp
        // would stall a run against hardware that's already at temperature.
        ..['Duration'] = 0.0
        // The plan moves on while it cools; each target waits for it.
        ..['WaitForTemperature'] = false,
    _item(unparkScopeType),
    _item(setTrackingType), // catalog default: sidereal
    target,
    if (parkAtEnd) _item(parkScopeType),
    if (warmAtEnd) _item(warmCameraType)..['Duration'] = 0.0,
  ];

  var root = _item(sequentialContainerType);
  final name = targetName?.trim();
  if (name != null && name.isNotEmpty) {
    root['Name'] = name;
  }
  root = withChildren(root, children);
  root['schemaVersion'] = sequenceSchemaVersion;
  return root;
}

/// One step of a filter-aware target plan: image [filterName] for
/// [frameCount] subs of [exposureSeconds] each. Built by the Run tab's plan
/// chooser (SHO / LRGB / single filter) from the user's planning filter set.
class FilterPlanStep {
  final String filterName;
  final double exposureSeconds;
  final int frameCount;

  const FilterPlanStep({
    required this.filterName,
    required this.exposureSeconds,
    required this.frameCount,
  });
}

/// One target's worth of session — a SequentialContainer named after the
/// target holding slew → optional filter switch → optional autofocus →
/// go-to (with autofocus, the first slew is blind and this one centres) →
/// optional Start Guiding → optional waiting Cool Camera → the Take-Exposure
/// loop(s). With a [filterPlan],
/// one `<Filter> Imaging` loop per step (each with its own Switch Filter,
/// exposure and count); without one, the single default loop as before.
/// This is the unit [buildImagingRunBody] wraps once and
/// [appendTargetToRunBody] grafts onto an existing run for each further
/// "Add to Sequence".
Map<String, dynamic> buildTargetBlock({
  required double raDeg,
  required double decDeg,
  String? targetName,
  required double exposureSeconds,
  int gain = -1,
  int offset = -1,
  int binning = 1,
  required int frameCount,
  String? filterName,
  bool autofocusAtStart = true,
  int? autofocusEveryNExposures,
  // The profile's "refocus when it drifts N °C"; null or 0 = off.
  double? autofocusAfterTempChangeC,
  double? positionAngleDeg,
  List<FilterPlanStep>? filterPlan,
  bool startGuiding = false,
  int? ditherEveryNExposures,
  bool manualFilterSwap = false,
  RigCapabilities rig = RigCapabilities.everything,
  UnguidedOptions unguidedOptions = const UnguidedOptions(),
  double? trackingPriorSeconds,
  // The session's cooler target: the block waits for it just before its first
  // light (the session started the cool without waiting).
  double? coolToC,
  // Simple Mode: centre on the target whatever Settings → Plate Solving says.
  bool alwaysCenter = false,
  // #1560 — see [buildImagingRunBody].
  String? stopAtDawn,
}) {
  // #1577 — nothing regulates a set-point on this camera: no Cool Camera step.
  if (!rig.coolerRegulates) coolToC = null;
  // Cook the block from the rig: a device the rig lacks gets no step and no
  // trigger, whatever the caller asked for. Without a focuser there is
  // nothing to autofocus; without a guider nothing to guide or dither with;
  // without a filter wheel a filter change is a hand swap (Wait for User)
  // when the user chose a multi-filter plan, and nothing at all for a single
  // filter (it is already in the train).
  final hasFocuser = rig.focuser;
  final guide = startGuiding && rig.guider;
  // #1264 — no guider: the mount's tracking is the exposure ceiling. A
  // Tracking Probe measures it at the target before the first imaging loop
  // and an Unguided Exposure Guard adapts each loop during the run. Both are
  // cooked from the rig like everything else; with a guider they never appear.
  final unguided = !rig.guider;
  final dither = guide ? ditherEveryNExposures : null;
  final afAtStart = autofocusAtStart && hasFocuser;
  final afEvery = hasFocuser ? autofocusEveryNExposures : null;
  final afTemp = hasFocuser && (autofocusAfterTempChangeC ?? 0) > 0
      ? autofocusAfterTempChangeC
      : null;
  final handSwap = manualFilterSwap || !rig.filterWheel;
  // #1560 — one per container: on the block, a target not started by dawn is
  // skipped (no slew, filter or focus); on a loop, no frame starts that would
  // end after dawn. The daemon's TimeCondition also interrupts whatever is
  // still running at dawn (its 1 s watchdog).
  Map<String, dynamic>? dawn() =>
      stopAtDawn == null ? null : _dawnCondition(stopAtDawn);
  if (exposureSeconds <= 0) {
    throw ArgumentError.value(
      exposureSeconds,
      'exposureSeconds',
      'must be > 0 (server rejects it)',
    );
  }
  if (frameCount < 1) {
    throw ArgumentError.value(frameCount, 'frameCount', 'must be >= 1');
  }
  final plan = (filterPlan != null && filterPlan.isNotEmpty)
      ? filterPlan
      : null;
  if (plan != null) {
    for (final step in plan) {
      if (step.exposureSeconds <= 0 ||
          step.frameCount < 1 ||
          step.filterName.trim().isEmpty) {
        throw ArgumentError.value(
          '${step.filterName} ${step.exposureSeconds}s ×${step.frameCount}',
          'filterPlan',
          'each step needs a filter name, exposure > 0 and count >= 1',
        );
      }
    }
  }

  // With a filter wheel, a real SwitchFilter; without one (screw-in filters
  // on an OSC or manual rig), a Wait for User that parks the run in the
  // awaiting-user state until the human has swapped the glass and pressed
  // Resume (S58.12-b).
  Map<String, dynamic> switchTo(String filter) => handSwap
      ? (_item(waitForUserType)
          ..['Text'] =
              'Switch to the ${filter.trim()} filter, then press Resume.')
      : (_item(switchFilterType)..['Filter'] = buildFilterInfo(filter.trim()));

  // One `<name> Imaging` loop: TakeExposure × count, plus the autofocus /
  // dither cadence triggers. The AF cadence is expressed in EXPOSURES, so a
  // plan step with a different sub length re-derives it from the same time
  // cadence the caller computed for the default exposure.
  Map<String, dynamic> imagingLoop({
    required String name,
    required double expSeconds,
    required int count,
  }) {
    final exposure = _item(takeExposureType)
      ..['ExposureTime'] = expSeconds
      ..['Gain'] = gain
      ..['Offset'] = offset;
    if (binning != 1) {
      final bin = Map<String, dynamic>.from(exposure['Binning'] as Map);
      bin['X'] = binning;
      bin['Y'] = binning;
      exposure['Binning'] = bin;
    }

    final loopDef = conditionForType(loopConditionType);
    if (loopDef == null) {
      throw StateError('LoopCondition missing from the condition catalog');
    }
    final loop = loopDef.build()..['Iterations'] = count;

    var imaging = _item(sequentialContainerType);
    imaging['Name'] = name;
    imaging = withChildren(imaging, [exposure]);
    imaging = withConditions(imaging, [loop, ?dawn()]);

    final triggers = <Map<String, dynamic>>[];
    if (afEvery != null) {
      final afTriggerDef = triggerForType(autofocusAfterExposuresType);
      if (afTriggerDef == null) {
        throw StateError(
          'AutofocusAfterExposures missing from the trigger catalog',
        );
      }
      // Keep the TIME cadence constant across differing sub lengths: the
      // caller derived its exposure count at [exposureSeconds].
      final scaled = expSeconds == exposureSeconds
          ? afEvery
          : math.max(
              1,
              (afEvery * exposureSeconds / expSeconds)
                  .round(),
            );
      triggers.add(afTriggerDef.build()..['AfterExposures'] = scaled);
    }
    if (afTemp != null) {
      final def = triggerForType(autofocusAfterTempChangeType);
      if (def == null) {
        throw StateError(
          'AutofocusAfterTemperatureChange missing from the trigger catalog',
        );
      }
      triggers.add(def.build()..['Amount'] = afTemp);
    }
    if (dither != null) {
      final ditherDef = triggerForType(ditherAfterExposuresType);
      if (ditherDef == null) {
        throw StateError(
          'DitherAfterExposures missing from the trigger catalog',
        );
      }
      triggers.add(ditherDef.build()..['AfterExposures'] = dither);
    }
    if (unguided) {
      final guardDef = triggerForType(unguidedExposureGuardType);
      if (guardDef == null) {
        throw StateError(
          'UnguidedExposureGuard missing from the trigger catalog',
        );
      }
      // The planned sub is the longest the guard may step back up to; the
      // thresholds and streak lengths come from Imaging → Unguided. The guard
      // can be switched off there (the probe and re-centre stay).
      if (unguidedOptions.guardEnabled) {
        triggers.add(
          guardDef.build()
            ..['MaxExposure'] = expSeconds
            ..['EccentricityTolerance'] = unguidedOptions.eccentricityTolerance
            ..['GoodEccentricity'] = unguidedOptions.goodEccentricity
            ..['StepDownAfter'] = unguidedOptions.stepDownAfter
            ..['StepUpAfter'] = unguidedOptions.stepUpAfter
            ..['Rungs'] = unguidedOptions.probeRungsSeconds,
        );
      }
      // #1195 — and the periodic re-centre: unguided, nothing else pulls the
      // field back when polar-alignment drift walks it off the framing.
      final driftDef = triggerForType(centerAfterDriftType);
      if (driftDef == null) {
        throw StateError(
          'CenterAfterDriftTrigger missing from the trigger catalog',
        );
      }
      triggers.add(
        driftDef.build()
          ..['Coordinates'] = inputCoordinatesFromDeg(raDeg, decDeg)
          ..['DistanceArcMinutes'] = unguidedOptions.recentreDriftArcmin
          ..['AfterExposures'] = unguidedOptions.recentreSolveEveryN,
      );
    }
    if (triggers.isNotEmpty) imaging = withTriggers(imaging, triggers);
    return imaging;
  }

  // #1264 — the probe's ladder is capped at the longest planned sub of this
  // block (a multi-filter plan may differ per filter; the probe measures once
  // for the whole block and every loop is shortened to the same ceiling).
  Map<String, dynamic> trackingProbe() {
    final longest = plan == null
        ? exposureSeconds
        : plan.map((s) => s.exposureSeconds).reduce(math.max);
    final probe = _item(trackingProbeType)
      ..['MaxExposure'] = longest
      ..['Rungs'] = unguidedOptions.probeRungsSeconds
      ..['EccentricityTolerance'] = unguidedOptions.eccentricityTolerance;
    if (trackingPriorSeconds != null && trackingPriorSeconds > 0) {
      probe['PriorSeconds'] = trackingPriorSeconds;
    }
    return probe;
  }

  // §36/§38 — a dialed framing position angle upgrades the plain slew to a
  // Center and Rotate carrying it (slew + plate-solve centre + rotate). With
  // no PA the plain slew stays; the daemon centres it when "Center target
  // after slew" is on (#1446).
  //
  // A failed go-to skips the rest of this target (SkipInstructionSetOnError)
  // instead of the default ContinueOnError: filter, focus, guiding and every
  // frame after it would run on the wrong patch of sky.
  //
  // With autofocus, the block slews blind, focuses near the target, and only
  // then centres: a solve on an out-of-focus field fails (user, 2026-11-02).
  final focusFirst = afAtStart;
  final goToTarget = (positionAngleDeg != null
      ? (_item(centerAndRotateType)
          ..['Coordinates'] = inputCoordinatesFromDeg(raDeg, decDeg)
          ..['PositionAngle'] = ((positionAngleDeg % 360) + 360) % 360)
      : (_item(slewScopeToRaDecType)
          ..['Coordinates'] = inputCoordinatesFromDeg(raDeg, decDeg)
          // Absent follows Settings → Plate Solving; Simple Mode always centres.
          ..addAll({if (alwaysCenter) 'Center': true})))
    ..['ErrorBehavior'] = skipInstructionSetOnError;
  final blindSlew = _item(slewScopeToRaDecType)
    ..['Coordinates'] = inputCoordinatesFromDeg(raDeg, decDeg)
    ..['Center'] = false
    ..['ErrorBehavior'] = skipInstructionSetOnError;

  final children = <Map<String, dynamic>>[
    // A framing angle on a rig WITHOUT a rotator is the user's job before the
    // run: the Plan screen's "Rotate camera" panel plate-solves the camera
    // against the framing so it can be turned by hand. Center and Rotate then
    // centres (and, without a rotator, leaves the angle as the user set it).
    focusFirst ? blindSlew : goToTarget,
    // Autofocus runs through the plan's FIRST filter (or the single chosen
    // one) — focusing through a random previously-loaded filter would hand
    // the whole session a soft start.
    if (plan != null)
      switchTo(plan.first.filterName)
    else if (filterName != null &&
        filterName.trim().isNotEmpty &&
        rig.filterWheel)
      switchTo(filterName),
    if (afAtStart) _item(runAutofocusType),
    if (focusFirst) goToTarget,
    if (guide) _item(startGuidingType),
    // After focus (the probe judges FWHM growth against its first rung, so it
    // must see focused stars) and before the first imaging loop.
    if (unguided) trackingProbe(),
    // Waits for the sensor: the session switched the cooler on without waiting,
    // so the go-to, focus and guiding ran while it cooled.
    if (coolToC != null)
      _item(coolCameraType)
        ..['Temperature'] = coolToC
        ..['Duration'] = 0.0,
    if (plan != null)
      for (var i = 0; i < plan.length; i++) ...[
        if (i > 0) switchTo(plan[i].filterName),
        imagingLoop(
          name: '${plan[i].filterName.trim()} Imaging',
          expSeconds: plan[i].exposureSeconds,
          count: plan[i].frameCount,
        ),
      ]
    else
      imagingLoop(
        name: 'Imaging',
        expSeconds: exposureSeconds,
        count: frameCount,
      ),
  ];

  var block = _item(sequentialContainerType);
  final name = targetName?.trim();
  block['Name'] = (name != null && name.isNotEmpty) ? name : 'Target';
  block = withChildren(block, children);
  // §38.10a — the block self-terminates when the target sets below the
  // profile's (custom) horizon: the daemon evaluates this at every instruction
  // boundary, so a paused-then-resumed (or simply long-running) target that
  // sank too low ends its block instead of imaging the murk. The target's own
  // coordinates ride in the condition — these generated blocks are plain
  // SequentialContainers, not DSO containers, so there is no parent to
  // inherit them from.
  final horizonDef = conditionForType(aboveHorizonConditionType);
  final loopDef = conditionForType(loopConditionType);
  if (horizonDef == null || loopDef == null) {
    throw StateError('condition catalog is missing a required condition');
  }
  final horizon = horizonDef.build();
  (horizon['Data'] as Map<String, dynamic>)['Coordinates'] =
      inputCoordinatesFromDeg(raDeg, decDeg);
  // A container WITH conditions loops its whole child list while they hold
  // (SequentialStrategy semantics — a lone horizon condition would re-slew,
  // re-focus, and re-image the block all night until the target sets). The
  // conditions are ANDed, so pairing a one-iteration LoopCondition makes the
  // block run ONCE but still end early mid-pass when the target sets
  // (review #876 — proven by SequentialStrategyConditionSemanticsTest).
  final onePass = loopDef.build()..['Iterations'] = 1;
  return withConditions(block, [onePass, horizon, ?dawn()]);
}

/// A copy of [root] with [targetBlock] appended as another target: inserted
/// before the trailing run of session-END steps (Warm Camera / Park Scope)
/// when the session ends with any — the new target must still image before
/// the camera warms up or the scope parks — else at the end. (An imported
/// NINA sequence that wraps its end steps in an "End" CONTAINER is not
/// detected — containers are indistinguishable from target blocks here; see
/// the P1–P5 issue backlog.) Throws [ArgumentError] when [root] isn't a
/// container — the caller should fall back to creating a fresh run instead.
Map<String, dynamic> appendTargetToRunBody(
  Map<String, dynamic> root,
  Map<String, dynamic> targetBlock,
) {
  if (!isContainer(root)) {
    throw ArgumentError.value(
      root[r'$type'],
      'root',
      'the sequence root is not a container',
    );
  }
  final children = childrenOf(root);
  final insertAt = liveAppendIndex(root);
  return withChildren(
    root,
    [...children]..insert(insertAt, inheritDawnStop(root, targetBlock)),
  );
}

/// #1560 — [targetBlock] stopping at the dawn [root]'s targets already stop
/// at (a Simple Mode queue), on the block and on each imaging loop in it, so
/// a target added from the Full UI doesn't image past dawn. Unchanged when
/// [root] has no dawn stop or the block already has one.
Map<String, dynamic> inheritDawnStop(
  Map<String, dynamic> root,
  Map<String, dynamic> targetBlock,
) {
  final dawn = dawnStopOf(root);
  if (dawn == null || _dawnOf(targetBlock) != null) return targetBlock;
  Map<String, dynamic> stops(Map<String, dynamic> n) =>
      withConditions(n, [...conditionsOf(n), _dawnCondition(dawn)]);
  return stops(
    withChildren(targetBlock, [
      for (final c in childrenOf(targetBlock)) isContainer(c) ? stops(c) : c,
    ]),
  );
}

/// The dawn label ('Civil Dawn' …) the first of [root]'s target blocks that
/// has one stops at, or null.
String? dawnStopOf(Map<String, dynamic> root) {
  if (!isContainer(root)) return null;
  for (final c in childrenOf(root)) {
    if (!isContainer(c)) continue;
    if (_dawnOf(c) case final dawn?) return dawn;
  }
  return null;
}

/// The dawn label of [node]'s own dawn `TimeCondition`, or null.
String? _dawnOf(Map<String, dynamic> node) {
  for (final c in conditionsOf(node)) {
    if (c[r'$type'] != timeConditionType) continue;
    final label = timeProviderLabel(c['SelectedProvider']);
    if (label.endsWith(' Dawn')) return label;
  }
  return null;
}

Map<String, dynamic> _dawnCondition(String label) {
  final def = conditionForType(timeConditionType);
  if (def == null) {
    throw StateError('TimeCondition missing from the condition catalog');
  }
  return def.build()..['SelectedProvider'] = timeProviderValue(label);
}

/// The root-child index a new target block should land at: the end, but before
/// any trailing session-end steps (warm/park). Shared by the compose-mood
/// graft above and the §38.9 live-append (which sends the daemon this index).
int liveAppendIndex(Map<String, dynamic> root) {
  if (!isContainer(root)) return 0;
  final children = childrenOf(root);
  var insertAt = children.length;
  while (insertAt > 0 && _isSessionEndStep(children[insertAt - 1])) {
    insertAt--;
  }
  return insertAt;
}

/// A leaf step that belongs at the very end of a session — appending a target
/// after it would image with a warm sensor / a parked mount.
bool _isSessionEndStep(Map<String, dynamic> node) =>
    node[r'$type'] == warmCameraType || node[r'$type'] == parkScopeType;

/// Index of the target block named [targetName] among [root]'s children — the
/// per-target container [buildTargetBlock] emits and [appendTargetToRunBody]
/// grafts — or -1 when absent (or [root] isn't a container). Matched on the
/// container's Name, trimmed and case-insensitive, so a catalog-cased name
/// ("NGC 7092") still finds the block it created.
int indexOfTargetBlock(Map<String, dynamic> root, String targetName) {
  if (!isContainer(root)) return -1;
  final wanted = targetName.trim().toLowerCase();
  if (wanted.isEmpty) return -1;
  final children = childrenOf(root);
  for (var i = 0; i < children.length; i++) {
    final child = children[i];
    if (!isContainer(child)) continue;
    final name = child['Name'];
    if (name is String && name.trim().toLowerCase() == wanted) return i;
  }
  return -1;
}

/// A copy of [root] without the target block named [targetName] — the undo of
/// [appendTargetToRunBody] for one target, leaving the other targets and the
/// session-wide steps (cool / unpark / tracking / warm) untouched. Null when
/// no such block exists (nothing to remove).
Map<String, dynamic>? removeTargetFromRunBody(
  Map<String, dynamic> root,
  String targetName,
) {
  final index = indexOfTargetBlock(root, targetName);
  if (index < 0) return null;
  return withChildren(root, childrenOf(root)..removeAt(index));
}

/// A scheduled start: the run waits until [startAt], cooling 15 minutes
/// before so the camera is cold on time. Replaces the waits of an earlier
/// schedule; a null [startAt] just removes them. The daemon's Wait (until
/// time) compares hours and minutes with its own local clock, so they are
/// written at [daemonUtcOffsetMinutes] (from GET /server/time-sync).
// shortcut: a daemon too old to report its offset (null) gets the client's
// wall clock, right only when both share a time zone.
Map<String, dynamic> scheduleRunBody(
  Map<String, dynamic> root,
  DateTime? startAt, {
  int? daemonUtcOffsetMinutes,
}) {
  final children = childrenOf(root)
      .where((c) => c[r'$type'] != waitForTimeType)
      .toList();
  if (startAt != null) {
    Map<String, dynamic> waitUntil(DateTime instant) {
      final t = daemonUtcOffsetMinutes == null
          ? instant
          : instant.toUtc().add(Duration(minutes: daemonUtcOffsetMinutes));
      return _item(waitForTimeType)
        ..['Hours'] = t.hour
        ..['Minutes'] = t.minute
        ..['Seconds'] = 0;
    }

    final cool = children.indexWhere((c) => c[r'$type'] == coolCameraType);
    if (cool < 0) {
      children.insert(0, waitUntil(startAt));
    } else {
      children
        ..insert(cool + 1, waitUntil(startAt))
        ..insert(
          cool,
          waitUntil(startAt.subtract(const Duration(minutes: 15))),
        );
    }
  }
  return withChildren(root, children);
}

Map<String, dynamic> _item(String type) {
  final def = instructionForType(type);
  if (def == null) {
    // Catalog entries are compile-time constants; a miss means the catalog
    // changed under us — fail loudly rather than emit a body with a hole.
    throw StateError('$type missing from the instruction catalog');
  }
  return def.build();
}

/// How many subs fit the plan: fill [remainingDarkHours] of tonight's window
/// at [exposureSeconds] per sub, clamped to a sane editable range — at least
/// [minFrames] (a run that quits after two subs isn't a session), at most
/// [maxFrames] (the loop count is a starting point the user can raise, not a
/// week of clicking "-"). With no window on the wire, [fallbackFrames].
int defaultFrameCount(
  double exposureSeconds, {
  double? remainingDarkHours,
  int minFrames = 12,
  int maxFrames = 300,
  int fallbackFrames = 60,
}) {
  if (exposureSeconds <= 0) return fallbackFrames;
  if (remainingDarkHours == null || remainingDarkHours <= 0) {
    return fallbackFrames;
  }
  final fits = (remainingDarkHours * 3600.0) ~/ exposureSeconds;
  return math.max(minFrames, math.min(maxFrames, fits));
}

// The `$type` constants this builder assembles all live in the catalogs
// (instruction_catalog / condition_catalog / trigger_catalog) — imported
// above, no local mirrors.
