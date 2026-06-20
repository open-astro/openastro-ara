import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/condition_catalog.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/trigger_catalog.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/settings/filter_wheel_labels_state.dart';
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
const _switchFilter =
    'OpenAstroAra.Sequencer.SequenceItem.FilterWheel.SwitchFilter, OpenAstroAra.Sequencer';
const _altitude =
    'OpenAstroAra.Sequencer.Conditions.AltitudeCondition, OpenAstroAra.Sequencer';

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

    testWidgets('a trailing decimal point does not snap the field mid-edit',
        (tester) async {
      final c = await _pump(tester, detail: _detailWith(_slew), select: const [0]);
      final dec = find.byKey(const Key('Coordinates_dec_s'));
      await tester.enterText(dec, '30.5');
      await tester.pump();
      // Backspace to '30.' (mid-edit) — must NOT commit 30.0 or rewrite to '30'.
      await tester.enterText(dec, '30.');
      await tester.pump();
      final f = tester.widget<TextField>(
          find.descendant(of: dec, matching: find.byType(TextField)));
      expect(f.controller!.text, '30.'); // field left as-is
      expect((_nodeAt(c, [0])['Coordinates'] as Map)['DecSeconds'], 30.5); // model unchanged
    });

    testWidgets('an out-of-range loaded seconds value displays clamped (not 60)',
        (tester) async {
      // A server body with 59.9995 must NOT display as a rounded-up "60".
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
                'DecSeconds': 59.9995,
              }},
            ],
          },
        },
      );
      final c = await _pump(tester, detail: detail, select: const [0]);
      final f = tester.widget<TextField>(find.descendant(
          of: find.byKey(const Key('Coordinates_dec_s')), matching: find.byType(TextField)));
      expect(f.controller!.text, '59.999'); // displayed in range
      // The model stays verbatim until the user actually edits it.
      expect((_nodeAt(c, [0])['Coordinates'] as Map)['DecSeconds'], 59.9995);
    });

    testWidgets('an ordinary decimal (30.3) is not corrupted by truncation',
        (tester) async {
      // 30.3 is stored as 30.2999…; a naive *1000-floor would show '30.299'.
      final c = await _pump(tester, detail: _detailWith(_slew), select: const [0]);
      final dec = find.byKey(const Key('Coordinates_dec_s'));
      await tester.enterText(dec, '30.3');
      await tester.pump(); // commit → rebuild → didUpdateWidget reformats
      final f = tester.widget<TextField>(
          find.descendant(of: dec, matching: find.byType(TextField)));
      expect(f.controller!.text, '30.3'); // not '30.299'
      expect((_nodeAt(c, [0])['Coordinates'] as Map)['DecSeconds'], 30.3);
    });

    testWidgets('an in-range value truncates (not rounds) on display', (tester) async {
      // 29.9995 must show '29.999', not a rounded-up '30'.
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
                'DecSeconds': 29.9995,
              }},
            ],
          },
        },
      );
      await _pump(tester, detail: detail, select: const [0]);
      final f = tester.widget<TextField>(find.descendant(
          of: find.byKey(const Key('Coordinates_dec_s')), matching: find.byType(TextField)));
      expect(f.controller!.text, '29.999');
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

  group('container Name editor', () {
    testWidgets('a selected container shows a Name field, no "No settings" line',
        (tester) async {
      // The root SequentialContainer (named "root") is selected.
      await _pump(tester, detail: sampleDetail(), select: const []);
      expect(find.text('Name'), findsOneWidget); // the field label
      expect(find.byKey(const Key('container_name')), findsOneWidget);
      expect(find.text('No settings — this instruction runs as-is.'), findsNothing);
    });

    testWidgets('editing the Name writes it back to the container and goes dirty',
        (tester) async {
      final c = await _pump(tester, detail: sampleDetail(), select: const []);
      await tester.enterText(find.byKey(const Key('container_name')), 'Lights');
      await tester.pump();
      expect(_nodeAt(c, const [])['Name'], 'Lights');
      expect(c.read(sequenceEditorProvider)!.isDirty, isTrue);
    });

    testWidgets('a leaf instruction has no Name editor', (tester) async {
      // StartGuiding (index 1) is a leaf with a boolean field, not a Name.
      await _pump(tester, detail: sampleDetail(), select: const [1]);
      expect(find.text('Name'), findsNothing);
    });

    testWidgets('reflects an external Name change for the still-selected container',
        (tester) async {
      // The field owns a controller + didUpdateWidget so an external mutation
      // (e.g. a future undo/redo) of the same node's Name updates the display.
      final c = await _pump(tester, detail: sampleDetail(), select: const []);
      c.read(sequenceEditorProvider.notifier).setNodeField(const [], 'Name', 'Renamed externally');
      await tester.pump();
      final field = tester.widget<TextField>(find.byKey(const Key('container_name')));
      expect(field.controller!.text, 'Renamed externally');
    });
  });

  group('container conditions editor', () {
    testWidgets('shows the Conditions header and empty state for a bare container',
        (tester) async {
      await _pump(tester, detail: sampleDetail(), select: const []);
      expect(find.text('Conditions'), findsOneWidget);
      expect(find.text('No loop conditions — the container runs once.'), findsOneWidget);
    });

    testWidgets('adding a Loop condition via the menu appends it to the body',
        (tester) async {
      final c = await _pump(tester, detail: sampleDetail(), select: const []);
      // Tooltip-scoped: the Triggers section has its own add button too.
      await tester.tap(find.byTooltip('Add condition'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Loop (iterations)').last);
      await tester.pumpAndSettle();
      final conditions = conditionsOf(_nodeAt(c, const []));
      expect(conditions, hasLength(1));
      expect(conditions.single[r'$type'], contains('LoopCondition'));
      expect(conditions.single['Iterations'], 2); // catalog default
      expect(find.text('No loop conditions — the container runs once.'), findsNothing);
    });

    testWidgets('editing a condition field writes via setConditionFieldOn',
        (tester) async {
      final c = await _pump(tester, detail: sampleDetail(), select: const []);
      c.read(sequenceEditorProvider.notifier).addConditionTo(
          const [],
          conditionForType(
              'OpenAstroAra.Sequencer.Conditions.LoopCondition, OpenAstroAra.Sequencer')!);
      await tester.pump();
      // Iterations is a bounded (min 1) field → a clamped _NumField (TextField).
      await tester.enterText(
        find.descendant(
          of: find.byKey(const ValueKey('/cond/0/Iterations')),
          matching: find.byType(TextField),
        ),
        '15',
      );
      await tester.pump();
      expect(conditionsOf(_nodeAt(c, const [])).single['Iterations'], 15);
    });

    testWidgets('a bounded condition field clamps the entry (Minutes max 59)',
        (tester) async {
      final c = await _pump(tester, detail: sampleDetail(), select: const []);
      c.read(sequenceEditorProvider.notifier).addConditionTo(
          const [],
          conditionForType(
              'OpenAstroAra.Sequencer.Conditions.TimeSpanCondition, OpenAstroAra.Sequencer')!);
      await tester.pump();
      final minutes = find.descendant(
        of: find.byKey(const ValueKey('/cond/0/Minutes')),
        matching: find.byType(TextField),
      );
      await tester.enterText(minutes, '99');
      await tester.pump();
      expect(conditionsOf(_nodeAt(c, const [])).single['Minutes'], 59); // clamped
      // and the displayed text was corrected to the clamped value
      expect(tester.widget<TextField>(minutes).controller!.text, '59');
    });

    testWidgets('TimeCondition When dropdown sets a sky-event provider',
        (tester) async {
      final c = await _pump(tester, detail: sampleDetail(), select: const []);
      c.read(sequenceEditorProvider.notifier).addConditionTo(
          const [],
          conditionForType(
              'OpenAstroAra.Sequencer.Conditions.TimeCondition, OpenAstroAra.Sequencer')!);
      await tester.pump();
      expect(find.text('Custom time'), findsOneWidget); // default provider label
      await tester.tap(find.text('Custom time'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Civil Dusk').last);
      await tester.pumpAndSettle();
      final provider = conditionsOf(_nodeAt(c, const [])).single['SelectedProvider'];
      expect(provider, isA<Map>());
      expect((provider as Map)[r'$type'], contains('CivilDuskProvider'));
      // After the write-back rebuild, the controlled dropdown reflects the new
      // selection (a FormField.initialValue would have stuck on 'Custom time').
      expect(find.text('Civil Dusk'), findsOneWidget);
      expect(find.text('Custom time'), findsNothing);
      // …and the now-inert H/M/S fields are disabled (the daemon computes them).
      final hours = find.byKey(const ValueKey('/cond/0/Hours'));
      expect(
        tester
            .widgetList<IgnorePointer>(
                find.ancestor(of: hours, matching: find.byType(IgnorePointer)))
            .any((w) => w.ignoring),
        isTrue,
      );
    });

    testWidgets('TimeCondition H/M/S are enabled in Custom-time mode',
        (tester) async {
      final c = await _pump(tester, detail: sampleDetail(), select: const []);
      c.read(sequenceEditorProvider.notifier).addConditionTo(
          const [],
          conditionForType(
              'OpenAstroAra.Sequencer.Conditions.TimeCondition, OpenAstroAra.Sequencer')!);
      await tester.pump();
      // Default is Custom time (null provider) → H/M/S editable: no ignoring wrap.
      final hours = find.byKey(const ValueKey('/cond/0/Hours'));
      expect(
        tester
            .widgetList<IgnorePointer>(
                find.ancestor(of: hours, matching: find.byType(IgnorePointer)))
            .any((w) => w.ignoring),
        isFalse,
      );
    });

    testWidgets('removing a condition deletes it from the body', (tester) async {
      final c = await _pump(tester, detail: sampleDetail(), select: const []);
      c.read(sequenceEditorProvider.notifier).addConditionTo(
          const [],
          conditionForType(
              'OpenAstroAra.Sequencer.Conditions.LoopCondition, OpenAstroAra.Sequencer')!);
      await tester.pump();
      expect(conditionsOf(_nodeAt(c, const [])), hasLength(1));
      await tester.tap(find.byIcon(Icons.delete_outline));
      await tester.pump();
      expect(conditionsOf(_nodeAt(c, const [])), isEmpty);
    });
  });

  group('altitude condition WaitLoopData editor', () {
    Future<ProviderContainer> pumpAltitude(WidgetTester tester) async {
      final c = await _pump(tester, detail: sampleDetail(), select: const []);
      c
          .read(sequenceEditorProvider.notifier)
          .addConditionTo(const [], conditionForType(_altitude)!);
      await tester.pump();
      return c;
    }

    Map<String, dynamic> data(ProviderContainer c) =>
        conditionsOf(_nodeAt(c, const [])).single['Data'] as Map<String, dynamic>;

    testWidgets('renders the comparator/offset/coordinates controls', (tester) async {
      await pumpAltitude(tester);
      expect(find.byKey(const Key('Data_comparator')), findsOneWidget);
      expect(find.byKey(const Key('Data_offset')), findsOneWidget);
      expect(find.byKey(const Key('Data_coords_ra_h')), findsOneWidget);
    });

    testWidgets('picking a comparator writes Data.Comparator', (tester) async {
      final c = await pumpAltitude(tester);
      expect(data(c)['Comparator'], 1); // AltitudeCondition default = LessThan
      await tester.tap(find.byKey(const Key('Data_comparator')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Greater than (>)').last);
      await tester.pumpAndSettle();
      expect(data(c)['Comparator'], 3);
    });

    testWidgets('a double-typed comparator (1.0) is read, not defaulted to 3',
        (tester) async {
      // A serializer that emits the enum as 1.0 must still read as LessThan (1),
      // not silently coerce to GreaterThan (an `is int` check would miss it).
      final detail = SequenceDetail(
        id: 's',
        body: {
          r'$type':
              'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
          'Name': 'root',
          'Items': {r'$type': itemsWrapperType, r'$values': <Map<String, dynamic>>[]},
          'Conditions': {
            r'$type': conditionsWrapperType,
            r'$values': [
              {
                ...conditionForType(_altitude)!.build(),
                'Data': {
                  ...conditionForType(_altitude)!.build()['Data'] as Map<String, dynamic>,
                  'Comparator': 1.0,
                },
              },
            ],
          },
        },
      );
      await _pump(tester, detail: detail, select: const []);
      expect(find.text('Less than (<)'), findsOneWidget); // read as 1, not 3
    });

    testWidgets('editing the offset writes Data.Offset (signed)', (tester) async {
      final c = await pumpAltitude(tester);
      await tester.enterText(
        find.descendant(
          of: find.byKey(const Key('Data_offset')),
          matching: find.byType(TextField),
        ),
        '-12.5',
      );
      await tester.pump();
      expect(data(c)['Offset'], -12.5);
    });

    testWidgets('the offset clamps to the ±90° altitude domain', (tester) async {
      final c = await pumpAltitude(tester);
      final field = find.descendant(
        of: find.byKey(const Key('Data_offset')),
        matching: find.byType(TextField),
      );
      await tester.enterText(field, '-200');
      await tester.pump();
      expect(data(c)['Offset'], -90.0); // clamped to the lower bound
      expect(tester.widget<TextField>(field).controller!.text, '-90');
    });

    testWidgets('a partial "-" offset entry commits nothing (model unchanged)',
        (tester) async {
      final c = await pumpAltitude(tester);
      expect(data(c)['Offset'], 30.0); // AltitudeCondition default
      await tester.enterText(
        find.descendant(
          of: find.byKey(const Key('Data_offset')),
          matching: find.byType(TextField),
        ),
        '-',
      );
      await tester.pump();
      // '-' doesn't parse → no write; the model keeps its prior value (not 0).
      expect(data(c)['Offset'], 30.0);
    });

    testWidgets('a Data missing Comparator falls back to the condition default',
        (tester) async {
      // AltitudeCondition → LessThan(1); AboveHorizonCondition → GreaterThan(3).
      for (final (type, label) in [
        (_altitude, 'Less than (<)'),
        ('OpenAstroAra.Sequencer.Conditions.AboveHorizonCondition, OpenAstroAra.Sequencer',
            'Greater than (>)'),
      ]) {
        final data = conditionForType(type)!.build()['Data'] as Map<String, dynamic>;
        data.remove('Comparator'); // a body that arrived without the key
        final detail = SequenceDetail(
          id: 's',
          body: {
            r'$type':
                'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
            'Name': 'root',
            'Items': {r'$type': itemsWrapperType, r'$values': <Map<String, dynamic>>[]},
            'Conditions': {
              r'$type': conditionsWrapperType,
              r'$values': [
                {...conditionForType(type)!.build(), 'Data': data},
              ],
            },
          },
        );
        await _pump(tester, detail: detail, select: const []);
        expect(find.text(label), findsOneWidget, reason: type);
      }
    });

    testWidgets('a stored comparator outside the allow-list coerces (no assert)',
        (tester) async {
      // A body persisted with GreaterThanOrEqual (4) — not user-selectable.
      final detail = SequenceDetail(
        id: 's',
        body: {
          r'$type':
              'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
          'Name': 'root',
          'Items': {r'$type': itemsWrapperType, r'$values': <Map<String, dynamic>>[]},
          'Conditions': {
            r'$type': conditionsWrapperType,
            r'$values': [
              {
                ...conditionForType(_altitude)!.build(),
                'Data': {
                  ...conditionForType(_altitude)!.build()['Data'] as Map<String, dynamic>,
                  'Comparator': 4,
                },
              },
            ],
          },
        },
      );
      final c = await _pump(tester, detail: detail, select: const []);
      expect(tester.takeException(), isNull); // no DropdownButton assert
      // The dropdown coerces to THIS condition's default (AltitudeCondition →
      // Less than), not a fixed GreaterThan bias, and never a blank.
      expect(find.text('Less than (<)'), findsOneWidget);
      // Editing an UNRELATED field preserves the raw stored comparator (4) —
      // coercion is display-only and never silently flips the loop direction.
      await tester.enterText(
        find.descendant(
          of: find.byKey(const Key('Data_offset')),
          matching: find.byType(TextField),
        ),
        '15',
      );
      await tester.pump();
      expect(
        (conditionsOf(_nodeAt(c, const [])).single['Data'] as Map)['Comparator'],
        4,
      );
    });

    testWidgets('editing the target coordinates writes Data.Coordinates', (tester) async {
      final c = await pumpAltitude(tester);
      await tester.enterText(
        find.descendant(
          of: find.byKey(const Key('Data_coords_ra_h')),
          matching: find.byType(TextField),
        ),
        '12',
      );
      await tester.pump();
      expect((data(c)['Coordinates'] as Map)['RAHours'], 12);
      // The other Data fields survive a coordinates edit.
      expect(data(c)['Comparator'], 1);
      expect(data(c)['Offset'], 30.0);
    });
  });

  group('container triggers editor', () {
    testWidgets('shows the Triggers header and empty state for a bare container',
        (tester) async {
      await _pump(tester, detail: sampleDetail(), select: const []);
      expect(find.text('Triggers'), findsOneWidget);
      expect(find.text('No triggers.'), findsOneWidget);
    });

    testWidgets('adding Meridian Flip via the menu appends it to the body',
        (tester) async {
      final c = await _pump(tester, detail: sampleDetail(), select: const []);
      await tester.tap(find.byTooltip('Add trigger'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Meridian Flip').last);
      await tester.pumpAndSettle();
      final triggers = triggersOf(_nodeAt(c, const []));
      expect(triggers, hasLength(1));
      expect(triggers.single[r'$type'], contains('MeridianFlipTrigger'));
      expect(triggers.single['TriggerRunner'], isA<Map>()); // empty runner built
      expect(find.text('No triggers.'), findsNothing);
    });

    testWidgets('removing a trigger deletes it from the body', (tester) async {
      final c = await _pump(tester, detail: sampleDetail(), select: const []);
      c.read(sequenceEditorProvider.notifier).addTriggerTo(
          const [],
          triggerForType(
              'OpenAstroAra.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger, OpenAstroAra.Sequencer')!);
      await tester.pump();
      expect(triggersOf(_nodeAt(c, const [])), hasLength(1));
      await tester.tap(find.byIcon(Icons.delete_outline));
      await tester.pump();
      expect(triggersOf(_nodeAt(c, const [])), isEmpty);
    });

    testWidgets('a Reconnect Device trigger edits its device via the dropdown',
        (tester) async {
      final c = await _pump(tester, detail: sampleDetail(), select: const []);
      c.read(sequenceEditorProvider.notifier).addTriggerTo(
          const [],
          triggerForType(
              'OpenAstroAra.Sequencer.Trigger.Connect.ReconnectTrigger, OpenAstroAra.Sequencer')!);
      await tester.pump();
      expect(triggersOf(_nodeAt(c, const [])).single['SelectedDevice'], 'Camera');
      // The trigger card renders the SelectedDevice stringEnum as a dropdown.
      await tester.tap(find.text('Camera'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Mount').last);
      await tester.pumpAndSettle();
      expect(triggersOf(_nodeAt(c, const [])).single['SelectedDevice'], 'Mount');
    });
  });

  group('filter picker (SwitchFilter)', () {
    // Default filter-wheel labels: L R G B Hα OIII SII (+ one empty slot).
    testWidgets('shows the hint when unset and lists the configured filters',
        (tester) async {
      await _pump(tester, detail: _detailWith(_switchFilter), select: const [0]);
      expect(find.text('Select a filter'), findsOneWidget); // null Filter → hint
      await tester.tap(find.byType(DropdownButton<String>));
      await tester.pumpAndSettle();
      expect(find.text('Hα').last, findsOneWidget); // a configured slot label
      expect(find.text('OIII').last, findsOneWidget);
    });

    testWidgets('picking a filter writes a minimal FilterInfo (name + position)',
        (tester) async {
      final c = await _pump(tester, detail: _detailWith(_switchFilter), select: const [0]);
      await tester.tap(find.byType(DropdownButton<String>));
      await tester.pumpAndSettle();
      await tester.tap(find.text('OIII').last);
      await tester.pumpAndSettle();
      final filter = _nodeAt(c, [0])['Filter'] as Map;
      expect(filter['_name'], 'OIII');
      expect(filter['_position'], 5); // 0-based slot index (6th slot)
      expect(filter[r'$type'], contains('FilterInfo'));
    });

    testWidgets('an unknown stored filter stays visible rather than blanking',
        (tester) async {
      final detail = SequenceDetail(
        id: 's',
        body: {
          r'$type':
              'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
          'Name': 'root',
          'Items': {
            r'$type': itemsWrapperType,
            r'$values': [
              {
                ..._node(_switchFilter),
                'Filter': {
                  r'$type': 'OpenAstroAra.Core.Model.Equipment.FilterInfo, OpenAstroAra.Core',
                  '_name': 'Clear',
                  '_position': 0,
                },
              },
            ],
          },
        },
      );
      await _pump(tester, detail: detail, select: const [0]);
      // 'Clear' isn't a configured slot but is shown as the selected value.
      expect(find.text('Clear'), findsOneWidget);
      expect(find.text('Select a filter'), findsNothing);
    });

    testWidgets('prompts to configure filters when no slots are labelled',
        (tester) async {
      final c = await _pump(tester, detail: _detailWith(_switchFilter), select: const [0]);
      final n = c.read(filterWheelLabelsProvider.notifier);
      for (var s = 1; s <= c.read(filterWheelLabelsProvider).slotCount; s++) {
        n.setLabel(s, '');
      }
      await tester.pump();
      expect(find.textContaining('No filters configured'), findsOneWidget);
    });
  });
}
