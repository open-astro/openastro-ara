import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/state/focus/autofocus_live_state.dart';
import 'package:openastroara/state/focus/guide_focus_state.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/widgets/focus/guide_focus_card.dart';
import 'package:openastroara/widgets/imaging/guiding_assistant_dialog.dart';

class _StubAutofocus extends AutofocusLiveNotifier {
  @override
  AutofocusLive build() => AutofocusLive.idle;
  @override
  Future<void> refresh() async {}
}

class _StubGuideFocus extends GuideFocusNotifier {
  @override
  GuideFocusLive build() => GuideFocusLive.idle;
  @override
  Future<void> refresh() async {}
}

class _Guider extends GuiderStatusNotifier {
  final GuiderRuntimeState runtime;
  final GuiderConnectionState connection;
  _Guider(this.runtime, {this.connection = GuiderConnectionState.connected});
  @override
  Future<GuiderStatus?> build() async => GuiderStatus(
    name: 'Guider',
    connectionState: connection,
    runtimeState: runtime,
  );
}

Future<void> _pump(
  WidgetTester t,
  GuiderRuntimeState runtime, {
  GuiderConnectionState connection = GuiderConnectionState.connected,
}) async {
  await t.pumpWidget(
    ProviderScope(
      overrides: [
        autofocusLiveProvider.overrideWith(_StubAutofocus.new),
        guideFocusProvider.overrideWith(_StubGuideFocus.new),
        guiderStatusProvider.overrideWith(
          () => _Guider(runtime, connection: connection),
        ),
      ],
      child: const MaterialApp(
        home: Scaffold(
          body: SingleChildScrollView(
            child: SizedBox(width: 1100, child: GuideFocusCard()),
          ),
        ),
      ),
    ),
  );
  await t.pump();
  await t.pump();
}

FilledButton _start(WidgetTester t) => t.widget<FilledButton>(
  find.widgetWithText(FilledButton, 'Start live focus'),
);

void main() {
  // #1364 item 6 — a looping guider used to read "stopped", so the card offered
  // a Start the guider then refused (it takes no single frame while it loops).
  for (final busy in [
    GuiderRuntimeState.looping,
    GuiderRuntimeState.starSelected,
    GuiderRuntimeState.guiding,
  ]) {
    testWidgets('a guider that is ${busy.name} blocks Start live focus', (
      t,
    ) async {
      await _pump(t, busy);
      expect(_start(t).onPressed, isNull);
      expect(find.textContaining('Stop the guider first'), findsOneWidget);
    });
  }

  testWidgets('a stopped guider leaves Start live focus enabled', (t) async {
    await _pump(t, GuiderRuntimeState.stopped);
    expect(_start(t).onPressed, isNotNull);
    expect(find.textContaining('Stop the guider first'), findsNothing);
  });

  // #1364 item 4 — the Guiding Assistant entry under Setup → Smart Focus →
  // Guide camera: only with a connected guider, and it opens the dialog.
  testWidgets('a connected guider offers the Guiding Assistant and it opens', (
    t,
  ) async {
    await _pump(t, GuiderRuntimeState.stopped);
    await t.tap(find.byKey(const Key('guide-focus-assistant')));
    await t.pump();
    await t.pump();
    expect(find.byType(GuidingAssistantPanel), findsOneWidget);
  });

  testWidgets('no guider connected, no Guiding Assistant entry', (t) async {
    await _pump(
      t,
      GuiderRuntimeState.stopped,
      connection: GuiderConnectionState.disconnected,
    );
    expect(find.byKey(const Key('guide-focus-assistant')), findsNothing);
  });
}
