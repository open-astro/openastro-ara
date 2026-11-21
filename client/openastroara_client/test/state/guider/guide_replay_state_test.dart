import 'dart:async';

import 'package:fake_async/fake_async.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/state/guider/guide_replay_state.dart';
import 'package:openastroara/state/guider/guide_step_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/util/phd2_guide_log.dart';

const _log = '''
Guiding Begins at 2026-10-04 07:12:08
Pixel scale = 2.00 arc-sec/px, Binning = 1, Focal length = 120 mm
Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,XStep,YStep,StarMass,SNR,ErrorCode
1,1.0,"Mount",0,0,0.1,0.1,0,0,0,,0,,,,1,1,0
2,2.0,"Mount",0,0,0.2,0.2,0,0,0,,0,,,,1,1,0
INFO: DITHER by 1.0, 1.0, new lock pos = 1, 1
3,3.0,"Mount",0,0,0.3,0.3,0,0,0,,0,,,,1,1,0
Guiding Ends at 2026-10-04 07:12:20
''';

void main() {
  test('plays one frame per tick, markers at their logged place, then stops', () {
    final ws = StreamController<WsEvent>.broadcast();
    final c = ProviderContainer(overrides: [
      wsEventsProvider.overrideWith((ref) => ws.stream),
    ]);
    addTearDown(() => unawaited(ws.close()));
    addTearDown(c.dispose);
    final sub = c.listen(guideReplayProvider, (_, _) {});
    addTearDown(sub.close);
    // Pre-existing graph content is replaced by the replay.
    c.read(guideStepsProvider.notifier).add(GuideStep(at: DateTime(2026), raPx: 9));

    final n = c.read(guideReplayProvider.notifier);
    expect(n.start('x.txt', Phd2GuideLog.parse(_log), tick: const Duration(days: 1)), isTrue);
    expect(c.read(guideStepsProvider), isEmpty);
    expect(c.read(guideReplayProvider)!.total, 3);

    n.tickOnce();
    expect(c.read(guideStepsProvider).length, 1);
    // guiding_started (at session begin) lands before frame 1.
    expect(c.read(guideMarkersProvider).map((m) => m.kind), [GuideMarkerKind.guidingStarted]);
    n.tickOnce();
    expect(c.read(guideMarkersProvider).length, 1, reason: 'the dither is after frame 2');
    n.tickOnce();
    expect(c.read(guideMarkersProvider).map((m) => m.kind),
        [GuideMarkerKind.guidingStarted, GuideMarkerKind.dithered]);
    expect(c.read(guideReplayProvider)!.played, 3);
    expect(c.read(guideReplayProvider)!.playing, isTrue);
    n.tickOnce(); // past the end: trailing guiding_stopped, playback over
    expect(c.read(guideMarkersProvider).last.kind, GuideMarkerKind.guidingStopped);
    expect(c.read(guideReplayProvider)!.playing, isFalse);

    n.stop();
    expect(c.read(guideReplayProvider), isNull);
    expect(c.read(guideStepsProvider), isEmpty);
  });

  test('the timer drives playback; a live guider.step ends it and clears', () {
    fakeAsync((async) {
      final ws = StreamController<WsEvent>.broadcast();
      final c = ProviderContainer(overrides: [
        wsEventsProvider.overrideWith((ref) => ws.stream),
      ]);
      final sub = c.listen(guideReplayProvider, (_, _) {});
      final n = c.read(guideReplayProvider.notifier);
      n.start('x.txt', Phd2GuideLog.parse(_log), tick: const Duration(milliseconds: 100));
      async.elapse(const Duration(milliseconds: 250));
      expect(c.read(guideStepsProvider).length, 2);

      ws.add(WsEvent(type: 'guider.step', ts: DateTime(2026), seq: 1, payload: const {'ra_raw_px': 0.1}));
      async.flushMicrotasks();
      expect(c.read(guideReplayProvider), isNull);
      // The live step itself is kept; the replay frames are gone.
      expect(c.read(guideStepsProvider).length, 1);
      expect(c.read(guideStepsProvider).single.raPx, 0.1);
      async.elapse(const Duration(seconds: 1));
      expect(c.read(guideStepsProvider).length, 1, reason: 'timer cancelled');
      sub.close();
      c.dispose();
      unawaited(ws.close());
    });
  });

  test('an empty log does not start', () {
    final c = ProviderContainer(overrides: [
      wsEventsProvider.overrideWith((ref) => const Stream<WsEvent>.empty()),
    ]);
    addTearDown(c.dispose);
    expect(c.read(guideReplayProvider.notifier).start('e.txt', Phd2GuideLog.parse('')), isFalse);
    expect(c.read(guideReplayProvider), isNull);
  });
}
