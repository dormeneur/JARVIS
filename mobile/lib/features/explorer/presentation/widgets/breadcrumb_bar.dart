import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/explorer/presentation/explorer_provider.dart';

/// Horizontally scrollable breadcrumb navigation bar.
///
/// Shows the full directory path as clickable segments:
///   🏠 › Documents › Notes
///
/// Tapping any segment navigates directly to that directory.
class BreadcrumbBar extends ConsumerWidget {
  const BreadcrumbBar({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final currentDir = ref.watch(currentDirectoryProvider);
    final theme = Theme.of(context);
    final segments = currentDir.isEmpty ? <String>[] : currentDir.split('/');

    return Container(
      width: double.infinity,
      color: theme.colorScheme.surfaceContainerLow,
      padding: const EdgeInsets.symmetric(horizontal: 4),
      height: 40,
      child: SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        reverse: true, // Auto-scroll to end (deepest folder)
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            // Home (root) button
            _BreadcrumbSegment(
              label: 'Vault',
              icon: Icons.home_rounded,
              isLast: segments.isEmpty,
              onTap: () {
                ref.read(currentDirectoryProvider.notifier).state = '';
              },
            ),
            // Each folder segment
            for (int i = 0; i < segments.length; i++) ...[
              Icon(
                Icons.chevron_right,
                size: 18,
                color: theme.colorScheme.onSurfaceVariant.withValues(alpha: 0.5),
              ),
              _BreadcrumbSegment(
                label: segments[i],
                isLast: i == segments.length - 1,
                onTap: () {
                  final path = segments.sublist(0, i + 1).join('/');
                  ref.read(currentDirectoryProvider.notifier).state = path;
                },
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _BreadcrumbSegment extends StatelessWidget {
  final String label;
  final IconData? icon;
  final bool isLast;
  final VoidCallback onTap;

  const _BreadcrumbSegment({
    required this.label,
    this.icon,
    required this.isLast,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final color = isLast
        ? theme.colorScheme.primary
        : theme.colorScheme.onSurfaceVariant;

    return InkWell(
      onTap: isLast ? null : onTap,
      borderRadius: BorderRadius.circular(6),
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 8),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (icon != null) ...[
              Icon(icon, size: 16, color: color),
              const SizedBox(width: 4),
            ],
            Text(
              label.toUpperCase(),
              style: theme.textTheme.labelSmall?.copyWith(
                color: color,
                fontWeight: isLast ? FontWeight.w700 : FontWeight.w500,
                letterSpacing: 0.5,
              ),
            ),
          ],
        ),
      ),
    );
  }
}
