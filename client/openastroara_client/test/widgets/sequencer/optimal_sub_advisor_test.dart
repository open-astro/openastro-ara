import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart'
    show takeExposureType;
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/sequencer/sequence_editor_state.dart';
import 'package:openastroara/state/settings/camera_electronics_state.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/util/optimal_sub_local.dart';
import 'package:openastroara/util/input_coordinates.dart';
import 'package:openastroara/widgets/sequencer/optimal_sub_advisor.dart';

const _switchFilter =
    'OpenAstroAra.Sequencer.SequenceItem.FilterWheel.SwitchFilter, OpenAstroAra.Sequencer';

/// A SmartExposure-shaped body: root container → [SwitchFilter(Ha), TakeExposure].
SequenceDetail smartExposureDetail() => SequenceDetail(
      id: 'seq-1',
      name: 'Veil',
      body: {
        'schemaVersion': 'openastroara-sequence-v1',
        r'$type':
            'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
        'Name': 'root',
        'Items': {
          r'$type': itemsWrapperType,
          r'$values': [
            {
              r'$type': _switchFilter,
              'Filter': {'Name': 'Ha 7nm', 'Position': 4},
            },
            {r'$type': takeExposureType, 'ExposureTime': 60.0, 'Gain': 100},
          ],
        },
      },
    );

void main() {
  group('filterNameForExposure', () {
    test('finds the SwitchFilter sibling ahead of the exposure', () {
      final body = smartExposureDetail().body;
      expect(filterNameForExposure(body, [1]), 'Ha 7nm');
    });

    test('the nearest preceding switch wins; later switches are ignored', () {
      final body = {
        r'$type': 'X.Seq',
        'Items': {
          r'$type': itemsWrapperType,
          r'$values': [
            {
              r'$type': _switchFilter,
              'Filter': {'Name': 'L'},
            },
            {
              r'$type': _switchFilter,
              'Filter': {'Name': 'OIII'},
            },
            {r'$type': takeExposureType, 'ExposureTime': 10.0},
            {
              r'$type': _switchFilter,
              'Filter': {'Name': 'SII'},
            },
          ],
        },
      };
      expect(filterNameForExposure(body, [2]), 'OIII');
    });

    test('no switch before it (or a nameless filter) → null', () {
      final body = {
        r'$type': 'X.Seq',
        'Items': {
          r'$type': itemsWrapperType,
          r'$values': [
            {r'$type': takeExposureType, 'ExposureTime': 10.0},
            {r'$type': _switchFilter, 'Filter': null},
            {r'$type': takeExposureType, 'ExposureTime': 10.0},
          ],
        },
      };
      expect(filterNameForExposure(body, [0]), isNull);
      expect(filterNameForExposure(body, [2]), isNull,
          reason: 'a null Filter (unset SwitchFilter) names nothing');
      expect(filterNameForExposure(body, []), isNull, reason: 'root has no siblings');
    });
  });

  group('targetPositionForExposure', () {
    const slewType =
        'OpenAstroAra.Sequencer.SequenceItem.Telescope.SlewScopeToRaDec, OpenAstroAra.Sequencer';
    const dsoType =
        'OpenAstroAra.Sequencer.Container.DeepSkyObjectContainer, OpenAstroAra.Sequencer';

    Map<String, dynamic> items(List<Map<String, dynamic>> values) =>
        {r'$type': itemsWrapperType, r'$values': values};

    test('a preceding slew names the position — converted to DEGREES', () {
      final body = {
        r'$type': 'X.Seq',
        'Items': items([
          {r'$type': slewType, 'Coordinates': inputCoordinatesFromDeg(83.822, -5.391)},
          {r'$type': takeExposureType, 'ExposureTime': 10.0},
        ]),
      };
      final pos = targetPositionForExposure(body, [1]);
      expect(pos, isNotNull);
      expect(pos!.raDeg, closeTo(83.822, 1e-6),
          reason: 'NINA stores RA in HOURS — the scan must hand back degrees');
      expect(pos.decDeg, closeTo(-5.391, 1e-6));
    });

    test('an enclosing DSO container target wins over an outer slew', () {
      final body = {
        r'$type': 'X.Seq',
        'Items': items([
          {r'$type': slewType, 'Coordinates': inputCoordinatesFromDeg(10.0, 10.0)},
          {
            r'$type': dsoType,
            'Target': {
              'TargetName': 'M42',
              'InputCoordinates': inputCoordinatesFromDeg(83.822, -5.391),
            },
            'Items': items([
              {r'$type': takeExposureType, 'ExposureTime': 10.0},
            ]),
          },
        ]),
      };
      final pos = targetPositionForExposure(body, [1, 0]);
      expect(pos!.raDeg, closeTo(83.822, 1e-6),
          reason: 'the DSO container wraps the exposure more tightly');
    });

    test('a zeroed default position (fresh instruction) counts as not-set', () {
      final body = {
        r'$type': 'X.Seq',
        'Items': items([
          {r'$type': slewType, 'Coordinates': inputCoordinatesFromDeg(0, 0)},
          {r'$type': takeExposureType, 'ExposureTime': 10.0},
        ]),
      };
      expect(targetPositionForExposure(body, [1]), isNull,
          reason: 'RA 0h Dec 0° is the catalog default, not a chosen target');
    });

    test('an Inherited slew with stale coordinates yields to the DSO target', () {
      // NINA resolves Inherited: true from the enclosing target at runtime; the
      // node's own Coordinates may be leftovers from before the toggle and must
      // not shadow the container's actual target.
      const centerType =
          'OpenAstroAra.Sequencer.SequenceItem.Platesolving.CenterAndRotate, OpenAstroAra.Sequencer';
      final body = {
        r'$type': dsoType,
        'Target': {
          'TargetName': 'M42',
          'InputCoordinates': inputCoordinatesFromDeg(83.822, -5.391),
        },
        'Items': items([
          {
            r'$type': centerType,
            'Inherited': true,
            'Coordinates': inputCoordinatesFromDeg(200.0, 50.0), // stale
          },
          {r'$type': takeExposureType, 'ExposureTime': 10.0},
        ]),
      };
      final pos = targetPositionForExposure(body, [1]);
      expect(pos!.raDeg, closeTo(83.822, 1e-6),
          reason: 'the inherited slew is skipped; the DSO target is the truth');
      expect(pos.decDeg, closeTo(-5.391, 1e-6));
    });

    test('a non-inherited CenterAndRotate names the position like a slew', () {
      const centerType =
          'OpenAstroAra.Sequencer.SequenceItem.Platesolving.CenterAndRotate, OpenAstroAra.Sequencer';
      final body = {
        r'$type': 'X.Seq',
        'Items': items([
          {
            r'$type': centerType,
            'Inherited': false,
            'Coordinates': inputCoordinatesFromDeg(150.0, 20.0),
          },
          {r'$type': takeExposureType, 'ExposureTime': 10.0},
        ]),
      };
      final pos = targetPositionForExposure(body, [1]);
      expect(pos!.raDeg, closeTo(150.0, 1e-6));
      expect(pos.decDeg, closeTo(20.0, 1e-6));
    });

    test('no coordinates anywhere → null (pure Glover window)', () {
      expect(targetPositionForExposure(smartExposureDetail().body, [1]), isNull);
    });
  });


  group('OptimalSubAdvisor (local compute)', () {
    late ProviderContainer container;

    Future<void> pumpAdvisor(
      WidgetTester tester, {
      String? filterName,
      ({double raDeg, double decDeg})? targetPosition,
      OpticsSettings optics = _rigOptics,
      CameraElectronics electronics = _rigElectronics,
      FilterSetSettings filterSet = _rigFilters,
      SiteSettings site = _rigSite,
    }) async {
      container = ProviderContainer(overrides: [
        opticsSettingsProvider.overrideWith(() => _SeededOptics(optics)),
        cameraElectronicsProvider
            .overrideWith(() => _SeededElectronics(electronics)),
        filterSetProvider.overrideWith(() => _SeededFilters(filterSet)),
        siteSettingsProvider.overrideWith(() => _SeededSite(site)),
      ]);
      addTearDown(container.dispose);
      container.read(sequenceEditorProvider.notifier).load(smartExposureDetail());
      await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: MaterialApp(
          home: Scaffold(
            body: OptimalSubAdvisor(
                path: const [1],
                filterName: filterName,
                targetPosition: targetPosition),
          ),
        ),
      ));
      await tester.pump(); // settle the compute future
    }

    testWidgets('renders the figure, filter and attribution from local math',
        (tester) async {
      await pumpAdvisor(tester, filterName: 'Ha 7nm');
      expect(find.textContaining('Optimal Sub:'), findsOneWidget);
      expect(find.textContaining('usable window'), findsOneWidget);
      expect(find.textContaining('For filter "Ha 7nm"'), findsOneWidget);
      expect(find.textContaining('Dr. Robin Glover'), findsOneWidget,
          reason: 'the attribution is a condition of the permission');
    });

    testWidgets('Apply writes ONLY ExposureTime, matching the local resolver',
        (tester) async {
      await pumpAdvisor(tester, filterName: 'Ha 7nm');
      final before = container.read(sequenceEditorProvider)!.body;

      await tester.tap(find.text('Apply'));
      await tester.pump();

      // The expected figure comes from the SAME resolver the advisor uses.
      final expected = (resolveOptimalSubLocal(
        optics: _rigOptics,
        electronics: _rigElectronics,
        site: _rigSite,
        filterSet: _rigFilters,
        filterName: 'Ha 7nm',
      ) as LocalOptimalSubSuccess)
          .result
          .recommendedSec
          .roundToDouble();

      final after = container.read(sequenceEditorProvider)!.body;
      final exposure = nodeAt(after, [1])!;
      expect(exposure['ExposureTime'], expected,
          reason: 'a plain double — NINA-valid');
      expect(exposure['Gain'], 100, reason: 'sibling fields untouched');
      expect(exposure[r'$type'], takeExposureType, reason: 'no \$type churn');
      expect(nodeAt(after, [0]), nodeAt(before, [0]),
          reason: 'other nodes untouched');
    });

    testWidgets('a collapsed window renders the saturation-limited warning',
        (tester) async {
      // Huge read noise over a tiny well: floor >> ceiling.
      await pumpAdvisor(tester,
          electronics: const CameraElectronics(
              readNoiseE: 100, fullWellE: 500, quantumEfficiencyPeak: 0.8));
      expect(find.textContaining('Saturation-limited'), findsOneWidget);
      expect(find.text('Apply'), findsOneWidget); // still applicable
    });

    testWidgets('unset electronics render the generic-defaults hint',
        (tester) async {
      await pumpAdvisor(tester, electronics: const CameraElectronics());
      expect(find.textContaining('generic defaults for'), findsOneWidget);
      expect(find.textContaining('read noise'), findsOneWidget);
    });

    testWidgets('a target position renders the local star line',
        (tester) async {
      await pumpAdvisor(tester,
          filterName: 'Ha 7nm',
          targetPosition: (raDeg: 83.822, decDeg: -5.391));
      expect(find.textContaining('stars/sub'), findsOneWidget);
    });

    testWidgets('no target → no star line', (tester) async {
      await pumpAdvisor(tester, filterName: 'Ha 7nm');
      expect(find.textContaining('stars/sub'), findsNothing);
    });

    testWidgets('an unknown filter shows the actionable detail quietly',
        (tester) async {
      await pumpAdvisor(tester, filterName: 'H-alpha');
      expect(find.textContaining("Unknown filter 'H-alpha'"), findsOneWidget);
      expect(find.text('Open filter set'), findsOneWidget);
      expect(find.text('Apply'), findsNothing);
    });

    testWidgets('an incomplete imaging train points at optics', (tester) async {
      await pumpAdvisor(tester,
          optics: const OpticsSettings(
              focalLengthMm: 0,
              reducerFactor: 1,
              sensorWidthPx: 0,
              sensorHeightPx: 0,
              pixelSizeUm: 0,
              apertureMm: 0));
      expect(find.textContaining('imaging train is incomplete'), findsOneWidget);
      expect(find.text('Open optics'), findsOneWidget);
      expect(find.text('Apply'), findsNothing);
    });
  });
}

// ── Seeded-notifier doubles + the reference rig ──────────────────────────────

const _rigOptics = OpticsSettings(
    focalLengthMm: 250,
    reducerFactor: 1.0,
    sensorWidthPx: 6248,
    sensorHeightPx: 4176,
    pixelSizeUm: 3.76,
    apertureMm: 51);
const _rigElectronics = CameraElectronics(
    sensorName: 'IMX571',
    readNoiseE: 1.5,
    fullWellE: 51000,
    quantumEfficiencyPeak: 0.8);
const _rigFilters = FilterSetSettings(filters: [
  PlanningFilter(name: 'Ha 7nm', kind: FilterKind.ha, bandwidthNm: 7),
  PlanningFilter(name: 'L', kind: FilterKind.l, bandwidthNm: 0),
]);
const _rigSite = SiteSettings(
    siteName: 'test',
    latitudeDeg: 34,
    longitudeDeg: -84,
    bortleClass: 6,
    typicalSeeingArcsec: 2.5);

class _SeededOptics extends OpticsSettingsNotifier {
  _SeededOptics(this._v);
  final OpticsSettings _v;
  @override
  OpticsSettings build() => _v;
}

class _SeededElectronics extends CameraElectronicsNotifier {
  _SeededElectronics(this._v);
  final CameraElectronics _v;
  @override
  CameraElectronics build() => _v;
}

class _SeededFilters extends FilterSetNotifier {
  _SeededFilters(this._v);
  final FilterSetSettings _v;
  @override
  FilterSetSettings build() => _v;
}

class _SeededSite extends SiteSettingsNotifier {
  _SeededSite(this._v);
  final SiteSettings _v;
  @override
  SiteSettings build() => _v;
}
