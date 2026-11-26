import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/home/presentation/home_screen.dart';
import 'package:jarvis_mobile/features/hunches/hunch_repository.dart';

/// The home's hunch card, the phone's version of the notch's: "✦ I HAVE A HUNCH", the guess, three
/// replies, In my words, Why? (what it noticed) and Skip (onto the stack). Answering brings the next one.
/// Nothing shows when there's no hunch or the PC can't be reached.
class HunchCard extends ConsumerStatefulWidget {
  const HunchCard({super.key, this.onDemand = false});

  /// Opened from "Ask me a hunch": past the daily cap, and says so when there's none.
  final bool onDemand;

  @override
  ConsumerState<HunchCard> createState() => _HunchCardState();
}

class _HunchCardState extends ConsumerState<HunchCard> {
  Hunch? _hunch;
  bool _loading = true;
  bool _why = false;
  String? _noted;

  HunchRepository get _repo => ref.read(hunchRepositoryProvider);

  @override
  void initState() {
    super.initState();
    _next();
  }

  Future<void> _next({String? noted}) async {
    setState(() {
      _loading = true;
      _why = false;
    });
    try {
      final batch = await _repo.next(onDemand: widget.onDemand);
      if (mounted) {
        setState(() {
          _hunch = batch.hunch;
          _noted = noted;
        });
      }
    } on DioException {
      if (mounted) setState(() => _hunch = null);
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _answer({int? choice, String? text}) async {
    final hunch = _hunch;
    if (hunch == null) return;
    setState(() => _loading = true);
    try {
      final saved = await _repo.answer(hunch.id, choice: choice, text: text);
      await _next(noted: 'Noted in ${saved ?? 'your profile'}');
    } on DioException {
      if (mounted) {
        setState(() {
          _loading = false;
          _noted = 'Couldn\'t save that. Try again in a moment.';
        });
      }
    }
  }

  Future<void> _skip() async {
    final hunch = _hunch;
    if (hunch == null) return;
    await _repo.skip(hunch.id);
    await _next(noted: 'On your stack for next time');
  }

  Future<void> _ownWords() async {
    final text = TextEditingController();
    final said = await showDialog<String>(
      context: context,
      builder: (d) => AlertDialog(
        title: const Text('In your words'),
        content: TextField(controller: text, autofocus: true, maxLines: 3, decoration: const InputDecoration(hintText: 'What\'s actually true?')),
        actions: [
          TextButton(onPressed: () => Navigator.of(d).pop(), child: const Text('Cancel')),
          FilledButton(onPressed: () => Navigator.of(d).pop(text.text.trim()), child: const Text('Save')),
        ],
      ),
    );
    if (said != null && said.isNotEmpty) await _answer(text: said);
  }

  @override
  Widget build(BuildContext context) {
    final hunch = _hunch;
    if (hunch == null) {
      if (!widget.onDemand || _loading) return const SizedBox.shrink();
      return _shell(const [Text('No hunches yet. Jarvis is still noticing; ask again in a while.', style: TextStyle(color: JarvisLook.textSecondary))]);
    }
    return AnimatedOpacity(
      opacity: _loading ? 0.5 : 1,
      duration: const Duration(milliseconds: 200),
      child: _shell([
        Text(
          _noted == null ? '✦  I HAVE A HUNCH' : '✓  ${_noted!.toUpperCase()}  ·  NEXT HUNCH',
          style: const TextStyle(color: Color(0xFF7FB2FF), fontSize: 10.5, fontWeight: FontWeight.w600, letterSpacing: 1.2),
        ),
        const SizedBox(height: 8),
        Text(hunch.question, style: const TextStyle(color: JarvisLook.textPrimary, fontSize: 16, fontWeight: FontWeight.w600, height: 1.35)),
        if (_why && hunch.evidence.isNotEmpty) ...[
          const SizedBox(height: 8),
          Text('What I noticed: ${hunch.evidence.join(' · ')}', style: const TextStyle(color: JarvisLook.textSecondary, fontSize: 12, fontStyle: FontStyle.italic, height: 1.35)),
        ],
        const SizedBox(height: 12),
        Wrap(spacing: 8, runSpacing: 8, children: [
          for (var i = 0; i < hunch.options.length; i++)
            (i == 0 ? FilledButton.tonal : OutlinedButton.new)(onPressed: _loading ? null : () => _answer(choice: i), child: Text(hunch.options[i])),
        ]),
        const SizedBox(height: 4),
        Row(children: [
          TextButton(onPressed: _loading ? null : _ownWords, child: const Text('In my words')),
          if (hunch.evidence.isNotEmpty) TextButton(onPressed: () => setState(() => _why = !_why), child: Text(_why ? 'Hide why' : 'Why?')),
          const Spacer(),
          TextButton(onPressed: _loading ? null : _skip, child: const Text('Skip')),
        ]),
      ]),
    );
  }

  Widget _shell(List<Widget> children) => Container(
        margin: const EdgeInsets.only(bottom: 20),
        padding: const EdgeInsets.fromLTRB(16, 14, 12, 6),
        decoration: BoxDecoration(gradient: JarvisLook.glass, border: JarvisLook.glassEdge, borderRadius: BorderRadius.circular(18)),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: children),
      );
}
