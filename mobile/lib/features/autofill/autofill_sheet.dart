import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/autofill/autofill_repository.dart';
import 'package:jarvis_mobile/features/home/presentation/home_screen.dart';

/// The sheet Android shows over another app's form (android: AutofillSheetActivity). Two jobs:
///  - fill: what memory knows goes in at once; the rest is asked of the vault's brain on the PC,
///    each field ticking off as its answer arrives, then everything is handed back to Android.
///  - save: what the user sent is learned into Profile/form-memory.json, for the phone and the PC.
///  - text: "Autofill with Jarvis" from the text-selection menu: the selected words are the
///    question; what memory and the vault have for it are offered, and the one picked replaces them.
class AutofillSheetApp extends StatelessWidget {
  const AutofillSheetApp({super.key});

  @override
  Widget build(BuildContext context) => MaterialApp(
    debugShowCheckedModeBanner: false,
    theme: ThemeData(
      brightness: Brightness.dark,
      colorScheme: ColorScheme.fromSeed(seedColor: JarvisLook.accent, brightness: Brightness.dark),
      useMaterial3: true,
    ),
    home: const AutofillSheet(),
  );
}

class AutofillSheet extends ConsumerStatefulWidget {
  const AutofillSheet({super.key});

  static const channel = MethodChannel('jarvis/autofill-sheet');

  @override
  ConsumerState<AutofillSheet> createState() => _AutofillSheetState();
}

enum _Row { known, asking, answered, unknown }

class _AutofillSheetState extends ConsumerState<AutofillSheet> {
  String _mode = 'fill';
  String _title = '';
  List<String> _labels = const [];
  List<String?> _values = const [];
  List<_Row> _rows = const [];
  String _status = 'Reading the form…';
  bool _working = true;
  bool _closing = false;

  /// Text mode: the answers on offer, and whether the selection can be replaced (else it's copied).
  final List<String> _choices = [];
  bool _readOnly = false;

  @override
  void initState() {
    super.initState();
    _start();
  }

  Future<void> _start() async {
    final request = await AutofillSheet.channel.invokeMapMethod<String, dynamic>('request') ?? const {};
    _mode = request['mode'] as String? ?? 'fill';
    _title = request['title'] as String? ?? '';
    _labels = List<String>.from(request['labels'] as List? ?? const []);
    if (_mode == 'text') {
      _choices.addAll(List<String>.from(request['choices'] as List? ?? const []));
      _readOnly = request['readOnly'] as bool? ?? false;
      return _lookUp();
    }
    if (_mode == 'save') {
      _values = List<String?>.from(request['values'] as List? ?? const []);
      setState(() => _rows = List.filled(_labels.length, _Row.known));
      return _save();
    }
    final known = List<String>.from(request['known'] as List? ?? const []);
    _values = [for (final value in known) value.isEmpty ? null : value];
    setState(() => _rows = [for (final value in _values) value == null ? _Row.asking : _Row.known]);
    await _ask();
  }

  /// Fields memory doesn't know, asked of the vault's brain on the PC.
  Future<void> _ask() async {
    final unknown = {
      for (var i = 0; i < _labels.length; i++)
        if (_values[i] == null) 'f$i': _labels[i],
    };
    final knownCount = _labels.length - unknown.length;
    if (unknown.isEmpty) return _fill();
    setState(() => _status = knownCount > 0 ? '$knownCount from forms you filled before · asking your vault about ${unknown.length}…' : 'Asking your vault about ${unknown.length} fields…');
    try {
      await for (final answer in ref.read(autofillRepositoryProvider).answers(_title, unknown)) {
        final index = int.parse(answer.key.substring(1));
        if (!mounted || _closing) return;
        setState(() {
          _values[index] = answer.value;
          _rows[index] = answer.value == null ? _Row.unknown : _Row.answered;
        });
      }
      await _fill();
    } catch (e) {
      if (!mounted || _closing) return;
      setState(() {
        _working = false;
        _rows = [for (final row in _rows) row == _Row.asking ? _Row.unknown : row];
        final ready = _values.where((value) => value != null).length;
        _status = ready > 0
            ? 'Jarvis on your PC is unreachable (${AutofillRepository.reason(e)}). Fill the $ready your phone remembers?'
            : "Couldn't reach Jarvis on your PC (${AutofillRepository.reason(e)}), and your phone doesn't remember these yet.";
      });
    }
  }

  /// Text mode: memory's answers at once, then the vault's.
  Future<void> _lookUp() async {
    final question = _labels.isEmpty ? '' : _labels.first;
    setState(() => _status = 'Asking your vault about "$question"…');
    try {
      await for (final answer in ref.read(autofillRepositoryProvider).answers(_title, {'f0': question})) {
        final value = answer.value;
        if (!mounted || _closing) return;
        if (value != null && !_choices.any((choice) => choice.toLowerCase() == value.toLowerCase())) {
          setState(() => _choices.add(value));
        }
      }
      if (!mounted) return;
      setState(
        () => _status = _choices.isEmpty
            ? 'Nothing in your vault for "$question" yet.'
            : _readOnly
            ? 'Tap one to copy it.'
            : 'Tap one to put it in.',
      );
    } catch (e) {
      if (!mounted) return;
      setState(() => _status = _choices.isEmpty ? "Couldn't reach Jarvis on your PC (${AutofillRepository.reason(e)})." : "Couldn't reach your PC; these are from forms you filled before.");
    }
    if (mounted) setState(() => _working = false);
  }

  Future<void> _pick(String choice) async {
    if (_readOnly) {
      await Clipboard.setData(ClipboardData(text: choice));
      return _close(const []);
    }
    return _close([choice]);
  }

  /// Hands what's ready to Android; the form fills as the sheet closes.
  Future<void> _fill() async {
    final ready = _values.where((value) => value != null).length;
    setState(() {
      _working = false;
      _status = ready == 0 ? 'Nothing in your vault fits this form yet.' : 'Filling in $ready of ${_labels.length}…';
    });
    if (ready > 0) {
      await Future<void>.delayed(const Duration(milliseconds: 700));
      await _close(_values);
    }
  }

  Future<void> _save() async {
    setState(() => _status = 'Remembering ${_labels.length} answers${_title.isEmpty ? '' : ' from $_title'}…');
    final repository = ref.read(autofillRepositoryProvider);
    try {
      // On the phone first: from here the answers are safe, whatever the PC is doing.
      await repository.queueLearned(_title, _labels, _values);
    } catch (e) {
      if (!mounted) return;
      return setState(() {
        _working = false;
        _status = "Couldn't save this on your phone ($e).";
      });
    }
    var synced = false;
    try {
      await repository.flushPending();
      synced = true;
    } catch (_) {} // stays queued; the app retries whenever the PC is reachable
    if (!mounted) return;
    setState(() {
      _working = false;
      _status = synced
          ? 'Remembered. Your phone and PC will offer these next time.'
          : 'Remembered on your phone. It will reach your PC when it is back online.';
    });
    await Future<void>.delayed(Duration(milliseconds: synced ? 1200 : 2200));
    await _close(const []);
  }

  Future<void> _close(List<String?> values) async {
    if (_closing) return;
    _closing = true;
    await AutofillSheet.channel.invokeMethod<void>('finish', {'values': values});
  }

  @override
  Widget build(BuildContext context) {
    final ready = _values.where((value) => value != null).length;
    final filling = _mode == 'fill';
    return PopScope(
      canPop: false,
      onPopInvokedWithResult: (didPop, _) => _close(const []),
      child: Scaffold(
        backgroundColor: Colors.black54,
        body: SafeArea(
          child: Align(
            alignment: Alignment.bottomCenter,
            child: Container(
              margin: const EdgeInsets.all(12),
              padding: const EdgeInsets.fromLTRB(18, 16, 18, 12),
              constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(context).height * 0.7),
              decoration: BoxDecoration(gradient: JarvisLook.glass, border: JarvisLook.glassEdge, borderRadius: BorderRadius.circular(20), color: JarvisLook.background),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      const Icon(Icons.near_me, color: JarvisLook.accent, size: 20),
                      const SizedBox(width: 10),
                      Expanded(
                        child: Text(
                          _mode == 'text'
                              ? 'Jarvis'
                              : filling
                              ? 'Jarvis is filling this form'
                              : 'Jarvis is learning this form',
                          style: const TextStyle(color: JarvisLook.textPrimary, fontSize: 16, fontWeight: FontWeight.w600),
                        ),
                      ),
                      if (_working) const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2, color: JarvisLook.accentLight)),
                    ],
                  ),
                  if (_title.isNotEmpty)
                    Padding(
                      padding: const EdgeInsets.only(left: 30, top: 2),
                      child: Text(
                        _title,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(color: JarvisLook.textSecondary, fontSize: 12),
                      ),
                    ),
                  const SizedBox(height: 12),
                  Text(_status, style: const TextStyle(color: JarvisLook.textPrimary, fontSize: 13)),
                  const SizedBox(height: 8),
                  Flexible(
                    child: _mode == 'text'
                        ? ListView(
                            shrinkWrap: true,
                            children: [
                              for (final choice in _choices)
                                Material(
                                  type: MaterialType.transparency,
                                  child: ListTile(
                                    dense: true,
                                    contentPadding: EdgeInsets.zero,
                                    leading: Icon(_readOnly ? Icons.copy : Icons.input, size: 18, color: JarvisLook.accentLight),
                                    title: Text(choice, style: const TextStyle(color: JarvisLook.textPrimary, fontSize: 15)),
                                    onTap: () => _pick(choice),
                                  ),
                                ),
                            ],
                          )
                        : ListView.builder(
                            shrinkWrap: true,
                            itemCount: _labels.length,
                            itemBuilder: (context, i) => _FieldRow(label: _labels[i], value: _values.length > i ? _values[i] : null, row: _rows.length > i ? _rows[i] : _Row.asking),
                          ),
                  ),
                  const SizedBox(height: 8),
                  Row(
                    mainAxisAlignment: MainAxisAlignment.end,
                    children: [
                      TextButton(onPressed: () => _close(const []), child: Text(_working || !filling ? 'Cancel' : 'Close')),
                      if (filling && ready > 0 && !_closing) ...[
                        const SizedBox(width: 8),
                        FilledButton(onPressed: () => _close(_values), child: Text(_working ? 'Fill the $ready ready' : 'Fill $ready')),
                      ],
                    ],
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _FieldRow extends StatelessWidget {
  const _FieldRow({required this.label, required this.value, required this.row});

  final String label;
  final String? value;
  final _Row row;

  @override
  Widget build(BuildContext context) {
    final (IconData icon, Color color, String note) = switch (row) {
      _Row.known => (Icons.history, JarvisLook.accentLight, 'remembered'),
      _Row.answered => (Icons.check_circle_outline, JarvisLook.accentLight, 'from your vault'),
      _Row.asking => (Icons.more_horiz, JarvisLook.textSecondary, 'asking…'),
      _Row.unknown => (Icons.remove_circle_outline, JarvisLook.textSecondary, 'not in your vault'),
    };
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 5),
      child: Row(
        children: [
          Icon(icon, size: 16, color: color),
          const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  label,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(color: JarvisLook.textSecondary, fontSize: 12),
                ),
                Text(
                  value ?? note,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(color: value == null ? JarvisLook.textSecondary : JarvisLook.textPrimary, fontSize: 14),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
