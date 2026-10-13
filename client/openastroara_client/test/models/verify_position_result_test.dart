import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/verify_position_result.dart';

void main() {
  test('§57.4 parses the daemon result and prints the three positions with their offsets', () {
    final r = VerifyPositionResult.fromJson({
      'success': true,
      'solved_ra_hours': 9.93,
      'solved_dec_degrees': 69.07,
      'mount_ra_hours': 9.93,
      'mount_dec_degrees': 69.57,
      'intended_ra_hours': 9.93,
      'intended_dec_degrees': 69.07,
      'solved_to_mount_arcmin': 30.0,
      'solved_to_intended_arcmin': 0.4,
      'exposure_seconds': 2.0,
      'note': null,
    });
    expect(r.success, isTrue);
    expect(r.lines, [
      'Image solved: RA 9.93h, Dec +69.07°',
      'Mount reports: RA 9.93h, Dec +69.57° (30.0′ off)',
      'Slew was aimed at: RA 9.93h, Dec +69.07° (24″ off)',
    ]);
  });

  test('§57.4 a failed solve keeps the mount figures and the daemon note; degrees past 2°', () {
    final r = VerifyPositionResult.fromJson({
      'success': false,
      'mount_ra_hours': 1.0,
      'mount_dec_degrees': -5.5,
      'exposure_seconds': 2.0,
      'note': 'The solve failed (clouds…). The mount\'s own figures are shown.',
    });
    expect(r.lines, [
      'Image did not solve.',
      'Mount reports: RA 1.00h, Dec -5.50°',
      'The solve failed (clouds…). The mount\'s own figures are shown.',
    ]);
    final far = VerifyPositionResult.fromJson({
      'success': true,
      'solved_ra_hours': 1,
      'solved_dec_degrees': 0,
      'mount_ra_hours': 1,
      'mount_dec_degrees': 3,
      'solved_to_mount_arcmin': 180.0,
      'exposure_seconds': 2,
    });
    expect(far.lines[1], 'Mount reports: RA 1.00h, Dec +3.00° (3.0° off)');
    expect(
      VerifyPositionResult.fromJson(const {}).lines.first,
      'Image did not solve.',
    );
  });
}
