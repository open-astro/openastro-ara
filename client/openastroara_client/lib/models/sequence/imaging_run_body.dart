import 'dart:math' as math;

import '../../util/input_coordinates.dart';
import 'condition_catalog.dart';
import 'instruction_catalog.dart';
import 'nina_dom.dart';
import 'slew_target_body.dart';
import 'trigger_catalog.dart';

/// §36 Planning → "Create Run": build a COMPLETE, runnable imaging session for
/// a target, not just a slew. The tree (all `$type`s are registered prototypes
/// in the daemon's `HeadlessSequencerFactory`, so every step executes for real
/// against connected equipment — simulators included):
///
/// ```
/// SequentialContainer  (named after the target)
/// ├─ Cool Camera        (when a cooler target is configured)
/// ├─ Unpark Scope
/// ├─ Set Tracking       (sidereal)
/// ├─ Target SequentialContainer  (named after the target — see [buildTargetBlock])
/// │   ├─ Slew to RA/Dec     (the target's J2000 coordinates)
/// │   ├─ Switch Filter      (when a filter is chosen — matched by name)
/// │   ├─ Run Autofocus      (when enabled)
/// │   └─ "Imaging" SequentialContainer
/// │        Conditions: Loop (× frame count)
/// │        Triggers:  Autofocus After Exposures (when a cadence is set)
/// │        └─ Take Exposure (Imaging Defaults: time / gain / offset / binning, LIGHT)
/// └─ Warm Camera        (when "warm up at session end" is on)
/// ```
///
/// The per-target steps live in their own container (not flat at the root) so
/// a session can hold SEVERAL targets: "Add to Sequence" with a sequence
/// already open appends another target block via [appendTargetToRunBody]
/// instead of creating a parallel run. Session-wide steps (cool / unpark /
/// tracking / warm) stay at the root and are emitted once.
///
/// Deliberately omitted until the daemon supports them end-to-end:
/// - `StartGuiding` / `Dither`: the daemon's guider mediator is still the
///   headless stub — both would fail validation on every run and read as
///   FAILED steps in the report.
/// - `CenterAndRotate`: the rotate half isn't ported; the instruction throws
///   when a rotator IS connected, so a plain slew is the reliable default.
///   (The framing position angle is therefore still not acted on — the §36
///   sequencer-fidelity TODO in `stellarium_view.dart` stands.)
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
  double? positionAngleDeg,
}) {
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
    positionAngleDeg: positionAngleDeg,
  );

  // ── The session around it, in run order ──────────────────────────────────
  final children = <Map<String, dynamic>>[
    if (coolToC != null)
      _item(coolCameraType)
        ..['Temperature'] = coolToC
        // 0 = regulate as fast as the driver allows; a fixed multi-minute ramp
        // would stall a run against hardware that's already at temperature.
        ..['Duration'] = 0.0,
    _item(unparkScopeType),
    _item(setTrackingType), // catalog default: sidereal
    target,
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

/// One target's worth of session — a SequentialContainer named after the
/// target holding slew → optional filter switch → optional autofocus → the
/// Take-Exposure loop. This is the unit [buildImagingRunBody] wraps once and
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
  double? positionAngleDeg,
}) {
  if (exposureSeconds <= 0) {
    throw ArgumentError.value(
      exposureSeconds,
      'exposureSeconds',
      'must be > 0 (daemon rejects it)',
    );
  }
  if (frameCount < 1) {
    throw ArgumentError.value(frameCount, 'frameCount', 'must be >= 1');
  }

  // ── The imaging loop: TakeExposure × frameCount ──────────────────────────
  final exposure = _item(takeExposureType)
    ..['ExposureTime'] = exposureSeconds
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
  final loop = loopDef.build()..['Iterations'] = frameCount;

  var imaging = _item(sequentialContainerType);
  imaging['Name'] = 'Imaging';
  imaging = withChildren(imaging, [exposure]);
  imaging = withConditions(imaging, [loop]);

  if (autofocusEveryNExposures != null) {
    final afTriggerDef = triggerForType(autofocusAfterExposuresType);
    if (afTriggerDef == null) {
      throw StateError(
        'AutofocusAfterExposures missing from the trigger catalog',
      );
    }
    final afTrigger = afTriggerDef.build()
      ..['AfterExposures'] = autofocusEveryNExposures;
    imaging = withTriggers(imaging, [afTrigger]);
  }

  // §36/§38 — a dialed framing position angle upgrades the plain slew to a
  // Center and Rotate carrying it (slew + plate-solve centre + rotate); with
  // no PA the blind slew stays, so runs never require a solver the user
  // didn't opt into by framing with rotation.
  final goToTarget = positionAngleDeg != null
      ? (_item(centerAndRotateType)
          ..['Coordinates'] = inputCoordinatesFromDeg(raDeg, decDeg)
          ..['PositionAngle'] = ((positionAngleDeg % 360) + 360) % 360)
      : (_item(slewScopeToRaDecType)
          ..['Coordinates'] = inputCoordinatesFromDeg(raDeg, decDeg));

  final children = <Map<String, dynamic>>[
    goToTarget,
    if (filterName != null && filterName.trim().isNotEmpty)
      _item(switchFilterType)..['Filter'] = buildFilterInfo(filterName.trim()),
    if (autofocusAtStart) _item(runAutofocusType),
    imaging,
  ];

  var block = _item(sequentialContainerType);
  final name = targetName?.trim();
  block['Name'] = (name != null && name.isNotEmpty) ? name : 'Target';
  return withChildren(block, children);
}

/// A copy of [root] with [targetBlock] appended as another target: inserted
/// before the trailing run of session-END steps (Warm Camera / Park Scope)
/// when the session ends with any — the new target must still image before
/// the camera warms up or the scope parks — else at the end. (An imported
/// NINA sequence that wraps its end steps in an "End" CONTAINER is not
/// detected — containers are indistinguishable from target blocks here; see
/// design/PORT_TODO.md.) Throws [ArgumentError] when [root] isn't a
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
  var insertAt = children.length;
  while (insertAt > 0 && _isSessionEndStep(children[insertAt - 1])) {
    insertAt--;
  }
  return withChildren(root, [...children]..insert(insertAt, targetBlock));
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
