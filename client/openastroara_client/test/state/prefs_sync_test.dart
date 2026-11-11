import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/client_settings.dart';
import 'package:openastroara/services/client_settings_api.dart';
import 'package:openastroara/services/night_mode_prefs_service.dart';
import 'package:openastroara/services/planetarium_prefs_service.dart';
import 'package:openastroara/services/planning_prefs_service.dart';
import 'package:openastroara/services/preflight_prefs_service.dart';
import 'package:openastroara/services/synced_prefs_pending_service.dart';
import 'package:openastroara/state/client_settings_state.dart';
import 'package:openastroara/state/planning/planning_prefs_state.dart';
import 'package:openastroara/state/preflight/preflight_state.dart';
import 'package:openastroara/state/prefs_sync.dart';
import 'package:openastroara/state/sky_atlas/planetarium_prefs_sync.dart';

import '../helpers/fake_rig_settings.dart';

/// One simulated device: its own app-support folder (the local files) and its
/// own container, talking to a rig that several devices can share.
class _Device {
  _Device(this.rig, {Directory? dir, bool noRig = false, ClientSettingsClient? api})
    : dir = dir ?? Directory.systemTemp.createTempSync('ara-sync-') {
    Future<Directory> support() async => this.dir;
    container = ProviderContainer(
      overrides: [
        clientSettingsApiProvider.overrideWithValue(noRig ? null : (api ?? rig)),
        planningPrefsServiceProvider.overrideWithValue(PlanningPrefsService(supportDir: support)),
        preflightPrefsProvider.overrideWithValue(PreflightPrefsService(supportDir: support)),
        syncedPrefsPendingProvider.overrideWithValue(SyncedPrefsPendingService(supportDir: support)),
      ],
    );
    support_ = support;
    addTearDown(container.dispose);
  }

  final FakeRigSettings rig;
  final Directory dir;
  late final ProviderContainer container;
  late final Future<Directory> Function() support_;

  PlanningPrefsService get planningFile => PlanningPrefsService(supportDir: support_);
  PreflightPrefsService get preflightFile => PreflightPrefsService(supportDir: support_);
  PlanetariumPrefsService get planetariumFile => PlanetariumPrefsService(supportDir: support_);
  SyncedPrefsPendingService get pending => SyncedPrefsPendingService(supportDir: support_);

  /// Open the app on this device: build the synced notifiers and wait for the
  /// rig's settings and the first reconcile.
  Future<void> open() async {
    container.listen(planningPrefsProvider, (_, _) {});
    container.listen(preflightPromptProvider, (_, _) {});
    await container.read(planningPrefsProvider.notifier).ready;
    await container.read(clientSettingsProvider.future);
    await settle();
  }

  PlanningPrefs get planning => container.read(planningPrefsProvider);
}

/// Real file IO sits behind the notifiers: poll until it has drained.
Future<void> settle() async {
  for (var i = 0; i < 25; i++) {
    await Future<void>.delayed(const Duration(milliseconds: 10));
  }
}

Future<void> eventually(bool Function() ok) async {
  for (var i = 0; i < 100 && !ok(); i++) {
    await Future<void>.delayed(const Duration(milliseconds: 20));
  }
  expect(ok(), isTrue);
}

/// The pending marker is written after the upload settles: poll for it.
Future<void> pendingHas(SyncedPrefsPendingService Function() pending, bool present) async {
  for (var i = 0; i < 100 && (await pending().load()).contains('planning') != present; i++) {
    await Future<void>.delayed(const Duration(milliseconds: 20));
  }
}

void main() {
  test('a planning knob saved on device A is what device B reads after its fetch', () async {
    final rig = FakeRigSettings();
    final a = _Device(rig);
    await a.open();
    a.container.read(planningPrefsProvider.notifier).setNoiseTolerancePct(3);
    await eventually(() => rig.stored.containsKey('planning'));

    final b = _Device(rig);
    await b.open();
    await eventually(() => b.planning.noiseTolerancePct == 3);
    await settle();
    expect(
      (await b.planningFile.load()).noiseTolerancePct,
      3,
      reason: 'the rig value lands in the local file too (the offline fallback)',
    );
  });

  test('clearing the magnitude floor on A clears it on B', () async {
    final rig = FakeRigSettings();
    final a = _Device(rig);
    await a.open();
    a.container.read(planningPrefsProvider.notifier).setMagnitudeFloor(11);
    await eventually(() => (rig.stored['planning'] as Map?)?['magnitude_floor'] == 11);
    final b = _Device(rig);
    await b.open();
    expect(b.planning.magnitudeFloor, 11);

    a.container.read(planningPrefsProvider.notifier).setMagnitudeFloor(null);
    await eventually(() => (rig.stored['planning'] as Map)['magnitude_floor'] == null);
    await b.container.read(clientSettingsProvider.notifier).refetch();
    await eventually(() => b.planning.magnitudeFloor == null);
  });

  test('a device-local preference never reaches the rig', () async {
    final rig = FakeRigSettings();
    final a = _Device(rig);
    await a.open();
    a.container.read(planningPrefsProvider.notifier).setNoiseTolerancePct(4);
    await NightModePrefsService(supportDir: a.support_).save(true);
    await eventually(() => rig.stored.containsKey('planning'));
    await settle();
    expect(rig.stored.keys, ['planning'], reason: 'night mode stays on the device');
  });

  test('a rig with no value is seeded from the local file', () async {
    final rig = FakeRigSettings();
    final dir = Directory.systemTemp.createTempSync('ara-sync-');
    await PlanningPrefsService(supportDir: () async => dir).save(const PlanningPrefs(noiseTolerancePct: 7));
    final a = _Device(rig, dir: dir);
    await a.open();
    await eventually(() => rig.stored.containsKey('planning'));
    expect((rig.stored['planning'] as Map)['noise_tolerance_pct'], 7);
  });

  test('a device with no stored value never seeds the rig with defaults', () async {
    final rig = FakeRigSettings();
    final a = _Device(rig);
    await a.open();
    expect(rig.merges, 0);
    expect(rig.stored, isEmpty);
  });

  test('the second device takes the rig value over its own local one', () async {
    final rig = FakeRigSettings({
      'planning': {'noise_tolerance_pct': 9.0, 'magnitude_floor': null},
    });
    final dir = Directory.systemTemp.createTempSync('ara-sync-');
    await PlanningPrefsService(supportDir: () async => dir).save(const PlanningPrefs(noiseTolerancePct: 2));
    final b = _Device(rig, dir: dir);
    await b.open();
    expect(b.planning.noiseTolerancePct, 9);
  });

  test('a failed save stays on the device, is marked pending, and is sent on the next load before the rig value applies', () async {
    final rig = FakeRigSettings({
      'planning': {'noise_tolerance_pct': 9.0, 'magnitude_floor': null},
    });
    final a = _Device(rig);
    await a.open();
    expect(a.planning.noiseTolerancePct, 9);

    rig.failWrites = true;
    a.container.read(planningPrefsProvider.notifier).setNoiseTolerancePct(4);
    await eventually(() => rig.merges == 1);
    await pendingHas(() => a.pending, true);
    expect(a.planning.noiseTolerancePct, 4, reason: 'the local value stays');
    expect((await a.planningFile.load()).noiseTolerancePct, 4);
    expect(await a.pending.load(), {'planning'});
    expect((rig.stored['planning'] as Map)['noise_tolerance_pct'], 9, reason: 'the rig has not heard');

    // The app restarts with the link back: the pending key goes up first.
    rig.failWrites = false;
    a.container.dispose();
    final again = _Device(rig, dir: a.dir);
    await again.open();
    await eventually(() => (rig.stored['planning'] as Map)['noise_tolerance_pct'] == 4);
    expect(again.planning.noiseTolerancePct, 4, reason: 'the rig value did not overwrite the offline edit');
    await pendingHas(() => again.pending, false);
    expect(await again.pending.load(), isEmpty);
  });

  test('two devices saving different keys at once keep both', () async {
    final rig = FakeRigSettings();
    final a = _Device(rig);
    final b = _Device(rig);
    await a.open();
    await b.open();
    a.container.read(planningPrefsProvider.notifier).setNoiseTolerancePct(3);
    final nights = b.container.read(preflightPromptProvider.notifier);
    await b.container.read(preflightPromptProvider.future);
    await nights.acknowledgeTonight();
    await eventually(() => rig.stored.containsKey('planning') && rig.stored.containsKey('preflight_nights'));
  });

  test('answered nights from two devices form a union, capped like the local file', () async {
    final rig = FakeRigSettings({
      'preflight_nights': [for (var i = 0; i < 59; i++) '2026-01-${(i + 1).toString().padLeft(2, '0')}x'],
    });
    final dir = Directory.systemTemp.createTempSync('ara-sync-');
    final local = PreflightPrefsService(supportDir: () async => dir);
    await local.saveAcknowledgedNights({'2026-11-01', '2026-11-02', '2026-11-03'});
    final a = _Device(rig, dir: dir);
    await a.open();
    await eventually(() => (rig.stored['preflight_nights'] as List).contains('2026-11-03'));

    final sent = (rig.stored['preflight_nights'] as List).cast<String>();
    expect(sent.length, PreflightPrefsService.cap, reason: 'capped like the local file');
    expect(sent, containsAll(['2026-11-01', '2026-11-02', '2026-11-03']));
    expect(await local.loadAcknowledgedNights(), hasLength(PreflightPrefsService.cap));
  });

  test('a night answered on A is answered on B', () async {
    final rig = FakeRigSettings();
    final a = _Device(rig);
    await a.open();
    await a.container.read(preflightPromptProvider.future);
    await a.container.read(preflightPromptProvider.notifier).acknowledgeTonight();
    await eventually(() => rig.stored.containsKey('preflight_nights'));

    final b = _Device(rig);
    await b.open();
    await eventually(() => b.container.read(preflightPromptProvider).asData?.value.acknowledgedTonight == true);
  });

  test('planetarium toggles: the rig value wins over a local one, a local-only toggle survives and reaches the rig', () async {
    final rig = FakeRigSettings({
      'planetarium': {'dss': true, 'cat:ngc': false},
    });
    final dir = Directory.systemTemp.createTempSync('ara-sync-');
    final file = PlanetariumPrefsService(supportDir: () async => dir);
    await file.save({'dss': false, 'atmosphere': true});
    final a = _Device(rig, dir: dir);
    await a.open();

    final applied = await a.container
        .read(prefsSyncProvider)
        .reconcile(planetariumSyncedPref(file));
    expect(applied, {'dss': true, 'atmosphere': true, 'cat:ngc': false});
    await eventually(() => (rig.stored['planetarium'] as Map).containsKey('atmosphere'));
    expect(await file.load(), {'dss': true, 'atmosphere': true, 'cat:ngc': false});
  });

  test('a planetarium toggle changed on this device is merged over the rig value, not the whole map replaced', () async {
    final rig = FakeRigSettings({
      'planetarium': {'dss': true, 'landscape': true},
    });
    final a = _Device(rig);
    await a.open();
    final file = a.planetariumFile;
    await file.save({'dss': false});
    await a.container.read(prefsSyncProvider).push(planetariumSyncedPref(file), {'dss': false});
    expect(rig.stored['planetarium'], {'dss': false, 'landscape': true});
  });

  test('an older daemon: nothing is sent, nothing is marked, the local value stays', () async {
    final rig = FakeRigSettings()..oldDaemon = true;
    final a = _Device(rig);
    await a.open();
    a.container.read(planningPrefsProvider.notifier).setNoiseTolerancePct(6);
    await eventually(() => rig.merges == 1);
    await settle();
    expect(a.planning.noiseTolerancePct, 6);
    expect((await a.planningFile.load()).noiseTolerancePct, 6);
    expect(await a.pending.load(), isEmpty, reason: 'no retry loop against a daemon that cannot merge');
  });

  test('no rig connected: the change is saved locally and marked pending', () async {
    final rig = FakeRigSettings();
    final a = _Device(rig, noRig: true);
    await a.open();
    a.container.read(planningPrefsProvider.notifier).setNoiseTolerancePct(5.5);
    await eventually(() => a.planning.noiseTolerancePct == 5.5);
    for (var i = 0; i < 100 && !(await a.pending.load()).contains('planning'); i++) {
      await Future<void>.delayed(const Duration(milliseconds: 20));
    }
    expect((await a.planningFile.load()).noiseTolerancePct, 5.5);
    expect(await a.pending.load(), {'planning'});
  });

  test('an unreachable rig on load does not throw into the UI and leaves the local value', () async {
    final rig = FakeRigSettings();
    final dir = Directory.systemTemp.createTempSync('ara-sync-');
    await PlanningPrefsService(supportDir: () async => dir).save(const PlanningPrefs(noiseTolerancePct: 8));
    final a = _Device(rig, dir: dir, api: _Down());
    a.container.listen(planningPrefsProvider, (_, _) {});
    await a.container.read(planningPrefsProvider.notifier).ready;
    await settle();
    expect(a.planning.noiseTolerancePct, 8);
  });
}

class _Down extends FakeRigSettings {
  @override
  Future<ClientSettings> fetch() async => throw StateError('unreachable');
}
