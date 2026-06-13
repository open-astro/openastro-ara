import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/equipment/alpaca_bridge_warning_state.dart';

void main() {
  group('AlpacaBridgeWarning.fromPayload', () {
    test('parses a full warn-band payload', () {
      final w = AlpacaBridgeWarning.fromPayload(<String, dynamic>{
        'version': '1.3.0',
        'minimum': '1.2.0',
        'recommended': '1.5.0',
      });
      expect(w.version, '1.3.0');
      expect(w.minimum, '1.2.0');
      expect(w.recommended, '1.5.0');
    });

    test('tolerates missing fields with sensible fallbacks', () {
      final w = AlpacaBridgeWarning.fromPayload(<String, dynamic>{});
      expect(w.version, 'unknown');
      expect(w.minimum, '1.2.0');
      expect(w.recommended, '1.5.0');
    });

    test('the warn-band event token matches the server contract', () {
      expect(alpacaBridgeOutdatedWarnEvent, 'equipment.alpaca_bridge_outdated_warn');
    });
  });
}
