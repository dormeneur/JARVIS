import AppKit
import QuartzCore

// The transparent overlay: one click-through panel per display. C# runs the companion's state
// machine (Jarvis.Core) once per display refresh, from jv_overlay_start's frame callback on the
// main thread, and tells these views what to draw. Positions are y-down desktop points.

public typealias FrameCallback = @convention(c) (Double) -> Void

final class FlippedView: NSView {
    override var isFlipped: Bool { true }
}

final class OverlayPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// Makes a panel float above everything (full-screen apps included) on every Space, never
/// take focus, and stay out of screenshots taken by other apps' legacy capture APIs.
func float(_ panel: NSPanel, level: NSWindow.Level) {
    panel.isOpaque = false
    panel.backgroundColor = .clear
    panel.hasShadow = false
    panel.level = level
    panel.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
    panel.isReleasedWhenClosed = false
    panel.hidesOnDeactivate = false
    panel.sharingType = Overlay.showInRecordings ? .readOnly : .none
}

final class OverlayWindow {
    let screen: NSScreen
    let frame: CGRect // y-down desktop
    let panel: OverlayPanel
    let root = FlippedView()
    let ink: InkView

    init(screen: NSScreen) {
        self.screen = screen
        frame = desktopFrame(screen)
        panel = OverlayPanel(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        float(panel, level: .screenSaver)
        panel.ignoresMouseEvents = true
        root.frame = NSRect(origin: .zero, size: screen.frame.size)
        root.wantsLayer = true
        ink = InkView(frame: root.bounds)
        root.addSubview(ink)
        panel.contentView = root
        panel.setFrame(screen.frame, display: false)
        panel.orderFrontRegardless()
    }

    func local(_ x: Double, _ y: Double) -> CGPoint {
        CGPoint(x: x - frame.minX, y: y - frame.minY)
    }

    func close() {
        panel.orderOut(nil)
    }
}

final class Overlay: NSObject {
    static var shared: Overlay?
    static var showInRecordings = false

    let frame: FrameCallback
    let clicked: VoidCallback
    let displaysChanged: VoidCallback
    var windows: [OverlayWindow] = []
    let cursor = CursorLayer()
    let bubble = BubbleLayer()
    let border = BorderLayers()
    var link: CADisplayLink?
    var catcher: ClickCatcher?

    init(frame: FrameCallback, clicked: VoidCallback, displaysChanged: VoidCallback) {
        self.frame = frame
        self.clicked = clicked
        self.displaysChanged = displaysChanged
        super.init()
        rebuild()
        NotificationCenter.default.addObserver(self, selector: #selector(screensChanged), name: NSApplication.didChangeScreenParametersNotification, object: nil)
    }

    func rebuild() {
        link?.invalidate()
        windows.forEach { $0.close() }
        windows = NSScreen.screens.map(OverlayWindow.init)
        cursor.removeFromSuperlayer()
        bubble.removeFromSuperlayer()
        border.detach()
        // The display link follows the primary display (60 Hz, or 120 Hz on ProMotion).
        link = windows.first?.root.displayLink(target: self, selector: #selector(tick(_:)))
        link?.add(to: .main, forMode: .common)
    }

    @objc func screensChanged() {
        rebuild()
        displaysChanged()
    }

    @objc func tick(_ link: CADisplayLink) {
        frame(link.timestamp)
    }

    func host(_ x: Double, _ y: Double) -> OverlayWindow? {
        windows.first { $0.frame.contains(CGPoint(x: x, y: y)) } ?? windows.first
    }

    func raise() {
        windows.forEach { $0.panel.orderFrontRegardless() }
    }
}

@_cdecl("jv_overlay_start")
public func jv_overlay_start(_ frame: FrameCallback, _ clicked: VoidCallback, _ displaysChanged: VoidCallback, _ showInRecordings: Int32) {
    onMain {
        Overlay.showInRecordings = showInRecordings != 0
        Overlay.shared = Overlay(frame: frame, clicked: clicked, displaysChanged: displaysChanged)
    }
}

@_cdecl("jv_overlay_raise")
public func jv_overlay_raise() {
    onMain { Overlay.shared?.raise() }
}

// MARK: Cursor

/// The companion: a rounded triangle with a soft glow of its own colour, or three hopping
/// thinking dots, or five listening bars, plus the notch-dwell ring. Drawn only when it looks
/// different; moving it is a layer position change.
final class CursorLayer: CALayer {
    static let canvas: CGFloat = 72
    var rotation = 0.0
    var swell = 1.0
    var rgb: UInt32 = 0
    var dots: [Double]?
    var bars: [Double]?
    var dwell: Double?

    override init() {
        super.init()
        bounds = CGRect(x: 0, y: 0, width: Self.canvas, height: Self.canvas)
        needsDisplayOnBoundsChange = true
        actions = ["position": NSNull(), "contents": NSNull(), "hidden": NSNull()]
    }

    override init(layer: Any) {
        super.init(layer: layer)
    }

    required init?(coder: NSCoder) {
        fatalError("unused")
    }

    override func draw(in context: CGContext) {
        let centre = CGPoint(x: bounds.midX, y: bounds.midY)
        let ink = color(rgb)
        // The glow: the same shape blurred (σ 3.5 → shadow blur ≈ 2σ) at 75 %.
        context.setShadow(offset: .zero, blur: 7, color: color(rgb, 0.75))
        context.setFillColor(ink)
        context.saveGState()
        context.translateBy(x: centre.x, y: centre.y)
        context.scaleBy(x: swell, y: swell)
        if let bars {
            let pitch = 4.0
            for (index, height) in bars.enumerated() {
                let left = (Double(index) - 2) * pitch - 1
                context.addPath(CGPath(roundedRect: CGRect(x: left, y: -height / 2, width: 2, height: height), cornerWidth: 1.5, cornerHeight: 1.5, transform: nil))
            }
            context.fillPath()
        } else if let dots {
            for index in 0..<3 {
                let base = index * 5
                context.saveGState()
                context.translateBy(x: dots[base], y: dots[base + 1])
                context.rotate(by: dots[base + 4])
                let along = dots[base + 2]
                let across = along * dots[base + 3]
                context.fillEllipse(in: CGRect(x: -along, y: -across, width: along * 2, height: across * 2))
                context.restoreGState()
            }
        } else {
            context.rotate(by: rotation * .pi / 180)
            context.addPath(roundedTriangle())
            context.fillPath()
        }
        context.restoreGState()

        if let dwell {
            context.setShadow(offset: .zero, blur: 0, color: nil)
            let line = 2.5
            let radius = (16 - line) / 2
            let at = CGPoint(x: centre.x + 16, y: centre.y + 16)
            context.setLineWidth(line)
            context.setStrokeColor(color(rgb, 0.25))
            context.strokeEllipse(in: CGRect(x: at.x - radius, y: at.y - radius, width: radius * 2, height: radius * 2))
            if dwell > 0 {
                context.setStrokeColor(ink)
                context.setLineCap(.round)
                let start = -Double.pi / 2
                context.addArc(center: at, radius: radius, startAngle: start, endAngle: start + (2 * .pi * min(dwell, 0.999)), clockwise: false)
                context.strokePath()
            }
        }
    }

    /// Side 16 with fillets of 9 % of the side, tip up (the Windows renderer's geometry).
    private func roundedTriangle() -> CGPath {
        let side = 16.0
        let circumradius = side / 3.0.squareRoot()
        let vertices = (0..<3).map { index -> CGPoint in
            let angle = (-Double.pi / 2) + (Double(index) * 2 * .pi / 3)
            return CGPoint(x: cos(angle) * circumradius, y: sin(angle) * circumradius)
        }
        let inset = side * 0.09 * 3.0.squareRoot()
        func toward(_ from: CGPoint, _ to: CGPoint) -> CGPoint {
            let dx = to.x - from.x, dy = to.y - from.y
            let length = (dx * dx + dy * dy).squareRoot()
            return CGPoint(x: from.x + dx / length * inset, y: from.y + dy / length * inset)
        }
        let path = CGMutablePath()
        for index in 0..<3 {
            let corner = vertices[index]
            let entry = toward(corner, vertices[(index + 2) % 3])
            let exit = toward(corner, vertices[(index + 1) % 3])
            if index == 0 { path.move(to: entry) } else { path.addLine(to: entry) }
            path.addQuadCurve(to: exit, control: corner)
        }
        path.closeSubpath()
        return path
    }
}

/// dots: 15 numbers (x, y, radius along, across scale, angle per dot) or null; bars: 5 heights or null; dwell < 0 = none.
@_cdecl("jv_cursor")
public func jv_cursor(_ visible: Int32, _ x: Double, _ y: Double, _ rotation: Double, _ swell: Double, _ rgb: UInt32, _ dots: UnsafePointer<Double>?, _ bars: UnsafePointer<Double>?, _ dwell: Double) {
    guard let overlay = Overlay.shared else { return }
    let cursor = overlay.cursor
    CATransaction.begin()
    CATransaction.setDisableActions(true)
    defer { CATransaction.commit() }
    guard visible != 0, let host = overlay.host(x, y) else {
        cursor.isHidden = true
        return
    }

    if cursor.superlayer !== host.root.layer {
        host.root.layer?.addSublayer(cursor)
        cursor.contentsScale = host.screen.backingScaleFactor
        cursor.setNeedsDisplay()
    }
    cursor.isHidden = false
    cursor.zPosition = 10
    cursor.position = host.local(x, y)

    let newDots = dots.map { Array(UnsafeBufferPointer(start: $0, count: 15)).map { ($0 * 100).rounded() / 100 } }
    let newBars = bars.map { Array(UnsafeBufferPointer(start: $0, count: 5)).map { ($0 * 10).rounded() / 10 } }
    let newDwell = dwell < 0 ? nil : (dwell * 100).rounded() / 100
    let newRotation = (rotation * 10).rounded() / 10
    let newSwell = (swell * 1000).rounded() / 1000
    if newRotation != cursor.rotation || newSwell != cursor.swell || rgb != cursor.rgb || newDots != cursor.dots || newBars != cursor.bars || newDwell != cursor.dwell {
        cursor.rotation = newRotation
        cursor.swell = newSwell
        cursor.rgb = rgb
        cursor.dots = newDots
        cursor.bars = newBars
        cursor.dwell = newDwell
        cursor.setNeedsDisplay()
    }
}

// MARK: Bubble

/// The label beside the cursor once it points: semibold 11, white on the cursor's colour.
final class BubbleLayer: CALayer {
    let text = CATextLayer()

    override init() {
        super.init()
        cornerRadius = 6
        anchorPoint = .zero
        actions = ["position": NSNull(), "bounds": NSNull(), "hidden": NSNull(), "backgroundColor": NSNull()]
        text.actions = ["contents": NSNull(), "bounds": NSNull(), "position": NSNull()]
        text.anchorPoint = .zero
        text.truncationMode = .end
        addSublayer(text)
    }

    override init(layer: Any) {
        super.init(layer: layer)
    }

    required init?(coder: NSCoder) {
        fatalError("unused")
    }
}

@_cdecl("jv_bubble")
public func jv_bubble(_ label: UnsafePointer<CChar>?, _ x: Double, _ y: Double, _ rgb: UInt32) {
    guard let overlay = Overlay.shared else { return }
    let bubble = overlay.bubble
    CATransaction.begin()
    CATransaction.setDisableActions(true)
    defer { CATransaction.commit() }
    guard let value = string(label), !value.isEmpty, let host = overlay.host(x, y) else {
        bubble.isHidden = true
        return
    }

    if bubble.superlayer !== host.root.layer {
        host.root.layer?.addSublayer(bubble)
        bubble.zPosition = 11
        bubble.text.contentsScale = host.screen.backingScaleFactor
    }
    let font = NSFont.systemFont(ofSize: 11, weight: .semibold)
    let attributed = NSAttributedString(string: value, attributes: [.font: font, .foregroundColor: NSColor.white])
    let size = attributed.size()
    let width = min(ceil(size.width), 300)
    bubble.text.string = attributed
    bubble.text.frame = CGRect(x: 8, y: 4, width: width, height: ceil(size.height))
    bubble.bounds = CGRect(x: 0, y: 0, width: width + 16, height: ceil(size.height) + 8)
    let at = host.local(x, y)
    bubble.position = CGPoint(x: at.x + 10, y: at.y + 18)
    bubble.backgroundColor = color(rgb)
    bubble.isHidden = false
}

// MARK: Click catcher

/// After a shake the cursor waits where it was; this small, nearly invisible panel on it takes the
/// click that starts listening (the overlay itself is click-through).
final class ClickCatcher: NSPanel {
    let clicked: VoidCallback

    init(clicked: VoidCallback) {
        self.clicked = clicked
        super.init(contentRect: NSRect(x: 0, y: 0, width: 44, height: 44), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        float(self, level: NSWindow.Level(rawValue: NSWindow.Level.screenSaver.rawValue + 1))
        let view = CatcherView(frame: NSRect(x: 0, y: 0, width: 44, height: 44))
        view.owner = self
        contentView = view
    }

    override var canBecomeKey: Bool { false }
}

final class CatcherView: NSView {
    weak var owner: ClickCatcher?

    override func draw(_ dirtyRect: NSRect) {
        NSColor(white: 0, alpha: 0.01).setFill() // fully clear windows don't receive clicks
        bounds.fill()
    }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with event: NSEvent) {
        owner?.clicked()
    }
}

@_cdecl("jv_catcher")
public func jv_catcher(_ show: Int32, _ x: Double, _ y: Double) {
    guard let overlay = Overlay.shared else { return }
    if show == 0 {
        overlay.catcher?.orderOut(nil)
        return
    }

    let catcher = overlay.catcher ?? ClickCatcher(clicked: overlay.clicked)
    overlay.catcher = catcher
    let at = cocoaPoint(x, y)
    catcher.setFrameOrigin(NSPoint(x: at.x - 22, y: at.y - 22))
    if !catcher.isVisible { catcher.orderFrontRegardless() }
}

// MARK: Ink, highlighter trail

/// Hand-drawn marks and the highlighter trail, painted with Core Graphics. C# sends the whole
/// frame's shapes (already outlined by Jarvis.Core) and commits; only the area they cover repaints.
final class InkView: NSView {
    struct Shape {
        var points: [CGPoint]
        var rgb: UInt32
        var alpha: CGFloat
        var kind: Int // 0 stroke (glows), 1 face (filled and outlined)
    }

    struct Label {
        var text: String
        var anchor: CGPoint
        var badge: Bool
        var growsLeft: Bool
        var pop: CGFloat
        var rgb: UInt32
        var alpha: CGFloat
    }

    var shapes: [Shape] = []
    var labels: [Label] = []
    var trail: [CGPoint] = []
    var trailRgb: UInt32 = 0
    var trailAlpha: CGFloat = 0
    var painted = CGRect.null

    override var isFlipped: Bool { true }

    func commit() {
        var cover = CGRect.null
        for shape in shapes {
            for point in shape.points { cover = cover.union(CGRect(origin: point, size: .zero)) }
        }
        for label in labels { cover = cover.union(CGRect(origin: label.anchor, size: .zero).insetBy(dx: -320, dy: -40)) }
        for point in trail { cover = cover.union(CGRect(origin: point, size: .zero)) }
        cover = cover.isNull ? cover : cover.insetBy(dx: -24, dy: -24)
        let dirty = painted.union(cover)
        painted = cover
        if !dirty.isNull { setNeedsDisplay(dirty) }
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let context = NSGraphicsContext.current?.cgContext else { return }
        context.clear(dirtyRect)

        // Strokes glow together (σ 3 → blur 6, 60 %), like ink bleeding into paper.
        let strokes = shapes.filter { $0.kind == 0 }
        if let first = strokes.first {
            context.saveGState()
            context.setShadow(offset: .zero, blur: 6, color: color(first.rgb, 0.6 * first.alpha))
            context.beginTransparencyLayer(auxiliaryInfo: nil)
            for stroke in strokes {
                context.setFillColor(color(stroke.rgb, stroke.alpha))
                context.addLines(between: stroke.points)
                context.closePath()
                context.fillPath()
            }
            context.endTransparencyLayer()
            context.restoreGState()
        }

        context.setLineWidth(1)
        for face in shapes where face.kind == 1 {
            context.setFillColor(color(face.rgb, face.alpha))
            context.setStrokeColor(color(face.rgb, face.alpha))
            context.addLines(between: face.points)
            context.closePath()
            context.drawPath(using: .fillStroke)
        }

        for label in labels {
            draw(label, context)
        }

        if trail.count > 1 {
            context.setStrokeColor(color(trailRgb, trailAlpha))
            context.setLineWidth(8)
            context.setLineCap(.round)
            context.setLineJoin(.round)
            context.addLines(between: trail)
            context.strokePath()
        }
    }

    private func draw(_ label: Label, _ context: CGContext) {
        let font = NSFont.systemFont(ofSize: 14, weight: .semibold)
        let text = NSAttributedString(string: label.text, attributes: [.font: font, .foregroundColor: NSColor(white: 1, alpha: label.alpha)])
        let size = text.size()
        var centre = label.anchor
        context.saveGState()
        context.setFillColor(color(label.rgb, label.alpha))
        if label.badge {
            context.translateBy(x: centre.x, y: centre.y)
            context.scaleBy(x: label.pop, y: label.pop)
            context.fillEllipse(in: CGRect(x: -12, y: -12, width: 24, height: 24))
        } else {
            let width = size.width + 18, height = size.height + 8
            let left = label.growsLeft ? label.anchor.x - width : label.anchor.x
            centre = CGPoint(x: left + width / 2, y: label.anchor.y)
            context.translateBy(x: centre.x, y: centre.y)
            context.scaleBy(x: label.pop, y: label.pop)
            context.addPath(CGPath(roundedRect: CGRect(x: -width / 2, y: -height / 2, width: width, height: height), cornerWidth: height / 2, cornerHeight: height / 2, transform: nil))
            context.fillPath()
        }
        NSGraphicsContext.saveGraphicsState()
        text.draw(at: NSPoint(x: -size.width / 2, y: -size.height / 2))
        NSGraphicsContext.restoreGraphicsState()
        context.restoreGState()
    }
}

private var pendingShapes: [InkView.Shape] = []
private var pendingLabels: [InkView.Label] = []

/// Points are x,y pairs on the desktop. kind 0 = a stroke outline (glows), 1 = a 3D figure's face.
@_cdecl("jv_ink_shape")
public func jv_ink_shape(_ points: UnsafePointer<Float>?, _ count: Int32, _ rgb: UInt32, _ alpha: Float, _ kind: Int32) {
    guard let points, count >= 3 else { return }
    let list = (0..<Int(count)).map { CGPoint(x: Double(points[$0 * 2]), y: Double(points[$0 * 2 + 1])) }
    pendingShapes.append(.init(points: list, rgb: rgb, alpha: CGFloat(alpha), kind: Int(kind)))
}

@_cdecl("jv_ink_label")
public func jv_ink_label(_ text: UnsafePointer<CChar>?, _ x: Float, _ y: Float, _ badge: Int32, _ growsLeft: Int32, _ pop: Float, _ rgb: UInt32, _ alpha: Float) {
    pendingLabels.append(.init(text: string(text) ?? "", anchor: CGPoint(x: Double(x), y: Double(y)), badge: badge != 0, growsLeft: growsLeft != 0, pop: CGFloat(pop), rgb: rgb, alpha: CGFloat(alpha)))
}

/// Shows the shapes and labels sent since the last commit on the display containing (x, y);
/// with nothing sent, clears the ink everywhere.
@_cdecl("jv_ink_commit")
public func jv_ink_commit(_ x: Double, _ y: Double) {
    guard let overlay = Overlay.shared else { return }
    let host = pendingShapes.isEmpty && pendingLabels.isEmpty ? nil : overlay.host(x, y)
    for window in overlay.windows {
        let ink = window.ink
        if window === host {
            ink.shapes = pendingShapes.map { shape in var moved = shape; moved.points = shape.points.map { window.local($0.x, $0.y) }; return moved }
            ink.labels = pendingLabels.map { label in var moved = label; moved.anchor = window.local(label.anchor.x, label.anchor.y); return moved }
        } else if !ink.shapes.isEmpty || !ink.labels.isEmpty {
            ink.shapes = []
            ink.labels = []
        } else {
            continue
        }
        ink.commit()
    }
    pendingShapes = []
    pendingLabels = []
}

/// The highlighter trail (desktop x,y pairs); count 0 hides it.
@_cdecl("jv_highlight")
public func jv_highlight(_ points: UnsafePointer<Double>?, _ count: Int32, _ rgb: UInt32, _ alpha: Double) {
    guard let overlay = Overlay.shared else { return }
    let host = count > 0 && points != nil ? overlay.host(points![0], points![1]) : nil
    for window in overlay.windows {
        let ink = window.ink
        if window === host, let points {
            ink.trail = (0..<Int(count)).map { window.local(points[$0 * 2], points[$0 * 2 + 1]) }
            ink.trailRgb = rgb
            ink.trailAlpha = alpha
        } else if !ink.trail.isEmpty {
            ink.trail = []
        } else {
            continue
        }
        ink.commit()
    }
}

// MARK: Control border

/// While Jarvis drives, every display gets a 22-point frame of flowing colour (blue, purple, pink,
/// orange) that fades toward the middle.
final class BorderLayers {
    static let palette: [UInt32] = [0x3380FF, 0x9B5CFF, 0xFF5CA8, 0xFFA659]
    var strips: [ObjectIdentifier: [CAGradientLayer]] = [:]

    func detach() {
        strips.values.joined().forEach { $0.removeFromSuperlayer() }
        strips = [:]
    }

    static func colour(_ around: Double) -> CGColor {
        let t = ((around.truncatingRemainder(dividingBy: 1)) + 1).truncatingRemainder(dividingBy: 1) * Double(palette.count)
        let index = Int(t), mix = t - Double(index)
        let a = palette[index % palette.count], b = palette[(index + 1) % palette.count]
        func channel(_ shift: UInt32) -> CGFloat {
            CGFloat(Double((a >> shift) & 0xFF) * (1 - mix) + Double((b >> shift) & 0xFF) * mix) / 255
        }
        return CGColor(srgbRed: channel(16), green: channel(8), blue: channel(0), alpha: 1)
    }

    func set(_ opacity: Double, _ phase: Double, windows: [OverlayWindow]) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        defer { CATransaction.commit() }
        for window in windows {
            guard let root = window.root.layer else { continue }
            let key = ObjectIdentifier(window)
            if opacity <= 0 {
                strips[key]?.forEach { $0.isHidden = true }
                continue
            }

            let layers = strips[key] ?? (0..<4).map { _ in
                let strip = CAGradientLayer()
                let fade = CAGradientLayer()
                fade.colors = [CGColor(gray: 0, alpha: 1), CGColor(gray: 0, alpha: 0)]
                strip.mask = fade
                root.addSublayer(strip)
                return strip
            }
            strips[key] = layers
            let size = window.root.bounds.size, thickness = 22.0
            // Top, right, bottom, left: each covers its share of one trip around the screen.
            let frames = [CGRect(x: 0, y: 0, width: size.width, height: thickness), CGRect(x: size.width - thickness, y: 0, width: thickness, height: size.height),
                          CGRect(x: 0, y: size.height - thickness, width: size.width, height: thickness), CGRect(x: 0, y: 0, width: thickness, height: size.height)]
            let perimeter = 2 * (size.width + size.height)
            let starts = [0, size.width, size.width + size.height, 2 * size.width + size.height]
            for (index, strip) in layers.enumerated() {
                strip.isHidden = false
                strip.frame = frames[index]
                strip.opacity = Float(opacity)
                strip.zPosition = 9
                let length = index % 2 == 0 ? size.width : size.height
                let reversed = index >= 2 // bottom runs right-to-left, left runs bottom-to-top
                strip.colors = (0...8).map { BorderLayers.colour((starts[index] + (Double($0) / 8 * length)) / perimeter - phase) }
                let horizontal = index % 2 == 0
                strip.startPoint = horizontal ? CGPoint(x: reversed ? 1 : 0, y: 0.5) : CGPoint(x: 0.5, y: reversed ? 1 : 0)
                strip.endPoint = horizontal ? CGPoint(x: reversed ? 0 : 1, y: 0.5) : CGPoint(x: 0.5, y: reversed ? 0 : 1)
                if let fade = strip.mask as? CAGradientLayer {
                    fade.frame = strip.bounds
                    // Opaque at the screen's edge, clear toward its middle.
                    let edge: [CGPoint] = [CGPoint(x: 0.5, y: 0), CGPoint(x: 1, y: 0.5), CGPoint(x: 0.5, y: 1), CGPoint(x: 0, y: 0.5)]
                    fade.startPoint = edge[index]
                    fade.endPoint = CGPoint(x: 1 - edge[index].x, y: 1 - edge[index].y)
                }
            }
        }
    }
}

@_cdecl("jv_border")
public func jv_border(_ opacity: Double, _ phase: Double) {
    guard let overlay = Overlay.shared else { return }
    overlay.border.set(opacity, phase, windows: overlay.windows)
}
