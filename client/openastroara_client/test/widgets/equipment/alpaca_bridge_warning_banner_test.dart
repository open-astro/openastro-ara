import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/equipment/alpaca_bridge_warning_state.dart';
import 'package:openastroara/widgets/equipment/alpaca_bridge_warning_banner.dart';

/// Overrides [alpacaBridgeWarningProvider]'s build() to a fixed value so the
/// banner can be tested without a live WS stream.
class _FixedWarnNotifier extends AlpacaBridgeWarningNotifier {
  _FixedWarnNotifier(this._value);
  final AlpacaBridgeWarning? _value;
  @override
  AlpacaBridgeWarning? build() => _value;
}

Widget _host(AlpacaBridgeWarning? warning) => ProviderScope(
      overrides: [
        alpacaBridgeWarningProvider.overrideWith(() => _FixedWarnNotifier(warning)),
      ],
      child: const MaterialApp(home: Scaffold(body: AlpacaBridgeWarningBanner())),
    );

void main() {
  testWidgets('self-hides when there is no warning', (tester) async {
    await tester.pumpWidget(_host(null));
    expect(find.textContaining('AlpacaBridge'), findsNothing);
    expect(find.byTooltip('Dismiss'), findsNothing);
  });

  testWidgets('shows the detected version + recommended target', (tester) async {
    await tester.pumpWidget(_host(
        const AlpacaBridgeWarning(version: '1.3.0', minimum: '1.2.0', recommended: '1.5.0')));

    expect(find.textContaining('AlpacaBridge 1.3.0 detected'), findsOneWidget);
    expect(find.textContaining('1.5.0+ recommended'), findsOneWidget);
  });

  testWidgets('dismiss hides the banner for that version', (tester) async {
    await tester.pumpWidget(_host(
        const AlpacaBridgeWarning(version: '1.3.0', minimum: '1.2.0', recommended: '1.5.0')));

    await tester.tap(find.byTooltip('Dismiss'));
    await tester.pump();

    expect(find.textContaining('AlpacaBridge 1.3.0 detected'), findsNothing);
  });

  testWidgets('dismissal survives the banner being unmounted + remounted', (tester) async {
    // The banner is conditionally mounted on the eq.* panels, so navigating away
    // and back recreates the widget — the dismissal (held in a provider, not widget
    // state) must persist across that teardown.
    const warning =
        AlpacaBridgeWarning(version: '1.3.0', minimum: '1.2.0', recommended: '1.5.0');
    final container = ProviderContainer(overrides: [
      alpacaBridgeWarningProvider.overrideWith(() => _FixedWarnNotifier(warning)),
    ]);
    addTearDown(container.dispose);

    Widget mount(bool show) => UncontrolledProviderScope(
          container: container,
          child: MaterialApp(
            home: Scaffold(body: show ? const AlpacaBridgeWarningBanner() : const SizedBox()),
          ),
        );

    await tester.pumpWidget(mount(true));
    await tester.tap(find.byTooltip('Dismiss'));
    await tester.pump();
    expect(find.textContaining('AlpacaBridge 1.3.0 detected'), findsNothing);

    await tester.pumpWidget(mount(false)); // navigate away — banner unmounts
    await tester.pumpWidget(mount(true)); // …and back — banner remounts
    await tester.pump();

    expect(find.textContaining('AlpacaBridge 1.3.0 detected'), findsNothing,
        reason: 'a banner dismissed before navigation must stay dismissed after remount');
  });
}
