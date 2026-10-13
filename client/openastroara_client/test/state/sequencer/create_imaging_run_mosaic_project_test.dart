import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/library/mosaic.dart';
import 'package:openastroara/models/sequence/draft_sequence.dart';
import 'package:openastroara/services/draft_sequence_service.dart';
import 'package:openastroara/services/mosaic_api.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/library/mosaics_state.dart';
import 'package:openastroara/state/sequencer/create_imaging_run.dart';
import 'package:openastroara/state/sequencer/draft_sequences_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';

/// §47 / #1146 — a connected MOSAIC run also registers a mosaic project on the
/// daemon (name, grid, the per-panel centres, the run's frame count as "done");
/// a single-target run registers nothing.

class _MemDraftService extends DraftSequenceService {
  final Map<String, DraftSequence> store = {};
  int _n = 0;
  @override
  String newId() => '${draftIdPrefix}mem-${_n++}';
  @override
  Future<List<DraftSequence>> loadAll() async => store.values.toList();
  @override
  Future<void> save(DraftSequence draft) async => store[draft.id] = draft;
  @override
  Future<void> delete(String id) async => store.remove(id);
}

class _SeqClient implements SequenceClient {
  final created = <String>[];
  @override
  Future<String> create(
    String name,
    Map<String, dynamic> body, {
    String? description,
    String? idempotencyKey,
  }) async {
    created.add(name);
    return 'seq-${created.length}';
  }

  @override
  void close() {}
  @override
  void noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

class _MosaicClient implements MosaicClient {
  final creates = <Map<String, Object?>>[];
  @override
  Future<Mosaic> create({
    required String name,
    required double centerRaDeg,
    required double centerDecDeg,
    required int cols,
    required int rows,
    required double overlapPercent,
    required double positionAngleDeg,
    required List<({double raDeg, double decDeg})> panels,
    required int framesPerPanel,
  }) async {
    creates.add({
      'name': name,
      'cols': cols,
      'rows': rows,
      'overlap': overlapPercent,
      'pa': positionAngleDeg,
      'panels': panels.length,
      'framesPerPanel': framesPerPanel,
      'centerRa': centerRaDeg,
    });
    return Mosaic(id: 'm1', name: name);
  }

  @override
  Future<List<MosaicProject>> fetchProjects() async => const [];
  @override
  Future<void> delete(String id) async {}
  @override
  void close() {}
}

Future<ImagingRunResult?> _run(
  WidgetTester tester,
  ProviderContainer container, {
  required List<({double raDeg, double decDeg})> panels,
  ({int cols, int rows, double overlapPercent})? grid,
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
              onPressed: () =>
                  createImagingRun(
                    ref,
                    raDeg: 312.75,
                    decDeg: 30.5,
                    targetName: 'Veil',
                    positionAngleDeg: 20,
                    mosaicPanels: panels,
                    mosaicGrid: grid,
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
  testWidgets(
    'a connected mosaic run registers one mosaic project with the grid and panels',
    (tester) async {
      final seq = _SeqClient();
      final mosaics = _MosaicClient();
      final container = ProviderContainer(
        overrides: [
          draftSequenceServiceProvider.overrideWithValue(_MemDraftService()),
          sequenceApiProvider.overrideWith((ref) => seq),
          mosaicApiProvider.overrideWithValue(mosaics),
        ],
      );
      addTearDown(container.dispose);

      final result = await _run(
        tester,
        container,
        panels: const [
          (raDeg: 311.5, decDeg: 31.0),
          (raDeg: 314.0, decDeg: 31.0),
          (raDeg: 311.5, decDeg: 30.0),
          (raDeg: 314.0, decDeg: 30.0),
        ],
        grid: (cols: 2, rows: 2, overlapPercent: 15),
      );

      expect(result?.draft, isFalse);
      expect(seq.created, ['Veil']);
      expect(mosaics.creates, hasLength(1));
      final c = mosaics.creates.single;
      expect(c['name'], 'Veil');
      expect(c['cols'], 2);
      expect(c['rows'], 2);
      expect(c['overlap'], 15.0);
      expect(c['pa'], 20.0);
      expect(c['panels'], 4);
      expect(
        c['centerRa'],
        closeTo(312.75, 1e-9),
        reason: 'the mean of the panel centres',
      );
      expect(
        c['framesPerPanel'],
        greaterThan(0),
        reason: "the run's planned frame count is when a panel is done",
      );
    },
  );

  testWidgets('a single-target run registers no project', (tester) async {
    final mosaics = _MosaicClient();
    final container = ProviderContainer(
      overrides: [
        draftSequenceServiceProvider.overrideWithValue(_MemDraftService()),
        sequenceApiProvider.overrideWith((ref) => _SeqClient()),
        mosaicApiProvider.overrideWithValue(mosaics),
      ],
    );
    addTearDown(container.dispose);
    await _run(tester, container, panels: const []);
    expect(mosaics.creates, isEmpty);
  });
}
