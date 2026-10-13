import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_readiness.dart';
import 'package:openastroara/models/tracking_calibration.dart';
import 'package:openastroara/models/mount_spec_sheet.dart';
import 'package:openastroara/services/camera_geometry_api.dart';
import 'package:openastroara/services/filter_wheel_names_api.dart';
import 'package:openastroara/services/focuser_props_api.dart';
import 'package:openastroara/services/rotator_props_api.dart';
import 'package:openastroara/services/telescope_optics_api.dart';
import 'package:openastroara/state/settings/equipment_connection_state.dart';

void main() {
  group('alpacaDeviceSetupUri', () {
    test('builds the Alpaca-standard per-device setup page', () {
      final uri = alpacaDeviceSetupUri(const DiscoveredDevice(
        uniqueId: 'u1',
        name: 'ZWO EFW',
        deviceType: EquipmentDeviceType.filterWheel,
        hostName: 'rc91.lan',
        ipAddress: '192.168.8.118',
        ipPort: 11111,
        alpacaDeviceNumber: 2,
        useHttps: false,
      ));
      expect(uri.toString(),
          'http://192.168.8.118:11111/setup/v1/filterwheel/2/setup');
    });

    test('falls back to hostName and honors https', () {
      final uri = alpacaDeviceSetupUri(const DiscoveredDevice(
        uniqueId: 'u1',
        name: 'Mount',
        deviceType: EquipmentDeviceType.mount,
        hostName: 'rc91.lan',
        ipAddress: '',
        ipPort: 443,
        alpacaDeviceNumber: 0,
        useHttps: true,
      ));
      expect(uri.toString(), 'https://rc91.lan/setup/v1/telescope/0/setup');
    });
  });

  group('ReadinessRules.camera', () {
    test('a reporting camera is ready with pixel size + sensor facts', () {
      final r = ReadinessRules.camera(
          'ASI2600MM',
          null,
          const CameraGeometry(
              sensorWidthPx: 6248,
              sensorHeightPx: 4176,
              pixelSizeUm: 3.76,
              maxBin: 4));
      expect(r.state, ReadinessState.ready);
      expect(r.hasBlockingGap, isFalse);
      expect(r.facts.map((f) => f.value),
          containsAll(['3.76 µm', '6248×4176', 'up to 4×4']));
    });

    test('no usable sensor is a REQUIRED gap, not ready', () {
      final r = ReadinessRules.camera('ASI2600MM', null, null);
      expect(r.state, ReadinessState.gaps);
      expect(r.hasBlockingGap, isTrue);
    });
  });

  group('ReadinessRules.mount', () {
    test('optics reported → ready, name comes from mount props', () {
      final r = ReadinessRules.mount(
          'generic',
          null,
          const TelescopeOptics(focalLengthMm: 530, apertureMm: 106),
          const MountProps(name: 'iOptron CEM70', maxSlewRateDegPerSec: 4));
      expect(r.state, ReadinessState.ready);
      expect(r.label, 'iOptron CEM70');
      expect(r.facts.map((f) => f.value),
          containsAll(['530 mm', '106 mm', '4°/s']));
    });

    test('#1264 the spec sheet is a fact when the bridge ships one, and silent when not', () {
      const optics = TelescopeOptics(focalLengthMm: 530, apertureMm: 106);
      final withSheet = ReadinessRules.mount('EQM-35', null, optics,
          const MountProps(name: 'EQM-35', specs: MountSpecSheet(model: 'EQM-35 Pro', wormPeriodS: 638, periodicErrorPpArcsec: 25)));
      expect(withSheet.state, ReadinessState.ready);
      expect(withSheet.facts.singleWhere((f) => f.label == 'Spec sheet').value,
          'Periodic error 25″ p-p over 638 s · manufacturer');
      final without = ReadinessRules.mount('EQM-35', null, optics, const MountProps(name: 'EQM-35'));
      expect(without.state, ReadinessState.ready, reason: 'no sheet is no gap: the Unguided panel says so instead');
      expect(without.gaps, isEmpty);
      expect(without.facts.where((f) => f.label == 'Spec sheet'), isEmpty);
    });

    test('#1264 a stored tracking ceiling is an informational fact; none is no gap', () {
      final bare = ReadinessRules.mount('CEM70', null,
          const TelescopeOptics(focalLengthMm: 530, apertureMm: 106), const MountProps());
      expect(bare.facts.map((f) => f.label), isNot(contains('Unguided ceiling')));
      expect(bare.gaps, isEmpty);

      final r = ReadinessRules.mount(
          'CEM70',
          null,
          const TelescopeOptics(focalLengthMm: 530, apertureMm: 106),
          const MountProps(),
          tracking: TrackingCalibration(
              ceilingSeconds: 45, measuredUtc: DateTime.utc(2026, 10, 11, 23), source: 'measured'));
      expect(r.state, ReadinessState.ready);
      final fact = r.facts.singleWhere((f) => f.label == 'Unguided ceiling');
      expect(fact.value, startsWith('45 s · measured 2026-10-1'));
    });

    test('missing focal length is a required gap even with aperture', () {
      final r = ReadinessRules.mount('CEM70', null,
          const TelescopeOptics(apertureMm: 106), const MountProps());
      expect(r.state, ReadinessState.gaps);
      expect(r.hasBlockingGap, isTrue);
      expect(r.gaps.map((g) => g.label), contains('Focal length'));
      expect(r.gaps.map((g) => g.label), isNot(contains('Aperture')));
    });
  });

  group('ReadinessRules.filterWheel', () {
    test('named slots → ready with a filters fact', () {
      final r = ReadinessRules.filterWheel(
          'EFW',
          null,
          const FilterWheelSlots([
            FilterWheelSlot(name: 'L', focusOffset: 0),
            FilterWheelSlot(name: 'Hα', focusOffset: 20),
          ]));
      expect(r.state, ReadinessState.ready);
      expect(r.facts.first.value, 'L · Hα');
      expect(r.facts.map((f) => f.label), contains('Focus offsets'));
    });

    test('all-unnamed slots are a required gap', () {
      final r = ReadinessRules.filterWheel(
          'EFW',
          null,
          const FilterWheelSlots(
              [FilterWheelSlot(name: '  ', focusOffset: 0)]));
      expect(r.state, ReadinessState.gaps);
      expect(r.hasBlockingGap, isTrue);
    });
  });

  group('ReadinessRules.focuser / rotator — unreported props stay workable',
      () {
    test('EAF-style focuser without step size is ready + informational gap',
        () {
      final r = ReadinessRules.focuser(
          'ZWO EAF', null, const FocuserProps(canTempComp: false));
      expect(r.state, ReadinessState.ready);
      expect(r.hasBlockingGap, isFalse);
      expect(r.gaps.single.need, FactNeed.informational);
    });

    test('CAA-style rotator shows the driver-owned reverse state as a fact',
        () {
      final r = ReadinessRules.rotator('ZWO CAA', null,
          const RotatorProps(canReverse: true, reverse: true));
      expect(r.state, ReadinessState.ready);
      expect(
          r.facts.any((f) => f.label == 'Reverse' && f.value == 'on'), isTrue);
    });
  });
}
