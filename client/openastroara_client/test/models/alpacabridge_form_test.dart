import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/alpacabridge_form.dart';
import 'package:openastroara/models/alpacabridge_setup.dart';

// The catalog sample is the one in design/preview/alpacabridge-add-devices-mock.html (#1580): the shape
// AlpacaBridge's devicecatalog sends. The form is generated from it, so each rule is checked against it.
Map<String, Object?> _field(
  String key,
  String type, {
  Object? def,
  bool required = false,
  bool secret = false,
  Map<String, Object?>? range,
  List<String>? enumValues,
  Map<String, Object?>? appliesWhen,
  List<Map<String, Object?>>? fields,
}) => {
  'key': key,
  'type': type,
  'default': def,
  'required': required,
  'secret': secret,
  'role': 'Plain',
  'range': ?range,
  'enum': ?enumValues,
  'appliesWhen': ?appliesWhen,
  'fields': ?fields,
};

BridgeForm _form(List<Map<String, Object?>> fields) => BridgeForm(
  BridgeDriver.fromJson({
    'vendor': 'acme',
    'deviceType': 'telescope',
    'displayName': 'Acme mount',
    'vendorLabel': 'Acme',
    'available': true,
    'fields': fields,
  }),
);

void main() {
  test('defaults fill the form; a record list starts empty', () {
    final f = _form([
      _field('usbBandwidth', 'int', def: 40),
      _field('portPath', 'string', def: '/dev/ttyUSB0'),
      _field('ports', 'recordList', fields: [_field('gpio', 'int', def: 0)]),
    ]);
    expect(f.initialValues(), {
      'usbBandwidth': 40,
      'portPath': '/dev/ttyUSB0',
      'ports': <Object?>[],
    });
  });

  test(
    'a visible required field with no value is an error; a filled one is not',
    () {
      final f = _form([_field('portPath', 'string', required: true)]);
      expect(f.errors(f.initialValues()).keys, ['portPath']);
      expect(f.errors({'portPath': '/dev/ttyUSB0'}), isEmpty);
      expect(f.errors({'portPath': '   '}).keys, [
        'portPath',
      ], reason: 'blank text is not a value');
    },
  );

  test('a number outside its range is an error, the bounds are allowed', () {
    final f = _form([
      _field('usbBandwidth', 'int', def: 40, range: {'min': 0, 'max': 100}),
    ]);
    expect(f.errors({'usbBandwidth': 101}).keys, ['usbBandwidth']);
    expect(f.errors({'usbBandwidth': -1}).keys, ['usbBandwidth']);
    expect(f.errors({'usbBandwidth': 0}), isEmpty);
    expect(f.errors({'usbBandwidth': 100}), isEmpty);
  });

  test('a double field keeps its range and its fraction', () {
    final f = _form([
      _field('siteLatitude', 'double', def: 0, range: {'min': -90, 'max': 90}),
    ]);
    expect(f.errors({'siteLatitude': -33.856784}), isEmpty);
    expect(f.errors({'siteLatitude': 90.5}).keys, ['siteLatitude']);
  });

  test('an enum field offers its values and refuses any other', () {
    final f = _form([
      _field(
        'connectionType',
        'string',
        def: 'serial',
        required: true,
        enumValues: ['serial', 'network'],
      ),
    ]);
    expect(f.driver.fields.single.enumValues, ['serial', 'network']);
    expect(f.errors({'connectionType': 'bluetooth'}).keys, ['connectionType']);
    expect(f.errors({'connectionType': 'network'}), isEmpty);
  });

  test(
    'appliesWhen shows a field only while its switch holds the named value',
    () {
      final f = _form([
        _field(
          'connectionType',
          'string',
          def: 'serial',
          required: true,
          enumValues: ['serial', 'network'],
        ),
        _field(
          'portPath',
          'string',
          required: true,
          appliesWhen: {'key': 'connectionType', 'value': 'serial'},
        ),
        _field(
          'host',
          'string',
          required: true,
          appliesWhen: {'key': 'connectionType', 'value': 'network'},
        ),
      ]);
      expect(f.visibleFields({'connectionType': 'serial'}).map((x) => x.key), [
        'connectionType',
        'portPath',
      ]);
      expect(f.visibleFields({'connectionType': 'network'}).map((x) => x.key), [
        'connectionType',
        'host',
      ]);
    },
  );

  test(
    'a hidden required field is not an error and is left out of the body',
    () {
      final f = _form([
        _field(
          'connectionType',
          'string',
          def: 'serial',
          required: true,
          enumValues: ['serial', 'network'],
        ),
        _field(
          'portPath',
          'string',
          required: true,
          appliesWhen: {'key': 'connectionType', 'value': 'serial'},
        ),
        _field(
          'host',
          'string',
          required: true,
          appliesWhen: {'key': 'connectionType', 'value': 'network'},
        ),
      ]);
      final values = {
        'connectionType': 'serial',
        'portPath': '/dev/ttyUSB0',
        'host': 'stale',
      };
      expect(f.errors(values), isEmpty);
      expect(f.body(values), {
        'vendor': 'acme',
        'deviceType': 'telescope',
        'connectionType': 'serial',
        'portPath': '/dev/ttyUSB0',
      });
    },
  );

  test(
    'a nested group is checked row by row, with the row in the error key',
    () {
      final f = _form([
        _field(
          'ports',
          'recordList',
          fields: [
            _field('name', 'string', def: ''),
            _field('gpio', 'int', required: true, range: {'min': 0, 'max': 40}),
          ],
        ),
      ]);
      expect(
        f.errors({
          'ports': [
            {'name': 'a', 'gpio': 3},
          ],
        }),
        isEmpty,
      );
      expect(
        f.errors({
          'ports': [
            {'name': 'a', 'gpio': 3},
            {'name': 'b'},
          ],
        }).keys,
        ['ports[1].gpio'],
      );
      expect(
        f.errors({
          'ports': [
            {'gpio': 41},
          ],
        }).keys,
        ['ports[0].gpio'],
      );
    },
  );

  test('a secret field is flagged secret and still goes into the body', () {
    final f = _form([_field('password', 'string', def: '', secret: true)]);
    expect(f.driver.fields.single.secret, isTrue);
    expect(f.body({'password': 'hunter2'})['password'], 'hunter2');
  });

  test(
    'an unknown field type is kept as unknown and its value is sent as text',
    () {
      final f = _form([_field('meshAlignment', 'futuretype', def: 3)]);
      expect(f.driver.fields.single.type, BridgeFieldType.unknown);
      expect(f.initialValues(), {
        'meshAlignment': '3',
      }, reason: 'the default (a number here) is held as text');
      expect(f.errors({'meshAlignment': '4'}), isEmpty);
      expect(
        f.body({'meshAlignment': 4})['meshAlignment'],
        '4',
        reason: 'sent as text, whatever the value was',
      );
    },
  );
}
