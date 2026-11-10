import 'package:dio/dio.dart';
import 'package:flutter/widgets.dart' show Locale;
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/l10n.dart';
import 'package:openastroara/util/friendly_error.dart';

/// #1699 — one rule for every failed request, in both presentations: a 400,
/// 409 or 422 shows the first line of the daemon's own sentence; anything else
/// is a sentence in the app's language, never a status code.
final _en = lookupAppLocalizations(const Locale('en'));
final _pt = lookupAppLocalizations(const Locale('pt', 'BR'));

DioException _status(int code, [Object? data]) => DioException(
  requestOptions: RequestOptions(path: '/x'),
  type: DioExceptionType.badResponse,
  response: Response(requestOptions: RequestOptions(path: '/x'), statusCode: code, data: data),
);

void main() {
  const detail = {'detail': "The mount is parked.\n(Parameter 'x')\nActual value was 3."};
  final table = <(String, Object, String)>[
    ('400 detail, first line', _status(400, detail), 'The mount is parked.'),
    ('409 detail, first line', _status(409, detail), 'The mount is parked.'),
    ('422 detail, first line', _status(422, detail), 'The mount is parked.'),
    ('409 detail in a raw problem+json body', _status(409, '{"detail":"Busy slewing."}'), 'Busy slewing.'),
    ('500 detail is not for people', _status(500, {'detail': 'NullReferenceException at Foo'}),
        'Your rig hit an error. Support → Logs has the details.'),
    ('404 detail is not for people', _status(404, {'detail': 'No route', 'title': 'not_found'}),
        "Your rig doesn't have that. It may be running an older version of Ara."),
    ('409 with no detail', _status(409, {'title': 'run_in_progress'}), 'Something else is using it right now.'),
    ('a bare 503', _status(503), 'Your rig hit an error. Support → Logs has the details.'),
  ];
  for (final (name, error, words) in table) {
    test('failureReason: $name', () => expect(failureReason(_en, error), words));
  }

  test('friendlyError follows the same rule: no daemon text on a 500', () {
    expect(friendlyError(_en, _status(500, {'detail': 'NullReferenceException at Foo'}), action: 'park'),
        "Couldn't park — your rig hit an error. Support → Logs has the details.");
    expect(friendlyError(_en, _status(422, detail), action: 'park'), 'The mount is parked.');
  });

  test('the sentence is in the app language, never a status code', () {
    final words = failureReason(_pt, _status(500));
    expect(words, isNot(contains('500')));
    expect(words, _pt.errorServer[0].toUpperCase() + _pt.errorServer.substring(1));
  });

  test('our own written-for-people messages pass through', () {
    expect(failureReason(_en, StateError('Connect to your rig first.')), 'Connect to your rig first.');
  });
}
