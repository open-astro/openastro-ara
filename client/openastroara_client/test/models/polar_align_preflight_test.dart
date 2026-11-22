import 'package:flutter_test/flutter_test.dart';

import '../support/polar_preflight.dart';

void main() {
  // #2165: the clearance is confirmed for a reach, and the list's reach is the
  // fine stage's; a Check turns by its own step, so the rig judges it.
  test("A failing clearance blocks Start but not a Check; a parked mount blocks both", () {
    final unconfirmed = rigPreflight(confirmed: false);
    expect(unconfirmed.blocksStart, isTrue);
    expect(unconfirmed.blocksCheck, isFalse);

    final parked = rigPreflight(confirmed: true, parked: true);
    expect(parked.blocksStart, isTrue);
    expect(parked.blocksCheck, isTrue);
  });
}
