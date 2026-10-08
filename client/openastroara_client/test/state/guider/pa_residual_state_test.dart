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
}
