import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/saved_server_state.dart';

/// A device's driver and SDK versions, and its firmware and serial number
/// when the rig's Alpaca server reports them (`GET /api/v1/equipment/driver/{type}`).
class DeviceDriver {
  const DeviceDriver({
    this.driverVersion,
    this.sdkVersion,
    this.firmware,
    this.serialNumber,
  });

  final String? driverVersion;
  final String? sdkVersion;
  final String? firmware;
  final String? serialNumber;

  factory DeviceDriver.fromJson(Map<String, dynamic> json) => DeviceDriver(
    driverVersion: json['driver_version'] as String?,
    sdkVersion: json['sdk_version'] as String?,
    firmware: json['firmware'] as String?,
    serialNumber: json['serial_number'] as String?,
  );

  /// "SDK 1.41.0.0 · Driver 6.0.0 · Firmware 3.47 · S/N 0c19…", what's known.
  String get line => [
    if (sdkVersion != null) 'SDK $sdkVersion',
    if (driverVersion != null) 'Driver $driverVersion',
    if (firmware != null) 'Firmware $firmware',
    if (serialNumber != null) 'S/N $serialNumber',
  ].join(' · ');
}

/// The versions of the selected device of [type] ("camera", "telescope",
/// "focuser"…); null with no rig, nothing selected or an older daemon.
final deviceDriverProvider = FutureProvider.autoDispose
    .family<DeviceDriver?, String>((ref, type) async {
      final server = ref.watch(activeServerProvider);
      if (server == null) return null;
      final dio = Dio(
        BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 10),
        ),
      );
      ref.onDispose(() => dio.close(force: true));
      try {
        final res = await dio.get<Map<String, dynamic>>(
          '/api/v1/equipment/driver/$type',
        );
        final data = res.data;
        return data == null ? null : DeviceDriver.fromJson(data);
      } on DioException {
        return null; // 404 (nothing selected), an older daemon, no link
      }
    });
