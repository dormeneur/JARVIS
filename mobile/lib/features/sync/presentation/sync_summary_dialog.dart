import 'package:flutter/material.dart';
import 'package:jarvis_mobile/shared/models/sync_result.dart';

/// Dialog shown after a sync operation completes.
class SyncSummaryDialog extends StatelessWidget {
  final SyncResult result;

  const SyncSummaryDialog({super.key, required this.result});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return AlertDialog(
      title: Row(
        children: [
          Icon(
            result.hasError
                ? Icons.error_outline
                : result.hasConflicts
                ? Icons.warning_amber
                : Icons.check_circle_outline,
            color: result.hasError
                ? theme.colorScheme.error
                : result.hasConflicts
                ? Colors.orange
                : Colors.green,
          ),
          const SizedBox(width: 8),
          const Text('Sync Complete'),
        ],
      ),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _SummaryRow(
            icon: Icons.cloud_upload,
            label: 'Pushed',
            count: result.pushed,
            color: theme.colorScheme.primary,
          ),
          const SizedBox(height: 8),
          _SummaryRow(
            icon: Icons.cloud_download,
            label: 'Pulled',
            count: result.pulled,
            color: theme.colorScheme.primary,
          ),
          if (result.hasConflicts) ...[
            const SizedBox(height: 8),
            _SummaryRow(
              icon: Icons.warning_amber,
              label: 'Conflicts',
              count: result.conflicts,
              color: Colors.orange,
            ),
            const SizedBox(height: 12),
            Text(
              'Conflict files are saved with a _conflict_ suffix.\n'
              'They appear normally in the file explorer.',
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
          ],
          if (result.totalChanges == 0 && !result.hasConflicts)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(
                'Everything is in sync.',
                style: theme.textTheme.bodyMedium,
              ),
            ),
        ],
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('OK'),
        ),
      ],
    );
  }
}

class _SummaryRow extends StatelessWidget {
  final IconData icon;
  final String label;
  final int count;
  final Color color;

  const _SummaryRow({
    required this.icon,
    required this.label,
    required this.count,
    required this.color,
  });

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Icon(icon, size: 20, color: color),
        const SizedBox(width: 8),
        Text('$label: '),
        Text(
          '$count',
          style: TextStyle(fontWeight: FontWeight.bold, color: color),
        ),
      ],
    );
  }
}
