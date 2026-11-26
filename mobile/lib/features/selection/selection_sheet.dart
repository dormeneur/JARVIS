import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/home/presentation/home_screen.dart';
import 'package:jarvis_mobile/features/selection/selection_repository.dart';

/// The sheet Android opens over any app (android: JarvisSelectionActivity) for selected or shared
/// text: "Explain with Jarvis" / "Save to Jarvis" in the text-selection toolbar, or Jarvis in the
/// share sheet. Explaining, summarising and questions are answered by the local model on the PC
/// (never a cloud model); saving shows where the vault's brain would file it, and writes only on Save.
class SelectionSheetApp extends StatelessWidget {
  const SelectionSheetApp({super.key});

  @override
  Widget build(BuildContext context) => MaterialApp(
        debugShowCheckedModeBanner: false,
        theme: ThemeData(
          brightness: Brightness.dark,
          colorScheme: ColorScheme.fromSeed(seedColor: JarvisLook.accent, brightness: Brightness.dark),
          useMaterial3: true,
        ),
        home: const SelectionSheet(),
      );
}

class SelectionSheet extends ConsumerStatefulWidget {
  const SelectionSheet({super.key});

  static const channel = MethodChannel('jarvis/selection');

  @override
  ConsumerState<SelectionSheet> createState() => _SelectionSheetState();
}

enum _Step { choose, answering, asking, planning, plan, saving, saved, failed }

class _SelectionSheetState extends ConsumerState<SelectionSheet> {
  String _text = '';
  String _source = '';
  String? _url;
  _Step _step = _Step.choose;
  String _title = '';
  final StringBuffer _answer = StringBuffer();
  String _problem = '';
  IngestPlan? _plan;
  StreamSubscription<String>? _stream;
  final _question = TextEditingController();

  @override
  void initState() {
    super.initState();
    _start();
  }

  @override
  void dispose() {
    _stream?.cancel();
    _question.dispose();
    super.dispose();
  }

  Future<void> _start() async {
    final request = Map<String, Object?>.from(await SelectionSheet.channel.invokeMethod<Map>('request') ?? const {});
    setState(() {
      _text = (request['text'] as String? ?? '').trim();
      _source = request['source'] as String? ?? '';
      _url = request['url'] as String?;
    });
    switch (request['action']) {
      case 'explain':
        _explain('explain');
      case 'save':
        _makePlan();
    }
  }

  void _explain(String mode, {String question = ''}) {
    _stream?.cancel();
    _answer.clear();
    setState(() {
      _step = _Step.answering;
      _title = switch (mode) { 'summarise' => 'Summary', 'ask' => question, _ => 'What it means' };
    });
    _stream = ref.read(selectionRepositoryProvider).explain(_text, mode, question: question).listen(
          (token) => setState(() => _answer.write(token)),
          onError: (Object error) => setState(() {
            _step = _Step.failed;
            _problem = error is SelectionException ? error.message : 'Your PC couldn\'t be reached. Is Jarvis running there?';
          }),
        );
  }

  Future<void> _makePlan() async {
    setState(() => _step = _Step.planning);
    try {
      final plan = await ref.read(selectionRepositoryProvider).plan(_text, _source, _url);
      if (mounted) setState(() { _plan = plan; _step = _Step.plan; });
    } on SelectionException catch (e) {
      if (mounted) setState(() { _problem = e.message; _step = _Step.failed; });
    }
  }

  Future<void> _save() async {
    final plan = _plan;
    if (plan == null) return;
    setState(() => _step = _Step.saving);
    try {
      await ref.read(selectionRepositoryProvider).save(plan);
      if (!mounted) return;
      setState(() => _step = _Step.saved);
      await Future<void>.delayed(const Duration(milliseconds: 1400));
      _close();
    } catch (_) {
      if (mounted) {
        setState(() {
          _problem = 'Couldn\'t save it. Nothing was changed.';
          _step = _Step.failed;
        });
      }
    }
  }

  void _close() => SystemNavigator.pop();

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.black54,
      body: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: _close, // a tap outside the card closes it
        child: SafeArea(
          child: Align(
            alignment: Alignment.bottomCenter,
            child: GestureDetector(
              onTap: () {}, // taps on the card stay on the card
              child: Container(
                margin: const EdgeInsets.all(12),
                padding: const EdgeInsets.fromLTRB(18, 14, 10, 16),
                constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(context).height * 0.75),
                decoration: BoxDecoration(gradient: JarvisLook.glass, border: JarvisLook.glassEdge, borderRadius: BorderRadius.circular(22)),
                child: SingleChildScrollView(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      _header(),
                      const SizedBox(height: 8),
                      _quote(),
                      const SizedBox(height: 14),
                      ..._body(),
                    ],
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _header() => Row(
        children: [
          Expanded(
            child: Text(
              _source.isEmpty ? '✦  JARVIS' : '✦  JARVIS  ·  ${_source.toUpperCase()}',
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: const TextStyle(color: Color(0xFF7FB2FF), fontSize: 11, fontWeight: FontWeight.w600, letterSpacing: 1.2),
            ),
          ),
          IconButton(icon: const Icon(Icons.close, size: 18, color: JarvisLook.textSecondary), onPressed: _close, tooltip: 'Close'),
        ],
      );

  Widget _quote() => Container(
        padding: const EdgeInsets.only(left: 10, right: 8),
        decoration: const BoxDecoration(border: Border(left: BorderSide(color: JarvisLook.accent, width: 2))),
        child: Text(
          _text.isEmpty ? 'Nothing was selected.' : _text,
          maxLines: 3,
          overflow: TextOverflow.ellipsis,
          style: const TextStyle(color: JarvisLook.textSecondary, fontSize: 13, fontStyle: FontStyle.italic, height: 1.35),
        ),
      );

  List<Widget> _body() {
    switch (_step) {
      case _Step.choose:
        return [
          Wrap(spacing: 8, runSpacing: 8, children: [
            _chip(Icons.lightbulb_outline, 'Explain', () => _explain('explain')),
            _chip(Icons.short_text, 'Summarise', () => _explain('summarise')),
            _chip(Icons.bookmark_add_outlined, 'Save to Jarvis', _makePlan),
            _chip(Icons.help_outline, 'Ask about it', () => setState(() => _step = _Step.asking)),
          ]),
          const SizedBox(height: 10),
          const Text('Answered by the vault\'s brain on your PC. Nothing goes to a cloud model.',
              style: TextStyle(color: JarvisLook.textSecondary, fontSize: 11)),
        ];
      case _Step.asking:
        return [
          TextField(
            controller: _question,
            autofocus: true,
            textInputAction: TextInputAction.send,
            onSubmitted: (q) => _explain('ask', question: q.trim().isEmpty ? 'What does it mean?' : q.trim()),
            decoration: const InputDecoration(hintText: 'What do you want to know about it?', border: OutlineInputBorder()),
          ),
        ];
      case _Step.answering:
        return [
          Text(_title, style: const TextStyle(color: JarvisLook.textPrimary, fontSize: 15, fontWeight: FontWeight.w600)),
          const SizedBox(height: 8),
          if (_answer.isEmpty) const LinearProgressIndicator(minHeight: 2) else SelectableText(_answer.toString(), style: const TextStyle(color: JarvisLook.textPrimary, fontSize: 15, height: 1.45)),
          const SizedBox(height: 12),
          Wrap(spacing: 8, children: [
            _chip(Icons.copy, 'Copy', () => Clipboard.setData(ClipboardData(text: _answer.toString()))),
            _chip(Icons.bookmark_add_outlined, 'Save the text', _makePlan),
            _chip(Icons.arrow_back, 'Other actions', () => setState(() => _step = _Step.choose)),
          ]),
        ];
      case _Step.planning:
        return const [
          Text('Working out where this belongs…', style: TextStyle(color: JarvisLook.textPrimary, fontSize: 15)),
          SizedBox(height: 10),
          LinearProgressIndicator(minHeight: 2),
        ];
      case _Step.plan:
        final plan = _plan!;
        return [
          const Text('SAVE TO YOUR VAULT', style: TextStyle(color: Color(0xFF7FB2FF), fontSize: 10.5, fontWeight: FontWeight.w600, letterSpacing: 1.2)),
          const SizedBox(height: 6),
          Text(plan.notePath, style: const TextStyle(color: JarvisLook.textPrimary, fontSize: 15, fontWeight: FontWeight.w600)),
          const SizedBox(height: 4),
          const Text('Kept word for word, with where it came from, so answers can quote it.', style: TextStyle(color: JarvisLook.textSecondary, fontSize: 12)),
          if (plan.facts.isNotEmpty) ...[
            const SizedBox(height: 12),
            const Text('AND ABOUT YOU', style: TextStyle(color: Color(0xFF7FB2FF), fontSize: 10.5, fontWeight: FontWeight.w600, letterSpacing: 1.2)),
            for (final (path, fact) in plan.facts)
              Padding(
                padding: const EdgeInsets.only(top: 6),
                child: Text('• $fact  →  $path', style: const TextStyle(color: JarvisLook.textPrimary, fontSize: 13, height: 1.35)),
              ),
          ],
          const SizedBox(height: 14),
          Row(children: [
            FilledButton(onPressed: _save, child: const Text('Save')),
            const SizedBox(width: 8),
            TextButton(onPressed: _close, child: const Text('Cancel')),
          ]),
        ];
      case _Step.saving:
        return const [Text('Saving…', style: TextStyle(color: JarvisLook.textPrimary, fontSize: 15)), SizedBox(height: 10), LinearProgressIndicator(minHeight: 2)];
      case _Step.saved:
        return const [
          Row(children: [
            Icon(Icons.check_circle, color: Color(0xFF3FD07A), size: 20),
            SizedBox(width: 8),
            Text('In your vault', style: TextStyle(color: JarvisLook.textPrimary, fontSize: 15, fontWeight: FontWeight.w600)),
          ]),
        ];
      case _Step.failed:
        return [
          Text(_problem, style: const TextStyle(color: JarvisLook.textPrimary, fontSize: 14, height: 1.4)),
          const SizedBox(height: 10),
          _chip(Icons.arrow_back, 'Back', () => setState(() => _step = _Step.choose)),
        ];
    }
  }

  Widget _chip(IconData icon, String label, VoidCallback onTap) => ActionChip(
        avatar: Icon(icon, size: 16, color: JarvisLook.accentLight),
        label: Text(label),
        onPressed: onTap,
        side: const BorderSide(color: Color(0x38FFFFFF)),
        backgroundColor: const Color(0x22101629),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(18)),
      );
}
