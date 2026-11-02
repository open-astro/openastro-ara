import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/create_imaging_run.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';

/// Simple Mode's Run hands createImagingRun a finished plan: no plan chooser,
/// its own sub length and frame count, and a run of its own even with a
/// sequence open (no append).

class _SeqClient implements SequenceClient {
  final bodies = <Map<String, dynamic>>[];
  @override
  Future<String> create(
    String name,
    Map<String, dynamic> body, {
    String? description,
    String? idempotencyKey,
  }) async {
    bodies.add(body);
    return 'seq-${bodies.length}';
  }

  @override
  void close() {}
  // Any append path (run state, detail, update) is a failure here.
  @override
  void noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

Future<ImagingRunResult?> _run(
  WidgetTester tester,
  ProviderContainer container, {
  List<FilterPlanStep>? filterPlan,
  double? exposureSeconds,
  int? frameCount,
}) async {
  ImagingRunResult? result;
  var done = false;
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        home: Scaffold(
          body: Consumer(
            builder: (context, ref, _) => ElevatedButton(
              onPressed: () => createImagingRun(
                ref,
                raDeg: 10.68,
                decDeg: 41.27,
                targetName: 'M31',
                jumpToRun: false,
                appendToOpen: false,
                filterPlan: filterPlan,
                exposureSeconds: exposureSeconds,
                frameCount: frameCount,
              ).then((r) {
                result = r;
                done = true;
              }),
              child: const Text('go'),
            ),
          ),
        ),
      ),
    ),
  );
  await tester.tap(find.text('go'));
  for (var i = 0; i < 20 && !done; i++) {
    await tester.pump(const Duration(milliseconds: 100));
  }
  expect(done, isTrue, reason: 'createImagingRun must finish');
  return result;
}

void main() {
  late _SeqClient seq;
  late ProviderContainer container;

  setUp(() {
    seq = _SeqClient();
    container = ProviderContainer(
      overrides: [sequenceApiProvider.overrideWith((ref) => seq)],
    );
    // A sequence is open: Simple Mode's run must not join it.
    container.read(selectedSequenceIdProvider.notifier).select('open-one');
  });
  tearDown(() => container.dispose());

  testWidgets('a given filter plan becomes a fresh run with one loop per filter', (
    tester,
  ) async {
    final r = await _run(
      tester,
      container,
      filterPlan: const [
        FilterPlanStep(filterName: 'L', exposureSeconds: 300, frameCount: 40),
        FilterPlanStep(filterName: 'R', exposureSeconds: 180, frameCount: 20),
      ],
    );
    expect(r?.appended, isFalse);
    expect(r?.sequenceId, 'seq-1');
    expect(seq.bodies, hasLength(1));
    final json = jsonEncode(seq.bodies.single);
    expect(json, contains('"L Imaging"'));
    expect(json, contains('"R Imaging"'));
    expect(json, contains('"ExposureTime":300.0'));
    expect(json, contains('"Iterations":40'));
    expect(json, contains('"ExposureTime":180.0'));
  });

  testWidgets('without a plan the given sub length and frame count are used', (
    tester,
  ) async {
    await _run(tester, container, exposureSeconds: 120, frameCount: 30);
    final json = jsonEncode(seq.bodies.single);
    expect(json, contains('"ExposureTime":120.0'));
    expect(json, contains('"Iterations":30'));
  });
}
