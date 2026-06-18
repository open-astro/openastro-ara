import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_node.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/widgets/sequencer/sequence_import.dart';

/// importNina returns a configured result (or throws); records what was sent.
class _ImportClient implements SequenceClient {
  _ImportClient({this.result, this.throwOnImport = false});
  final SequenceImportResult? result;
  final bool throwOnImport;
  String? lastName;
  Map<String, dynamic>? lastBody;

  @override
  Future<SequenceImportResult> importNina(String name, Map<String, dynamic> file,
      {bool treatWarningsAsErrors = false}) async {
    lastName = name;
    lastBody = file;
    if (throwOnImport) throw Exception('boom');
    return result ??
        const SequenceImportResult(createdSequenceId: 'imp-1', name: 'Imported');
  }

  @override
  Future<SequencePage> list({int limit = 50}) async => const SequencePage(items: []);
  @override
  Future<SequenceNode> getSequence(String id) async =>
      SequenceNode(id: 'root', kind: SequenceNodeKind.root, displayName: id);
  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async => null;
  @override
  Future<String> start(String id) async => 'op';
  @override
  Future<String> pause(String id) async => 'op';
  @override
  Future<String> resume(String id) async => 'op';
  @override
  Future<String> abort(String id) async => 'op';
  @override
  Future<String> stop(String id) async => 'op';
  @override
  void close() {}
}

void main() {
  Future<ProviderContainer> pump(WidgetTester tester, _ImportClient client) async {
    final container = ProviderContainer(overrides: [
      sequenceApiProvider.overrideWithValue(client),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        home: Scaffold(
          body: Consumer(builder: (context, ref, _) {
            return ElevatedButton(
              onPressed: () => importSequenceFromJson(context, ref,
                  name: 'M42', ninaJson: const {'Name': 'M42'}),
              child: const Text('import'),
            );
          }),
        ),
      ),
    ));
    return container;
  }

  testWidgets('a clean import selects the new sequence + confirms', (tester) async {
    final container = await pump(
        tester, _ImportClient(result: const SequenceImportResult(
            createdSequenceId: 'imp-9', name: 'M42 imported')));
    await tester.tap(find.text('import'));
    await tester.pumpAndSettle();

    expect(container.read(selectedSequenceIdProvider), 'imp-9');
    expect(find.textContaining('Imported "M42 imported"'), findsOneWidget); // SnackBar
  });

  testWidgets('a lossy import shows the warnings dialog', (tester) async {
    final container = await pump(
        tester,
        _ImportClient(
            result: const SequenceImportResult(
          createdSequenceId: 'imp-2',
          name: 'M42',
          lossyTranslation: true,
          droppedInstructionTypes: ['NINA.X.Foo'],
          warnings: ['Approximated a trigger'],
        )));
    await tester.tap(find.text('import'));
    await tester.pumpAndSettle();

    expect(find.textContaining('with warnings'), findsOneWidget);
    expect(find.textContaining('NINA.X.Foo'), findsOneWidget);
    // Still selected the new sequence.
    expect(container.read(selectedSequenceIdProvider), 'imp-2');
  });

  testWidgets('an import failure shows a SnackBar and selects nothing',
      (tester) async {
    final container = await pump(tester, _ImportClient(throwOnImport: true));
    await tester.tap(find.text('import'));
    await tester.pumpAndSettle();

    expect(find.textContaining("Couldn't import the sequence"), findsOneWidget);
    expect(container.read(selectedSequenceIdProvider), isNull);
  });

  testWidgets('sends the NINA body + name to the API', (tester) async {
    final client = _ImportClient();
    await pump(tester, client);
    await tester.tap(find.text('import'));
    await tester.pumpAndSettle();
    expect(client.lastName, 'M42');
    expect(client.lastBody, const {'Name': 'M42'});
  });
}
