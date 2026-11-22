import 'package:flutter/widgets.dart' show Locale;
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/l10n.dart';
import 'package:openastroara/services/refusal.dart';
import 'package:openastroara/util/friendly_error.dart';

/// ADR 0022 rule 6 — each known Refusal cause is worded by the app, in
/// English and in pt-BR, from the arb files; the daemon's English `detail`
/// shows only for a cause the app does not know or an answer with no reason.
final _en = lookupAppLocalizations(const Locale('en'));
final _pt = lookupAppLocalizations(const Locale('pt', 'BR'));

const _detail = 'DAEMON DETAIL SENTENCE';

Refusal _known(RefusalCause cause) => Refusal(
  cause: cause,
  reason: cause.token,
  detail: _detail,
  parameters: cause == RefusalCause.opticsNotSet
      ? const {
          'missing': ['focal_length'],
        }
      : cause == RefusalCause.centerInProgress
      ? const {'job_id': '0b6c0e8e-4c1f-4d43-9b9a-2f7d1c4e5a10'}
      : cause == RefusalCause.belowHorizonLimit
      ? const {'altitude_deg': 14.2, 'limit_deg': 20}
      : cause == RefusalCause.aboveClearanceLimit
      ? const {'altitude_deg': 84.6, 'limit_deg': 80}
      : cause == RefusalCause.counterweightLimit
      ? const {
          'hour_angle_hours': -11.13,
          'counterweight_angle_deg': 77.0,
          'limit_deg': 60,
        }
      : const {},
);

void main() {
  for (final cause in RefusalCause.values) {
    test(
      '${cause.token} is worded by the app in English and in pt-BR, not with the daemon detail',
      () {
        final en = failureReason(_en, _known(cause));
        final pt = failureReason(_pt, _known(cause));
        expect(en, isNot(contains(_detail)));
        expect(pt, isNot(contains(_detail)));
        expect(
          en,
          isNot(failureReason(_en, Exception('x'))),
          reason: 'its own sentence, not the generic one',
        );
        expect(
          pt,
          isNot(failureReason(_pt, Exception('x'))),
          reason: 'its own sentence, not the generic one',
        );
        expect(pt, isNot(en), reason: 'the pt-BR sentence is its own');
        expect(
          friendlyError(_en, _known(cause), action: 'check the position'),
          en,
        );
      },
    );
  }

  test('each cause has a sentence of its own', () {
    final en = {
      for (final c in RefusalCause.values) failureReason(_en, _known(c)),
    };
    final pt = {
      for (final c in RefusalCause.values) failureReason(_pt, _known(c)),
    };
    expect(en, hasLength(RefusalCause.values.length));
    expect(pt, hasLength(RefusalCause.values.length));
  });

  test('the optics sentence names the missing value', () {
    String words(List<String> missing) => failureReason(
      _en,
      Refusal(
        cause: RefusalCause.opticsNotSet,
        reason: 'optics-not-set',
        detail: _detail,
        parameters: {'missing': missing},
      ),
    );
    expect(words(['focal_length']), isNot(words(['pixel_size'])));
  });

  test('the below-horizon sentence names the altitude and the limit', () {
    for (final l in [_en, _pt]) {
      final words = failureReason(l, _known(RefusalCause.belowHorizonLimit));
      expect(words, contains('14'));
      expect(words, contains('20'));
    }
  });

  test('the clearance sentence names the altitude and the limit, in both languages (#1589)', () {
    for (final l in [_en, _pt]) {
      final words = failureReason(l, _known(RefusalCause.aboveClearanceLimit));
      expect(words, contains('84.6'));
      expect(words, contains('80.0'));
    }
  });

  test("the polar-align clearance sentence names the run's reach, in both languages (#2165)", () {
    const refusal = Refusal(
      cause: RefusalCause.polarAlignClearanceNotConfirmed,
      reason: 'polar-align-clearance-not-confirmed',
      detail: _detail,
      parameters: {'reach_deg': 20.5},
    );
    expect(failureReason(_en, refusal), contains('up to 20.5° each way'));
    expect(failureReason(_pt, refusal), contains('até 20,5° para cada lado'));
  });

  test('an unverified mount position read too early says to try again (#2070)', () {
    for (final l in [_en, _pt]) {
      const early = Refusal(
        cause: RefusalCause.mountPositionUnverified,
        reason: 'mount-position-unverified',
        detail: 'The daemon detail.',
        parameters: {'position_unverified_reason': 'not_read_yet'},
      );
      expect(failureReason(l, early), l.refusalMountPositionNotReadYet);
      expect(failureReason(l, _known(RefusalCause.mountPositionUnverified)),
          l.refusalMountPositionUnverified);
    }
  });

  test('a position left unverified by an unconfirmed stop says so, in both languages (#2066)', () {
    for (final l in [_en, _pt]) {
      const stop = Refusal(
        cause: RefusalCause.mountPositionUnverified,
        reason: 'mount-position-unverified',
        detail: 'The daemon detail.',
        parameters: {'position_unverified_reason': 'stop_unconfirmed'},
      );
      expect(failureReason(l, stop), l.refusalMountPositionStopUnconfirmed);
    }
    expect(_pt.refusalMountPositionStopUnconfirmed, isNot(_en.refusalMountPositionStopUnconfirmed));
  });
  test(
    'the counterweight sentence names the hour angle, the angle and the limit',
    () {
      for (final l in [_en, _pt]) {
        final words = failureReason(l, _known(RefusalCause.counterweightLimit));
        expect(words, contains('-11.1'));
        expect(words, contains('77'));
        expect(words, contains('60'));
      }
    },
  );
  test('the sweep-limit sentence says how far the mount turned, in whole degrees (#2089)', () {
    const r = Refusal(
      cause: RefusalCause.polarAlignSweepLimit,
      reason: 'polar-align-sweep-limit',
      detail: _detail,
      parameters: {'offset_deg': -60.3, 'limit_deg': 30},
    );
    expect(
      failureReason(_en, r),
      'The mount has turned 60° from where Polar Align started. Slew it back near the pole, counterweights down, and start again.',
    );
    expect(failureReason(_pt, r), contains('60°'));
  });

  test('a cause the app does not know shows the daemon detail', () {
    const r = Refusal(
      cause: null,
      reason: 'dome-closed',
      detail: 'The dome is closed.',
    );
    expect(
      friendlyError(_en, r, action: 'check the position'),
      'The dome is closed.',
    );
    expect(failureReason(_pt, r), 'The dome is closed.');
  });

  test('an answer with no reason shows the daemon detail', () {
    const r = Refusal(
      cause: null,
      reason: null,
      detail: 'Plate-solver executable not found.',
    );
    expect(failureReason(_en, r), 'Plate-solver executable not found.');
  });
}
