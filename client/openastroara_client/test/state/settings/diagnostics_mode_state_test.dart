import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/diagnostics_mode_state.dart';

void main() {
  group('DiagnosticsModeNotifier', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('default is notifyOnly per §51', () {
      expect(container.read(diagnosticsModeProvider),
          DiagnosticsMode.notifyOnly);
    });

    test('setMode cycles all three modes', () {
      final n = container.read(diagnosticsModeProvider.notifier);
      n.setMode(DiagnosticsMode.pauseOnCritical);
      expect(container.read(diagnosticsModeProvider),
          DiagnosticsMode.pauseOnCritical);
      n.setMode(DiagnosticsMode.abortOnCritical);
      expect(container.read(diagnosticsModeProvider),
          DiagnosticsMode.abortOnCritical);
      n.setMode(DiagnosticsMode.notifyOnly);
      expect(container.read(diagnosticsModeProvider),
          DiagnosticsMode.notifyOnly);
    });
  });
}
