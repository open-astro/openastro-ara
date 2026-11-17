import 'alpacabridge_setup.dart';

/// #1580 — the add-device form AlpacaBridge's catalog describes for one [driver]. Pure: the
/// widget holds only text controllers, the rules live here.
class BridgeForm {
  const BridgeForm(this.driver);
  final BridgeDriver driver;

  /// Error codes [errors] returns; the widget words them.
  static const required = 'required';
  static const outOfRange = 'range';
  static const notAllowed = 'enum';

  /// The defaults. A record list starts empty; an unknown type holds its default as text.
  Map<String, Object?> initialValues() => {
    for (final f in driver.fields)
      if (f.type == BridgeFieldType.recordList)
        f.key: <Object?>[]
      else if (f.defaultValue != null)
        f.key: f.type == BridgeFieldType.unknown
            ? '${f.defaultValue}'
            : f.defaultValue,
  };

  /// The fields that show for [values]: one with `appliesWhen` shows only while its switch holds that value.
  List<BridgeField> visibleFields(Map<String, Object?> values) =>
      visibleOf(driver.fields, values);

  static List<BridgeField> visibleOf(
    List<BridgeField> fields,
    Map<String, Object?> values,
  ) => [
    for (final f in fields)
      if (f.appliesWhenKey == null ||
          '${values[f.appliesWhenKey]}' == '${f.appliesWhenValue}')
        f,
  ];

  /// `key -> code` for every visible field that is wrong; nested rows are `ports[1].gpio`.
  Map<String, String> errors(Map<String, Object?> values) {
    final out = <String, String>{};
    _check(driver.fields, values, '', out);
    return out;
  }

  static void _check(
    List<BridgeField> fields,
    Map<String, Object?> values,
    String prefix,
    Map<String, String> out,
  ) {
    for (final f in visibleOf(fields, values)) {
      final v = values[f.key];
      final key = '$prefix${f.key}';
      if (f.type == BridgeFieldType.recordList) {
        final rows = v is List ? v : const [];
        for (var i = 0; i < rows.length; i++) {
          final row = rows[i];
          if (row is Map<String, Object?>) {
            _check(f.fields, row, '$key[$i].', out);
          }
        }
        continue;
      }
      if (v == null || (v is String && v.trim().isEmpty)) {
        if (f.required) out[key] = required;
        continue;
      }
      if (v is num &&
          ((f.min != null && v < f.min!) || (f.max != null && v > f.max!))) {
        out[key] = outOfRange;
      } else if (f.enumValues != null && !f.enumValues!.contains('$v')) {
        out[key] = notAllowed;
      }
    }
  }

  /// The POST body: vendor, deviceType and the visible fields' values (unknown types as text).
  Map<String, Object?> body(Map<String, Object?> values) => {
    'vendor': driver.vendor,
    'deviceType': driver.deviceType,
    ..._bodyOf(driver.fields, values),
  };

  static Map<String, Object?> _bodyOf(
    List<BridgeField> fields,
    Map<String, Object?> values,
  ) => {
    for (final f in visibleOf(fields, values))
      if (values[f.key] != null)
        f.key: switch (f.type) {
          BridgeFieldType.unknown => '${values[f.key]}',
          BridgeFieldType.recordList => [
            for (final row in values[f.key] as List)
              if (row is Map<String, Object?>) _bodyOf(f.fields, row),
          ],
          _ => values[f.key],
        },
  };
}
