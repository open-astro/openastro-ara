import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/state/polar_align/polar_aligner_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/polar_align_settings_state.dart';

import '../../support/fake_section_rig.dart';

WsEvent _event(String type, [Map<String, dynamic> payload = const {}]) =>
    WsEvent(type: type, ts: DateTime.utc(2026, 11, 16, 9), seq: 1, payload: payload);

class _Aligner implements PolarAlignerClient {
  /// What each read returns, in turn; the last one repeats.
  final reads = <PolarAligner?>[];
  final calls = <String>[];
  int _i = 0;
  @override
  Future<PolarAligner?> get() async {
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
  void run(String state) => this.state = PolarAlignLive(phase: PolarAlignStates.adjusting, correction: PolarAlignerRun(state: state));
}

const _still = PolarAligner(present: true, drivable: true);
const _moving = PolarAligner(present: true, drivable: true, moving: true);

void main() {
  test('PolarAligner parses the daemon DTO; an unreported position stays null, not 0', () {
    final d = PolarAligner.fromJson({
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
    final none = PolarAligner.fromJson({'present': false});
    expect((none.present, none.drivable, none.azimuthPositionArcmin), (false, false, null));
    final t2 = PolarAligner.fromJson(
        {'present': true, 'carrier': 'mount', 'drivable': false, 'not_drivable_reason': 'device_runs_its_own_loop'});
    expect((t2.carrier, t2.notDrivableReason), ('mount', 'device_runs_its_own_loop'));
  });

  test('PolarAlignerRun parses every state, with the optional fields absent', () {
    for (final s in [
      PolarAlignerStates.manual,
      PolarAlignerStates.probing,
      PolarAlignerStates.moving,
      PolarAlignerStates.settling,
      PolarAlignerStates.measuring,
      PolarAlignerStates.converged,
      PolarAlignerStates.stopped,
    ]) {
      final r = PolarAlignerRun.tryParse({'state': s, 'moves': 3})!;
      expect((r.state, r.moves, r.axis, r.arcmin, r.seconds, r.cause, r.deviceText), (s, 3, null, null, null, null, null));
    }
    final full = PolarAlignerRun.tryParse({
      'state': 'stopped', 'moves': 2, 'axis': 'altitude', 'arcmin': -1.5, 'seconds': 3,
      'cause': 'device_fault', 'device_text': 'Altitude: end of travel',
    })!;
    expect((full.axis, full.arcmin, full.seconds, full.cause, full.deviceText),
        ('altitude', -1.5, 3.0, 'device_fault', 'Altitude: end of travel'));
    expect(PolarAlignerRun.tryParse(null), isNull);
    expect(PolarAlignerRun.tryParse({'moves': 1}), isNull);
  });

  test('motorsBusy: a move, its settle and the measurement after it', () {
    bool busy(String s) => PolarAlignerRun(state: s).motorsBusy;
    expect([for (final s in ['probing', 'moving', 'settling', 'measuring']) busy(s)], everyElement(isTrue));
    expect([for (final s in ['manual', 'converged', 'stopped']) busy(s)], everyElement(isFalse));
  });

  test('the status carries correction, absent when the daemon sends none', () {
    final with_ = PolarAlignStatus.fromJson({'state': 'adjusting', 'correction': {'state': 'moving', 'axis': 'azimuth', 'arcmin': 6.1, 'moves': 2}});
    expect((with_.correction!.state, with_.correction!.arcmin), ('moving', 6.1));
    expect(PolarAlignStatus.fromJson({'state': 'adjusting'}).correction, isNull);
    expect(liveFromStatus(with_).correction!.axis, 'azimuth', reason: 'a panel opening hydrates the run');
  });

  test('polar_align.correction folds into the live view; stopped and error clear it', () {
    const live = PolarAlignLive(phase: PolarAlignStates.adjusting);
    final moving = foldPolarAlignEvent(live, _event(PolarAlignWsEvents.correction, {'state': 'probing', 'axis': 'azimuth', 'arcmin': 2.0, 'moves': 1}))!;
    expect((moving.correction!.state, moving.correction!.moves, moving.phase), ('probing', 1, PolarAlignStates.adjusting));
    expect(foldPolarAlignEvent(moving, _event(PolarAlignWsEvents.stopped))!.correction, isNull,
        reason: 'the run ends without an aligner event');
    expect(foldPolarAlignEvent(moving, _event(PolarAlignWsEvents.error, {'reason': 'x'}))!.correction, isNull);
    expect(foldPolarAlignEvent(moving, _event(PolarAlignWsEvents.started))!.correction, isNull);
    expect(foldPolarAlignEvent(moving, _event(PolarAlignWsEvents.correction, {'moves': 1})), isNull,
        reason: 'a payload with no state writes nothing');
  });

  test('a profile round trip keeps polar_aligner_auto', () async {
    final rig = FakeSectionRig({
      'polar-align': {...rigDefaults['polar-align']!, 'polar_aligner_auto': false},
    });
    final c = ProviderContainer(overrides: [profileApiProvider.overrideWithValue(rig.api())]);
    addTearDown(c.dispose);
    c.listen(polarAlignSettingsProvider, (_, _) {});
    await settle(() => c.read(polarAlignSettingsProvider).canSave);
    expect(c.read(polarAlignSettingsProvider).value!.polarAlignerAuto, isFalse);
    await c.read(polarAlignSettingsProvider.notifier).saveKnob((s) => s.copyWith(exposureSeconds: 4));
    final put = rig.puts.single.body as Map;
    expect(put['polar_aligner_auto'], isFalse, reason: 'a save of another field does not wipe it');
    expect(put['exposure_seconds'], 4);
  });

  test('the app knows every stop cause the daemon declares', () {
    final src = File('../../OpenAstroAra.Server/Services/PolarAlignService.Aligner.cs').readAsStringSync();
    final block = RegExp(r'class PolarAlignCorrectionCauses \{([^}]*)\}').firstMatch(src)!.group(1)!;
    final daemon = RegExp(r'= "([a-z_]+)";').allMatches(block).map((m) => m.group(1)).toSet();
    expect(PolarAlignerCauses.all.toSet(), daemon);
  });

  testWidgets('the device is read again when the run measures, converges or stops, not on every move', (tester) async {
    final api = _Aligner()..reads.add(_still);
    final live = _Live();
    final c = ProviderContainer(overrides: [
      polarAlignerApiProvider.overrideWithValue(api),
      polarAlignLiveProvider.overrideWith(() => live),
    ]);
    addTearDown(c.dispose);
    final sub = c.listen(polarAlignerProvider, (_, _) {});
    addTearDown(sub.close);
    await c.read(polarAlignerProvider.future);
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
    final api = _Aligner()..reads.addAll([_still, _moving, _moving, _still]);
    final c = ProviderContainer(overrides: [
      polarAlignerApiProvider.overrideWithValue(api),
      polarAlignLiveProvider.overrideWith(_Live.new),
    ]);
    addTearDown(c.dispose);
    final sub = c.listen(polarAlignerProvider, (_, _) {});
    addTearDown(sub.close);
    await c.read(polarAlignerProvider.future);
    await c.read(polarAlignerProvider.notifier).home();
    expect(c.read(polarAlignerProvider).value!.moving, isTrue);
    await tester.pump(const Duration(milliseconds: 1500));
    await tester.pump(const Duration(milliseconds: 1500));
    expect(c.read(polarAlignerProvider).value!.moving, isFalse);
    expect(api.calls, ['get', 'home', 'get', 'get', 'get']);
    await tester.pump(const Duration(seconds: 10));
    expect(api.calls.length, 5, reason: 'no read once it is still');
  });
}
