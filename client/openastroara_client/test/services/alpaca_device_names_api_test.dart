import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/alpaca_device_names_api.dart';

void main() {
  group('AlpacaDeviceNamesApi.parseNames (#1067 daemon envelope)', () {
    test('maps the names object verbatim', () {
      expect(
        AlpacaDeviceNamesApi.parseNames({
          'names': {'camera/1': 'ZWO ASI290MM Mini', 'telescope/0': 'AM5N'},
        }),
        {'camera/1': 'ZWO ASI290MM Mini', 'telescope/0': 'AM5N'},
      );
    });

    test('anything malformed is an empty map (labels stay generic)', () {
      expect(AlpacaDeviceNamesApi.parseNames(null), isEmpty);
      expect(AlpacaDeviceNamesApi.parseNames('nope'), isEmpty);
      expect(AlpacaDeviceNamesApi.parseNames({'names': []}), isEmpty);
      expect(
        AlpacaDeviceNamesApi.parseNames({
          'names': {'camera/1': '', 'x': 3},
        }),
        isEmpty,
      );
    });
  });
}
