import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/gps_site_fill.dart';

void main() {
  // "Fill from GPS" became reachable on phones and tablets when the Android
  // and iOS platforms shipped (#1063); before that the copy had only desktop
  // arms and a tablet fell through to the Linux wording ("plug a USB GPS
  // dongle into the machine running Ara Server").
  group('GPS site-fill copy per platform', () {
    test('mobile is not called a computer', () {
      expect(thisDeviceLabel(ClientPlatform.android), isNot('this computer'));
      expect(thisDeviceLabel(ClientPlatform.iOS), isNot('this computer'));
      expect(thisDeviceLabel(ClientPlatform.linux), 'this computer');
      expect(thisDeviceLabel(ClientPlatform.macOS), 'this Mac');
      expect(thisDeviceLabel(ClientPlatform.windows), 'this PC');
    });

    test('mobile permission hints point at the phone settings, not Linux', () {
      for (final p in [ClientPlatform.android, ClientPlatform.iOS]) {
        final hint = permissionHint(p);
        expect(hint, isNot(contains('Linux')), reason: '$p');
        expect(hint, isNot(contains('USB GPS dongle')), reason: '$p');
        expect(hint, contains('Location'), reason: '$p');
        expect(hint, contains('tap Fill from GPS again'), reason: '$p');
      }
      expect(permissionHint(ClientPlatform.linux), contains('Linux'));
    });

    test('a missing fix on mobile is not blamed on desktop networking', () {
      for (final p in [ClientPlatform.android, ClientPlatform.iOS]) {
        expect(noFixHint(p), isNot(contains('Desktop')), reason: '$p');
      }
      expect(noFixHint(ClientPlatform.macOS), contains('Desktop location'));
    });
  });
}
