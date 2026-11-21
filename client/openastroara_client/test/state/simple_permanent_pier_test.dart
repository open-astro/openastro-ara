import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';

import '../support/fake_section_rig.dart';

class _File extends SimpleModePrefsService {
  _File(this.value);
  bool? value; // null: no file on this device
  @override
  Future<bool> load() async => value ?? false;
  @override
  Future<void> save(bool enabled) async => value = enabled;
}

/// The rig's site section, with the made-up site the tests share, served
/// with its tag (ADR 0015).
class _Rig {
  _Rig({bool pier = false})
    : http = FakeSectionRig({
        'site': {
          ...rigDefaults['site']!,
          'latitude_deg': -33.856784,
          'longitude_deg': 151.215297,
          'permanent_pier': pier,
        },
      });
  final FakeSectionRig http;

  /// Every PUT of the site fails (nothing written) while on.
  set failPut(bool on) {
    http.failPut.removeWhere((s) => s == 'site');
    if (on) http.failPut.addAll(List.filled(20, 'site'));
  }

  int get puts => http.puts.length;
  ({bool permanentPier, double latitudeDeg}) get site {
    final m = http.sectionOf('site')! as Map;
    return (
      permanentPier: m['permanent_pier'] as bool,
      latitudeDeg: (m['latitude_deg'] as num).toDouble(),
    );
  }

  ProfileApi api() => http.api();
}

ProviderContainer _app(_File file, _Rig? rig) {
  final c = ProviderContainer(
    overrides: [
      simplePermanentPierPrefsProvider.overrideWithValue(file),
      profileApiProvider.overrideWithValue(rig?.api()),
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

  test(
    'the switch keeps a site edit the Full UI panel has not saved',
    () async {
      final rig = _Rig();
      final c = _app(_File(false), rig);
      await c.read(simplePermanentPierProvider.future);
      // Typed in Settings > Location & sky; Save not pressed yet.
      c.read(siteSettingsProvider.notifier).setLatitudeDeg(51.477833);

      await c.read(simplePermanentPierProvider.notifier).set(true);

      expect(c.read(siteSettingsProvider).value!.latitudeDeg, 51.477833);
      expect(c.read(siteSettingsProvider).value!.permanentPier, isTrue);
      expect(
        rig.site.latitudeDeg,
        -33.856784,
        reason: 'only the pier is saved',
      );
      expect(rig.site.permanentPier, isTrue);
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

  test(
    'a refused switch-off puts the switch back to what the rig holds',
    () async {
      final rig = _Rig(pier: true)..failPut = true;
      final file = _File(null);
      final c = _app(file, rig);
      await c.read(simplePermanentPierProvider.future);

      await expectLater(
        c.read(simplePermanentPierProvider.notifier).set(false),
        throwsA(isA<DioException>()),
        reason: 'so the screen can say the save failed',
      );

      expect(c.read(simplePermanentPierProvider).value, isTrue);
      expect(rig.site.permanentPier, isTrue);
      expect(file.value, isNull, reason: 'the refused off is not written down');
    },
  );

  test('a refused switch-off drops an on still waiting for the rig', () async {
    final rig = _Rig()..failPut = true;
    final file = _File(false);
    final c = _app(file, rig);
    await c.read(simplePermanentPierProvider.future);
    final pier = c.read(simplePermanentPierProvider.notifier);
    await pier.set(true); // refused: kept on this device for the next start
    await pier.set(false); // refused too, but the rig holds off already
    expect(c.read(simplePermanentPierProvider).value, isFalse);
    expect(file.value, isFalse);

    rig.failPut = false;
    expect(
      await _app(file, rig).read(simplePermanentPierProvider.future),
      isFalse,
    );
    expect(
      rig.site.permanentPier,
      isFalse,
      reason: 'the off was the last word',
    );
  });

  test('a switch-off with the site not loaded from the rig sends nothing and '
      'stays on (ADR 0015)', () async {
    final rig = _Rig(pier: true);
    rig.http.failGet.add('site');
    final file = _File(true);
    final c = _app(file, rig);
    expect(await c.read(simplePermanentPierProvider.future), isTrue);

    await expectLater(
      c.read(simplePermanentPierProvider.notifier).set(false),
      throwsA(isA<SectionNotLoadedException>()),
      reason: 'so the screen can say nothing was saved',
    );

    expect(c.read(simplePermanentPierProvider).value, isTrue);
    expect(rig.puts, 0);
    expect(file.value, isTrue, reason: 'the refused off is not written down');
  });

  test('a switch-off with only the cache copy of the site (pier off) sends '
      'nothing and stays on (ADR 0015)', () async {
    final rig = _Rig(pier: true);
    rig.http.failGet.add('site');
    final file = _File(true);
    final c = _app(file, rig);
    // The offline cache says off; the rig (unreachable) still holds on.
    c
        .read(siteSettingsProvider.notifier)
        .seed(const SiteSettings(permanentPier: false));
    expect(await c.read(simplePermanentPierProvider.future), isTrue);
    expect(c.read(siteSettingsProvider).value!.permanentPier, isFalse);

    await expectLater(
      c.read(simplePermanentPierProvider.notifier).set(false),
      throwsA(isA<SectionNotLoadedException>()),
      reason: 'a cache copy is not what the rig holds',
    );

    expect(c.read(simplePermanentPierProvider).value, isTrue);
    expect(rig.puts, 0);
    expect(file.value, isTrue, reason: 'the refused off is not written down');
  });

  test('no rig: the file alone decides', () async {
    final file = _File(false);
    final c = _app(file, null);
    await c.read(simplePermanentPierProvider.future);
    await c.read(simplePermanentPierProvider.notifier).set(true);
    expect(file.value, isTrue);
  });
}
