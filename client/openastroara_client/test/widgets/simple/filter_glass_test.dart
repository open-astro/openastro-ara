import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/simple/filter_wheel.dart';

void main() {
  test('a full filter name is tinted as its letter (Green as G)', () {
    expect(filterGlass('Red').glass, AraColors.glassRed);
    expect(filterGlass('Green').glass, AraColors.glassGreen);
    expect(filterGlass('Blue').glass, AraColors.glassBlue);
    expect(filterGlass('H-alpha').glass, AraColors.glassHa);
    expect(filterGlass('S2').glass, AraColors.glassSii);
  });

  test(
    'L, Lum, Luminance and Clear are spectrum glass; other names are not',
    () {
      for (final n in ['L', 'lum', 'Luminance', 'Clear']) {
        expect(filterSpectrum(n), isTrue, reason: n);
      }
      for (final n in ['R', 'Ha', 'UV/IR cut', 'L-eXtreme', '']) {
        expect(filterSpectrum(n), isFalse, reason: n);
      }
    },
  );

  test('a name Ara does not know stays plain glass', () {
    expect(filterGlass('L-eXtreme').glass, AraColors.glassPlain);
  });
}
