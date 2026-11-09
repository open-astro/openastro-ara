import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/frame_histogram.dart';

void main() {
  test('parses clipped stars (#1723)', () {
    expect(FrameHistogram.fromJson(const {'clipped_stars': 3}).clippedStars, 3);
  });

  test('a response without clipped stars parses as null, not 0 (#1723)', () {
    expect(FrameHistogram.fromJson(const {}).clippedStars, isNull);
  });
}
