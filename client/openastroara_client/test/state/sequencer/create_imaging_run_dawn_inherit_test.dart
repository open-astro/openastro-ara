import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/condition_catalog.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/create_imaging_run.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';

/// #1560 — a target the Full UI adds to a Simple Mode queue (which stops at
/// dawn) stops at that dawn too, whether the queue is idle (PATCH) or imaging
/// (live add). A sequence without a dawn stop gets none.

class _SeqClient implements SequenceClient {
  _SeqClient(this.existingBody, {this.running = false});
  final Map<String, dynamic> existingBody;
  final bool running;
  Map<String, dynamic>? patched;
  final liveItems = <Map<String, dynamic>>[];

  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async =>
      SequenceRunStateInfo(
        sequenceId: id,
        state: running ? SequenceRunState.running : SequenceRunState.idle,
      );

  @override
  Future<SequenceDetail> getSequenceDetail(String id) async =>
      SequenceDetail(id: id, name: 'Queue', body: existingBody);

  @override
  Future<SequenceDetail> updateSequence(
    String id, {
    String? name,
    String? description,
    Map<String, dynamic>? body,
  }) async {
    patched = body;
    return SequenceDetail(id: id, name: 'Queue', body: body ?? const {});
  }

  @override
  Future<SequenceDetail> addRunItem(
    String id, {
    required List<int> parentPath,
    int? index,
    required Map<String, dynamic> item,
  }) async {
    liveItems.add(item);
    return SequenceDetail(id: id, name: 'Queue', body: existingBody);
  }

  @override
  void close() {}
  @override
  void noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

Map<String, dynamic> _queue({String? dawn}) => buildImagingRunBody(
  raDeg: 10.68,
  decDeg: 41.27,
  targetName: 'M31',
  exposureSeconds: 120,
  frameCount: 10,
  stopAtDawn: dawn,
  parkAtEnd: dawn != null,
);

// The dawn providers on a block and on each of its imaging loops.
List<Object?> _dawns(Map<String, dynamic> block) => [
  for (final n in [block, ...childrenOf(block).where(isContainer)])
    for (final c in conditionsOf(n))
      if (c[r'$type'] == timeConditionType) c['SelectedProvider'],
];

Future<void> _addM42(WidgetTester tester, _SeqClient seq) async {
  final container = ProviderContainer(
    overrides: [sequenceApiProvider.overrideWith((ref) => seq)],
  );
  addTearDown(container.dispose);
  container.read(selectedSequenceIdProvider.notifier).select('queue');
  var done = false;
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        home: Scaffold(
          body: Consumer(
            builder: (context, ref, _) => ElevatedButton(
              // The Full UI's Planning: no dawn stop of its own.
              onPressed: () => createImagingRun(
                ref,
                raDeg: 83.82,
                decDeg: -5.39,
                targetName: 'M42',
                jumpToRun: false,
                filterPlan: const [
                  FilterPlanStep(filterName: 'L', exposureSeconds: 60, frameCount: 5),
                  FilterPlanStep(filterName: 'R', exposureSeconds: 60, frameCount: 5),
                ],
              ).then((_) => done = true),
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
}

void main() {
  final nautical = {r'$type': timeProviders['Nautical Dawn']};

  testWidgets('added to an idle queue that stops at dawn: the new target stops at it too', (tester) async {
    final seq = _SeqClient(_queue(dawn: 'Nautical Dawn'));
    await _addM42(tester, seq);
    final m42 = childrenOf(seq.patched!).singleWhere((c) => c['Name'] == 'M42');
    expect(_dawns(m42), [nautical, nautical, nautical], reason: 'the block and its two loops');
  });

  testWidgets('added live to an imaging queue that stops at dawn: the new target stops at it too', (tester) async {
    final seq = _SeqClient(_queue(dawn: 'Nautical Dawn'), running: true);
    await _addM42(tester, seq);
    expect(_dawns(seq.liveItems.single), [nautical, nautical, nautical]);
  });

  testWidgets('added to a sequence without a dawn stop: none', (tester) async {
    final seq = _SeqClient(_queue());
    await _addM42(tester, seq);
    final m42 = childrenOf(seq.patched!).singleWhere((c) => c['Name'] == 'M42');
    expect(_dawns(m42), isEmpty);
  });
}
