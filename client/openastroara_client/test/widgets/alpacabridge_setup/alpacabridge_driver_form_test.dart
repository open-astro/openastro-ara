import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/alpacabridge_setup.dart';
import 'package:openastroara/widgets/alpacabridge_setup/alpacabridge_driver_form.dart';

BridgeDriver _driver() => BridgeDriver.fromJson({
  'vendor': 'acme',
  'deviceType': 'telescope',
  'displayName': 'Acme mount',
  'vendorLabel': 'Acme',
  'available': true,
  'fields': [
    {
      'key': 'connectionType',
      'type': 'string',
      'default': 'serial',
      'required': true,
      'secret': false,
      'role': 'Discriminator',
      'enum': ['serial', 'network'],
    },
    {
      'key': 'portPath',
      'type': 'string',
      'default': '',
      'required': true,
      'secret': false,
      'role': 'PortPath',
      'appliesWhen': {'key': 'connectionType', 'value': 'serial'},
    },
    {
      'key': 'password',
      'type': 'string',
      'default': '',
      'required': false,
      'secret': true,
      'role': 'Secret',
    },
    {
      'key': 'meshAlignment',
      'type': 'futuretype',
      'default': 'off',
      'required': false,
      'secret': false,
      'role': 'Plain',
    },
  ],
});

Future<List<(Map<String, Object?>, Map<String, String>)>> _pump(
  WidgetTester tester,
) async {
  final sent = <(Map<String, Object?>, Map<String, String>)>[];
  await tester.pumpWidget(
    MaterialApp(
      home: Scaffold(
        body: AlpacaBridgeDriverForm(
          driver: _driver(),
          onSubmit: (v, s) => sent.add((v, s)),
        ),
      ),
    ),
  );
  return sent;
}

void main() {
  testWidgets('a secret field is obscured', (tester) async {
    await _pump(tester);
    expect(
      tester
          .widget<TextField>(
            find.descendant(
              of: find.byKey(const Key('form-field-password')),
              matching: find.byType(TextField),
            ),
          )
          .obscureText,
      isTrue,
    );
  });

  testWidgets(
    'an unknown field type falls back to a plain text field holding its default',
    (tester) async {
      await _pump(tester);
      final tf = tester.widget<TextField>(
        find.descendant(
          of: find.byKey(const Key('form-field-meshAlignment')),
          matching: find.byType(TextField),
        ),
      );
      expect(tf.controller!.text, 'off');
      expect(tf.obscureText, isFalse);
    },
  );

  testWidgets(
    'a required field blocks Add until it is filled, then the secret goes out separately from the values',
    (tester) async {
      final sent = await _pump(tester);
      await tester.tap(find.byKey(const Key('form-submit')));
      await tester.pump();
      expect(sent, isEmpty, reason: 'portPath is required and empty');
      await tester.enterText(
        find.descendant(
          of: find.byKey(const Key('form-field-portPath')),
          matching: find.byType(TextField),
        ),
        '/dev/ttyUSB0',
      );
      await tester.enterText(
        find.descendant(
          of: find.byKey(const Key('form-field-password')),
          matching: find.byType(TextField),
        ),
        'hunter2',
      );
      await tester.tap(find.byKey(const Key('form-submit')));
      await tester.pump();
      expect(sent.single.$1['portPath'], '/dev/ttyUSB0');
      expect(
        sent.single.$1.containsKey('password'),
        isFalse,
        reason: 'secrets travel in their own map',
      );
      expect(sent.single.$2, {'password': 'hunter2'});
    },
  );

  testWidgets('an enum field with no default sends the value it shows', (
    tester,
  ) async {
    final sent = <Map<String, Object?>>[];
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: AlpacaBridgeDriverForm(
            driver: BridgeDriver.fromJson({
              'vendor': 'acme',
              'deviceType': 'focuser',
              'displayName': 'Acme focuser',
              'vendorLabel': 'Acme',
              'available': true,
              'fields': [
                {
                  'key': 'baudRate',
                  'type': 'string',
                  'required': true,
                  'secret': false,
                  'role': 'Plain',
                  'enum': ['9600', '115200'],
                },
              ],
            }),
            onSubmit: (v, _) => sent.add(v),
          ),
        ),
      ),
    );
    expect(find.text('9600'), findsWidgets);
    await tester.tap(find.byKey(const Key('form-submit')));
    await tester.pump();
    expect(find.text('Required'), findsNothing);
    expect(sent.single['baudRate'], '9600');
  });
}
