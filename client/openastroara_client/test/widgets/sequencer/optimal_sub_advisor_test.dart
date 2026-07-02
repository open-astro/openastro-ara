import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/services/optimal_sub_api.dart';
import 'package:openastroara/state/sequencer/sequence_editor_state.dart';
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

/// Canned-response fake; records the filter it was asked about.
class _FakeApi implements OptimalSubApi {
  _FakeApi(this.result, {this.unavailable, this.notFound = false});
  final OptimalSubResult? result;
  final String? unavailable;
  final bool notFound;
  String? askedFilter;

  @override
  Future<OptimalSubResult?> get({String? filter, double? bandwidthNm}) async {
    askedFilter = filter;
    if (unavailable != null) throw OptimalSubUnavailable(unavailable!);
    if (notFound) return null;
    return result;
  }

  @override
  void close() {}
}

const _viable = OptimalSubResult(
  skyFluxEPerSecPerPx: 0.07,
  floorSec: 300,
  ceilingSec: 24000,
  viable: true,
  limitingBound: 'readnoisefloor',
  recommendedSec: 300,
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

  group('OptimalSubAdvisor', () {
    late ProviderContainer container;

    Future<void> pumpAdvisor(WidgetTester tester, _FakeApi api,
        {String? filterName}) async {
      container = ProviderContainer(overrides: [
        optimalSubApiProvider.overrideWith((ref) => api),
      ]);
      addTearDown(container.dispose);
      container.read(sequenceEditorProvider.notifier).load(smartExposureDetail());
      await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: MaterialApp(
          home: Scaffold(
            body: OptimalSubAdvisor(path: const [1], filterName: filterName),
          ),
        ),
      ));
      await tester.pump(); // resolve the fetch future
    }

    testWidgets('renders the figure, window, filter and attribution',
        (tester) async {
      final api = _FakeApi(_viable);
      await pumpAdvisor(tester, api, filterName: 'Ha 7nm');

      expect(api.askedFilter, 'Ha 7nm');
      expect(find.textContaining('Optimal Sub: 5.0 min'), findsOneWidget);
      expect(find.textContaining('usable window'), findsOneWidget);
      expect(find.textContaining('For filter "Ha 7nm"'), findsOneWidget);
      expect(find.textContaining('Dr. Robin Glover'), findsOneWidget,
          reason: 'the attribution is a condition of the permission');
    });

    testWidgets('Apply writes ONLY ExposureTime into the raw body',
        (tester) async {
      await pumpAdvisor(tester, _FakeApi(_viable), filterName: 'Ha 7nm');
      final before = container.read(sequenceEditorProvider)!.body;

      await tester.tap(find.text('Apply'));
      await tester.pump();

      final after = container.read(sequenceEditorProvider)!.body;
      final exposure = nodeAt(after, [1])!;
      expect(exposure['ExposureTime'], 300.0, reason: 'a plain double — NINA-valid');
      expect(exposure['Gain'], 100, reason: 'sibling fields untouched');
      expect(exposure[r'$type'], takeExposureType, reason: 'no \$type churn');
      final beforeSwitch = nodeAt(before, [0]);
      final afterSwitch = nodeAt(after, [0]);
      expect(afterSwitch, beforeSwitch, reason: 'other nodes untouched');
    });

    testWidgets('a collapsed window renders the saturation-limited warning',
        (tester) async {
      const collapsed = OptimalSubResult(
        skyFluxEPerSecPerPx: 50,
        floorSec: 100,
        ceilingSec: 40,
        viable: false,
        limitingBound: 'saturationceiling',
        recommendedSec: 40,
      );
      await pumpAdvisor(tester, _FakeApi(collapsed));
      expect(find.textContaining('Saturation-limited: 40 s max'), findsOneWidget);
    });

    testWidgets('assumed defaults render the set-them-up hint', (tester) async {
      const assumed = OptimalSubResult(
        skyFluxEPerSecPerPx: 1.5,
        floorSec: 70,
        ceilingSec: 26000,
        viable: true,
        limitingBound: 'readnoisefloor',
        recommendedSec: 70,
        assumedDefaults: ['read_noise_e', 'filter_bandwidth_nm'],
      );
      await pumpAdvisor(tester, _FakeApi(assumed));
      expect(find.textContaining('generic defaults for read noise, filter bandwidth'),
          findsOneWidget);
    });

    testWidgets('a 400 from the daemon shows its detail quietly', (tester) async {
      await pumpAdvisor(
          tester, _FakeApi(null, unavailable: 'Set up your optics first.'));
      expect(find.text('Set up your optics first.'), findsOneWidget);
      expect(find.text('Apply'), findsNothing);
    });

    testWidgets('a 404 (older daemon) renders nothing at all', (tester) async {
      await pumpAdvisor(tester, _FakeApi(null, notFound: true));
      expect(find.textContaining('Optimal Sub'), findsNothing);
      expect(find.textContaining('Glover'), findsNothing);
      expect(find.text('Apply'), findsNothing);
    });

    testWidgets('a stale slow response cannot overwrite a newer one',
        (tester) async {
      // First request (filter A) resolves SLOWLY; the filter then changes and
      // the second request (filter B) resolves fast. When A's response finally
      // lands it must be dropped — the advisor keeps B's figure.
      final slowA = Completer<OptimalSubResult?>();
      final api = _CompleterFakeApi({
        'A': slowA.future,
        'B': Future.value(_viable), // 300 s → "5.0 min"
      });
      container = ProviderContainer(overrides: [
        optimalSubApiProvider.overrideWith((ref) => api),
      ]);
      addTearDown(container.dispose);
      container.read(sequenceEditorProvider.notifier).load(smartExposureDetail());

      Widget host(String filter) => UncontrolledProviderScope(
            container: container,
            child: MaterialApp(
              home: Scaffold(
                body: OptimalSubAdvisor(path: const [1], filterName: filter),
              ),
            ),
          );

      await tester.pumpWidget(host('A'));
      await tester.pump();
      await tester.pumpWidget(host('B')); // filter change → second fetch
      await tester.pump();
      expect(find.textContaining('Optimal Sub: 5.0 min'), findsOneWidget,
          reason: "B's fast response renders");

      // Now the stale A response arrives — with a very different figure.
      slowA.complete(const OptimalSubResult(
        skyFluxEPerSecPerPx: 10,
        floorSec: 2,
        ceilingSec: 4000,
        viable: true,
        limitingBound: 'readnoisefloor',
        recommendedSec: 2,
      ));
      await tester.pump();
      expect(find.textContaining('Optimal Sub: 5.0 min'), findsOneWidget,
          reason: "the newer result survives; A's stale 2 s must not overwrite it");
      expect(find.textContaining('Optimal Sub: 2 s'), findsNothing);
    });
  });
}

/// Per-filter canned futures, so a test can hold one response open (a
/// [Completer]) while another resolves — the stale-response race harness.
class _CompleterFakeApi implements OptimalSubApi {
  _CompleterFakeApi(this.byFilter);
  final Map<String, Future<OptimalSubResult?>> byFilter;

  @override
  Future<OptimalSubResult?> get({String? filter, double? bandwidthNm}) =>
      byFilter[filter] ?? Future.value(null);

  @override
  void close() {}
}
