import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/condition_catalog.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/nina_dom.dart' as dom show triggersOf;
import 'package:openastroara/models/sequence/rig_capabilities.dart';
import 'package:openastroara/models/sequence/trigger_catalog.dart';
import 'package:openastroara/util/input_coordinates.dart';
import 'package:openastroara/models/sequence/slew_target_body.dart';

/// The cadence triggers: every loop also carries the flip, pinned in its own group.
List<Map<String, dynamic>> triggersOf(Map<String, dynamic> node) =>
    [for (final t in dom.triggersOf(node)) if (t[r'$type'] != meridianFlipTriggerType) t];

void main() {
  group('buildImagingRunBody', () {
    Map<String, dynamic> build({
      double? coolToC,
      bool warmAtEnd = false,
      bool autofocusAtStart = true,
      int? afEvery,
      String? filterName,
      int binning = 1,
      double? positionAngleDeg,
    }) =>
        buildImagingRunBody(
          raDeg: 83.8,
          decDeg: -5.4,
          targetName: 'M 42',
          exposureSeconds: 120,
          gain: 100,
          offset: 30,
          binning: binning,
          frameCount: 48,
          coolToC: coolToC,
          warmAtEnd: warmAtEnd,
          autofocusAtStart: autofocusAtStart,
          autofocusEveryNExposures: afEvery,
          filterName: filterName,
          positionAngleDeg: positionAngleDeg,
        );

    String type(Map<String, dynamic> node) => node[r'$type'] as String;

    /// The per-target block inside a built session ([buildTargetBlock]'s
    /// output) — the container named after the target.
    Map<String, dynamic> targetBlockOf(Map<String, dynamic> root) =>
        childrenOf(root).singleWhere((c) => c['Name'] == 'M 42');

    test('assembles the full session in run order with the schema envelope',
        () {
      final root = build(coolToC: -10, warmAtEnd: true, afEvery: 60);

      expect(root['schemaVersion'], sequenceSchemaVersion);
      expect(root['Name'], 'M 42');
      expect(type(root), contains('.Container.SequentialContainer'));

      final types = childrenOf(root).map(type).toList();
      expect(types, hasLength(5));
      expect(types[0], contains('.Camera.CoolCamera'));
      expect(types[1], contains('.Telescope.UnparkScope'));
      expect(types[2], contains('.Telescope.SetTracking'));
      expect(types[3], contains('.Container.SequentialContainer'));
      expect(types[4], contains('.Camera.WarmCamera'));

      // The per-target steps live in their own named container so further
      // targets can be appended as siblings.
      final target = targetBlockOf(root);
      final targetTypes = childrenOf(target).map(type).toList();
      expect(targetTypes, hasLength(5));
      expect(targetTypes[0], contains('.Telescope.SlewScopeToRaDec')); // blind
      expect(targetTypes[1], contains('.Autofocus.RunAutofocus'));
      expect(targetTypes[2], contains('.Telescope.SlewScopeToRaDec')); // centres on focused stars
      // Waits for the session's cool, which ran during the slew and focus.
      expect(targetTypes[3], contains('.Camera.CoolCamera'));
      expect(targetTypes[4], contains('.Container.SequentialContainer'));
    });

    test('the imaging loop carries the exposure, loop count and AF trigger',
        () {
      final root = build(afEvery: 60);
      final imaging = childrenOf(targetBlockOf(root))
          .singleWhere((c) => c['Name'] == 'Imaging');

      final conditions = conditionsOf(imaging);
      expect(conditions, hasLength(1));
      expect(type(conditions.single), contains('LoopCondition'));
      expect(conditions.single['Iterations'], 48);

      final triggers = triggersOf(imaging);
      expect(triggers, hasLength(1));
      expect(type(triggers.single), contains('AutofocusAfterExposures'));
      expect(triggers.single['AfterExposures'], 60);

      final exposure = childrenOf(imaging).single;
      expect(type(exposure), contains('.Imaging.TakeExposure'));
      expect(exposure['ExposureTime'], 120);
      expect(exposure['Gain'], 100);
      expect(exposure['Offset'], 30);
      expect(exposure['ImageType'], 'LIGHT');
    });

    test('slew coordinates are the sexagesimal InputCoordinates of the target',
        () {
      final root = build();
      final slew = childrenOf(targetBlockOf(root))
          .firstWhere((c) => type(c).contains('SlewScopeToRaDec'));
      final coords = slew['Coordinates'] as Map<String, dynamic>;
      // 83.8° = 5h 35m 12s; -5.4° = -5° 24' 0"
      expect(coords['RAHours'], 5);
      expect(coords['RAMinutes'], 35);
      expect(coords['NegativeDec'], true);
      expect(coords['DecDegrees'], -5, reason: 'NINA shape: the sign rides on DecDegrees');
      expect(coords['DecMinutes'], 24);
    });

    test(
        'a framing position angle upgrades the slew to a Center and Rotate '
        'carrying it', () {
      final root = build(positionAngleDeg: 137.5);
      final target = targetBlockOf(root);
      final types = childrenOf(target).map(type).toList();
      expect(
        types.where((t) => t.contains('SlewScopeToRaDec')),
        hasLength(1),
        reason: 'one blind slew to focus near the target; Center and Rotate then owns the centring',
      );
      final car = childrenOf(target)
          .singleWhere((c) => type(c).contains('CenterAndRotate'));
      expect(car['PositionAngle'], 137.5);
      final coords = car['Coordinates'] as Map<String, dynamic>;
      expect(coords['RAHours'], 5, reason: 'same target coordinates as a slew');
      expect(coords['DecMinutes'], 24);
    });

    test('a failed go-to skips the rest of the target, not just itself', () {
      for (final pa in [null, 137.5]) {
        final goTo = childrenOf(targetBlockOf(build(positionAngleDeg: pa)))
            .first;
        expect(goTo['ErrorBehavior'], skipInstructionSetOnError,
            reason: 'filter/focus/frames after a failed go-to would shoot '
                'the wrong sky (PA $pa)');
      }
    });

    test('a negative framing angle is normalized into [0, 360)', () {
      final root = build(positionAngleDeg: -10);
      final car = childrenOf(targetBlockOf(root))
          .singleWhere((c) => type(c).contains('CenterAndRotate'));
      expect(car['PositionAngle'], 350.0);
    });

    test('no position angle keeps the plain slew (no solver requirement)', () {
      final root = build();
      final types = childrenOf(targetBlockOf(root)).map(type).toList();
      expect(types.where((t) => t.contains('CenterAndRotate')), isEmpty);
      expect(
        types.where((t) => t.contains('SlewScopeToRaDec')),
        hasLength(2), // blind, then the centring one after autofocus
      );
    });

    test('optional steps drop out when not configured', () {
      final root = build(
          coolToC: null,
          warmAtEnd: false,
          autofocusAtStart: false,
          afEvery: null);
      final types = childrenOf(root).map(type).toList();
      expect(types.any((t) => t.contains('CoolCamera')), isFalse);
      expect(types.any((t) => t.contains('WarmCamera')), isFalse);
      final target = targetBlockOf(root);
      expect(childrenOf(target).map(type).any((t) => t.contains('RunAutofocus')),
          isFalse);
      final imaging = childrenOf(target)
          .singleWhere((c) => c['Name'] == 'Imaging');
      expect(triggersOf(imaging), isEmpty);
    });

    test('a refocus-after-temperature-change amount adds that trigger to each loop; none without a focuser', () {
      List<String> loopTriggers(Map<String, dynamic> b) => [
            for (final l in childrenOf(b).where((c) => (c['Name'] as String? ?? '').endsWith('Imaging')))
              for (final t in triggersOf(l)) t[r'$type'] as String,
          ];
      final b = buildTargetBlock(raDeg: 1, decDeg: 2, exposureSeconds: 60, frameCount: 10, autofocusAfterTempChangeC: 2);
      final loop = childrenOf(b).singleWhere((c) => c['Name'] == 'Imaging');
      final temp = triggersOf(loop).singleWhere((t) => t[r'$type'] == autofocusAfterTempChangeType);
      expect(temp['Amount'], 2.0);
      expect(loopTriggers(buildTargetBlock(raDeg: 1, decDeg: 2, exposureSeconds: 60, frameCount: 10)),
          isNot(contains(autofocusAfterTempChangeType)), reason: 'off unless asked');
      expect(
          loopTriggers(buildTargetBlock(
              raDeg: 1, decDeg: 2, exposureSeconds: 60, frameCount: 10, autofocusAfterTempChangeC: 2,
              rig: RigCapabilities.nothing)),
          isNot(contains(autofocusAfterTempChangeType)),
          reason: 'no focuser, nothing to refocus');
      final run = buildImagingRunBody(raDeg: 1, decDeg: 2, targetName: 'T', exposureSeconds: 60, frameCount: 10,
          autofocusAfterTempChangeC: 1.5);
      expect(loopTriggers(childrenOf(run).singleWhere((c) => c['Name'] == 'T')), contains(autofocusAfterTempChangeType));
    });

    test('a chosen filter emits a SwitchFilter matched by name (slot -1)', () {
      final root = build(filterName: 'Ha');
      final filter = childrenOf(targetBlockOf(root))
          .singleWhere((c) => type(c).contains('SwitchFilter'));
      final info = filter['Filter'] as Map<String, dynamic>;
      expect(info['_name'], 'Ha');
      expect(info['_position'], -1);
      expect(info[r'$type'], filterInfoType);
    });

    test('non-default binning is stamped onto the exposure', () {
      final root = build(binning: 2);
      final imaging = childrenOf(targetBlockOf(root))
          .singleWhere((c) => c['Name'] == 'Imaging');
      final exposure = childrenOf(imaging).single;
      final bin = exposure['Binning'] as Map<String, dynamic>;
      expect(bin['X'], 2);
      expect(bin['Y'], 2);
    });

    test('rejects a non-positive exposure or frame count', () {
      expect(
          () => buildImagingRunBody(
              raDeg: 0, decDeg: 0, exposureSeconds: 0, frameCount: 10),
          throwsArgumentError);
      expect(
          () => buildImagingRunBody(
              raDeg: 0, decDeg: 0, exposureSeconds: 1, frameCount: 0),
          throwsArgumentError);
    });
  });

  group('appendTargetToRunBody', () {
    Map<String, dynamic> session({bool warmAtEnd = true}) => buildImagingRunBody(
          raDeg: 83.8,
          decDeg: -5.4,
          targetName: 'M 42',
          exposureSeconds: 120,
          frameCount: 48,
          coolToC: -10,
          warmAtEnd: warmAtEnd,
        );

    Map<String, dynamic> block(String name) => buildTargetBlock(
          raDeg: 10.7,
          decDeg: 41.3,
          targetName: name,
          exposureSeconds: 120,
          frameCount: 30,
        );

    String type(Map<String, dynamic> node) => node[r'$type'] as String;

    test('inserts the new target before a trailing Warm Camera', () {
      final grown = appendTargetToRunBody(session(), block('M 31'));
      final names = childrenOf(grown).map((c) => c['Name']).toList();
      final types = childrenOf(grown).map(type).toList();
      // ... cool/unpark/track, M 42, M 31, warm — the new target still images
      // before the camera warms up.
      expect(names[names.length - 3], 'M 42');
      expect(names[names.length - 2], 'M 31');
      expect(types.last, contains('.Camera.WarmCamera'));
      // The envelope survives the graft.
      expect(grown['schemaVersion'], sequenceSchemaVersion);
      expect(grown['Name'], 'M 42');
    });

    test('appends at the end when the session has no warm-up step', () {
      final grown = appendTargetToRunBody(session(warmAtEnd: false), block('M 31'));
      expect(childrenOf(grown).last['Name'], 'M 31');
    });

    test('walks back over a trailing warm-up AND park run', () {
      // A hand-edited session ending [..., WarmCamera, ParkScope]: the new
      // target must image BEFORE the scope parks and the camera warms —
      // appending after either would shoot a parked mount with a warm sensor.
      final park = instructionForType(parkScopeType)!.build();
      final base = session(); // ends with WarmCamera
      final withPark = withChildren(base, [...childrenOf(base), park]);

      final grown = appendTargetToRunBody(withPark, block('M 31'));
      final types =
          childrenOf(grown).map((c) => c[r'$type'] as String).toList();
      final names = childrenOf(grown).map((c) => c['Name']).toList();
      expect(names[names.length - 3], 'M 31');
      expect(types[types.length - 2], contains('.Camera.WarmCamera'));
      expect(types.last, contains('.Telescope.ParkScope'));
    });

    test('does not mutate the source body', () {
      final root = session();
      final before = childrenOf(root).length;
      appendTargetToRunBody(root, block('M 31'));
      expect(childrenOf(root), hasLength(before));
    });

    test('rejects a non-container root (caller falls back to a fresh run)', () {
      expect(
          () => appendTargetToRunBody(
              <String, dynamic>{r'$type': takeExposureType}, block('M 31')),
          throwsArgumentError);
    });
  });

  group('removeTargetFromRunBody / indexOfTargetBlock', () {
    Map<String, dynamic> twoTargetSession() {
      final base = buildImagingRunBody(
        raDeg: 83.8,
        decDeg: -5.4,
        targetName: 'M 42',
        exposureSeconds: 120,
        frameCount: 48,
        coolToC: -10,
        warmAtEnd: true,
      );
      return appendTargetToRunBody(
          base,
          buildTargetBlock(
            raDeg: 10.7,
            decDeg: 41.3,
            targetName: 'M 31',
            exposureSeconds: 120,
            frameCount: 30,
          ));
    }

    test('finds a target block by name, case/whitespace-insensitively', () {
      final root = twoTargetSession();
      expect(indexOfTargetBlock(root, 'M 31'), greaterThan(0));
      expect(indexOfTargetBlock(root, '  m 31 '), greaterThan(0));
      expect(indexOfTargetBlock(root, 'M 45'), -1);
      // The session-wide step names (Cool Camera etc.) aren't target blocks the
      // undo should ever touch — but those aren't containers, so they're skipped.
      expect(indexOfTargetBlock(root, 'Cool Camera'), -1);
    });

    test('removes just the named target, leaving the others and the frame', () {
      final root = twoTargetSession();
      final pruned = removeTargetFromRunBody(root, 'M 31')!;
      final names = childrenOf(pruned).map((c) => c['Name']).toList();
      expect(names, contains('M 42'));
      expect(names, isNot(contains('M 31')));
      // The session frame (cool → … → warm) is intact.
      final types =
          childrenOf(pruned).map((c) => c[r'$type'] as String).toList();
      expect(types.first, contains('.Camera.CoolCamera'));
      expect(types.last, contains('.Camera.WarmCamera'));
      expect(pruned['schemaVersion'], sequenceSchemaVersion);
    });

    test('returns null when the target is not present (nothing to remove)', () {
      expect(removeTargetFromRunBody(twoTargetSession(), 'M 45'), isNull);
    });

    test('does not mutate the source body', () {
      final root = twoTargetSession();
      final before = childrenOf(root).length;
      removeTargetFromRunBody(root, 'M 31');
      expect(childrenOf(root), hasLength(before));
    });
  });

  group('focus before centring', () {
    List<Map<String, dynamic>> steps(Map<String, dynamic> b) => childrenOf(childrenOf(b).firstWhere((c) => c['Name'] == 'M31'));
    Map<String, dynamic> body({bool alwaysCenter = false, double? pa, bool af = true}) => buildImagingRunBody(
        raDeg: 10, decDeg: 41, targetName: 'M31', exposureSeconds: 120, frameCount: 10,
        autofocusAtStart: af, positionAngleDeg: pa, alwaysCenter: alwaysCenter);

    test('the target slews blind, focuses, then centres on sharp stars', () {
      final t = steps(body());
      final types = [for (final c in t) c[r'$type']];
      final slew = types.indexOf(slewScopeToRaDecType);
      final focus = types.indexOf(runAutofocusType);
      final centre = types.lastIndexOf(slewScopeToRaDecType);
      expect(slew, 0);
      expect(t[slew]['Center'], false, reason: 'no solve on an out-of-focus field');
      expect(focus, greaterThan(slew));
      expect(centre, greaterThan(focus));
      expect(t[centre]['Center'], isNull, reason: 'the full UI follows Settings → Plate Solving');
      expect(t[slew]['ErrorBehavior'], skipInstructionSetOnError);
      expect(t[centre]['ErrorBehavior'], skipInstructionSetOnError);
    });

    test('Simple Mode always centres', () {
      final t = steps(body(alwaysCenter: true));
      expect(t.lastWhere((c) => c[r'$type'] == slewScopeToRaDecType)['Center'], true);
    });

    test('a framing angle centres and rotates after the focus', () {
      final types = [for (final c in steps(body(pa: 30))) c[r'$type']];
      expect(types.first, slewScopeToRaDecType);
      expect(types.indexOf(centerAndRotateType), greaterThan(types.indexOf(runAutofocusType)));
    });

    test('no autofocus: one go-to, as before', () {
      final t = steps(body(af: false));
      expect(t.where((c) => c[r'$type'] == slewScopeToRaDecType), hasLength(1));
    });
  });

  group('a camera that cannot hold a set-point (#1577)', () {
    bool hasCool(Map<String, dynamic> n) =>
        n[r'$type'] == coolCameraType || childrenOf(n).any(hasCool);

    test('gets no Cool Camera step, session or per-target', () {
      final b = buildImagingRunBody(
          raDeg: 10, decDeg: 41, targetName: 'M31', exposureSeconds: 120, frameCount: 10,
          coolToC: -10, rig: const RigCapabilities(focuser: true, filterWheel: true, guider: true));
      expect(hasCool(b), isFalse);
    });

    test('one that can keeps both', () {
      final b = buildImagingRunBody(
          raDeg: 10, decDeg: 41, targetName: 'M31', exposureSeconds: 120, frameCount: 10,
          coolToC: -10, rig: RigCapabilities.everything);
      expect(hasCool(b), isTrue);
    });
  });

  group('cooling while the plan gets ready', () {
    test('the session starts the cool without waiting; the target waits for it just before the first light', () {
      final b = buildImagingRunBody(
          raDeg: 10, decDeg: 41, targetName: 'M31', exposureSeconds: 120, frameCount: 10, coolToC: -10);
      final cool = childrenOf(b).first;
      expect(cool[r'$type'], coolCameraType);
      expect(cool['WaitForTemperature'], false, reason: 'unpark, slew, centre, focus go on meanwhile');
      final target = childrenOf(b).firstWhere((c) => c['Name'] == 'M31');
      final steps = childrenOf(target);
      final wait = steps.indexWhere((c) => c[r'$type'] == coolCameraType);
      final firstLight = steps.indexWhere((c) => childrenOf(c).any((x) => x[r'$type'] == takeExposureType));
      expect(wait, greaterThan(0), reason: 'after the go-to');
      expect(wait, lessThan(firstLight), reason: 'before the first light frame');
      expect(steps[wait]['Temperature'], -10);
      expect(steps[wait]['WaitForTemperature'] ?? true, true);
      final focus = steps.indexWhere((c) => c[r'$type'] == runAutofocusType);
      expect(focus, lessThan(wait), reason: 'focus runs while it cools');
    });

    test('no cooler target: no cooling steps anywhere', () {
      final b = buildImagingRunBody(raDeg: 10, decDeg: 41, targetName: 'M31', exposureSeconds: 120, frameCount: 10);
      final target = childrenOf(b).firstWhere((c) => c['Name'] == 'M31');
      expect(childrenOf(b).where((c) => c[r'$type'] == coolCameraType), isEmpty);
      expect(childrenOf(target).where((c) => c[r'$type'] == coolCameraType), isEmpty);
    });
  });

  group('scheduleRunBody', () {
    Map<String, dynamic> body({double? cool}) => buildImagingRunBody(
        raDeg: 10, decDeg: 41, targetName: 'M31', exposureSeconds: 120, frameCount: 10, coolToC: cool);
    List<String> types(Map<String, dynamic> b) => [for (final c in childrenOf(b)) c[r'$type'] as String];
    (int, int, int) clock(Map<String, dynamic> w) => (w['Hours'] as int, w['Minutes'] as int, w['Seconds'] as int);

    test('with a cooler: wait until 15 min before, cool, wait until the start, then the rest', () {
      final b = scheduleRunBody(body(cool: -10), DateTime(2026, 11, 2, 21, 47));
      final t = types(b);
      expect(t.take(3), [waitForTimeType, coolCameraType, waitForTimeType]);
      expect(t[3], unparkScopeType);
      expect(clock(childrenOf(b)[0]), (21, 32, 0));
      expect(clock(childrenOf(b)[2]), (21, 47, 0));
    });

    test('without a cooler: one wait first', () {
      final b = scheduleRunBody(body(), DateTime(2026, 11, 3, 0, 10));
      expect(types(b).take(2), [waitForTimeType, unparkScopeType]);
      expect(clock(childrenOf(b)[0]), (0, 10, 0));
    });

    test('scheduling again replaces the waits; no start time removes them', () {
      final once = scheduleRunBody(body(cool: -10), DateTime(2026, 11, 2, 21, 47));
      final again = scheduleRunBody(once, DateTime(2026, 11, 2, 23, 0));
      expect(types(again).where((t) => t == waitForTimeType), hasLength(2));
      expect(clock(childrenOf(again)[2]), (23, 0, 0));
      expect(types(scheduleRunBody(again, null)), types(body(cool: -10)));
    });

    test("the waits are in the daemon's wall clock, not the client's", () {
      // Dark at 23:45 UTC; the daemon runs at UTC-4, so it waits for 19:45 on its own clock.
      final b = scheduleRunBody(body(cool: -10), DateTime.utc(2026, 11, 2, 23, 45), daemonUtcOffsetMinutes: -240);
      expect(clock(childrenOf(b)[0]), (19, 30, 0));
      expect(clock(childrenOf(b)[2]), (19, 45, 0));
      final utc = scheduleRunBody(body(), DateTime.utc(2026, 11, 2, 23, 45).toLocal(), daemonUtcOffsetMinutes: 0);
      expect(clock(childrenOf(utc)[0]), (23, 45, 0));
    });
  });

  group('defaultFrameCount', () {
    test('fills the remaining dark window, clamped to a sane range', () {
      // 6.2 h at 120 s/sub = 186 subs → within range.
      expect(defaultFrameCount(120, remainingDarkHours: 6.2), 186);
      // Tiny window still yields a session, not two subs.
      expect(defaultFrameCount(120, remainingDarkHours: 0.1), 12);
      // A whole night of short subs caps at the editable maximum.
      expect(defaultFrameCount(10, remainingDarkHours: 8), 300);
    });

    test('falls back without a window (or a bogus exposure)', () {
      expect(defaultFrameCount(120), 60);
      expect(defaultFrameCount(120, remainingDarkHours: 0), 60);
      expect(defaultFrameCount(0, remainingDarkHours: 4), 60);
    });
  });

  group('buildTargetBlock is cooked from the rig', () {
    // The night of 2026-10-02: a rig with a guider got a run without guiding.
    // Every device-bound step is gated on the rig, whatever the caller asks.
    List<String?> types(Map<String, dynamic> node) =>
        childrenOf(node).map((c) => c[r'$type'] as String?).toList();
    Map<String, dynamic> block(RigCapabilities rig,
            {List<FilterPlanStep>? plan, String? filter}) =>
        buildTargetBlock(
          raDeg: 1,
          decDeg: 2,
          targetName: 'Veil',
          exposureSeconds: 60,
          frameCount: 10,
          filterName: filter,
          filterPlan: plan,
          startGuiding: true,
          ditherEveryNExposures: 2,
          autofocusEveryNExposures: 30,
          rig: rig,
        );

    test('#1757 flats after the target: one set per filter after its last frame; a screen asked for with no panel', () {
      const plan = [
        FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30),
        FilterPlanStep(filterName: 'Ha', exposureSeconds: 300, frameCount: 20),
      ];
      List<String> kinds(RigCapabilities rig) => [
        for (final k in childrenOf(buildTargetBlock(
            raDeg: 1, decDeg: 2, exposureSeconds: 120, frameCount: 30, filterPlan: plan, rig: rig, flatsAfter: true)))
          k[r'$type'] == sequentialContainerType ? k['Name'] as String : k[r'$type'] as String,
      ];
      final panel = kinds(RigCapabilities.everything);
      expect(panel.where((k) => k == flatPanelFlatsType), hasLength(2));
      expect(panel.indexOf(flatPanelFlatsType), greaterThan(panel.indexOf('Ha Imaging')));
      expect(panel, isNot(contains(waitForUserType)), reason: 'a panel lights itself');
      final noPanel = kinds(RigCapabilities.everything.copyWith(flatPanel: false));
      expect(noPanel.indexOf(waitForUserType), noPanel.indexOf(flatPanelFlatsType) - 2,
          reason: 'asked once, before the first filter switch');
      final none = [
        for (final k in childrenOf(buildTargetBlock(raDeg: 1, decDeg: 2, exposureSeconds: 120, frameCount: 30, filterPlan: plan)))
          k[r'$type'],
      ];
      expect(none, isNot(contains(flatPanelFlatsType)), reason: 'no flats unless asked');
    });

    test('#1264 the probe carries a SPEC-SHEET prior as PriorSeconds only when given (never the stored ceiling)', () {
      Map<String, dynamic> probeOf(Map<String, dynamic> b) =>
          childrenOf(b).singleWhere((c) => c[r'$type'] == trackingProbeType);
      final bare = buildTargetBlock(
          raDeg: 1, decDeg: 2, exposureSeconds: 300, frameCount: 10, rig: RigCapabilities.nothing);
      expect(probeOf(bare)['PriorSeconds'], isNull, reason: 'catalog default stays null');
      final primed = buildTargetBlock(
          raDeg: 1, decDeg: 2, exposureSeconds: 300, frameCount: 10,
          rig: RigCapabilities.nothing, trackingPriorSeconds: 45);
      expect(probeOf(primed)['PriorSeconds'], 45);
      expect(probeOf(primed)['MaxExposure'], 300);
      final viaRun = buildImagingRunBody(
          raDeg: 1, decDeg: 2, exposureSeconds: 120, frameCount: 10,
          rig: RigCapabilities.nothing, trackingPriorSeconds: 30);
      Map<String, dynamic>? findProbe(Map<String, dynamic> node) {
        if (node[r'$type'] == trackingProbeType) return node;
        for (final c in childrenOf(node)) {
          final hit = findProbe(c);
          if (hit != null) return hit;
        }
        return null;
      }
      expect(findProbe(viaRun)!['PriorSeconds'], 30);
    });

    test('#1264 Imaging → Unguided knobs land on the probe, the guard and the re-centre; the guard can be switched off', () {
      const knobs = UnguidedOptions(
        guardEnabled: true, eccentricityTolerance: 0.6, goodEccentricity: 0.35, stepDownAfter: 3, stepUpAfter: 8,
        probeRungsSeconds: [5, 20, 80], recentreDriftArcmin: 4.5, recentreSolveEveryN: 2,
      );
      final b = buildTargetBlock(
          raDeg: 1, decDeg: 2, exposureSeconds: 300, frameCount: 10, rig: RigCapabilities.nothing, unguidedOptions: knobs);
      final probe = childrenOf(b).singleWhere((c) => c[r'$type'] == trackingProbeType);
      expect(probe['Rungs'], [5, 20, 80]);
      expect(probe['EccentricityTolerance'], 0.6, reason: 'the probe judges rungs with the same trailed bar as the guard');
      final loop = childrenOf(b).singleWhere((c) => c['Name'] == 'Imaging');
      final guard = triggersOf(loop).singleWhere((t) => t[r'$type'] == unguidedExposureGuardType);
      expect(guard['EccentricityTolerance'], 0.6);
      expect(guard['GoodEccentricity'], 0.35);
      expect(guard['StepDownAfter'], 3);
      expect(guard['StepUpAfter'], 8);
      expect(guard['Rungs'], [5, 20, 80]);
      expect(guard['MaxExposure'], 300);
      final drift = triggersOf(loop).singleWhere((t) => t[r'$type'] == centerAfterDriftType);
      expect(drift['DistanceArcMinutes'], 4.5);
      expect(drift['AfterExposures'], 2);

      final noGuard = buildTargetBlock(
          raDeg: 1, decDeg: 2, exposureSeconds: 300, frameCount: 10, rig: RigCapabilities.nothing,
          unguidedOptions: const UnguidedOptions(guardEnabled: false));
      final loop2 = childrenOf(noGuard).singleWhere((c) => c['Name'] == 'Imaging');
      expect(triggersOf(loop2).map((t) => t[r'$type']), [centerAfterDriftType], reason: 'probe and re-centre stay');
      expect(childrenOf(noGuard).map((c) => c[r'$type']), contains(trackingProbeType));

      // Defaults build the same body as before the knobs existed.
      final plain = buildTargetBlock(raDeg: 1, decDeg: 2, exposureSeconds: 300, frameCount: 10, rig: RigCapabilities.nothing);
      final plainGuard = triggersOf(childrenOf(plain).singleWhere((c) => c['Name'] == 'Imaging'))
          .singleWhere((t) => t[r'$type'] == unguidedExposureGuardType);
      expect(plainGuard['EccentricityTolerance'], 0.5);
      expect(plainGuard['StepDownAfter'], 2);
      expect(childrenOf(plain).singleWhere((c) => c[r'$type'] == trackingProbeType)['Rungs'], [10, 30, 60, 120]);
    });

    test('a bare rig gets slew + tracking probe + loop: no AF, no guiding, no filter', () {
      final b = block(RigCapabilities.nothing, filter: 'Ha');
      final t = types(b);
      expect(t, isNot(contains(runAutofocusType)));
      expect(t, isNot(contains(startGuidingType)));
      expect(t, isNot(contains(switchFilterType)));
      expect(t, isNot(contains(waitForUserType)));
      // #1264 — no guider is a real branch now: the mount's tracking is the
      // exposure ceiling, so the probe and the guard are the ONLY extras.
      expect(t, [slewScopeToRaDecType, trackingProbeType, sequentialContainerType]);
      final loop = childrenOf(b).singleWhere((c) => c['Name'] == 'Imaging');
      expect(triggersOf(loop).map((x) => x[r'$type']), [unguidedExposureGuardType, centerAfterDriftType],
          reason: 'no focuser → no AF trigger; no guider → no dither, but the exposure guard and the re-centre');
    });

    test('the full rig gets everything the caller asked for', () {
      final b = block(RigCapabilities.everything, filter: 'Ha');
      final t = types(b);
      expect(t, contains(runAutofocusType));
      expect(t, contains(startGuidingType));
      expect(t, contains(switchFilterType));
      final loop = childrenOf(b).singleWhere((c) => c['Name'] == 'Imaging');
      expect(triggersOf(loop).map((x) => x[r'$type']),
          containsAll([autofocusAfterExposuresType, ditherAfterExposuresType]));
    });

    // #1716
    group('recalibrateGuider', () {
      Map<String, dynamic> calibrated(bool on) => buildTargetBlock(
            raDeg: 1,
            decDeg: 2,
            targetName: 'Veil',
            exposureSeconds: 60,
            frameCount: 10,
            startGuiding: true,
            recalibrateGuider: on,
          );
      Map<String, dynamic> flipOf(Map<String, dynamic> b) => dom.triggersOf(
            childrenOf(b).singleWhere((c) => c['Name'] == 'Imaging'),
          ).singleWhere((t) => t[r'$type'] == meridianFlipTriggerType);
      Map<String, dynamic> startOf(Map<String, dynamic> b) =>
          childrenOf(b).singleWhere((c) => c[r'$type'] == startGuidingType);

      test('asks for a calibration each night and after each flip', () {
        final b = calibrated(true);
        expect(startOf(b)['CalibrateEachNight'], isTrue);
        expect(flipOf(b)['RecalibrateGuider'], isTrue);
      });

      test('a plan that does not ask leaves both alone', () {
        final b = calibrated(false);
        expect(startOf(b).containsKey('CalibrateEachNight'), isFalse);
        expect(flipOf(b).containsKey('RecalibrateGuider'), isFalse);
      });
    });

    test('a guider alone adds guiding + dither and nothing else', () {
      final b = block(const RigCapabilities(guider: true));
      expect(types(b), contains(startGuidingType));
      expect(types(b), isNot(contains(runAutofocusType)));
      final loop = childrenOf(b).singleWhere((c) => c['Name'] == 'Imaging');
      expect(triggersOf(loop).map((x) => x[r'$type']), [ditherAfterExposuresType]);
    });

    test('a framing angle without a rotator still centres — the turn by hand is the Plan screen\'s job', () {
      final b = buildTargetBlock(
        raDeg: 1, decDeg: 2, exposureSeconds: 60, frameCount: 10,
        positionAngleDeg: -61,
        rig: const RigCapabilities(focuser: true),
      );
      final t = types(b);
      // No by-hand step inside the run: the Plan screen's Rotate camera panel
      // is where the camera gets turned, before the run starts.
      expect(t.where((x) => (x ?? '').contains('RotateCameraByHand')), isEmpty);
      // Blind slew, autofocus, then Center and Rotate on focused stars.
      expect(t.indexOf(slewScopeToRaDecType), 0);
      final car = t.indexOf(centerAndRotateType);
      expect(car, greaterThan(t.indexOf(runAutofocusType)));
      expect(childrenOf(b)[car]['PositionAngle'], 299.0);
    });

    // #1264 — no guider → the mount's tracking is the exposure ceiling: a
    // Tracking Probe before the first loop and an Unguided Exposure Guard on
    // every loop. A guider makes both disappear.
    test('a rig without a guider gets a tracking probe and an exposure guard', () {
      final b = block(const RigCapabilities(focuser: true, filterWheel: true), filter: 'Ha');
      final t = types(b);
      expect(t, contains(trackingProbeType));
      expect(t.indexOf(trackingProbeType), greaterThan(t.indexOf(runAutofocusType)),
          reason: 'the probe judges FWHM growth, so it runs after focus');
      final loop = childrenOf(b).singleWhere((c) => c['Name'] == 'Imaging');
      expect(t.indexOf(trackingProbeType), lessThan(childrenOf(b).indexOf(loop)));
      final probe = childrenOf(b).singleWhere((c) => c[r'$type'] == trackingProbeType);
      expect(probe['MaxExposure'], 60.0, reason: 'capped at the planned sub');
      final guard = triggersOf(loop).singleWhere((x) => x[r'$type'] == unguidedExposureGuardType);
      expect(guard['MaxExposure'], 60.0);
      expect(guard['TriggerRunner'], isNotNull);
    });

    test('a rig with a guider gets neither the probe nor the guard', () {
      final b = block(RigCapabilities.everything, filter: 'Ha');
      expect(types(b), isNot(contains(trackingProbeType)));
      final loop = childrenOf(b).singleWhere((c) => c['Name'] == 'Imaging');
      expect(triggersOf(loop).map((x) => x[r'$type']), isNot(contains(unguidedExposureGuardType)));
      expect(triggersOf(loop).map((x) => x[r'$type']), isNot(contains(centerAfterDriftType)));
    });

    // #1195 — unguided, nothing pulls the field back when drift walks it off
    // the framing: a Center After Drift on each loop, aimed at the target.
    test('a rig without a guider also gets a Center After Drift aimed at the target', () {
      final b = block(const RigCapabilities(focuser: true, filterWheel: true), filter: 'Ha');
      final loop = childrenOf(b).singleWhere((c) => c['Name'] == 'Imaging');
      final drift = triggersOf(loop).singleWhere((x) => x[r'$type'] == centerAfterDriftType);
      final target = degFromInputCoordinates(drift['Coordinates'])!;
      expect(target.raDeg, closeTo(1, 1e-6));
      expect(target.decDeg, closeTo(2, 1e-6));
      expect(drift['DistanceArcMinutes'], 10.0);
      expect(drift['AfterExposures'], 1);
    });

    test('the probe is capped at the longest planned sub of a multi-filter plan; each guard at its own', () {
      const plan = [
        FilterPlanStep(filterName: 'Ha', exposureSeconds: 300, frameCount: 2),
        FilterPlanStep(filterName: 'OIII', exposureSeconds: 180, frameCount: 2),
      ];
      final b = block(const RigCapabilities(focuser: true, filterWheel: true), plan: plan);
      final probe = childrenOf(b).singleWhere((c) => c[r'$type'] == trackingProbeType);
      expect(probe['MaxExposure'], 300.0);
      final oiii = childrenOf(b).singleWhere((c) => c['Name'] == 'OIII Imaging');
      final guard = triggersOf(oiii).singleWhere((x) => x[r'$type'] == unguidedExposureGuardType);
      expect(guard['MaxExposure'], 180.0);
      expect(types(b).where((x) => x == trackingProbeType), hasLength(1), reason: 'one probe per block');
    });

    test('a multi-filter plan on a rig without a wheel swaps by hand', () {
      const plan = [
        FilterPlanStep(filterName: 'Ha', exposureSeconds: 300, frameCount: 2),
        FilterPlanStep(filterName: 'OIII', exposureSeconds: 300, frameCount: 2),
      ];
      final b = block(const RigCapabilities(focuser: true), plan: plan);
      expect(types(b).where((t) => t == waitForUserType), hasLength(2));
      expect(types(b), isNot(contains(switchFilterType)));
    });
  });

  group('buildTargetBlock filterPlan (smart targets)', () {
    const plan = [
      FilterPlanStep(filterName: 'Ha', exposureSeconds: 300, frameCount: 20),
      FilterPlanStep(filterName: 'OIII', exposureSeconds: 240, frameCount: 24),
    ];

    Map<String, dynamic> block() => buildTargetBlock(
          raDeg: 10,
          decDeg: 20,
          targetName: 'Veil',
          exposureSeconds: 60,
          frameCount: 30,
          filterPlan: plan,
          startGuiding: true,
          ditherEveryNExposures: 2,
          autofocusEveryNExposures: 30, // 30 × 60 s = every 30 min
        );

    List<String?> types(Map<String, dynamic> node) =>
        childrenOf(node).map((c) => c[r'$type'] as String?).toList();

    test('slew -> first-filter switch -> AF -> StartGuiding -> per-filter loops',
        () {
      final b = block();
      final t = types(b);
      expect(t, [
        slewScopeToRaDecType, // blind
        switchFilterType, // Ha, so autofocus runs through it
        runAutofocusType,
        slewScopeToRaDecType, // centres on focused stars
        startGuidingType,
        sequentialContainerType, // Ha Imaging
        switchFilterType, // OIII
        sequentialContainerType, // OIII Imaging
      ]);
      final kids = childrenOf(b);
      expect((kids[1]['Filter'] as Map)['_name'], 'Ha');
      expect(kids[5]['Name'], 'Ha Imaging');
      expect((kids[6]['Filter'] as Map)['_name'], 'OIII');
      expect(kids[7]['Name'], 'OIII Imaging');
    });

    test('each loop carries its own exposure, count, dither + scaled AF', () {
      final kids = childrenOf(block());
      final ha = kids[5];
      final exposure = childrenOf(ha).single;
      expect(exposure['ExposureTime'], 300.0);
      expect(conditionsOf(ha).single['Iterations'], 20);
      final triggers = triggersOf(ha);
      expect(triggers, hasLength(2));
      final dither = triggers
          .firstWhere((t) => t[r'$type'] == ditherAfterExposuresType);
      expect(dither['AfterExposures'], 2);
      final af = triggers.firstWhere(
          (t) => t[r'$type'] == autofocusAfterExposuresType);
      // 30 exposures at 60 s = every 30 min -> 6 exposures at 300 s.
      expect(af['AfterExposures'], 6);
    });

    test('no guiding -> no StartGuiding and no dither trigger', () {
      final b = buildTargetBlock(
        raDeg: 1,
        decDeg: 2,
        exposureSeconds: 60,
        frameCount: 10,
        filterPlan: plan,
      );
      expect(types(b), isNot(contains(startGuidingType)));
      final loops = childrenOf(b)
          .where((c) => c[r'$type'] == sequentialContainerType);
      for (final loop in loops) {
        expect(triggersOf(loop), isEmpty);
      }
    });

    test('an invalid plan step throws', () {
      expect(
        () => buildTargetBlock(
          raDeg: 1,
          decDeg: 2,
          exposureSeconds: 60,
          frameCount: 10,
          filterPlan: const [
            FilterPlanStep(filterName: ' ', exposureSeconds: 60, frameCount: 5),
          ],
        ),
        throwsArgumentError,
      );
    });

    test('empty plan falls back to the single default loop', () {
      final b = buildTargetBlock(
        raDeg: 1,
        decDeg: 2,
        exposureSeconds: 60,
        frameCount: 10,
        filterPlan: const [],
      );
      final loops = childrenOf(b)
          .where((c) => c[r'$type'] == sequentialContainerType)
          .toList();
      expect(loops.single['Name'], 'Imaging');
      expect(conditionsOf(loops.single).single['Iterations'], 10);
    });

    test('a rig that does not pause for a filter swap gets no Wait-for-User',
        () {
      final b = buildTargetBlock(
        raDeg: 10,
        decDeg: 20,
        exposureSeconds: 60,
        frameCount: 10,
        filterPlan: plan,
        rig: const RigCapabilities(pauseForFilterSwap: false),
      );
      final types = childrenOf(b).map((c) => c[r'$type'] as String?).toList();
      expect(types, isNot(contains(waitForUserType)));
      expect(types, isNot(contains(switchFilterType)));
    });

    test('manualFilterSwap emits Wait-for-User pauses instead of SwitchFilter',
        () {
      final b = buildTargetBlock(
        raDeg: 10,
        decDeg: 20,
        exposureSeconds: 60,
        frameCount: 10,
        filterPlan: plan,
        manualFilterSwap: true,
      );
      final kids = childrenOf(b);
      final types = kids.map((c) => c[r'$type'] as String?).toList();
      expect(types, isNot(contains(switchFilterType)));
      final waits =
          kids.where((c) => c[r'$type'] == waitForUserType).toList();
      expect(waits, hasLength(2));
      expect(waits[0]['Text'], contains('Ha'));
      expect(waits[1]['Text'], contains('OIII'));
      expect(waits[0]['Text'], contains('Resume'));
    });
  });

  // §38.10a — every generated target block carries an AboveHorizonCondition
  // with the TARGET's own coordinates, so a target that sets below the
  // profile's horizon ends its block instead of imaging the murk.
  group('the meridian flip', () {
    List<String> flips(Map<String, dynamic> node) =>
        [for (final t in (node['Triggers'] as Map<String, dynamic>?)?['\$values'] as List? ?? const []) (t as Map)['\$type'] as String];

    // On the block it fired before the first slew, from wherever the mount
    // happened to be (simulator run, 2026-11-09).
    test('the flip trigger is on the imaging loop, not the block that slews', () {
      final block = buildTargetBlock(raDeg: 1, decDeg: 2, exposureSeconds: 60, frameCount: 10);
      expect(flips(block).where((t) => t.contains('MeridianFlipTrigger')), isEmpty);
      final loop = childrenOf(block).singleWhere((c) => c['Name'] == 'Imaging');
      expect(flips(loop).where((t) => t.contains('MeridianFlipTrigger')), hasLength(1));
    });

    test('a filter plan has one on each loop', () {
      final block = buildTargetBlock(raDeg: 1, decDeg: 2, exposureSeconds: 60, frameCount: 10, filterPlan: const [
        FilterPlanStep(filterName: 'L', exposureSeconds: 60, frameCount: 5),
        FilterPlanStep(filterName: 'R', exposureSeconds: 60, frameCount: 5),
      ]);
      final loops = childrenOf(block).where((c) => (c['Name'] as String? ?? '').endsWith('Imaging'));
      expect(loops, hasLength(2));
      for (final loop in loops) {
        expect(flips(loop).where((t) => t.contains('MeridianFlipTrigger')), hasLength(1));
      }
    });
  });

  group('target block horizon condition', () {
    test('buildTargetBlock attaches AboveHorizonCondition with the target coords',
        () {
      final block = buildTargetBlock(
        raDeg: 83.8,
        decDeg: -5.4,
        targetName: 'M 42',
        exposureSeconds: 120,
        frameCount: 10,
      );
      final conditions = conditionsOf(block);
      // Paired with a one-iteration LoopCondition: conditions turn a container
      // into a LOOP (ANDed), so the lone horizon guard would re-run the whole
      // block all night — see SequentialStrategyConditionSemanticsTest.
      final loop = conditions
          .where((c) => (c[r'$type'] as String).contains('LoopCondition'))
          .single;
      expect(loop['Iterations'], 1);
      final horizon = conditions
          .where((c) => (c[r'$type'] as String).contains('AboveHorizonCondition'))
          .single;
      final data = horizon['Data'] as Map<String, dynamic>;
      final coords = data['Coordinates'] as Map<String, dynamic>;
      // 83.8° RA = 5h 35m 12s; dec −5.4° = −5° 24′ 0″.
      expect(coords['RAHours'], 5);
      expect(coords['NegativeDec'], isTrue);
      expect(coords['DecDegrees'], -5, reason: 'NINA shape: the sign rides on DecDegrees');
      expect(coords['DecMinutes'], 24);
    });

    test('the full run body carries the condition on every target block', () {
      final body = buildImagingRunBody(
        raDeg: 10.68,
        decDeg: 41.27,
        targetName: 'M 31',
        exposureSeconds: 60,
        frameCount: 5,
        warmAtEnd: true,
      );
      final target = childrenOf(body)
          .where((c) => c['Name'] == 'M 31')
          .single;
      expect(
          conditionsOf(target)
              .any((c) => (c[r'$type'] as String).contains('AboveHorizonCondition')),
          isTrue);
    });
  });

  group('stop at dawn and park at the end (#1560)', () {
    Map<String, dynamic> session({String? dawn, bool park = false, List<FilterPlanStep>? plan}) =>
        buildImagingRunBody(
          raDeg: 10,
          decDeg: 41,
          targetName: 'M31',
          exposureSeconds: 120,
          frameCount: 10,
          warmAtEnd: true,
          filterPlan: plan,
          stopAtDawn: dawn,
          parkAtEnd: park,
        );
    const lrgb = [
      FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 10),
      FilterPlanStep(filterName: 'R', exposureSeconds: 120, frameCount: 5),
    ];
    String type(Map<String, dynamic> n) => n[r'$type'] as String;
    List<Map<String, dynamic>> dawnsOf(Map<String, dynamic> n) =>
        [for (final c in conditionsOf(n)) if (type(c) == timeConditionType) c];
    // Every node in the tree, the root first.
    Iterable<Map<String, dynamic>> all(Map<String, dynamic> n) sync* {
      yield n;
      for (final c in childrenOf(n)) {
        yield* all(c);
      }
    }
    List<Map<String, dynamic>> loopsOf(Map<String, dynamic> block) =>
        [for (final c in childrenOf(block)) if ((c['Name'] as String? ?? '').endsWith('Imaging')) c];

    test('by default: no dawn condition and no park anywhere (the Full UI body stays as it is)', () {
      final b = session(plan: lrgb);
      expect(all(b).where((n) => dawnsOf(n).isNotEmpty), isEmpty);
      expect(all(b).where((n) => type(n) == parkScopeType), isEmpty);
    });

    for (final label in ['Civil Dawn', 'Nautical Dawn', 'Astronomical Dawn']) {
      test('$label: one dawn condition on the target block and on each imaging loop', () {
        final block = childrenOf(session(dawn: label, plan: lrgb)).singleWhere((c) => c['Name'] == 'M31');
        final loops = loopsOf(block);
        expect(loops, hasLength(2));
        for (final n in [block, ...loops]) {
          final d = dawnsOf(n);
          expect(d, hasLength(1), reason: n['Name'] as String);
          expect(d.single['SelectedProvider'], {r'$type': timeProviders[label]});
        }
      });
    }

    test('park at the end: Park Scope after the targets, before Warm Camera', () {
      final kids = childrenOf(session(park: true)).map(type).toList();
      expect(kids.sublist(kids.length - 2), [parkScopeType, warmCameraType]);
      expect(kids.where((t) => t == parkScopeType), hasLength(1));
      final noWarm = buildImagingRunBody(
          raDeg: 10, decDeg: 41, targetName: 'M31', exposureSeconds: 120, frameCount: 10, parkAtEnd: true);
      expect(type(childrenOf(noWarm).last), parkScopeType);
    });

    test('an appended target lands above the park and carries the dawn condition too', () {
      final grown = appendTargetToRunBody(
        session(dawn: 'Nautical Dawn', park: true),
        buildTargetBlock(
            raDeg: 83.8, decDeg: -5.4, targetName: 'M42', exposureSeconds: 60, frameCount: 5,
            stopAtDawn: 'Nautical Dawn'),
      );
      final kids = childrenOf(grown);
      final m42 = kids.indexWhere((c) => c['Name'] == 'M42');
      expect(m42, kids.indexWhere((c) => type(c) == parkScopeType) - 1);
      for (final n in [kids[m42], ...loopsOf(kids[m42])]) {
        expect(dawnsOf(n).single['SelectedProvider'], {r'$type': timeProviders['Nautical Dawn']});
      }
    });

    test('withDawnStop sets, switches and clears the dawn on every block and loop; the rest stays', () {
      final two = appendTargetToRunBody(
        session(park: true, plan: lrgb),
        buildTargetBlock(raDeg: 83.8, decDeg: -5.4, targetName: 'M42', exposureSeconds: 60, frameCount: 5),
      );
      List<Object?> provs(Map<String, dynamic> b) => [
            for (final c in childrenOf(b).where(isContainer))
              for (final n in [c, ...childrenOf(c).where(isContainer)])
                for (final d in dawnsOf(n)) d['SelectedProvider'],
          ];
      final civil = withDawnStop(two, 'Civil Dawn');
      // M31's block and its two loops, M42's block and its loop.
      expect(provs(civil), List.filled(5, {r'$type': timeProviders['Civil Dawn']}));
      final nautical = withDawnStop(civil, 'Nautical Dawn');
      expect(provs(nautical), List.filled(5, {r'$type': timeProviders['Nautical Dawn']}), reason: 'replaced, not added');
      final none = withDawnStop(nautical, null);
      expect(provs(none), isEmpty);
      expect(none, two, reason: 'clearing gives back the body it started from');
    });

    test('a scheduled start keeps the park and the dawn conditions', () {
      final b = session(dawn: 'Astronomical Dawn', park: true);
      final s = scheduleRunBody(b, DateTime(2026, 11, 2, 21, 47));
      expect(type(childrenOf(s).first), waitForTimeType);
      expect(childrenOf(s).skip(1).toList(), childrenOf(b));
    });
  });
}
