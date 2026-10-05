import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/custom_targets_service.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/sky_atlas/custom_targets_state.dart';
import 'package:openastroara/state/sky_atlas/sky_atlas_state.dart';
import 'package:openastroara/util/apparent_place.dart';
import 'package:openastroara/util/input_coordinates.dart';
import 'package:openastroara/widgets/sky_atlas/custom_target_dialog.dart';

/// In-memory recents: real file IO never completes under the widget test
/// binding's fake async zone, so the dialog's remember/forget go here.
class _MemoryTargets extends CustomTargetsService {
  _MemoryTargets() : super(supportDir: () async => throw StateError('no disk'));
  final List<CustomTarget> items = [];

  @override
  Future<List<CustomTarget>> load() async => List.of(items);

  @override
  Future<List<CustomTarget>> remember(CustomTarget target) async {
    items.removeWhere((t) => t.name == target.name);
    items.insert(0, target);
    return List.of(items);
  }

  @override
  Future<List<CustomTarget>> forget(CustomTarget target) async {
    items.removeWhere((t) => t.name == target.name);
    return List.of(items);
  }
}

/// Records create(); everything else the dialog's fresh-create path never
/// reaches (no sequence is selected).
class _RecordingClient implements SequenceClient {
  String? createdName;
  Map<String, dynamic>? createdBody;

  @override
  Future<String> create(String name, Map<String, dynamic> body,
      {String? description, String? idempotencyKey}) async {
    createdName = name;
    createdBody = body;
    return 'seq-1';
  }

  @override
  dynamic noSuchMethod(Invocation invocation) =>
      throw UnimplementedError(invocation.memberName.toString());
}

/// Walk the created run body for the first SlewScopeToRaDec and return its
/// J2000 RA/Dec in degrees.
({double raDeg, double decDeg})? _slewTarget(Object? node) {
  if (node is Map) {
    final type = node[r'$type'];
    if (type is String && type.contains('SlewScopeToRaDec')) {
      return degFromInputCoordinates(
          (node['Coordinates'] as Map).cast<String, dynamic>());
    }
    for (final v in node.values) {
      final hit = _slewTarget(v);
      if (hit != null) return hit;
    }
  } else if (node is List) {
    for (final v in node) {
      final hit = _slewTarget(v);
      if (hit != null) return hit;
    }
  }
  return null;
}

void main() {
  late _RecordingClient client;
  late _MemoryTargets targets;
  late ProviderContainer container;

  setUp(() {
    client = _RecordingClient();
    targets = _MemoryTargets();
    container = ProviderContainer(overrides: [
      sequenceApiProvider.overrideWith((ref) => client),
      customTargetsServiceProvider.overrideWithValue(targets),
    ]);
  });
  tearDown(() => container.dispose());

  Future<void> pump(WidgetTester tester, {String? initialText}) async {
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        home: Scaffold(
          body: Builder(
            builder: (context) => TextButton(
              onPressed: () =>
                  showCustomTargetDialog(context, initialText: initialText),
              child: const Text('open'),
            ),
          ),
        ),
      ),
    ));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
  }

  testWidgets('a SIMBAD line adds a run whose slew carries the J2000 position',
      (tester) async {
    await pump(tester);
    await tester.enterText(find.byKey(const Key('custom-target-ra')),
        '02 37 31.5 +71 18 16');
    await tester.pumpAndSettle();
    // The pasted pair split across both fields.
    expect(find.text('02 37 31.5'), findsOneWidget);
    expect(find.text('+71 18 16'), findsOneWidget);
    expect(find.byKey(const Key('custom-target-preview')), findsOneWidget);

    await tester.enterText(
        find.widgetWithText(TextField, 'Name'), 'AB Cas');
    await tester.tap(find.text('Add to plan'));
    await tester.pump();
    await tester.pump();
    expect(find.textContaining('Created an imaging run for "AB Cas"'),
        findsOneWidget);
    await tester.pump(const Duration(seconds: 7));

    expect(client.createdName, 'AB Cas');
    final slew = _slewTarget(client.createdBody)!;
    expect(slew.raDeg, closeTo((2 + 37 / 60 + 31.5 / 3600) * 15, 1e-3));
    expect(slew.decDeg, closeTo(71 + 18 / 60 + 16 / 3600, 1e-3));
    // Dialog closed, target remembered for next time.
    expect(find.text('Target by coordinates'), findsNothing);
    expect(targets.items.single.name, 'AB Cas');
    expect(targets.items.single.typedAsJNow, isFalse);
  });

  testWidgets('JNow input is converted to J2000 before it enters the run',
      (tester) async {
    await pump(tester);
    await tester.enterText(
        find.byKey(const Key('custom-target-ra')), '83.82208');
    await tester.enterText(
        find.byKey(const Key('custom-target-dec')), '-5.39111');
    await tester.tap(find.text('JNow'));
    await tester.pumpAndSettle();
    expect(find.textContaining('from JNow'), findsOneWidget);

    await tester.tap(find.text('Add to plan'));
    await tester.pump();
    await tester.pump();
    await tester.pump(const Duration(seconds: 7));

    final slew = _slewTarget(client.createdBody)!;
    final want =
        apparentToJ2000(83.82208, -5.39111, atUtc: DateTime.now().toUtc());
    expect(slew.raDeg, closeTo(want.raDeg, 1e-3));
    expect(slew.decDeg, closeTo(want.decDeg, 1e-3));
    // JNow differs from J2000 by ~20′ in 2026 — the conversion really ran.
    expect((slew.raDeg - 83.82208).abs(), greaterThan(0.1));
    expect(targets.items.single.typedAsJNow, isTrue);
    // No name typed → the position is the name.
    expect(client.createdName, contains('h '));
  });

  testWidgets('typing a pair key by key does not split early', (tester) async {
    await pump(tester);
    final ra = find.byKey(const Key('custom-target-ra'));
    var typed = '';
    for (final ch in '02 37 31.5 +71 18 16'.split('')) {
      typed += ch;
      await tester.enterText(ra, typed);
      await tester.pump();
    }
    expect(find.text('02 37 31.5 +71 18 16'), findsOneWidget);
    expect(find.text("Can't read this RA"), findsOneWidget);
    // Enter splits the finished line.
    await tester.testTextInput.receiveAction(TextInputAction.next);
    await tester.pumpAndSettle();
    expect(find.text('02 37 31.5'), findsOneWidget);
    expect(find.text('+71 18 16'), findsOneWidget);
    expect(find.byKey(const Key('custom-target-preview')), findsOneWidget);
  });

  testWidgets('an ambiguous decimal RA offers the hours/degrees choice',
      (tester) async {
    await pump(tester);
    await tester.enterText(find.byKey(const Key('custom-target-ra')), '5.5');
    await tester.enterText(find.byKey(const Key('custom-target-dec')), '10');
    await tester.pumpAndSettle();
    expect(find.text('RA in h'), findsOneWidget);
    await tester.tap(find.text('RA in h'));
    await tester.pumpAndSettle();
    expect(find.textContaining('5h 30m 00s'), findsOneWidget);

    await tester.enterText(find.byKey(const Key('custom-target-ra')), '83.8');
    await tester.pumpAndSettle();
    expect(find.text('RA in h'), findsNothing);
  });

  testWidgets('unreadable input disables the actions and says why',
      (tester) async {
    await pump(tester);
    await tester.enterText(
        find.byKey(const Key('custom-target-ra')), '25 00 00');
    await tester.enterText(find.byKey(const Key('custom-target-dec')), '95');
    await tester.pumpAndSettle();
    expect(find.text("Can't read this RA"), findsOneWidget);
    expect(find.text("Can't read this Dec"), findsOneWidget);
    final add = tester.widget<FilledButton>(
        find.widgetWithText(FilledButton, 'Add to plan'));
    expect(add.onPressed, isNull);
    expect(client.createdBody, isNull);
  });

  testWidgets('Show on sky frames the position on the planetarium',
      (tester) async {
    await pump(tester, initialText: '83.822 -5.391');
    Map<String, Object?>? cmd;
    container.listen(planetariumCommandProvider, (_, next) => cmd = next);
    await tester.tap(find.text('Show on sky'));
    await tester.pumpAndSettle();
    expect(cmd!['type'], 'goto');
    expect(cmd!['frame'], true);
    expect(cmd!['ra'], closeTo(83.822, 1e-9));
    expect(cmd!['dec'], closeTo(-5.391, 1e-9));
    expect(find.text('Target by coordinates'), findsNothing);
  });

  testWidgets('a recent target refills the fields on tap', (tester) async {
    targets.items.add(CustomTarget(
      name: 'Saved field',
      raDeg: 150,
      decDeg: -20,
      typedAsJNow: false,
      savedUtc: DateTime.utc(2026),
    ));
    await pump(tester);
    expect(find.text('Recent'), findsOneWidget);
    await tester.tap(find.text('Saved field'));
    await tester.pumpAndSettle();
    expect(find.text('10h 00m 00s'), findsOneWidget);
    expect(find.text('-20° 00\' 00"'), findsOneWidget);
    expect(find.byKey(const Key('custom-target-preview')), findsOneWidget);
  });
}
