import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/polar_align/polar_align_routine_state.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/state/polar_align/polar_corrector_state.dart';

WsEvent _event(String type, [Map<String, dynamic> payload = const {}]) =>
    WsEvent(type: type, ts: DateTime.utc(2026, 11, 16, 9), seq: 1, payload: payload);

class _Api implements PolarAlignClient {
  PolarAlignSettings settings = const PolarAlignSettings();
  final puts = <Map<String, dynamic>>[];
  @override
  Future<PolarAlignStatus?> getStatus() async => null;
  @override
  Future<PolarAlignSettings> getSettings() async => settings;
  @override
  Future<PolarAlignSettings> putSettings(PolarAlignSettings next) async {
    puts.add(next.toJson());
    return settings = next;
  }
  @override
  Future<void> start({String? stage}) async {}
  @override
  Future<void> next() async {}
  @override
  Future<void> stop() async {}
  @override
  Future<void> complete() async {}
  @override
  Future<void> requestCapture() async {}
  @override
  Future<Uint8List?> getLiveFrame() async => null;
  @override
  void close() {}
}

class _Corrector implements PolarCorrectorClient {
  /// What each read returns, in turn; the last one repeats.
  final reads = <PolarCorrector?>[];
  final calls = <String>[];
  int _i = 0;
  @override
  Future<PolarCorrector?> get() async {
    calls.add('get');
    return reads[_i < reads.length - 1 ? _i++ : _i];
  }

  @override
  Future<void> stop() async => calls.add('stop');
  @override
  Future<void> home() async => calls.add('home');
  @override
  Future<void> stopInRoutine() async => calls.add('stopInRoutine');
  @override
  Future<void> resumeInRoutine() async => calls.add('resumeInRoutine');
  @override
  void close() {}
}

class _Live extends PolarAlignLiveNotifier {
  @override
  PolarAlignLive build() => const PolarAlignLive(phase: PolarAlignStates.adjusting);
  void run(String state) => this.state = PolarAlignLive(phase: PolarAlignStates.adjusting, corrector: PolarCorrectorRun(state: state));
}

const _still = PolarCorrector(present: true, drivable: true);
const _moving = PolarCorrector(present: true, drivable: true, moving: true);

void main() {
  test('PolarCorrector parses the daemon DTO; an unreported position stays null, not 0', () {
    final d = PolarCorrector.fromJson({
      'present': true,
      'carrier': 'switch',
      'device_id': 'sw-1',
      'name': 'Polar base',
      'drivable': true,
      'not_drivable_reason': null,
      'capabilities': {'device_name': 'base', 'settle_seconds': 2},
      'moving': true,
      'azimuth_position_arcmin': 12.4,
      'altitude_position_arcmin': null,
      'unknown_field': 1,
    });
    expect((d.present, d.carrier, d.name, d.drivable, d.moving), (true, 'switch', 'Polar base', true, true));
    expect(d.azimuthPositionArcmin, 12.4);
    expect(d.altitudePositionArcmin, isNull);
    final none = PolarCorrector.fromJson({'present': false});
    expect((none.present, none.drivable, none.azimuthPositionArcmin), (false, false, null));
    final t2 = PolarCorrector.fromJson(
        {'present': true, 'carrier': 'mount', 'drivable': false, 'not_drivable_reason': 'device_runs_its_own_loop'});
    expect((t2.carrier, t2.notDrivableReason), ('mount', 'device_runs_its_own_loop'));
  });

  test('PolarCorrectorRun parses every state, with the optional fields absent', () {
    for (final s in [
      PolarCorrectorStates.manual,
      PolarCorrectorStates.probing,
      PolarCorrectorStates.moving,
      PolarCorrectorStates.settling,
      PolarCorrectorStates.measuring,
      PolarCorrectorStates.converged,
      PolarCorrectorStates.stopped,
    ]) {
      final r = PolarCorrectorRun.tryParse({'state': s, 'moves': 3})!;
      expect((r.state, r.moves, r.axis, r.arcmin, r.seconds, r.cause, r.deviceText), (s, 3, null, null, null, null, null));
    }
    final full = PolarCorrectorRun.tryParse({
      'state': 'stopped', 'moves': 2, 'axis': 'altitude', 'arcmin': -1.5, 'seconds': 3,
      'cause': 'device_fault', 'device_text': 'Altitude: end of travel',
    })!;
    expect((full.axis, full.arcmin, full.seconds, full.cause, full.deviceText),
        ('altitude', -1.5, 3.0, 'device_fault', 'Altitude: end of travel'));
    expect(PolarCorrectorRun.tryParse(null), isNull);
    expect(PolarCorrectorRun.tryParse({'moves': 1}), isNull);
  });

  test('motorsBusy: a move, its settle and the measurement after it', () {
    bool busy(String s) => PolarCorrectorRun(state: s).motorsBusy;
    expect([for (final s in ['probing', 'moving', 'settling', 'measuring']) busy(s)], everyElement(isTrue));
    expect([for (final s in ['manual', 'converged', 'stopped']) busy(s)], everyElement(isFalse));
  });

  test('the status carries corrector, absent when the daemon sends none', () {
    final with_ = PolarAlignStatus.fromJson({'state': 'adjusting', 'corrector': {'state': 'moving', 'axis': 'azimuth', 'arcmin': 6.1, 'moves': 2}});
    expect((with_.corrector!.state, with_.corrector!.arcmin), ('moving', 6.1));
    expect(PolarAlignStatus.fromJson({'state': 'adjusting'}).corrector, isNull);
    expect(liveFromStatus(with_).corrector!.axis, 'azimuth', reason: 'a panel opening hydrates the run');
  });

  test('polar_align.corrector folds into the live view; stopped and error clear it', () {
    const live = PolarAlignLive(phase: PolarAlignStates.adjusting);
    final moving = foldPolarAlignEvent(live, _event(PolarAlignWsEvents.corrector, {'state': 'probing', 'axis': 'azimuth', 'arcmin': 2.0, 'moves': 1}))!;
    expect((moving.corrector!.state, moving.corrector!.moves, moving.phase), ('probing', 1, PolarAlignStates.adjusting));
    expect(foldPolarAlignEvent(moving, _event(PolarAlignWsEvents.stopped))!.corrector, isNull,
        reason: 'the run ends without a corrector event');
    expect(foldPolarAlignEvent(moving, _event(PolarAlignWsEvents.error, {'reason': 'x'}))!.corrector, isNull);
    expect(foldPolarAlignEvent(moving, _event(PolarAlignWsEvents.started))!.corrector, isNull);
    expect(foldPolarAlignEvent(moving, _event(PolarAlignWsEvents.corrector, {'moves': 1})), isNull,
        reason: 'a payload with no state writes nothing');
  });

  test('a profile round trip keeps polar_corrector_auto', () async {
    expect(PolarAlignSettings.fromJson({'polar_corrector_auto': false}).polarCorrectorAuto, isFalse);
    expect(const PolarAlignSettings().polarCorrectorAuto, isTrue, reason: 'the daemon default');
    final api = _Api()..settings = const PolarAlignSettings(polarCorrectorAuto: false);
    final c = ProviderContainer(overrides: [polarAlignApiProvider.overrideWithValue(api)]);
    addTearDown(c.dispose);
    final loaded = await c.read(polarAlignSettingsProvider.future);
    await c.read(polarAlignSettingsProvider.notifier).save(loaded.copyWith(exposureSeconds: 4));
    expect(api.puts.single['polar_corrector_auto'], isFalse, reason: 'a save of another field does not wipe it');
    expect(api.puts.single['exposure_seconds'], 4);
  });

  test('the app knows every stop cause the daemon declares', () {
    final src = File('../../OpenAstroAra.Server/Services/PolarAlignService.Corrector.cs').readAsStringSync();
    final block = RegExp(r'class PolarAlignCorrectorCauses \{([^}]*)\}').firstMatch(src)!.group(1)!;
    final daemon = RegExp(r'= "([a-z_]+)";').allMatches(block).map((m) => m.group(1)).toSet();
    expect(PolarCorrectorCauses.all.toSet(), daemon);
  });

  testWidgets('the device is read again when the run measures, converges or stops, not on every move', (tester) async {
    final api = _Corrector()..reads.add(_still);
    final live = _Live();
    final c = ProviderContainer(overrides: [
      polarCorrectorApiProvider.overrideWithValue(api),
      polarAlignLiveProvider.overrideWith(() => live),
    ]);
    addTearDown(c.dispose);
    final sub = c.listen(polarCorrectorProvider, (_, _) {});
    addTearDown(sub.close);
    await c.read(polarCorrectorProvider.future);
    expect(api.calls, ['get']);
    for (final s in ['probing', 'moving', 'settling']) {
      live.run(s);
      await tester.pump();
    }
    expect(api.calls, ['get'], reason: 'the run comes from the events, not from reading the device');
    for (final s in ['measuring', 'converged', 'stopped']) {
      live.run(s);
      await tester.pump();
    }
    expect(api.calls, ['get', 'get', 'get', 'get']);
  });

  testWidgets('Home follows the device until it stops moving', (tester) async {
    final api = _Corrector()..reads.addAll([_still, _moving, _moving, _still]);
    final c = ProviderContainer(overrides: [
      polarCorrectorApiProvider.overrideWithValue(api),
      polarAlignLiveProvider.overrideWith(_Live.new),
    ]);
    addTearDown(c.dispose);
    final sub = c.listen(polarCorrectorProvider, (_, _) {});
    addTearDown(sub.close);
    await c.read(polarCorrectorProvider.future);
    await c.read(polarCorrectorProvider.notifier).home();
    expect(c.read(polarCorrectorProvider).value!.moving, isTrue);
    await tester.pump(const Duration(milliseconds: 1500));
    await tester.pump(const Duration(milliseconds: 1500));
    expect(c.read(polarCorrectorProvider).value!.moving, isFalse);
    expect(api.calls, ['get', 'home', 'get', 'get', 'get']);
    await tester.pump(const Duration(seconds: 10));
    expect(api.calls.length, 5, reason: 'no read once it is still');
  });
}
