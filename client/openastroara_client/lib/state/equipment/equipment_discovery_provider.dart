import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../services/equipment_discovery_api.dart';

/// Injectable factory for the daemon discovery API — tests swap a fake so the
/// §68.2 Next-gate (and the Switch card's discovery fallback) can be exercised
/// without a live daemon. Lived in the wizard's discovery screen until the
/// Switch state layer needed it too; that screen re-exports it.
final equipmentDiscoveryApiFactoryProvider =
    Provider<EquipmentDiscoveryApi Function(AraServer)>(
      (_) => EquipmentDiscoveryApi.new,
    );
