import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/models/pa_residual.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/guider/pa_residual_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

class _StatusNotifier extends GuiderStatusNotifier {
  _StatusNotifier(this.initial);
  final GuiderStatus? initial;
  @override
  Future<GuiderStatus?> build() async => initial;
  void set(GuiderStatus? value) => state = AsyncData(value);
}

void main() {
  final t = DateTime.utc(2026, 10, 8, 21);

  test(
    'follows guider.pa_residual and is seeded from the guider status',
    () async {
      final ws = StreamController<WsEvent>.broadcast();
      final container = ProviderContainer(
        overrides: [
          wsEventsProvider.overrideWith((ref) => ws.stream),
          guiderStatusProvider.overrideWith(
            () => _StatusNotifier(
              const GuiderStatus(
                name: 'OpenAstro Guider',
                connectionState: GuiderConnectionState.connected,
                runtimeState: GuiderRuntimeState.guiding,
                paResidual: PaResidual(
                  id: 'seed',
                  status: PaResidualStatus.measuring,
                  sampleSeconds: 90,
                  targetSeconds: 300,
                ),
              ),
            ),
          ),
        ],
      );
      addTearDown(() => unawaited(ws.close()));
      addTearDown(container.dispose);
      final sub = container.listen(paResidualProvider, (_, _) {});
      addTearDown(sub.close);
      await container.read(guiderStatusProvider.future);
      await Future<void>.delayed(Duration.zero);
      expect(
        container.read(paResidualProvider)?.id,
        'seed',
        reason: 'a client connecting mid-run catches up',
      );

      ws.add(
        WsEvent(
          type: 'guider.pa_residual',
          ts: t,
          seq: 1,
          payload: const {
            'id': 'seed',
            'status': 'done',
            'sample_seconds': 300.0,
            'target_seconds': 300.0,
            'frames': 120,
            'pa_error_min_arcmin': 2.1,
            'uncertainty_arcmin': 0.3,
            'reliable': true,
          },
        ),
      );
      await Future<void>.delayed(Duration.zero);
      expect(container.read(paResidualProvider)?.paErrorMinArcmin, 2.1);

      ws.add(
        WsEvent(
          type: 'guider.pa_residual',
          ts: t,
          seq: 2,
          payload: const {'status': 'idle'},
        ),
      );
      await Future<void>.delayed(Duration.zero);
      expect(
        container.read(paResidualProvider),
        isNull,
        reason: 'a dropped measurement with nothing before it',
      );
    },
  );

  test('a poll older than the WS event does not step a run back', () {
    const measuring = PaResidual(
      id: 'r',
      status: PaResidualStatus.measuring,
      sampleSeconds: 90,
    );
    const later = PaResidual(
      id: 'r',
      status: PaResidualStatus.measuring,
      sampleSeconds: 120,
    );
    const done = PaResidual(
      id: 'r',
      status: PaResidualStatus.done,
      paErrorMinArcmin: 1,
    );
    const nextRun = PaResidual(id: 'r2', status: PaResidualStatus.measuring);
    expect(PaResidualNotifier.isStale(done, measuring), isTrue);
    expect(PaResidualNotifier.isStale(later, measuring), isTrue);
    expect(PaResidualNotifier.isStale(measuring, later), isFalse);
    expect(PaResidualNotifier.isStale(measuring, done), isFalse);
    expect(
      PaResidualNotifier.isStale(done, nextRun),
      isFalse,
      reason: 'a new run always shows',
    );
    expect(PaResidualNotifier.isStale(null, measuring), isFalse);
    expect(
      PaResidualNotifier.isStale(done, null),
      isFalse,
      reason: 'the daemon has nothing: clear',
    );
  });

  test('a stale poll after the WS done keeps the result', () async {
    final ws = StreamController<WsEvent>.broadcast();
    late _StatusNotifier status;
    final container = ProviderContainer(
      overrides: [
        wsEventsProvider.overrideWith((ref) => ws.stream),
        guiderStatusProvider.overrideWith(() => status = _StatusNotifier(null)),
      ],
    );
    addTearDown(() => unawaited(ws.close()));
    addTearDown(container.dispose);
    final sub = container.listen(paResidualProvider, (_, _) {});
    addTearDown(sub.close);
    await container.read(guiderStatusProvider.future);

    ws.add(
      WsEvent(
        type: 'guider.pa_residual',
        ts: t,
        seq: 1,
        payload: const {
          'id': 'r',
          'status': 'done',
          'sample_seconds': 300.0,
          'target_seconds': 300.0,
          'frames': 120,
          'pa_error_min_arcmin': 2.1,
          'reliable': true,
        },
      ),
    );
    await Future<void>.delayed(Duration.zero);
    status.set(
      const GuiderStatus(
        name: 'OpenAstro Guider',
        connectionState: GuiderConnectionState.connected,
        runtimeState: GuiderRuntimeState.guiding,
        paResidual: PaResidual(
          id: 'r',
          status: PaResidualStatus.measuring,
          sampleSeconds: 270,
          targetSeconds: 300,
        ),
      ),
    );
    await Future<void>.delayed(Duration.zero);
    expect(container.read(paResidualProvider)?.status, PaResidualStatus.done);
  });
}
