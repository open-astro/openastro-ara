import 'dart:async';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_node.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/sequence/sequence_share_export.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/widgets/sequencer/sequence_new_dialog.dart';

/// instantiateTemplate returns a configured id (or throws); records what was sent.
class _NewClient implements SequenceClient {
  @override
  Future<SequenceValidationResult> validate(Map<String, dynamic> body) async =>
      const SequenceValidationResult(valid: true);
  _NewClient(
      {this.id = 'tmpl-1',
      this.throwOnInstantiate = false,
      this.templates = const []});
  final String id;
  final bool throwOnInstantiate;
  final List<SequenceTemplate> templates;
  String? lastTemplate;
  String? lastName;

  @override
  Future<String> instantiateTemplate(String t, String n) async {
    lastTemplate = t;
    lastName = n;
    if (throwOnInstantiate) throw Exception('boom');
    return id;
  }

  @override
  Future<String> create(String name, Map<String, dynamic> body,
          {String? description}) async =>
      id;
  @override
  Future<List<SequenceTemplate>> listTemplates() async => templates;
  @override
  Future<SequencePage> list({int limit = 50}) async => const SequencePage(items: []);
  @override
  Future<SequenceImportResult> importNina(String name, Map<String, dynamic> file,
          {bool treatWarningsAsErrors = false}) async =>
      const SequenceImportResult(createdSequenceId: 'imp');
  @override
  Future<SequenceDetail> getSequenceDetail(String id) async =>
      SequenceDetail(id: id, name: id, body: const {});
  @override
  Future<SequenceDetail> updateSequence(String id,
          {String? name, String? description, Map<String, dynamic>? body}) async =>
      SequenceDetail(id: id, name: name ?? id, description: description, body: body ?? const {});
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
  Future<String> skipCurrent(String id) async => 'op';
  @override
  Future<String> abort(String id) async => 'op';
  @override
  Future<String> stop(String id) async => 'op';
  @override
  Future<SequenceShareExport> exportShare(String id) async => throw UnimplementedError();
  @override
  void close() {}
}

void main() {
  late String? returnedId;

  Future<ProviderContainer> pump(WidgetTester tester, _NewClient client) async {
    returnedId = null;
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
              onPressed: () async {
                returnedId = await createSequenceFromTemplate(context, ref,
                    templateName: 'Deep-sky LRGB', newName: 'M42');
              },
              child: const Text('go'),
            );
          }),
        ),
      ),
    ));
    return container;
  }

  testWidgets('a successful create selects the new sequence + confirms',
      (tester) async {
    final container = await pump(tester, _NewClient(id: 'seq-9'));
    await tester.tap(find.text('go'));
    await tester.pumpAndSettle();

    expect(returnedId, 'seq-9');
    expect(container.read(selectedSequenceIdProvider), 'seq-9');
    expect(find.textContaining('Created "M42"'), findsOneWidget); // SnackBar
  });

  testWidgets('a create failure shows a SnackBar and selects nothing',
      (tester) async {
    final container = await pump(tester, _NewClient(throwOnInstantiate: true));
    await tester.tap(find.text('go'));
    await tester.pumpAndSettle();

    expect(returnedId, isNull);
    expect(find.textContaining("Couldn't create the sequence"), findsOneWidget);
    expect(container.read(selectedSequenceIdProvider), isNull);
  });

  testWidgets('sends the template name + new name to the API', (tester) async {
    final client = _NewClient();
    await pump(tester, client);
    await tester.tap(find.text('go'));
    await tester.pumpAndSettle();
    expect(client.lastTemplate, 'Deep-sky LRGB');
    expect(client.lastName, 'M42');
  });

  testWidgets('the dialog lists templates; picking one enables Create and '
      'instantiates the picked template', (tester) async {
    final client = _NewClient(id: 'seq-7', templates: const [
      SequenceTemplate(name: 'Deep-sky LRGB', category: 'Deep sky'),
      SequenceTemplate(name: 'Quick test', category: 'Utility'),
    ]);
    final container = ProviderContainer(overrides: [
      sequenceApiProvider.overrideWithValue(client),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        home: Scaffold(
          body: Builder(
            builder: (context) => ElevatedButton(
              onPressed: () => SequenceNewDialog.show(context),
              child: const Text('open'),
            ),
          ),
        ),
      ),
    ));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();

    // Both templates listed; Create disabled until one is picked.
    expect(find.text('Deep-sky LRGB'), findsOneWidget);
    expect(find.text('Quick test'), findsOneWidget);
    final createButton =
        tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Create'));
    expect(createButton.onPressed, isNull);

    // Pick a template → name prefilled → Create enabled → instantiates it.
    await tester.tap(find.text('Deep-sky LRGB'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Create'));
    await tester.pumpAndSettle();

    expect(client.lastTemplate, 'Deep-sky LRGB');
    expect(client.lastName, 'Deep-sky LRGB'); // prefilled name
    expect(container.read(selectedSequenceIdProvider), 'seq-7');
    // The dialog dismisses itself on a successful create.
    expect(find.byType(SequenceNewDialog), findsNothing);
  });

  // Pump the dialog directly with an overridden templates create fn so the
  // loading / error / no-server states can be asserted in isolation.
  Future<void> pumpDialog(WidgetTester tester,
      FutureOr<List<SequenceTemplate>?> Function(Ref ref) create) async {
    final container = ProviderContainer(overrides: [
      sequenceTemplatesProvider.overrideWith(create),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: SequenceNewDialog())),
    ));
  }

  testWidgets('shows a spinner while templates are loading', (tester) async {
    await pumpDialog(tester,
        (ref) => Completer<List<SequenceTemplate>?>().future); // never completes
    await tester.pump();
    expect(find.byType(CircularProgressIndicator), findsOneWidget);
  });

  testWidgets('shows an error message when the templates load fails',
      (tester) async {
    await pumpDialog(
        tester, (ref) => Future<List<SequenceTemplate>?>.error('x'));
    await tester.pumpAndSettle();
    expect(find.textContaining("Couldn't load templates"), findsOneWidget);
  });

  testWidgets('prompts to connect when there is no server', (tester) async {
    await pumpDialog(tester, (ref) async => null); // null = disconnected
    await tester.pumpAndSettle();
    expect(find.textContaining('Connect to a daemon'), findsOneWidget);
  });

  testWidgets('picking a template does not clobber a name the user typed',
      (tester) async {
    await pumpDialog(
        tester,
        (ref) async =>
            const [SequenceTemplate(name: 'Deep-sky LRGB', category: 'Deep sky')]);
    await tester.pumpAndSettle();

    await tester.enterText(find.byType(TextField), 'My own name');
    await tester.pump();
    await tester.tap(find.text('Deep-sky LRGB'));
    await tester.pumpAndSettle();

    // The typed name is preserved (only an empty field is pre-filled).
    expect(find.text('My own name'), findsOneWidget);
    expect(find.text('Deep-sky LRGB'), findsOneWidget); // the radio row, not the field
  });
}
