import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart';
import 'package:jarvis_mobile/features/about_me/domain/knowledge.dart';

/// "About me" as a living graph, the phone's version of the PC's: you in the middle, folders and
/// topics around you, facts around their topic. Pinch to zoom and drag to pan (InteractiveViewer);
/// what's drawn glides toward the simulation, an edit grows new nodes out of their parent instead of
/// reshuffling, and a tap (or a search hit) eases the camera in to centre that node. The ticker stops
/// once everything is still, so a settled graph costs no battery.
class KnowledgeGraphView extends StatefulWidget {
  const KnowledgeGraphView({super.key, required this.map, required this.query, required this.onPick});

  final KnowledgeMap map;
  final String query;
  final ValueChanged<GraphNode?> onPick;

  @override
  State<KnowledgeGraphView> createState() => _KnowledgeGraphViewState();
}

class _KnowledgeGraphViewState extends State<KnowledgeGraphView> with TickerProviderStateMixin {
  static const _world = 3000.0;
  static const _centre = _world / 2;
  static const _palette = [
    Color(0xFF5B9CFF), Color(0xFFB388FF), Color(0xFF3FD0B5), Color(0xFFFFB84D),
    Color(0xFFFF7AB6), Color(0xFF8BD85F), Color(0xFFFF8F5E), Color(0xFF5ED6F2),
  ];

  late ForceLayout _layout;
  List<Offset> _display = const [];
  late final Ticker _ticker;
  final _camera = TransformationController();
  late final AnimationController _cameraMotion;
  Animation<Matrix4>? _cameraPath;
  int _quiet = 0;
  int _selected = -1;
  Set<int> _matches = {};
  Size _viewport = Size.zero;
  bool _aimed = false;

  @override
  void initState() {
    super.initState();
    _layout = ForceLayout(widget.map);
    _display = List.of(_layout.positions);
    _ticker = createTicker(_tick)..start();
    _cameraMotion = AnimationController(vsync: this, duration: const Duration(milliseconds: 520))
      ..addListener(() {
        final path = _cameraPath;
        if (path != null) _camera.value = path.value;
      });
    _camera.addListener(() => setState(() {})); // labels appear once zoomed in
  }

  @override
  void didUpdateWidget(covariant KnowledgeGraphView old) {
    super.didUpdateWidget(old);
    if (!identical(old.map, widget.map)) {
      final previous = _layout.snapshot();
      final drawn = {for (var i = 0; i < _layout.nodes.length; i++) _layout.nodes[i].id: _display[i]};
      final selectedId = _selected >= 0 ? _layout.nodes[_selected].id : null;
      _layout = ForceLayout(widget.map, previous: previous);
      _display = [for (var i = 0; i < _layout.nodes.length; i++) drawn[_layout.nodes[i].id] ?? _layout.positions[i]];
      _selected = selectedId == null ? -1 : _layout.nodes.indexWhere((n) => n.id == selectedId);
      _wake();
    }
    if (old.query != widget.query || !identical(old.map, widget.map)) {
      _matches = {for (var i = 0; i < _layout.nodes.length; i++) if (_layout.nodes[i].matches(widget.query)) i};
      if (_matches.isNotEmpty && old.query != widget.query) _focus(_matches.reduce(math.min), 1.4);
    }
  }

  @override
  void dispose() {
    _ticker.dispose();
    _cameraMotion.dispose();
    _camera.dispose();
    super.dispose();
  }

  void _wake() {
    _quiet = 0;
    if (!_ticker.isActive) _ticker.start();
  }

  void _tick(Duration _) {
    final moving = _layout.step();
    var gap = 0.0;
    for (var i = 0; i < _display.length; i++) {
      final step = (_layout.positions[i] - _display[i]) * 0.2;
      _display[i] += step;
      gap = math.max(gap, step.distance);
    }
    setState(() {});
    _quiet = moving < 0.04 && gap < 0.05 ? _quiet + 1 : 0;
    if (_quiet > 30) _ticker.stop();
  }

  double get _scale => _camera.value.getMaxScaleOnAxis();

  /// Eases the camera until node [i] sits in the middle of the view at [zoom].
  void _focus(int i, double zoom) {
    if (_viewport == Size.zero) return;
    final s = math.max(_scale, zoom).clamp(0.3, 3.0);
    final p = _layout.positions[i] + const Offset(_centre, _centre);
    final target = Matrix4.identity()
      ..translateByDouble(_viewport.width / 2 - p.dx * s, _viewport.height / 2 - p.dy * s, 0, 1)
      ..scaleByDouble(s, s, 1, 1);
    _cameraPath = Matrix4Tween(begin: _camera.value, end: target).animate(CurvedAnimation(parent: _cameraMotion, curve: Curves.easeOutCubic));
    _cameraMotion.forward(from: 0);
  }

  void recenter() {
    _selected = -1;
    final target = Matrix4.identity()
      ..translateByDouble(_viewport.width / 2 - _centre * 0.9, _viewport.height / 2 - _centre * 0.9, 0, 1)
      ..scaleByDouble(0.9, 0.9, 1, 1);
    _cameraPath = Matrix4Tween(begin: _camera.value, end: target).animate(CurvedAnimation(parent: _cameraMotion, curve: Curves.easeOutCubic));
    _cameraMotion.forward(from: 0);
  }

  void _tap(Offset local) {
    final at = local - const Offset(_centre, _centre);
    var best = -1;
    var bestDistance = 26.0 / math.max(_scale, 0.5); // a finger-sized target whatever the zoom
    for (var i = 0; i < _display.length; i++) {
      final d = (_display[i] - at).distance;
      if (d < bestDistance) (best, bestDistance) = (i, d);
    }
    setState(() => _selected = best);
    if (best >= 0) _focus(best, 1.9);
    widget.onPick(best >= 0 ? _layout.nodes[best] : null);
  }

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(builder: (context, box) {
      _viewport = box.biggest;
      if (!_aimed && _viewport.width > 0) {
        _aimed = true;
        _camera.value = Matrix4.identity()
          ..translateByDouble(_viewport.width / 2 - _centre * 0.9, _viewport.height / 2 - _centre * 0.9, 0, 1)
          ..scaleByDouble(0.9, 0.9, 1, 1);
      }
      return Stack(children: [
        ClipRRect(
          borderRadius: BorderRadius.circular(16),
          child: ColoredBox(
            color: const Color(0x10FFFFFF),
            child: InteractiveViewer(
              transformationController: _camera,
              constrained: false,
              minScale: 0.3,
              maxScale: 3,
              boundaryMargin: const EdgeInsets.all(double.infinity),
              onInteractionStart: (_) => _cameraMotion.stop(),
              child: GestureDetector(
                onTapUp: (d) => _tap(d.localPosition),
                child: CustomPaint(
                  size: const Size(_world, _world),
                  painter: _GraphPainter(_layout.nodes, widget.map.edges, _display, _selected, _matches, _scale, _palette),
                ),
              ),
            ),
          ),
        ),
        Positioned(
          right: 8,
          bottom: 8,
          child: IconButton.filledTonal(icon: const Icon(Icons.center_focus_strong, size: 18), tooltip: 'See the whole map', onPressed: recenter),
        ),
        const Positioned(
          left: 12,
          bottom: 14,
          child: IgnorePointer(child: Text('Pinch to zoom · tap a dot to open it', style: TextStyle(color: Color(0x99FFFFFF), fontSize: 11))),
        ),
      ]);
    });
  }
}

class _GraphPainter extends CustomPainter {
  _GraphPainter(this.nodes, this.edges, this.at, this.selected, this.matches, this.scale, this.palette)
      : index = {for (var i = 0; i < nodes.length; i++) nodes[i].id: i};

  final List<GraphNode> nodes;
  final List<GraphEdge> edges;
  final List<Offset> at;
  final int selected;
  final Set<int> matches;
  final double scale;
  final List<Color> palette;
  final Map<String, int> index;

  static const _centre = Offset(1500, 1500);

  @override
  void paint(Canvas canvas, Size size) {
    canvas.translate(_centre.dx, _centre.dy);
    final lit = matches.isNotEmpty ? {...matches, if (selected >= 0) selected} : selected >= 0 ? _around(selected) : null;
    final edge = Paint()..strokeWidth = 1.1;
    for (final e in edges) {
      final a = index[e.from], b = index[e.to];
      if (a == null || b == null || a >= at.length || b >= at.length) continue;
      final bright = lit == null || (lit.contains(a) && lit.contains(b));
      edge.color = Colors.white.withValues(alpha: lit == null ? (e.related ? 0.22 : 0.32) : bright ? 0.5 : 0.04);
      canvas.drawLine(at[a], at[b], edge);
    }
    final close = scale >= 1.5;
    for (var i = 0; i < nodes.length && i < at.length; i++) {
      final node = nodes[i];
      final bright = lit == null || lit.contains(i);
      final radius = switch (node.kind) { NodeKind.you => 13.0, NodeKind.folder => 10.0, NodeKind.topic => 8.0, NodeKind.fact => 4.5 };
      final colour = switch (node.kind) {
        NodeKind.you => const Color(0xFFF4F7FF),
        NodeKind.folder => const Color(0xFFC9D2E3),
        _ => palette[math.max(0, node.group) % palette.length],
      };
      canvas.drawCircle(at[i], radius, Paint()..color = colour.withValues(alpha: bright ? 1 : 0.16));
      if (i == selected || matches.contains(i)) {
        canvas.drawCircle(at[i], radius + 2.5, Paint()
          ..style = PaintingStyle.stroke
          ..strokeWidth = 2.5
          ..color = i == selected ? Colors.white : const Color(0xFFFFE066));
      }
      final showLabel = bright && (node.kind != NodeKind.fact || close || lit != null);
      if (!showLabel) continue;
      final label = TextPainter(
        text: TextSpan(
          text: node.label,
          style: TextStyle(color: Colors.white, fontSize: node.kind == NodeKind.fact ? 10 : 12, fontWeight: node.kind == NodeKind.fact ? FontWeight.w400 : FontWeight.w600),
        ),
        textDirection: TextDirection.ltr,
      )..layout(maxWidth: 180);
      label.paint(canvas, at[i] + Offset(radius + 4, -8));
    }
  }

  Set<int> _around(int i) {
    final id = nodes[i].id;
    return {
      i,
      for (final e in edges)
        if (e.from == id && index[e.to] != null) index[e.to]! else if (e.to == id && index[e.from] != null) index[e.from]!,
    };
  }

  @override
  bool shouldRepaint(covariant _GraphPainter old) => true;
}
