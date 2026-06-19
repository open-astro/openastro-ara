import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/sequencer/sequence_editor_state.dart';
import 'package:openastroara/widgets/sequencer/sequence_field_editor.dart';

Map<String, dynamic> _node(String type) => instructionForType(type)!.build();
const _waitSpan =
    'OpenAstroAra.Sequencer.SequenceItem.Utility.WaitForTimeSpan, OpenAstroAra.Sequencer';
const _startGuiding =
    'OpenAstroAra.Sequencer.SequenceItem.Guider.StartGuiding, OpenAstroAra.Sequencer';
const _setTracking =
    'OpenAstroAra.Sequencer.SequenceItem.Telescope.SetTracking, OpenAstroAra.Sequencer';
const _takeExposure =
    'OpenAstroAra.Sequencer.SequenceItem.Imaging.TakeExposure, OpenAstroAra.Sequencer';
const _slew =
    'OpenAstroAra.Sequencer.SequenceItem.Telescope.SlewScopeToRaDec, OpenAstroAra.Sequencer';

SequenceDetail _detailWith(String type) => SequenceDetail(
      id: 's',
      body: {
        r'$type':
            'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
        'Name': 'root',
        'Items': {
          r'$type': itemsWrapperType,
          r'$values': [_node(type)],
        },
      },
    );

// root → [WaitForTimeSpan, StartGuiding, SetTracking, TakeExposure]
SequenceDetail sampleDetail() => SequenceDetail(
      id: 's',
      body: {
        r'$type':
            'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
        'Name': 'root',
        'Items': {
          r'$type': itemsWrapperType,
          r'$values': [
            _node(_waitSpan),
            _node(_startGuiding),
            _node(_setTracking),
            _node(_takeExposure),
          ],
        },
      },
    );

Future<ProviderContainer> _pump(WidgetTester tester,
    {SequenceDetail? detail, NodePath? select}) async {
  final container = ProviderContainer();
  addTearDown(container.dispose);
  if (detail != null) {
    container.read(sequenceEditorProvider.notifier).load(detail);
    if (select != null) {
      container.read(sequenceEditorProvider.notifier).select(select);
    }
  }
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: SequenceFieldEditor())),
    ),
  );
  return container;
}

Map<String, dynamic> _nodeAt(ProviderContainer c, NodePath p) =>
    nodeAt(c.read(sequenceEditorProvider)!.body, p)!;

void main() {
  testWidgets('placeholder when nothing is selected', (tester) async {
    await _pump(tester, detail: sampleDetail());
    expect(find.text('Select an instruction to edit its settings.'), findsOneWidget);
  });

  testWidgets('edits a number field back into the body', (tester) async {
    // WaitForTimeSpan has exactly one editable field (Time, number).
    final c = await _pump(tester, detail: sampleDetail(), select: const [0]);
    expect(find.text('Wait (duration)'), findsOneWidget);
    await tester.enterText(find.byType(TextField), '45');
    await tester.pump();
    expect(_nodeAt(c, [0])['Time'], 45.0);
  });

  testWidgets('toggles a boolean field', (tester) async {
    final c = await _pump(tester, detail: sampleDetail(), select: const [1]);
    expect(_nodeAt(c, [1])['ForceCalibration'], false);
    await tester.tap(find.byType(Switch));
    await tester.pump();
    expect(_nodeAt(c, [1])['ForceCalibration'], true);
  });

  testWidgets('changes an int-enum dropdown', (tester) async {
    final c = await _pump(tester, detail: sampleDetail(), select: const [2]);
    expect(_nodeAt(c, [2])['TrackingMode'], 0); // Sidereal
    await tester.tap(find.text('Sidereal'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Lunar').last);
    await tester.pumpAndSettle();
    expect(_nodeAt(c, [2])['TrackingMode'], 1); // Lunar
  });

  testWidgets('an out-of-range enum value clamps to null (no Flutter assertion)',
      (tester) async {
    // A body saved with a now-removed TrackingMode variant (99).
    final detail = SequenceDetail(
      id: 's',
      body: {
        r'$type':
            'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
        'Name': 'root',
        'Items': {
          r'$type': itemsWrapperType,
          r'$values': [
            {..._node(_setTracking), 'TrackingMode': 99},
          ],
        },
      },
    );
    await _pump(tester, detail: detail, select: const [0]);
    expect(tester.takeException(), isNull); // would assert if value not clamped
  });

  testWidgets('renders a string-enum dropdown + the binning X×Y editor',
      (tester) async {
    await _pump(tester, detail: sampleDetail(), select: const [3]); // TakeExposure
    expect(find.text('Take Exposure'), findsOneWidget);
    expect(find.text('LIGHT'), findsOneWidget); // ImageType dropdown value
    // Binning now has an inline X × Y editor (no placeholder).
    expect(find.text('×'), findsOneWidget);
    expect(find.textContaining('advanced field'), findsNothing);
  });

  testWidgets('editing binning X writes the whole BinningMode map back',
      (tester) async {
    final c = await _pump(tester, detail: sampleDetail(), select: const [3]);
    await tester.enterText(find.byKey(const Key('Binning_x')), '2');
    await tester.pump();
    final binning = _nodeAt(c, [3])['Binning'] as Map;
    expect(binning['X'], 2);
    expect(binning['Y'], 1); // other axis preserved
    expect(binning[r'$type'], contains('BinningMode')); // $type preserved
  });

  testWidgets('editing binning Y writes only the Y axis', (tester) async {
    final c = await _pump(tester, detail: sampleDetail(), select: const [3]);
    await tester.enterText(find.byKey(const Key('Binning_y')), '3');
    await tester.pump();
    final binning = _nodeAt(c, [3])['Binning'] as Map;
    expect(binning['Y'], 3);
    expect(binning['X'], 1); // X axis untouched
  });

  group('coordinates editor (Slew RA/Dec)', () {
    testWidgets('edits RA hours and a Dec seconds (int + double) in place',
        (tester) async {
      final c = await _pump(tester, detail: _detailWith(_slew), select: const [0]);
      expect(find.text('RA'), findsOneWidget);
      expect(find.text('Dec'), findsOneWidget);

      await tester.enterText(find.byKey(const Key('Coordinates_ra_h')), '12');
      await tester.pump();
      await tester.enterText(find.byKey(const Key('Coordinates_dec_s')), '30.5');
      await tester.pump();

      final coords = _nodeAt(c, [0])['Coordinates'] as Map;
      expect(coords['RAHours'], 12);
      expect(coords['DecSeconds'], 30.5);
      expect(coords['RAMinutes'], 0); // untouched components preserved
      expect(coords[r'$type'], contains('InputCoordinates'));
    });

    testWidgets('RA hours clamps to 23 and corrects the field', (tester) async {
      final c = await _pump(tester, detail: _detailWith(_slew), select: const [0]);
      await tester.enterText(find.byKey(const Key('Coordinates_ra_h')), '99');
      await tester.pump();
      expect((_nodeAt(c, [0])['Coordinates'] as Map)['RAHours'], 23);
      final f = tester.widget<TextField>(find.descendant(
          of: find.byKey(const Key('Coordinates_ra_h')), matching: find.byType(TextField)));
      expect(f.controller!.text, '23');
    });

    testWidgets('Dec at 90° forces minutes/seconds to 0 (pole boundary)',
        (tester) async {
      // Start at 89°30' so the cross-field rule has something to zero.
      final detail = SequenceDetail(
        id: 's',
        body: {
          r'$type':
              'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
          'Name': 'root',
          'Items': {
            r'$type': itemsWrapperType,
            r'$values': [
              {..._node(_slew), 'Coordinates': {
                ...?(_node(_slew)['Coordinates'] as Map?)?.cast<String, dynamic>(),
                'DecDegrees': 89, 'DecMinutes': 30,
              }},
            ],
          },
        },
      );
      final c = await _pump(tester, detail: detail, select: const [0]);
      await tester.enterText(find.byKey(const Key('Coordinates_dec_d')), '90');
      await tester.pump();
      final coords = _nodeAt(c, [0])['Coordinates'] as Map;
      expect(coords['DecDegrees'], 90);
      expect(coords['DecMinutes'], 0); // zeroed at the pole
      expect(coords['DecSeconds'], 0.0);
      // ...and the minutes/seconds fields are now disabled to show the constraint.
      final decM = tester.widget<TextField>(find.descendant(
          of: find.byKey(const Key('Coordinates_dec_m')), matching: find.byType(TextField)));
      expect(decM.enabled, isFalse);
    });

    testWidgets('a second decimal point is rejected, not blanked', (tester) async {
      final c = await _pump(tester, detail: _detailWith(_slew), select: const [0]);
      final dec = find.byKey(const Key('Coordinates_dec_s'));
      await tester.enterText(dec, '30.5'); // valid
      await tester.pump();
      expect((_nodeAt(c, [0])['Coordinates'] as Map)['DecSeconds'], 30.5);
      // A would-be second dot is rejected: the field keeps '30.5', not blank.
      await tester.enterText(dec, '30.5.');
      await tester.pump();
      final f = tester.widget<TextField>(
          find.descendant(of: dec, matching: find.byType(TextField)));
      expect(f.controller!.text, '30.5');
    });

    testWidgets('the ± toggle flips NegativeDec', (tester) async {
      final c = await _pump(tester, detail: _detailWith(_slew), select: const [0]);
      expect((_nodeAt(c, [0])['Coordinates'] as Map)['NegativeDec'], false);
      await tester.tap(find.text('+')); // currently positive
      await tester.pump();
      expect((_nodeAt(c, [0])['Coordinates'] as Map)['NegativeDec'], true);
    });
  });

  testWidgets('binning 0 snaps to 1 in both model and field', (tester) async {
    final c = await _pump(tester, detail: sampleDetail(), select: const [3]);
    await tester.enterText(find.byKey(const Key('Binning_x')), '0');
    await tester.pump();
    expect((_nodeAt(c, [3])['Binning'] as Map)['X'], 1); // model clamped
    final field = tester.widget<TextField>(find.descendant(
      of: find.byKey(const Key('Binning_x')),
      matching: find.byType(TextField),
    ));
    expect(field.controller!.text, '1'); // displayed text corrected (no divergence)
  });
}
