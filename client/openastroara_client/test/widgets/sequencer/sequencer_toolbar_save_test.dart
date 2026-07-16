import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/sequence_node.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/sequence/sequence_share_export.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/sequence_editor_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/widgets/sequencer/sequencer_toolbar.dart';

SequenceDetail _detail(String id) => SequenceDetail(
      id: id,
      name: id,
      body: {
        r'$type':
            'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer',
        'Name': 'root',
        'Items': {
          r'$type': itemsWrapperType,
          r'$values': [
            instructionForType(
                    'OpenAstroAra.Sequencer.SequenceItem.Utility.WaitForTimeSpan, OpenAstroAra.Sequencer')!
                .build(),
          ],
        },
      },
    );

class _SaveClient implements SequenceClient {
  @override
  Future<String> decideAutoFlats(String id,
          {required String choice, required bool remember}) =>
      throw UnimplementedError();
  Map<String, dynamic>? savedBody;
  int saveCalls = 0;
  Map<String, dynamic>? validatedBody;
  final int? throwStatus;
  final Object? throwData;
  final bool throwGeneric;
  final SequenceValidationResult validateResult;
  _SaveClient({
    this.throwStatus,
    this.throwData,
    this.throwGeneric = false,
    this.validateResult = const SequenceValidationResult(valid: true),
  });

  @override
  Future<bool> deleteSequence(String id) => throw UnimplementedError();
  @override
  Future<SequenceValidationResult> validate(Map<String, dynamic> body) async {
    validatedBody = body;
    return validateResult;
  }

  @override
  Future<SequenceDetail> updateSequence(String id,
      {String? name, String? description, Map<String, dynamic>? body}) async {
    saveCalls++;
    if (throwGeneric) throw StateError('unexpected response shape');
    if (throwStatus != null) {
      throw DioException(
        requestOptions: RequestOptions(path: '/sequences/$id'),
        response: Response(
          requestOptions: RequestOptions(path: '/sequences/$id'),
          statusCode: throwStatus,
          data: throwData,
        ),
      );
    }
    savedBody = body;
    return SequenceDetail(id: id, name: name ?? id, body: body ?? const {});
  }

  @override
  Future<SequenceDetail> getSequenceDetail(String id) async => _detail(id);
  @override
  Future<SequenceImportResult> importNina(String n, Map<String, dynamic> f,
          {bool treatWarningsAsErrors = false}) async =>
      const SequenceImportResult(createdSequenceId: 'new');
  @override
  Future<SequenceNode> getSequence(String id) async =>
      SequenceNode(id: 'root', kind: SequenceNodeKind.root, displayName: id);
  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async => null;
  @override
  Future<SequencePage> list({int limit = 50}) async => const SequencePage(items: []);
  @override
  Future<String> start(String id) async => 'op';
  @override
  Future<String> pause(String id) async => 'op';
  @override
  Future<String> resume(String id) async => 'op';
  @override
  Future<String> skipCurrent(String id) async => 'op';
  @override
  Future<String> abort(String id) async => 'op';
  @override
  Future<String> stop(String id) async => 'op';
  @override
  Future<SequenceShareExport> exportShare(String id) async => throw UnimplementedError();
  @override
  Future<List<SequenceTemplate>> listTemplates() async => const [];
  @override
  Future<String> instantiateTemplate(String t, String n) async => 'new-seq';
  @override
  Future<String> create(String name, Map<String, dynamic> body,
          {String? description, String? idempotencyKey}) async =>
      'new-seq';
  @override
  void close() {}
}

Future<ProviderContainer> _pump(WidgetTester tester, _SaveClient client,
    {required bool dirty}) async {
  final container = ProviderContainer(overrides: [
    sequenceApiProvider.overrideWithValue(client),
  ]);
  addTearDown(container.dispose);
  container.read(selectedSequenceIdProvider.notifier).select('seq-1');
  container.read(sequenceEditorProvider.notifier).load(_detail('seq-1'));
  if (dirty) {
    // Edit the WaitForTimeSpan's Time → the editor goes dirty.
    container.read(sequenceEditorProvider.notifier).setNodeField(const [0], 'Time', 99.0);
  }
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: const MaterialApp(home: Scaffold(body: SequencerToolbar())),
  ));
  await tester.pump();
  return container;
}

TextButton _saveButton(WidgetTester tester) => tester.widget<TextButton>(
      find.ancestor(of: find.text('Save'), matching: find.byType(TextButton)),
    );

void main() {
  testWidgets('Save is disabled when the editor is not dirty', (tester) async {
    await _pump(tester, _SaveClient(), dirty: false);
    expect(_saveButton(tester).onPressed, isNull);
  });

  testWidgets('Import (NINA) is present and enabled while connected', (tester) async {
    await _pump(tester, _SaveClient(), dirty: false);
    final importBtn = tester.widget<TextButton>(
      find.ancestor(of: find.text('Import'), matching: find.byType(TextButton)),
    );
    expect(importBtn.onPressed, isNotNull); // connected → can browse + import
  });

  testWidgets('Import is disabled when disconnected', (tester) async {
    final container = ProviderContainer(overrides: [
      sequenceApiProvider.overrideWithValue(null),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: SequencerToolbar())),
    ));
    await tester.pump();
    final importBtn = tester.widget<TextButton>(
      find.ancestor(of: find.text('Import'), matching: find.byType(TextButton)),
    );
    expect(importBtn.onPressed, isNull); // no server → can't import
  });

  testWidgets('Validate reports a valid sequence', (tester) async {
    final client = _SaveClient();
    await _pump(tester, client, dirty: false);
    await tester.ensureVisible(find.text('Validate'));
    await tester.tap(find.text('Validate'));
    await tester.pumpAndSettle();
    expect(client.validatedBody, isNotNull); // the editor body was sent
    expect(find.text('Sequence is valid.'), findsOneWidget);
  });

  testWidgets('Validate surfaces the validator reason when invalid', (tester) async {
    final client = _SaveClient(
        validateResult: const SequenceValidationResult(
            valid: false, reason: 'needs a capturable instruction'));
    await _pump(tester, client, dirty: false);
    await tester.ensureVisible(find.text('Validate'));
    await tester.tap(find.text('Validate'));
    await tester.pumpAndSettle();
    expect(find.textContaining('needs a capturable instruction'), findsOneWidget);
  });

  testWidgets('Save PATCHes the body, rebaselines dirty, and confirms',
      (tester) async {
    final client = _SaveClient();
    final container = await _pump(tester, client, dirty: true);
    expect(_saveButton(tester).onPressed, isNotNull); // enabled while dirty
    expect(container.read(sequenceEditorProvider)!.isDirty, isTrue);

    await tester.ensureVisible(find.text('Save'));
    await tester.tap(find.text('Save'));
    await tester.pumpAndSettle();

    expect(client.saveCalls, 1);
    // The verbatim working body (with the edit) was sent.
    expect(nodeAt(client.savedBody!, [0])!['Time'], 99.0);
    // markSaved rebaselined → no longer dirty → Save disables again.
    expect(container.read(sequenceEditorProvider)!.isDirty, isFalse);
    expect(find.text('Sequence saved.'), findsOneWidget);
  });

  testWidgets('a 422 surfaces the validator message', (tester) async {
    final client = _SaveClient(
        throwStatus: 422, throwData: {'detail': 'needs a capturable instruction'});
    final container = await _pump(tester, client, dirty: true);

    await tester.ensureVisible(find.text('Save'));
    await tester.tap(find.text('Save'));
    await tester.pumpAndSettle();

    expect(find.textContaining('needs a capturable instruction'), findsOneWidget);
    // Still dirty (save failed) so the user can retry.
    expect(container.read(sequenceEditorProvider)!.isDirty, isTrue);
  });

  testWidgets('a non-422 failure shows a generic error and keeps edits dirty',
      (tester) async {
    final client = _SaveClient(throwStatus: 500);
    final container = await _pump(tester, client, dirty: true);

    await tester.ensureVisible(find.text('Save'));
    await tester.tap(find.text('Save'));
    await tester.pumpAndSettle();

    expect(find.textContaining("Couldn't save the sequence"), findsOneWidget);
    expect(container.read(sequenceEditorProvider)!.isDirty, isTrue);
  });

  testWidgets('a non-Dio exception is caught (generic error, edits kept)',
      (tester) async {
    final client = _SaveClient(throwGeneric: true);
    final container = await _pump(tester, client, dirty: true);

    await tester.ensureVisible(find.text('Save'));
    await tester.tap(find.text('Save'));
    await tester.pumpAndSettle();

    expect(find.textContaining("Couldn't save the sequence"), findsOneWidget);
    expect(container.read(sequenceEditorProvider)!.isDirty, isTrue);
  });
}
