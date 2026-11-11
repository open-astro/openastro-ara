import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';

class _File extends SimpleModePrefsService {
  _File(this.value);
  bool value;
  @override
  Future<bool> load() async => value;
  @override
  Future<void> save(bool enabled) async => value = enabled;
}

/// The rig's site section, with the made-up site the tests share.
class _Rig extends ProfileApi {
  _Rig({bool pier = false})
    : site = SiteSettings(
        latitudeDeg: -33.856784,
        longitudeDeg: 151.215297,
        permanentPier: pier,
      ),
      super(const AraServer(hostname: '192.0.2.10', port: 5000));
  SiteSettings site;
  bool failPut = false;
  int puts = 0;
  @override
  Future<SiteSettings> getSiteSettings() async => site;
  @override
  Future<SiteSettings> putSiteSettings(SiteSettings value) async {
    puts++;
    if (failPut) throw StateError('rig unreachable');
    return site = value;
  }
}

ProviderContainer _app(_File file, _Rig? rig) {
  final c = ProviderContainer(
    overrides: [
      simplePermanentPierPrefsProvider.overrideWithValue(file),
      profileApiProvider.overrideWithValue(rig),
    ],
  );
  addTearDown(c.dispose);
  return c;
}

void main() {
  test(
    'the switch follows the rig profile, so a second device sees it on',
    () async {
      final c = _app(_File(false), _Rig(pier: true));
      expect(await c.read(simplePermanentPierProvider.future), isTrue);
    },
  );

  test(
    'turning it on writes the profile and keeps the rest of the site',
    () async {
      final rig = _Rig();
      final file = _File(false);
      final c = _app(file, rig);
      await c.read(simplePermanentPierProvider.future);
      await c.read(simplePermanentPierProvider.notifier).set(true);
      expect(rig.site.permanentPier, isTrue);
      expect(
        rig.site.latitudeDeg,
        -33.856784,
        reason: 'the section was read before it was written',
      );
      expect(file.value, isFalse, reason: 'nothing left to carry over');
      expect(c.read(simplePermanentPierProvider).value, isTrue);
    },
  );

  test('the old device-local answer is carried to the profile once', () async {
    final rig = _Rig();
    final file = _File(true);
    final c = _app(file, rig);
    expect(await c.read(simplePermanentPierProvider.future), isTrue);
    expect(rig.site.permanentPier, isTrue);
    expect(file.value, isFalse, reason: 'cleared once the rig has it');
  });

  test(
    'a failed carry-over keeps the local answer and tries again next start',
    () async {
      final rig = _Rig()..failPut = true;
      final file = _File(true);
      expect(
        await _app(file, rig).read(simplePermanentPierProvider.future),
        isTrue,
      );
      expect(file.value, isTrue);

      rig.failPut = false;
      expect(
        await _app(file, rig).read(simplePermanentPierProvider.future),
        isTrue,
      );
      expect(rig.site.permanentPier, isTrue);
      expect(file.value, isFalse);
    },
  );

  test('a failed write while switching on keeps it on this device', () async {
    final rig = _Rig()..failPut = true;
    final file = _File(false);
    final c = _app(file, rig);
    await c.read(simplePermanentPierProvider.future);
    await c.read(simplePermanentPierProvider.notifier).set(true);
    expect(c.read(simplePermanentPierProvider).value, isTrue);
    expect(file.value, isTrue);
  });

  test('no rig: the file alone decides', () async {
    final file = _File(false);
    final c = _app(file, null);
    await c.read(simplePermanentPierProvider.future);
    await c.read(simplePermanentPierProvider.notifier).set(true);
    expect(file.value, isTrue);
  });
}
