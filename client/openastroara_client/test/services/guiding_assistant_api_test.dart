import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guiding_assistant_api.dart';

/// Answers every request with an idle status and records the receive
/// timeout each one went out with.
Dio _recording(List<(String, Duration?)> seen) {
  final dio = Dio(
    GuidingAssistantApi.baseOptions(
      const AraServer(hostname: 'localhost', port: 1),
    ),
  );
  dio.interceptors.add(
    InterceptorsWrapper(
      onRequest: (options, handler) {
        seen.add((options.path, options.receiveTimeout));
        handler.resolve(
          Response(
            requestOptions: options,
            statusCode: 200,
            data: {'connected': true, 'state': 'idle'},
          ),
        );
      },
    ),
  );
  return dio;
}

void main() {
  test(
    'start waits 30 s for the guider; the other calls keep the 10 s default',
    () async {
      // Review of #1389: a Start that passed the 10 s receive timeout showed
      // an error while the run had in fact begun.
      final seen = <(String, Duration?)>[];
      final api = GuidingAssistantApi(
        const AraServer(hostname: 'localhost', port: 1),
        dio: _recording(seen),
      );
      addTearDown(api.close);
      await api.getStatus();
      await api.start(durationSec: 120, measureBacklash: true);
      await api.stop();
      expect(seen, [
        (
          '/api/v1/equipment/guider/live/assistant',
          const Duration(seconds: 10),
        ),
        (
          '/api/v1/equipment/guider/live/assistant/start',
          GuidingAssistantApi.startTimeout,
        ),
        (
          '/api/v1/equipment/guider/live/assistant/stop',
          const Duration(seconds: 10),
        ),
      ]);
      expect(GuidingAssistantApi.startTimeout, const Duration(seconds: 30));
    },
  );
}
