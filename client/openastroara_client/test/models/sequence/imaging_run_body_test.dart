import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/slew_target_body.dart';

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
      expect(targetTypes, hasLength(3));
      expect(targetTypes[0], contains('.Telescope.SlewScopeToRaDec'));
      expect(targetTypes[1], contains('.Autofocus.RunAutofocus'));
      expect(targetTypes[2], contains('.Container.SequentialContainer'));
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
          .singleWhere((c) => type(c).contains('SlewScopeToRaDec'));
      final coords = slew['Coordinates'] as Map<String, dynamic>;
      // 83.8° = 5h 35m 12s; -5.4° = -5° 24' 0"
      expect(coords['RAHours'], 5);
      expect(coords['RAMinutes'], 35);
      expect(coords['NegativeDec'], true);
      expect(coords['DecDegrees'], 5);
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
        isEmpty,
        reason: 'Center and Rotate owns the slew — a second slew would race it',
      );
      final car = childrenOf(target)
          .singleWhere((c) => type(c).contains('CenterAndRotate'));
      expect(car['PositionAngle'], 137.5);
      final coords = car['Coordinates'] as Map<String, dynamic>;
      expect(coords['RAHours'], 5, reason: 'same target coordinates as a slew');
      expect(coords['DecMinutes'], 24);
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
        hasLength(1),
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
}
