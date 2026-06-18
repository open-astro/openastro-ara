import 'profile_meta.dart';

/// Mirrors `ProfileListDto` from the daemon's §37 multi-profile API
/// (`GET /api/v1/profiles`): the known profiles plus which one is active.
class ProfileList {
  /// The currently-active profile id, or null if none exists yet.
  final String? activeId;
  final List<ProfileMeta> profiles;

  const ProfileList({required this.activeId, required this.profiles});

  /// The active profile's metadata, or null if [activeId] doesn't resolve.
  ProfileMeta? get active {
    for (final p in profiles) {
      if (p.id == activeId) return p;
    }
    return null;
  }

  factory ProfileList.fromJson(Map<String, dynamic> json) {
    // Daemon serializes with SnakeCaseLower (see Program.cs): active_id, profiles.
    final rawProfiles = json['profiles'];
    final list = <ProfileMeta>[];
    if (rawProfiles is List) {
      for (final item in rawProfiles) {
        if (item is Map<String, dynamic>) {
          list.add(ProfileMeta.fromJson(item));
        } else if (item is Map) {
          list.add(ProfileMeta.fromJson(Map<String, dynamic>.from(item)));
        }
      }
    }
    return ProfileList(
      activeId: json['active_id']?.toString(),
      profiles: list,
    );
  }
}
