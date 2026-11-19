import 'discovered_device.dart';
import '../util/host_port.dart';

/// #1580 — AlpacaBridge's device catalog and add-device vocabulary, as the
/// daemon relays it (`/api/v1/equipment/alpacabridge`). Pure data, no I/O.

/// The value kinds a catalog field carries. An unknown kind is kept as
/// [unknown] (a newer bridge), never dropped.
enum BridgeFieldType {
  boolean,
  integer,
  number,
  string,
  stringList,
  recordList,
  unknown;

  static BridgeFieldType parse(Object? raw) => switch (raw) {
    'bool' => boolean,
    'int' => integer,
    'double' => number,
    'string' => string,
    'stringList' => stringList,
    'recordList' => recordList,
    _ => unknown,
  };
}

/// One field of a driver's form. [fields] holds the columns of a record list.
class BridgeField {
  const BridgeField({
    required this.key,
    required this.type,
    required this.required,
    required this.secret,
    this.defaultValue,
    this.min,
    this.max,
    this.enumValues,
    this.appliesWhenKey,
    this.appliesWhenValue,
    this.fields = const [],
  });

  final String key;
  final BridgeFieldType type;
  final Object? defaultValue;
  final bool required;
  final bool secret;
  final num? min;
  final num? max;
  final List<String>? enumValues;

  /// The field shows only while the field named [appliesWhenKey] holds [appliesWhenValue].
  final String? appliesWhenKey;
  final Object? appliesWhenValue;
  final List<BridgeField> fields;

  factory BridgeField.fromJson(Map<String, dynamic> j) {
    final range = j['range'];
    final when = j['appliesWhen'];
    final en = j['enum'];
    final sub = j['fields'];
    return BridgeField(
      key: j['key'] as String? ?? '',
      type: BridgeFieldType.parse(j['type']),
      defaultValue: j['default'],
      required: j['required'] == true,
      secret: j['secret'] == true,
      min: range is Map ? range['min'] as num? : null,
      max: range is Map ? range['max'] as num? : null,
      enumValues: en is List ? [for (final v in en) '$v'] : null,
      appliesWhenKey: when is Map ? when['key'] as String? : null,
      appliesWhenValue: when is Map ? when['value'] : null,
      fields: sub is List
          ? [
              for (final f in sub.whereType<Map<String, dynamic>>())
                BridgeField.fromJson(f),
            ]
          : const [],
    );
  }
}

/// One driver the bridge can add: a vendor's device of one type.
class BridgeDriver {
  const BridgeDriver({
    required this.vendor,
    required this.deviceType,
    required this.displayName,
    required this.vendorLabel,
    required this.available,
    required this.fields,
  });

  final String vendor;
  final String deviceType;
  final String displayName;
  final String vendorLabel;
  final bool available;
  final List<BridgeField> fields;

  String get key => '$vendor-$deviceType';

  factory BridgeDriver.fromJson(Map<String, dynamic> j) => BridgeDriver(
    vendor: j['vendor'] as String? ?? '',
    deviceType: j['deviceType'] as String? ?? '',
    displayName: j['displayName'] as String? ?? '',
    vendorLabel: j['vendorLabel'] as String? ?? '',
    available: j['available'] != false,
    fields: [
      for (final f
          in (j['fields'] as List? ?? const [])
              .whereType<Map<String, dynamic>>())
        BridgeField.fromJson(f),
    ],
  );

  /// `{device_types: [{device_type, drivers: [...]}]}` flattened in order.
  static List<BridgeDriver> listFromCatalogJson(Map<String, dynamic> json) => [
    for (final t
        in (json['device_types'] as List? ?? const [])
            .whereType<Map<String, dynamic>>())
      for (final d
          in (t['drivers'] as List? ?? const [])
              .whereType<Map<String, dynamic>>())
        BridgeDriver.fromJson(d),
  ];
}

/// A camera the bridge's vendor SDK sees attached.
class AttachedCamera {
  const AttachedCamera({
    required this.index,
    required this.name,
    required this.serial,
  });
  final int index;
  final String name;
  final String serial;
}

/// What an add came to (ADR 0022 rule 5: decoded in the API layer, never from a status code in a widget).
sealed class AddOutcome {
  const AddOutcome();
}

/// The bridge registered it; [warning] is its non-fatal note, if any.
class AddAdded extends AddOutcome {
  const AddAdded(this.message, [this.warning]);
  final String message;
  final String? warning;
}

/// The bridge (or the daemon) said no; [detail] is the sentence to show, empty when none came.
class AddRefused extends AddOutcome {
  const AddRefused(this.detail);
  final String detail;
}

/// No answer: the device may or may not have landed.
class AddUnconfirmed extends AddOutcome {
  const AddUnconfirmed();
}

/// Where the daemon dials the bridge.
class BridgeAddress {
  const BridgeAddress(this.host, this.port);
  final String host;
  final int port;

  @override
  bool operator ==(Object other) =>
      other is BridgeAddress && other.host == host && other.port == port;
  @override
  int get hashCode => Object.hash(host, port);
  @override
  String toString() => '$host:$port';
}

const _defaultBridgePort = 6800;

/// The bridge the daemon dials: the wizard's address override (`host[:port]`), else the
/// first discovered device's, else `localhost:6800`. A bad override port throws
/// [FormatException]: never a guess.
BridgeAddress resolveBridgeAddress({
  String? override,
  List<DiscoveredDevice> discovered = const [],
}) {
  final raw = override?.trim() ?? '';
  if (raw.isNotEmpty) {
    final hp = parseHostPort(raw);
    final host = hp.host;
    if (host == null) throw FormatException('No host in "$raw"');
    final p = hp.port;
    // `host:port` with one colon must carry a good port; a bare host gets 6800.
    if (':'.allMatches(raw).length == 1 && (p == null || p < 1 || p > 65535)) {
      throw FormatException('Bad port in "$raw"');
    }
    return BridgeAddress(host, p ?? _defaultBridgePort);
  }
  if (discovered.isNotEmpty) {
    final d = discovered.first;
    return BridgeAddress(d.ipAddress, d.ipPort);
  }
  return const BridgeAddress('localhost', _defaultBridgePort);
}
