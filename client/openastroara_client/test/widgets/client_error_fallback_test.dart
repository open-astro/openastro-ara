import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/client_error_fallback.dart';

void main() {
  testWidgets('renders with no MaterialApp, Theme or Directionality above it',
      (tester) async {
    // Bare root: exactly the situation when the widget that failed was the
    // one that would have provided those ancestors.
    await tester.pumpWidget(const ClientErrorFallback());
    expect(tester.takeException(), isNull);
    expect(find.textContaining('Save client log'), findsOneWidget);
  });

  testWidgets('shows a custom message', (tester) async {
    await tester.pumpWidget(const ClientErrorFallback(message: 'custom'));
    expect(find.text('custom'), findsOneWidget);
  });

  testWidgets('works as an ErrorWidget.builder replacement', (tester) async {
    // flutter_test checks this global is back to its own value when the
    // test BODY ends, so it is restored inline rather than in a tearDown.
    final saved = ErrorWidget.builder;
    ErrorWidget.builder = (_) => const ClientErrorFallback();
    try {
      await tester.pumpWidget(
        Builder(builder: (_) => throw StateError('build failed')),
      );
      // The build error itself is reported (and swallowed here); the
      // fallback takes the failed widget's place.
      expect(tester.takeException(), isA<StateError>());
      expect(find.byType(ClientErrorFallback), findsOneWidget);
    } finally {
      ErrorWidget.builder = saved;
    }
  });
}
