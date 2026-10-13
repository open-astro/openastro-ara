import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/preflight_checklist.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/preflight/preflight_state.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/widgets/preflight_checklist_listener.dart';

class _Prompt extends PreflightPromptNotifier {
  _Prompt(this._prompt);
  final bool _prompt;
  int acked = 0;
  @override
  Future<PreflightPromptState> build() async =>
      PreflightPromptState(shouldPrompt: _prompt, acknowledgedTonight: false);
  @override
  Future<void> acknowledgeTonight() async {
    acked++;
    state = const AsyncData(
      PreflightPromptState(shouldPrompt: false, acknowledgedTonight: true),
    );
  }
}

class _Checklist extends PreflightChecklistNotifier {
  _Checklist(this._value);
  final PreflightChecklist? _value;
  int refreshes = 0;
  @override
  Future<PreflightChecklist?> build() async => _value;
  @override
  Future<PreflightChecklist?> refresh() async {
    refreshes++;
    return _value;
  }
}

final _ok = PreflightChecklist(
  generatedUtc: DateTime.utc(2026, 7, 19, 5),
  items: const [
    PreflightItem(
      key: 'mount',
      status: 'ok',
      title: 'Mount safety',
      detail: 'Tracking.',
    ),
  ],
  allGood: true,
);

void main() {
  Future<(ProviderContainer, _Prompt, _Checklist)> pump(
    WidgetTester tester, {
    required bool prompt,
    PreflightChecklist? checklist,
  }) async {
    final p = _Prompt(prompt);
    final c = _Checklist(checklist);
    final container = ProviderContainer(
      overrides: [
        preflightPromptProvider.overrideWith(() => p),
        preflightChecklistProvider.overrideWith(() => c),
      ],
    );
    addTearDown(container.dispose);
    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(
          home: Scaffold(
            body: PreflightChecklistListener(child: Text('shell')),
          ),
        ),
      ),
    );
    await tester.pump();
    await tester.pump();
    await tester.pump();
    return (container, p, c);
  }

  testWidgets(
    '#1138 the bedtime card shows a FRESH checklist and All good answers tonight',
    (tester) async {
      final (_, p, c) = await pump(tester, prompt: true, checklist: _ok);
      expect(find.text('Before you sleep'), findsOneWidget);
      expect(c.refreshes, 1, reason: 'read fresh, not the cached value');
      await tester.tap(find.text('All good — let me go to bed'));
      await tester.pumpAndSettle();
      expect(find.byType(AlertDialog), findsNothing);
      expect(p.acked, 1);
    },
  );

  testWidgets('#1138 Adjust answers too and opens Setup → Before you sleep', (
    tester,
  ) async {
    final (container, p, _) = await pump(tester, prompt: true, checklist: _ok);
    await tester.tap(find.text('Adjust'));
    await tester.pumpAndSettle();
    expect(p.acked, 1);
    expect(container.read(selectedTabIndexProvider), kSetupTabIndex);
    expect(
      container.read(setupPreSleepRequestProvider),
      1,
      reason: 'the tab is asked to select the step',
    );
  });

  testWidgets(
    '#1138 no prompt → no dialog; an older daemon (null checklist) → no dialog, nothing answered',
    (tester) async {
      await pump(tester, prompt: false, checklist: _ok);
      expect(find.byType(AlertDialog), findsNothing);
      await tester.pumpWidget(const SizedBox());
      final (_, p, _) = await pump(tester, prompt: true, checklist: null);
      expect(find.byType(AlertDialog), findsNothing);
      expect(p.acked, 0, reason: 'the prompt stays armed for the next tick');
    },
  );
}
