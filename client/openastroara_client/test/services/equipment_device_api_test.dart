import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/equipment_device_api.dart';

void main() {
  DioException refusal(int status, Object? data) {
    final request = RequestOptions(path: '/slew');
    return DioException.badResponse(
      statusCode: status,
      requestOptions: request,
      response: Response(requestOptions: request, statusCode: status, data: data),
    );
  }

  test('a 409 titled run_in_progress is the run refusal, nothing else is', () {
    expect(
      isRunInProgressRefusal(refusal(409, {'title': 'run_in_progress', 'detail': 'a run'})),
      isTrue,
    );
    expect(
      isRunInProgressRefusal(refusal(409, '{"title":"run_in_progress"}')),
      isTrue,
      reason: 'an undecoded problem+json body',
    );
    expect(isRunInProgressRefusal(refusal(409, {'title': 'not_connected'})), isFalse);
    expect(isRunInProgressRefusal(refusal(422, {'title': 'run_in_progress'})), isFalse);
    expect(isRunInProgressRefusal(StateError('x')), isFalse);
    expect(isRunInProgressRefusal(null), isFalse);
  });
}
