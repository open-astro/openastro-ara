import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_calibration_view.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guider_live_api.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/guider/guider_live_state.dart'
    show guiderLiveApiProvider;
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/rig_setup_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart'
    show planningSettingsBootstrapProvider;
import 'package:openastroara/state/ws/ws_providers.dart';

const _gap = RigSetupGap.site;

const _setUpSite = SiteSettings(
  latitudeDeg: -33.856784,
  longitudeDeg: 151.215297,
);
const _setUpOptics = OpticsSettings(
  focalLengthMm: 800,
  sensorWidthPx: 6248,
  sensorHeightPx: 4176,
  pixelSizeUm: 3.76,
);

/// The profile reads the facts wait for, without Dio. A null value is a read
/// that fails, as against a rig that is still booting.
class _Profile extends ProfileApi {
  _Profile({this.site = _setUpSite, this.optics = _setUpOptics})
    : super(const AraServer(hostname: '192.0.2.10', port: 5555));
  final SiteSettings? site;
  final OpticsSettings? optics;
  @override
  Future<SiteSettings> getSiteSettings() async =>
      site ?? (throw Exception('connection refused'));
  @override
  Future<OpticsSettings> getOptics() async =>
      optics ?? (throw Exception('connection refused'));
  @override
  Future<Phd2Settings> getPhd2Settings() async =>
      const Phd2Settings(guideFocalLength: 240, guidePixelSize: 3.75);
}

/// A profile without a Guider.
class _NoGuider extends GuiderStatusNotifier {
  @override
  Future<GuiderStatus?> build() async => null;
}

/// A connected Guider; [at] moves its runtime state on, as the status poll does.
class _ConnectedGuider extends GuiderStatusNotifier {
  @override
  Future<GuiderStatus?> build() async => _at(GuiderRuntimeState.stopped);
  static GuiderStatus _at(GuiderRuntimeState r) => GuiderStatus(
    name: 'Guider',
    connectionState: GuiderConnectionState.connected,
    runtimeState: r,
  );
  void at(GuiderRuntimeState r) => state = AsyncData(_at(r));
}

/// The Guider's calibration reads; [calibration] is what the next read returns.
class _Calibration implements GuiderLiveClient {
  GuiderCalibrationView calibration = const GuiderCalibrationView(
    connected: true,
  );
  @override
  Future<GuiderCalibrationView> getCalibration() async => calibration;
  @override
  dynamic noSuchMethod(Invocation i) => super.noSuchMethod(i);
}

Future<void> _settle() async {
  for (var i = 0; i < 5; i++) {
    await Future<void>.delayed(Duration.zero);
  }
}

/// The facts and gaps for [api], once its reads have settled.
Future<ProviderContainer> _trust(
  _Profile api, {
  bool linkUp = true,
  _ConnectedGuider Function()? guider,
  _Calibration? live,
}) async {
  final c = ProviderContainer(
    overrides: [
      serverLinkUpProvider.overrideWithValue(linkUp),
      profileApiProvider.overrideWithValue(api),
      guiderStatusProvider.overrideWith(guider ?? _NoGuider.new),
      if (live != null) guiderLiveApiProvider.overrideWithValue(live),
      // The planner's bootstrap has finished, but it swallows a failed read,
      // so it left the site and optics at their defaults.
      planningSettingsBootstrapProvider.overrideWith((ref) async {}),
    ],
  );
  addTearDown(c.dispose);
  c.listen(rigSetupGapsProvider, (_, _) {});
  await _settle();
  return c;
}

/// A fully set-up rig without a Guider.
RigSetupFacts _facts({
  double lat = -33.856784,
  double lon = 151.215297,
  bool optics = true,
  bool guider = false,
  int guideFocal = 240,
  double guidePixel = 3.75,
  bool connected = true,
  bool data = true,
  String verdict = 'good',
}) => RigSetupFacts(
  latitudeDeg: lat,
  longitudeDeg: lon,
  opticsConfigured: optics,
  hasGuider: guider,
  guideFocalLengthMm: guideFocal,
  guidePixelSizeUm: guidePixel,
  guiderConnected: connected,
  calibrationHasData: data,
  calibrationVerdict: verdict,
);

void main() {
  group('rigSetupGaps', () {
    test('a set-up rig has no gaps', () {
      expect(rigSetupGaps(_facts()), isEmpty);
      expect(rigSetupGaps(_facts(guider: true)), isEmpty);
    });

    test('the site is missing at exactly 0,0 and at no other coordinate', () {
      expect(rigSetupGaps(_facts(lat: 0, lon: 0)), [_gap]);
      expect(rigSetupGaps(_facts(lat: 0, lon: 151.215297)), isEmpty);
      expect(rigSetupGaps(_facts(lat: -33.856784, lon: 0)), isEmpty);
      expect(rigSetupGaps(_facts(lat: 51.477833, lon: -0.0015)), isEmpty);
    });

    test('the main camera and telescope are missing until the optics are configured', () {
      expect(rigSetupGaps(_facts(optics: false)), [RigSetupGap.mainOptics]);
    });

    test('the guide scope applies only with a Guider, and needs a focal length and a pixel size above 0', () {
      expect(
        rigSetupGaps(_facts(guideFocal: 0, guidePixel: 0)),
        isEmpty,
        reason: 'no Guider, no guiding lines',
      );
      expect(rigSetupGaps(_facts(guider: true, guideFocal: 0)), [
        RigSetupGap.guideScope,
      ]);
      expect(rigSetupGaps(_facts(guider: true, guidePixel: 0)), [
        RigSetupGap.guideScope,
      ]);
      expect(
        rigSetupGaps(_facts(guider: true, guideFocal: 1, guidePixel: 0.1)),
        isEmpty,
      );
    });

    test(
      'the Guider calibration is missing when connected with no stored data',
      () {
        expect(rigSetupGaps(_facts(guider: true, data: false)), [
          RigSetupGap.guiderCalibration,
        ]);
      },
    );

    test('the verdicts: good and check count as done, redo as missing', () {
      expect(rigSetupGaps(_facts(guider: true, verdict: 'good')), isEmpty);
      expect(rigSetupGaps(_facts(guider: true, verdict: 'check')), isEmpty);
      expect(rigSetupGaps(_facts(guider: true, verdict: 'redo')), [
        RigSetupGap.guiderCalibration,
      ]);
    });

    test(
      'a Guider that is not connected stays on the list as connect to check',
      () {
        final facts = _facts(
          guider: true,
          connected: false,
          data: false,
          verdict: 'redo',
        );
        expect(rigSetupGaps(facts), [RigSetupGap.guiderCalibration]);
        expect(facts.calibrationUnchecked, isTrue);
        expect(_facts(guider: true, data: false).calibrationUnchecked, isFalse);
        expect(
          _facts(guider: false, connected: false).calibrationUnchecked,
          isFalse,
        );
        expect(
          rigSetupGaps(_facts(connected: false)),
          isEmpty,
          reason: 'no Guider in the profile: nothing to connect',
        );
      },
    );

    test('every rule together lists them in the sheet order', () {
      final facts = _facts(
        lat: 0,
        lon: 0,
        optics: false,
        guider: true,
        guideFocal: 0,
        data: false,
      );
      expect(rigSetupGaps(facts), [
        RigSetupGap.site,
        RigSetupGap.mainOptics,
        RigSetupGap.guideScope,
        RigSetupGap.guiderCalibration,
      ]);
    });
  });

  group('rigSetupFactsProvider', () {
    test('the site, optics and guide values come from the rig: a set-up rig lists nothing', () async {
      final c = await _trust(_Profile());
      final facts = c.read(rigSetupFactsProvider);
      expect(facts, isNotNull);
      expect(facts!.latitudeDeg, -33.856784);
      expect(facts.opticsConfigured, isTrue);
      expect(c.read(rigSetupGapsProvider), isEmpty);
    });

    test('a site or optics read that fails leaves the facts untrusted and lists nothing', () async {
      for (final api in [_Profile(site: null), _Profile(optics: null)]) {
        final c = await _trust(api);
        expect(c.read(rigSetupFactsProvider), isNull);
        expect(c.read(rigSetupGapsProvider), isEmpty);
      }
    });

    test(
      'a link that is down leaves the facts untrusted and lists nothing',
      () async {
        final notSetUp = _Profile(
          site: const SiteSettings(),
          optics: const OpticsSettings(),
        );
        final c = await _trust(notSetUp, linkUp: false);
        expect(c.read(rigSetupFactsProvider), isNull);
        expect(c.read(rigSetupGapsProvider), isEmpty);
      },
    );

    test('a calibration made while the Guider stays connected takes the line off the list', () async {
      final live = _Calibration();
      late _ConnectedGuider guider;
      final c = await _trust(
        _Profile(),
        guider: () => guider = _ConnectedGuider(),
        live: live,
      );
      expect(c.read(rigSetupGapsProvider), [RigSetupGap.guiderCalibration]);

      // The user calibrates in Guider mode; the Guider never disconnects.
      guider.at(GuiderRuntimeState.calibrating);
      await _settle();
      live.calibration = const GuiderCalibrationView(
        connected: true,
        runState: 'complete',
        data: GuiderCalibrationData(calibrated: true),
        verdict: 'good',
      );
      guider.at(GuiderRuntimeState.guiding);
      await _settle();

      expect(c.read(rigSetupGapsProvider), isEmpty);
    });
  });

  group('rigSetupRigKey', () {
    test('keys on the rig uuid when it has one, else its address, safe as a file name', () {
      const withUuid = AraServer(
        hostname: '192.0.2.10',
        port: 8080,
        uuid: 'a1b2-c3',
      );
      const without = AraServer(hostname: '192.0.2.10', port: 8080);
      expect(rigSetupRigKey(withUuid), 'a1b2-c3');
      expect(rigSetupRigKey(without), '192_0_2_10-8080');
      expect(rigSetupRigKey(null), isNull);
      expect(
        rigSetupRigKey(
          const AraServer(hostname: 'x', port: 1, uuid: '../../etc'),
        ),
        '______etc',
      );
    });
  });
}
