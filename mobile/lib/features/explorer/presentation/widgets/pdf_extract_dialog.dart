import 'package:flutter/material.dart';

class PdfExtractDialog extends StatefulWidget {
  final String fileName;

  const PdfExtractDialog({super.key, required this.fileName});

  @override
  State<PdfExtractDialog> createState() => _PdfExtractDialogState();
}

class _PdfExtractDialogState extends State<PdfExtractDialog> {
  final _startPageController = TextEditingController(text: '1');
  final _endPageController = TextEditingController();

  @override
  void dispose() {
    _startPageController.dispose();
    _endPageController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('Extract Pages to Markdown'),
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('Extract text from ${widget.fileName}.'),
            const SizedBox(height: 16),
            Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _startPageController,
                    keyboardType: TextInputType.number,
                    decoration: const InputDecoration(
                      labelText: 'Start Page',
                      border: OutlineInputBorder(),
                    ),
                  ),
                ),
                const SizedBox(width: 16),
                Expanded(
                  child: TextField(
                    controller: _endPageController,
                    keyboardType: TextInputType.number,
                    decoration: const InputDecoration(
                      labelText: 'End Page (optional)',
                      border: OutlineInputBorder(),
                    ),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 12),
            Text(
              'Leave end page blank to extract to the end.',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: Theme.of(context).colorScheme.onSurfaceVariant,
                  ),
            )
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: () {
            final start = int.tryParse(_startPageController.text.trim()) ?? 1;
            int? end;
            if (_endPageController.text.trim().isNotEmpty) {
              end = int.tryParse(_endPageController.text.trim());
            }
            if (end != null && start > end) {
              ScaffoldMessenger.of(context).showSnackBar(
                const SnackBar(content: Text('Start page must be <= End page')),
              );
              return;
            }
            Navigator.of(context).pop({'start': start, 'end': end});
          },
          child: const Text('Extract'),
        ),
      ],
    );
  }
}
