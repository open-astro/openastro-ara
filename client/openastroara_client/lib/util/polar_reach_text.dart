import 'package:intl/intl.dart';

/// #2165 how far a Polar align run turns RA each way, for people: whole
/// degrees, or one decimal when the 0.5° direction test is counted
/// ("20", "20.5"; "20,5" in Portuguese). A missing value is "?".
String polarReachText(Object? deg, String localeName) =>
    deg is num ? NumberFormat('0.#', localeName).format(deg) : '?';
