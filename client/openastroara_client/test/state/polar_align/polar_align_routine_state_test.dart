import 'dart:async';
import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/polar_align/polar_align_routine_state.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';

/// Fake rig: frame fetches wait on [frames] in order; settings and status scripted.
class _Api implements PolarAlignClient {
  final frames = <Completer<Uint8List?>>[];
  int frameCalls = 0;
  PolarAlignStatus? status;
  PolarAlignSettings settings = const PolarAlignSettings();
  Object? putError;

  @override
  Future<Uint8List?> getLiveFrame() {
    frameCalls++;
    final c = Completer<Uint8List?>();
    frames.add(c);
    return c.future;
  }

  @override
  Future<PolarAlignStatus?> getStatus() async => status;
  @override
  Future<PolarAlignSettings> getSettings() async => settings;
  @override
  Future<PolarAlignSettings> putSettings(PolarAlignSettings s) async {
    if (putError != null) throw putError!;
    return settings = s;
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
  void close() {}
}

class _Live extends PolarAlignLiveNotifier {
  @override
  PolarAlignLive build() => const PolarAlignLive();
  void set(PolarAlignLive live) => state = live;
}

PolarAlignFrameInfo _frame(int n) => PolarAlignFrameInfo.fromJson({'exposure_seconds': n.toDouble()});

void main() {
  late _Api api;
  late ProviderContainer c;

  setUp(() {
    api = _Api();
    c = ProviderContainer(overrides: [
      polarAlignApiProvider.overrideWithValue(api),
      polarAlignLiveProvider.overrideWith(_Live.new),
    ]);
    addTearDown(c.dispose);
  });

  _Live live() => c.read(polarAlignLiveProvider.notifier) as _Live;

  test('a frame that finishes while one is fetched is fetched once after it, never more', () async {
    c.listen(polarAlignFrameProvider, (_, _) {});
    live().set(PolarAlignLive(phase: PolarAlignStates.adjusting, lastFrame: _frame(1)));
    live().set(PolarAlignLive(phase: PolarAlignStates.adjusting, lastFrame: _frame(2)));
    live().set(PolarAlignLive(phase: PolarAlignStates.adjusting, lastFrame: _frame(3)));
    expect(api.frameCalls, 1, reason: 'one fetch in flight');
    api.frames[0].complete(Uint8List.fromList([1]));
    await pumpEventQueue();
    expect(api.frameCalls, 2, reason: 'the newer frames, once');
    api.frames[1].complete(Uint8List.fromList([3]));
    await pumpEventQueue();
    expect(api.frameCalls, 2);
    expect(c.read(polarAlignFrameProvider), [3]);
  });

  test('the rough stage fetches no frame: its frames are the main camera\'s', () async {
    c.listen(polarAlignFrameProvider, (_, _) {});
    live().set(PolarAlignLive(
        phase: PolarAlignStates.adjusting, stage: PolarAlignStages.coarse, lastFrame: _frame(1)));
    await pumpEventQueue();
    expect(api.frameCalls, 0);
  });

  test('a failed fetch keeps the previous frame', () async {
    c.listen(polarAlignFrameProvider, (_, _) {});
    live().set(PolarAlignLive(phase: PolarAlignStates.adjusting, lastFrame: _frame(1)));
    api.frames[0].complete(Uint8List.fromList([7]));
    await pumpEventQueue();
    live().set(PolarAlignLive(phase: PolarAlignStates.adjusting, lastFrame: _frame(2)));
    api.frames[1].completeError(StateError('rig gone'));
    await pumpEventQueue();
    expect(c.read(polarAlignFrameProvider), [7]);
  });

  test('a failed settings save rolls back to what the rig uses', () async {
    await c.read(polarAlignSettingsProvider.future);
    api.putError = StateError('refused');
    await expectLater(
      c.read(polarAlignSettingsProvider.notifier).save(const PolarAlignSettings(exposureSeconds: 30)),
      throwsStateError,
    );
    expect(c.read(polarAlignSettingsProvider).value?.exposureSeconds, const PolarAlignSettings().exposureSeconds);
  });

  test('opening a panel seeds the live view from the rig', () async {
    api.status = PolarAlignStatus.fromJson({'state': PolarAlignStates.adjusting, 'current_error_arcmin': 4.0});
    await c.read(polarAlignLiveProvider.notifier).hydrate();
    expect(c.read(polarAlignLiveProvider).phase, PolarAlignStates.adjusting);
    expect(c.read(polarAlignLiveProvider).zone, 'green');
  });

  test('opening a panel on a running routine fetches its frame; on the rough stage it does not', () async {
    c.listen(polarAlignFrameProvider, (_, _) {});
    api.status = PolarAlignStatus.fromJson({'state': PolarAlignStates.adjusting, 'stage': PolarAlignStages.coarse});
    await c.read(polarAlignFrameProvider.notifier).hydrate();
    expect(api.frameCalls, 0);
    api.status = PolarAlignStatus.fromJson({'state': PolarAlignStates.adjusting, 'stage': PolarAlignStages.fine});
    final opening = c.read(polarAlignFrameProvider.notifier).hydrate();
    await pumpEventQueue();
    expect(api.frameCalls, 1);
    api.frames.last.complete(Uint8List.fromList([5]));
    await opening;
    expect(c.read(polarAlignFrameProvider), [5]);
  });

  test('a command is busy while in flight and words its failure', () async {
    final action = c.read(polarAlignActionProvider.notifier);
    c.listen(polarAlignActionProvider, (_, _) {});
    final gate = Completer<void>();
    final done = action.run((_) => gate.future, describe: (e) => 'Couldn\'t start: $e');
    expect(c.read(polarAlignActionProvider).busy, isTrue);
    gate.completeError('no camera');
    expect(await done, isFalse);
    expect(c.read(polarAlignActionProvider), (busy: false, error: 'Couldn\'t start: no camera'));
  });

  test('a Stop that ends first leaves the module busy while an earlier call is still in flight (#1770 review)', () async {
    final action = c.read(polarAlignActionProvider.notifier);
    c.listen(polarAlignActionProvider, (_, _) {});
    final save = Completer<void>();
    final saving = action.run((_) => save.future, describe: (_) => null);
    expect(await action.run((_) async {}, describe: (_) => null), isTrue, reason: 'the Stop');
    expect(c.read(polarAlignActionProvider).busy, isTrue, reason: 'the save still runs, so Start stays held');
    save.complete();
    await saving;
    expect(c.read(polarAlignActionProvider).busy, isFalse);
  });

  test('an exposure is more than 0 and at most 60 seconds', () {
    expect(polarExposureValid(null), isFalse);
    expect(polarExposureValid(0), isFalse);
    expect(polarExposureValid(0.5), isTrue);
    expect(polarExposureValid(60), isTrue);
    expect(polarExposureValid(60.1), isFalse);
  });
}
