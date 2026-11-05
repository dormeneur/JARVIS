import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:intl/intl.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:jarvis_mobile/features/chat/presentation/chat_screen.dart';
import 'package:jarvis_mobile/features/explorer/presentation/explorer_screen.dart';
import 'package:jarvis_mobile/features/inbox/data/inbox_repository.dart';
import 'package:jarvis_mobile/features/inbox/presentation/inbox_screen.dart';
import 'package:jarvis_mobile/features/jobs/presentation/jobs_screen.dart';
import 'package:jarvis_mobile/features/settings/presentation/settings_screen.dart';

/// The JARVIS look, shared with the desktop notch: black, with the reply blue pooling in a
/// corner, and frosted-glass tiles. (Colours are the notch's: accent 0x3380FF, text #ECECEF / #9093A0.)
class JarvisLook {
  static const background = Color(0xFF07090E);
  static const accent = Color(0xFF3380FF);
  static const accentLight = Color(0xFF40BFFF);
  static const textPrimary = Color(0xFFECECEF);
  static const textSecondary = Color(0xFF9093A0);

  static const glass = LinearGradient(
    begin: Alignment.topLeft,
    end: Alignment.bottomRight,
    colors: [Color(0xC8101629), Color(0xB40A0D18)],
  );

  static Border get glassEdge => Border.all(color: const Color(0x38FFFFFF));
}

/// Recent conversations, newest first, for the home's "Lately" list.
final recentChatsProvider = FutureProvider.autoDispose<List<ChatSession>>((ref) async {
  final sessions = await ref.watch(appDatabaseProvider).getAllChatSessions();
  return sessions.take(4).toList();
});

/// Home: ask JARVIS by voice or text, the main places as tiles, and recent conversations.
/// The phone's version of the notch's home on the PC.
class HomeScreen extends ConsumerWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final auth = ref.watch(authProvider);
    final recent = ref.watch(recentChatsProvider);
    final waiting = ref.watch(inboxCountProvider).valueOrNull ?? 0;
    final hour = DateTime.now().hour;
    final greeting = hour < 12 ? 'Good morning' : hour < 18 ? 'Good afternoon' : 'Good evening';

    void open(Widget screen) => Navigator.of(context)
        .push(MaterialPageRoute(builder: (_) => screen))
        .then((_) {
      ref.invalidate(recentChatsProvider);
      ref.invalidate(inboxCountProvider);
    });

    final tiles = <_Tile>[
      _Tile(Icons.chat_bubble_outline, 'Chat', 'Ask about your notes', () => open(const ChatScreen())),
      _Tile(Icons.badge_outlined, 'Interview', 'Teach JARVIS about you', () => open(const ChatScreen(interviewOnOpen: true))),
      _Tile(Icons.folder_open_outlined, 'Files', 'Your vault', () => open(const ExplorerScreen())),
      _Tile(Icons.inbox_outlined, 'Inbox', waiting > 0 ? '$waiting waiting for you' : 'Things JARVIS wants to save', () => open(const InboxScreen())),
      if (auth.isSecretsAuthorized) _Tile(Icons.work_outline, 'Jobs', 'Job search runs', () => open(const JobsScreen())),
      _Tile(Icons.settings_outlined, 'Settings', 'Devices, voice, backups', () => open(const SettingsScreen())),
    ];

    return Scaffold(
      backgroundColor: JarvisLook.background,
      body: Stack(
        children: [
          // The reply glow, deep in the bottom-right corner so text stays on black.
          const Positioned(
            right: -120,
            bottom: -120,
            child: IgnorePointer(
              child: SizedBox(
                width: 360,
                height: 360,
                child: DecoratedBox(
                  decoration: BoxDecoration(
                    gradient: RadialGradient(colors: [Color(0x803380FF), Color(0x3340BFFF), Color(0x00000000)]),
                  ),
                ),
              ),
            ),
          ),
          SafeArea(
            child: RefreshIndicator(
              onRefresh: () async => ref.invalidate(recentChatsProvider),
              child: ListView(
                padding: const EdgeInsets.fromLTRB(20, 24, 20, 32),
                children: [
                  Text(greeting, style: const TextStyle(color: JarvisLook.textPrimary, fontSize: 26, fontWeight: FontWeight.w600)),
                  const SizedBox(height: 4),
                  Text(
                    auth.deviceName == null ? 'What can I do?' : 'On ${auth.deviceName}',
                    style: const TextStyle(color: JarvisLook.textSecondary, fontSize: 14),
                  ),
                  const SizedBox(height: 28),
                  _AskButton(onTap: () => open(const ChatScreen(listenOnOpen: true))),
                  const SizedBox(height: 12),
                  _TypeBar(onTap: () => open(const ChatScreen())),
                  const SizedBox(height: 28),
                  GridView.count(
                    crossAxisCount: 2,
                    shrinkWrap: true,
                    physics: const NeverScrollableScrollPhysics(),
                    mainAxisSpacing: 12,
                    crossAxisSpacing: 12,
                    childAspectRatio: 1.6,
                    children: [for (final tile in tiles) _GlassTile(tile: tile)],
                  ),
                  const SizedBox(height: 28),
                  recent.when(
                    data: (sessions) => sessions.isEmpty
                        ? const SizedBox.shrink()
                        : Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              const Text('Lately', style: TextStyle(color: JarvisLook.textSecondary, fontSize: 13, fontWeight: FontWeight.w600)),
                              const SizedBox(height: 8),
                              for (final session in sessions)
                                _RecentRow(session: session, onTap: () => open(ChatScreen(openSessionId: session.id))),
                            ],
                          ),
                    loading: () => const SizedBox.shrink(),
                    error: (_, _) => const SizedBox.shrink(),
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _Tile {
  const _Tile(this.icon, this.title, this.detail, this.onTap);

  final IconData icon;
  final String title;
  final String detail;
  final VoidCallback onTap;
}

/// The big round mic: listening starts as the chat opens (recognised on the phone).
class _AskButton extends StatelessWidget {
  const _AskButton({required this.onTap});

  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Semantics(
        button: true,
        label: 'Ask JARVIS by voice',
        child: GestureDetector(
          onTap: onTap,
          child: Column(
            children: [
              Container(
                width: 112,
                height: 112,
                decoration: const BoxDecoration(
                  shape: BoxShape.circle,
                  gradient: RadialGradient(colors: [JarvisLook.accentLight, JarvisLook.accent]),
                  boxShadow: [BoxShadow(color: Color(0x663380FF), blurRadius: 36, spreadRadius: 4)],
                ),
                child: const Icon(Icons.mic, color: Colors.white, size: 44),
              ),
              const SizedBox(height: 12),
              const Text('Ask JARVIS', style: TextStyle(color: JarvisLook.textPrimary, fontSize: 15, fontWeight: FontWeight.w600)),
            ],
          ),
        ),
      ),
    );
  }
}

class _TypeBar extends StatelessWidget {
  const _TypeBar({required this.onTap});

  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return Material(
      color: Colors.transparent,
      child: InkWell(
        borderRadius: BorderRadius.circular(24),
        onTap: onTap,
        child: Ink(
          padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 14),
          decoration: BoxDecoration(gradient: JarvisLook.glass, border: JarvisLook.glassEdge, borderRadius: BorderRadius.circular(24)),
          child: const Row(
            children: [
              Icon(Icons.keyboard_outlined, color: JarvisLook.textSecondary, size: 20),
              SizedBox(width: 12),
              Text('Or type a question…', style: TextStyle(color: JarvisLook.textSecondary, fontSize: 14)),
            ],
          ),
        ),
      ),
    );
  }
}

class _GlassTile extends StatelessWidget {
  const _GlassTile({required this.tile});

  final _Tile tile;

  @override
  Widget build(BuildContext context) {
    return Material(
      color: Colors.transparent,
      child: InkWell(
        borderRadius: BorderRadius.circular(16),
        splashColor: JarvisLook.accent.withValues(alpha: 0.25),
        onTap: tile.onTap,
        child: Ink(
          padding: const EdgeInsets.all(14),
          decoration: BoxDecoration(gradient: JarvisLook.glass, border: JarvisLook.glassEdge, borderRadius: BorderRadius.circular(16)),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              Icon(tile.icon, color: JarvisLook.textPrimary, size: 22),
              Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(tile.title, style: const TextStyle(color: JarvisLook.textPrimary, fontSize: 15, fontWeight: FontWeight.w600)),
                  Text(tile.detail, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(color: JarvisLook.textSecondary, fontSize: 12)),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _RecentRow extends StatelessWidget {
  const _RecentRow({required this.session, required this.onTap});

  final ChatSession session;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final when = DateTime.tryParse(session.lastActiveAt)?.toLocal();
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          borderRadius: BorderRadius.circular(12),
          onTap: onTap,
          child: Ink(
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
            decoration: BoxDecoration(gradient: JarvisLook.glass, border: JarvisLook.glassEdge, borderRadius: BorderRadius.circular(12)),
            child: Row(
              children: [
                const Icon(Icons.forum_outlined, color: JarvisLook.textSecondary, size: 18),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(session.title, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(color: JarvisLook.textPrimary, fontSize: 14)),
                ),
                if (when != null)
                  Text(DateFormat('d MMM').format(when), style: const TextStyle(color: JarvisLook.textSecondary, fontSize: 12)),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
