import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/sequence_node.dart';
import 'package:openastroara/models/sequence/sequence_share_export.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/sequencer/sequence_editor_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/sky_atlas/sky_atlas_state.dart';
import 'package:openastroara/widgets/sky_atlas/target_action_bar.dart';

/// Records create/update; the remove path reads runState (null = idle) and
/// detail (the open sequence's body) exactly like the append path.
class _RecordingClient implements SequenceClient {
  SequenceRunStateInfo? runState;
  SequenceDetail? detail;
  String? updatedId;
  Map<String, dynamic>? updatedBody;
  String? createdName;

  @override
  Future<String> create(String name, Map<String, dynamic> body,
      {String? description, String? idempotencyKey}) async {
    createdName = name;
    return 'seq-new';
  }

  @override
  Future<SequenceDetail> getSequenceDetail(String id) async =>
      detail ?? (throw UnimplementedError());

  @override
  Future<SequenceDetail> updateSequence(String id,
      {String? name, String? description, Map<String, dynamic>? body}) async {
    updatedId = id;
    updatedBody = body;
    return SequenceDetail(id: id, name: name ?? '', body: body ?? const {});
  }

  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async => runState;

  @override
  dynamic noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName} not stubbed');

  @override
  Future<SequencePage> list({int limit = 50}) => throw UnimplementedError();
  @override
  Future<SequenceImportResult> importNina(String n, Map<String, dynamic> f,
          {bool treatWarningsAsErrors = false}) =>
      throw UnimplementedError();
  @override
  Future<List<SequenceTemplate>> listTemplates() => throw UnimplementedError();
  @override
  Future<String> instantiateTemplate(String t, String n) =>
      throw UnimplementedError();
  @override
  Future<SequenceNode> getSequence(String id) => throw UnimplementedError();
  @override
  Future<bool> deleteSequence(String id) => throw UnimplementedError();
  @override
  Future<SequenceValidationResult> validate(Map<String, dynamic> body) =>
      throw UnimplementedError();
  @override
  Future<String> start(String id) => throw UnimplementedError();
  @override
  Future<String> pause(String id) => throw UnimplementedError();
  @override
  Future<String> resume(String id) => throw UnimplementedError();
  @override
  Future<String> skipCurrent(String id) => throw UnimplementedError();
  @override
  Future<String> abort(String id) => throw UnimplementedError();
  @override
  Future<String> stop(String id) => throw UnimplementedError();
  @override
  Future<SequenceShareExport> exportShare(String id) =>
      throw UnimplementedError();
  @override
  void close() {}
}

/// The mount isn't exercised here (GoTo needs a connected one); a null-status
/// notifier keeps the bar's build off the real server-backed provider.
class _NullMountNotifier extends MountNotifier {
  @override
  Future<MountStatus?> build() async => null;
}

/// A two-target session (M 42 + Andromeda Galaxy) as the open sequence's body.
Map<String, dynamic> _twoTargetBody() => appendTargetToRunBody(
      buildImagingRunBody(
        raDeg: 83.8,
        decDeg: -5.4,
        targetName: 'M 42',
        exposureSeconds: 120,
        frameCount: 48,
        warmAtEnd: true,
      ),
      buildTargetBlock(
        raDeg: 10.6847,
        decDeg: 41.269,
        targetName: 'Andromeda Galaxy',
        exposureSeconds: 120,
        frameCount: 30,
      ),
    );

void main() {
  /// Pump the bar with [client], the given target selected, and (optionally)
  /// an editor loaded with [openBody] as sequence 'seq-open'. Returns the
  /// container so tests can inspect selection/editor after acting.
  Future<ProviderContainer> pump(
    WidgetTester tester, {
    required _RecordingClient client,
    required SkyTarget target,
    Map<String, dynamic>? openBody,
  }) async {
    final container = ProviderContainer(overrides: [
      sequenceApiProvider.overrideWith((ref) => client),
      mountProvider.overrideWith(_NullMountNotifier.new),
    ]);
    addTearDown(container.dispose);
    container.read(skyTargetProvider.notifier).set(target);
    if (openBody != null) {
      client.detail =
          SequenceDetail(id: 'seq-open', name: 'M 42', body: openBody);
      container.read(selectedSequenceIdProvider.notifier).select('seq-open');
      container
          .read(sequenceEditorProvider.notifier)
          .load(SequenceDetail(id: 'seq-open', name: 'M 42', body: openBody));
    }
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: TargetActionBar())),
    ));
    await tester.pump();
    return container;
  }

  const andromeda =
      SkyTarget(raDeg: 10.6847, decDeg: 41.269, name: 'Andromeda Galaxy');

  testWidgets('Remove is hidden until the target is in the open sequence',
      (tester) async {
    // No open sequence → no Remove.
    await pump(tester, client: _RecordingClient(), target: andromeda);
    expect(find.text('Remove'), findsNothing);
    expect(find.text('Add to Sequence'), findsOneWidget);
  });

  testWidgets('Remove shows for a target that has a block in the open sequence',
      (tester) async {
    await pump(tester,
        client: _RecordingClient(),
        target: andromeda,
        openBody: _twoTargetBody());
    expect(find.text('Remove'), findsOneWidget);
  });

  testWidgets('a target NOT in the open sequence gets no Remove', (tester) async {
    // The open sequence has M 42 + Andromeda; a different selected target (M 13)
    // has no block, so only Add is offered.
    await pump(tester,
        client: _RecordingClient(),
        target: const SkyTarget(raDeg: 250.4, decDeg: 36.5, name: 'M 13'),
        openBody: _twoTargetBody());
    expect(find.text('Remove'), findsNothing);
  });

  testWidgets('Remove prunes just this target and persists the rest',
      (tester) async {
    final client = _RecordingClient();
    final container = await pump(tester,
        client: client, target: andromeda, openBody: _twoTargetBody());

    await tester.tap(find.text('Remove'));
    await tester.pump(); // run the remove round-trip
    await tester.pump(); // settle the SnackBar

    expect(client.updatedId, 'seq-open');
    final names = childrenOf(client.updatedBody!).map((c) => c['Name']).toList();
    expect(names, contains('M 42'));
    expect(names, isNot(contains('Andromeda Galaxy')));
    expect(find.textContaining('Removed "Andromeda Galaxy"'), findsOneWidget);
    // The editor reloaded at the pruned body — the block is gone from state.
    final body = container.read(sequenceEditorProvider)!.body;
    expect(indexOfTargetBlock(body, 'Andromeda Galaxy'), -1);
    // ...so the bar drops its Remove button on the next frame.
    await tester.pump();
    expect(find.text('Remove'), findsNothing);
  });

  testWidgets('Remove is blocked while the sequence is running', (tester) async {
    final client = _RecordingClient()
      ..runState = const SequenceRunStateInfo(
          sequenceId: 'seq-open', state: SequenceRunState.running);
    await pump(tester,
        client: client, target: andromeda, openBody: _twoTargetBody());

    await tester.tap(find.text('Remove'));
    await tester.pump();
    await tester.pump();

    expect(client.updatedId, isNull);
    expect(find.textContaining("Can't edit the sequence while it's running"),
        findsOneWidget);
  });
}
