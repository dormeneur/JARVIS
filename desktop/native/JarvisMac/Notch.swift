import AppKit
import SwiftUI

// The top-centre notch. C# (MacNotchPresenter) decides what it shows; it sends one JSON view per
// change (jv_notch_show) with the frame, accent and content already worked out by Jarvis.Core
// (NotchLayout, NotchAccent). Buttons answer with the id C# gave them (jv_notch_start's action
// callback); typed text comes back through the text callback. Ids below zero are the notch's own:
// -1 Escape, -2 a click outside, -3 the pill clicked.

struct NotchButton: Decodable, Identifiable {
    let id: Int
    let label: String
}

struct NotchTurn: Decodable, Identifiable {
    var id: String { question + answer }
    let question: String
    let answer: String
    let at: String?
}

struct NotchHomeItem: Decodable, Identifiable {
    let id: Int
    let glyph: String
    let title: String
    let detail: String?
    let second: NotchButton?
}

struct NotchCard: Decodable, Identifiable {
    let id: String
    let question: String
    let answer: String?
    let source: String?
    let state: String
    let note: String?
    let filled: Bool
    let choices: [String]?
}

struct NotchForm: Decodable {
    let title: String
    let status: String
    let cards: [NotchCard]
    let buttons: [NotchButton]
    let progress: Double?
}

struct NotchEvent: Decodable {
    let title: String
    let when: String
    let location: String
    let destination: String
    let add: Int
    let cancel: Int
}

struct NotchView: Decodable {
    var kind = "idle"
    var width = 60.0
    var height = 8.0
    var radius = 4.0
    var sliver = true
    var response: Double?
    var damping: Double?
    var glow: [UInt32]?
    var glowMoves = false
    var glowReach: Double?
    var glowStrength = 1.0
    var sweep = false
    var caption: String?
    var busy = false
    var text: String?
    var actions: [NotchButton] = []
    var close = false
    var history: [NotchTurn]?
    var greeting: String?
    var hint: String?
    var homeActions: [NotchHomeItem]?
    var recent: [NotchHomeItem]?
    var form: NotchForm?
    var event: NotchEvent?
    var clickable = false
    var typing = false
    var hidden = false

    init() {}

    /// Every key is optional: C# sends only what a state has, the rest keep their defaults.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: Key.self)
        kind = try c.decodeIfPresent(String.self, forKey: .kind) ?? "idle"
        width = try c.decodeIfPresent(Double.self, forKey: .width) ?? 60.0
        height = try c.decodeIfPresent(Double.self, forKey: .height) ?? 8.0
        radius = try c.decodeIfPresent(Double.self, forKey: .radius) ?? 4.0
        sliver = try c.decodeIfPresent(Bool.self, forKey: .sliver) ?? true
        response = try c.decodeIfPresent(Double.self, forKey: .response)
        damping = try c.decodeIfPresent(Double.self, forKey: .damping)
        glow = try c.decodeIfPresent([UInt32].self, forKey: .glow)
        glowMoves = try c.decodeIfPresent(Bool.self, forKey: .glowMoves) ?? false
        glowReach = try c.decodeIfPresent(Double.self, forKey: .glowReach)
        glowStrength = try c.decodeIfPresent(Double.self, forKey: .glowStrength) ?? 1.0
        sweep = try c.decodeIfPresent(Bool.self, forKey: .sweep) ?? false
        caption = try c.decodeIfPresent(String.self, forKey: .caption)
        busy = try c.decodeIfPresent(Bool.self, forKey: .busy) ?? false
        text = try c.decodeIfPresent(String.self, forKey: .text)
        actions = try c.decodeIfPresent([NotchButton].self, forKey: .actions) ?? []
        close = try c.decodeIfPresent(Bool.self, forKey: .close) ?? false
        history = try c.decodeIfPresent([NotchTurn].self, forKey: .history)
        greeting = try c.decodeIfPresent(String.self, forKey: .greeting)
        hint = try c.decodeIfPresent(String.self, forKey: .hint)
        homeActions = try c.decodeIfPresent([NotchHomeItem].self, forKey: .homeActions)
        recent = try c.decodeIfPresent([NotchHomeItem].self, forKey: .recent)
        form = try c.decodeIfPresent(NotchForm.self, forKey: .form)
        event = try c.decodeIfPresent(NotchEvent.self, forKey: .event)
        clickable = try c.decodeIfPresent(Bool.self, forKey: .clickable) ?? false
        typing = try c.decodeIfPresent(Bool.self, forKey: .typing) ?? false
        hidden = try c.decodeIfPresent(Bool.self, forKey: .hidden) ?? false
    }

    private enum Key: String, CodingKey {
        case kind, width, height, radius, sliver, response, damping, glow, glowMoves, glowReach, glowStrength, sweep, caption, busy, text, actions, close, history, greeting, hint, homeActions, recent, form, event, clickable, typing, hidden
    }
}

final class NotchModel: ObservableObject {
    @Published var view = NotchView()
    @Published var draft = ""
    @Published var edits: [String: String] = [:]
    @Published var hardwareNotch: CGSize = .zero
    let action: IntCallback
    let text: TextCallback

    init(action: IntCallback, text: TextCallback) {
        self.action = action
        self.text = text
    }

    func send(_ id: Int) { action(Int32(id)) }

    /// Sends typed text: kind 0 = the composer's question, 1 = form edits as JSON.
    func send(text kind: Int32, _ value: String) {
        value.withCString { text(kind, $0) }
    }
}

final class NotchPanel: NSPanel {
    var typing = false
    override var canBecomeKey: Bool { typing }
    override var canBecomeMain: Bool { false }
}

final class Notch: NSObject {
    static var shared: Notch?
    static let envelope = CGSize(width: 840, height: 640) // the largest frame (settings) plus the glow

    let model: NotchModel
    let panel = NotchPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
    var clickMonitor: Any?
    var keyMonitor: Any?
    var previousApp: NSRunningApplication?

    init(action: IntCallback, text: TextCallback) {
        model = NotchModel(action: action, text: text)
        super.init()
        float(panel, level: NSWindow.Level(rawValue: NSWindow.Level.statusBar.rawValue + 2))
        panel.ignoresMouseEvents = true
        panel.becomesKeyOnlyIfNeeded = true
        let host = NSHostingView(rootView: NotchRoot(model: model))
        host.frame = NSRect(origin: .zero, size: Notch.envelope)
        panel.contentView = host
        place()
        panel.orderFrontRegardless()
        NotificationCenter.default.addObserver(self, selector: #selector(place), name: NSApplication.didChangeScreenParametersNotification, object: nil)

        // A click anywhere else closes a reply or the home (C# decides which states care).
        clickMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] _ in
            guard let self, self.model.view.clickable else { return }
            self.model.send(-2)
        }
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            if event.keyCode == 53, let self { // Escape while typing in the notch
                self.model.send(-1)
                return nil
            }
            return event
        }
    }

    /// Top centre of the main display (the one with the menu bar), hugging its top edge.
    @objc func place() {
        guard let screen = NSScreen.screens.first else { return }
        model.hardwareNotch = .zero
        if let left = screen.auxiliaryTopLeftArea, let right = screen.auxiliaryTopRightArea {
            model.hardwareNotch = CGSize(width: screen.frame.width - left.width - right.width, height: screen.safeAreaInsets.top)
        }
        let size = Notch.envelope
        panel.setFrame(NSRect(x: screen.frame.midX - (size.width / 2), y: screen.frame.maxY - size.height, width: size.width, height: size.height), display: true)
    }

    func show(_ view: NotchView) {
        let wasTyping = model.view.typing
        if view.kind != model.view.kind { model.edits = [:] }
        if view.typing && !wasTyping { model.draft = "" }
        model.view = view
        panel.ignoresMouseEvents = !view.clickable
        panel.orderFrontRegardless()
        if view.hidden {
            panel.orderOut(nil)
        }

        // Typing takes the keyboard without activating Jarvis (a non-activating panel can be key);
        // when it ends, the app that had it gets it back.
        panel.typing = view.typing
        if view.typing && !wasTyping {
            previousApp = NSWorkspace.shared.frontmostApplication
            NSApp.activate()
            panel.makeKey()
        } else if !view.typing && wasTyping {
            panel.resignKey()
            previousApp?.activate()
            previousApp = nil
        }
    }
}

@_cdecl("jv_notch_start")
public func jv_notch_start(_ action: IntCallback, _ text: TextCallback) {
    onMain { Notch.shared = Notch(action: action, text: text) }
}

@_cdecl("jv_notch_show")
public func jv_notch_show(_ view: UnsafePointer<CChar>?) {
    guard let view else { return }
    let decoded: NotchView
    do {
        decoded = try JSONDecoder().decode(NotchView.self, from: Data(bytes: view, count: strlen(view)))
    } catch {
        NSLog("Jarvis notch: unreadable view: %@", String(describing: error))
        return
    }
    onMain { Notch.shared?.show(decoded) }
}

/// The hardware notch's size in points (0 × 0 without one), so C# can size around it.
@_cdecl("jv_notch_hardware")
public func jv_notch_hardware(_ width: UnsafeMutablePointer<Double>?, _ height: UnsafeMutablePointer<Double>?) {
    let size = onMainSync { () -> CGSize in
        guard let screen = NSScreen.screens.first, let left = screen.auxiliaryTopLeftArea, let right = screen.auxiliaryTopRightArea else { return .zero }
        return CGSize(width: screen.frame.width - left.width - right.width, height: screen.safeAreaInsets.top)
    }
    width?.pointee = size.width
    height?.pointee = size.height
}

// MARK: Views

/// The notch body: black, with concave shoulders flaring into the top edge (or a floating sliver).
struct NotchShape: Shape {
    var width: Double
    var height: Double
    var radius: Double
    var shoulder: Double

    var animatableData: AnimatablePair<AnimatablePair<Double, Double>, AnimatablePair<Double, Double>> {
        get { AnimatablePair(AnimatablePair(width, height), AnimatablePair(radius, shoulder)) }
        set {
            width = newValue.first.first
            height = newValue.first.second
            radius = newValue.second.first
            shoulder = newValue.second.second
        }
    }

    func path(in rect: CGRect) -> Path {
        let left = rect.midX - width / 2, right = rect.midX + width / 2
        let r = min(radius, height / 2, (width - 2 * shoulder) / 2)
        var path = Path()
        path.move(to: CGPoint(x: left, y: 0))
        // Left shoulder: a quarter circle curving in from the top edge.
        path.addQuadCurve(to: CGPoint(x: left + shoulder, y: shoulder), control: CGPoint(x: left + shoulder, y: 0))
        path.addLine(to: CGPoint(x: left + shoulder, y: height - r))
        path.addQuadCurve(to: CGPoint(x: left + shoulder + r, y: height), control: CGPoint(x: left + shoulder, y: height))
        path.addLine(to: CGPoint(x: right - shoulder - r, y: height))
        path.addQuadCurve(to: CGPoint(x: right - shoulder, y: height - r), control: CGPoint(x: right - shoulder, y: height))
        path.addLine(to: CGPoint(x: right - shoulder, y: shoulder))
        path.addQuadCurve(to: CGPoint(x: right, y: 0), control: CGPoint(x: right - shoulder, y: 0))
        path.closeSubpath()
        return path
    }
}

struct NotchRoot: View {
    @ObservedObject var model: NotchModel

    var view: NotchView { model.view }

    /// On a Mac with a camera housing the panel grows out of it, so everything sits below it.
    var top: Double { view.sliver || model.hardwareNotch.height == 0 ? 0 : model.hardwareNotch.height }

    var shapeWidth: Double {
        let base = view.sliver ? view.width : view.width
        return model.hardwareNotch.width > 0 && !view.sliver ? max(base, model.hardwareNotch.width + 2 * 10 + 24) : base
    }

    var animation: Animation? {
        guard let response = view.response, let damping = view.damping else { return nil }
        return .spring(response: response, dampingFraction: damping)
    }

    var body: some View {
        ZStack(alignment: .top) {
            if !(view.sliver && model.hardwareNotch.width > 0) { // the hardware notch is the sliver
                ZStack(alignment: .top) {
                    NotchShape(width: shapeWidth, height: view.height + top + (view.sliver ? 4 : 0), radius: view.radius, shoulder: view.sliver ? 0 : 10)
                        .fill(Color.black)
                    if let glow = view.glow {
                        CornerGlow(colours: glow, moves: view.glowMoves, reach: view.glowReach ?? 150, strength: view.glowStrength)
                            .frame(width: shapeWidth - 20, height: view.height + top)
                            .clipShape(NotchShape(width: shapeWidth - 20, height: view.height + top, radius: view.radius, shoulder: 0))
                            .allowsHitTesting(false)
                    }
                    if view.sweep {
                        WorkingSweep().frame(width: view.width, height: view.height).padding(.top, 4)
                    }
                }
                .mask(view.sliver ? AnyView(Rectangle().padding(.top, 4)) : AnyView(Rectangle()))
                .frame(width: shapeWidth, height: view.height + top + 4)
                .overlay(alignment: .top) {
                    content
                        .frame(width: max(0, shapeWidth - 20 - 36), height: max(0, view.height))
                        .padding(.top, top)
                        .opacity(view.sliver ? 0 : 1)
                }
            }
        }
        .frame(width: Notch.envelope.width, height: Notch.envelope.height, alignment: .top)
        .animation(animation, value: view.width)
        .animation(animation, value: view.height)
        .animation(.easeOut(duration: 0.2), value: view.kind)
        .preferredColorScheme(.dark)
    }

    @ViewBuilder var content: some View {
        switch view.kind {
        case "listening", "dictating", "thinking", "speaking", "parked":
            ActivityPill(caption: view.caption ?? "", busy: view.busy, parked: view.kind == "parked") { model.send(-3) }
        case "response":
            ResponseView(text: view.text ?? "", actions: view.actions, close: view.close, model: model)
        case "composing":
            ComposerView(model: model)
        case "home":
            HomeView(model: model)
        case "form":
            if let form = view.form { FormView(form: form, model: model) }
        case "event":
            if let event = view.event { EventView(event: event, model: model) }
        default:
            EmptyView()
        }
    }
}

struct CloseButton: View {
    let model: NotchModel

    var body: some View {
        Button { model.send(-1) } label: {
            Image(systemName: "xmark").font(.system(size: 10, weight: .semibold)).foregroundStyle(.white.opacity(0.6)).frame(width: 20, height: 20)
        }
        .buttonStyle(.plain)
        .help("Close (Esc)")
    }
}

struct Pill: View {
    let label: String
    var primary = false
    let tap: () -> Void

    var body: some View {
        Button(action: tap) {
            Text(label).font(.system(size: 12, weight: .medium))
                .padding(.horizontal, 12).frame(height: 24)
                .background(Capsule().fill(primary ? Color(red: 0.2, green: 0.5, blue: 1) : Color.white.opacity(0.12)))
                .foregroundStyle(.white)
        }
        .buttonStyle(.plain)
    }
}

struct ActivityPill: View {
    let caption: String
    let busy: Bool
    let parked: Bool
    let tap: () -> Void

    var body: some View {
        HStack(spacing: 6) {
            Text(caption).font(.system(size: 12, weight: .medium)).foregroundStyle(.white.opacity(0.9)).lineLimit(1)
            if busy || !parked {
                TimelineView(.animation) { context in
                    let t = context.date.timeIntervalSinceReferenceDate
                    HStack(spacing: 3) {
                        ForEach(0..<3) { index in
                            Circle().fill(.white).frame(width: 4, height: 4)
                                .opacity(0.3 + 0.7 * max(0, sin((t * 4) - Double(index) * 0.7)))
                        }
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .contentShape(Rectangle())
        .onTapGesture { if parked { tap() } }
    }
}

struct ResponseView: View {
    let text: String
    let actions: [NotchButton]
    let close: Bool
    let model: NotchModel

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .top) {
                ScrollView {
                    Text(text).font(.system(size: 13)).foregroundStyle(.white).lineSpacing(3)
                        .frame(maxWidth: .infinity, alignment: .leading).textSelection(.enabled)
                }
                if close { CloseButton(model: model) }
            }
            if !actions.isEmpty {
                HStack(spacing: 8) {
                    ForEach(Array(actions.enumerated()), id: \.element.id) { index, action in
                        Pill(label: action.label, primary: index == 0) { model.send(action.id) }
                    }
                    Spacer()
                }
            }
        }
        .padding(.vertical, 16)
    }
}

struct ComposerView: View {
    @ObservedObject var model: NotchModel
    @FocusState var focused: Bool

    var body: some View {
        VStack(spacing: 10) {
            HStack {
                Spacer()
                CloseButton(model: model)
            }
            if let history = model.view.history, !history.isEmpty {
                ScrollViewReader { reader in
                    ScrollView {
                        VStack(spacing: 10) {
                            ForEach(history) { turn in
                                Bubble(text: turn.question, mine: true)
                                Bubble(text: turn.answer, mine: false)
                            }
                            Color.clear.frame(height: 1).id("end")
                        }
                    }
                    .onAppear { reader.scrollTo("end") }
                }
            } else {
                Spacer()
                Text("Ask anything about your screen").font(.system(size: 12)).foregroundStyle(.white.opacity(0.4))
                Spacer()
            }
            HStack(spacing: 8) {
                TextField("Ask Jarvis…", text: $model.draft)
                    .textFieldStyle(.plain).font(.system(size: 14)).foregroundStyle(.white)
                    .focused($focused)
                    .onSubmit {
                        let question = model.draft.trimmingCharacters(in: .whitespacesAndNewlines)
                        guard !question.isEmpty else { return }
                        model.draft = ""
                        model.send(text: 0, question)
                    }
                Image(systemName: "return").foregroundStyle(.white.opacity(0.35))
            }
            .padding(.horizontal, 14).frame(height: 38)
            .background(RoundedRectangle(cornerRadius: 12).fill(Color.white.opacity(0.08)))
        }
        .padding(.bottom, 14)
        .onAppear { focused = true }
    }
}

struct Bubble: View {
    let text: String
    let mine: Bool

    var body: some View {
        HStack {
            if mine { Spacer(minLength: 60) }
            Text(text).font(.system(size: 13)).foregroundStyle(.white).textSelection(.enabled)
                .padding(.horizontal, 12).padding(.vertical, 7)
                .background(RoundedRectangle(cornerRadius: 14).fill(mine ? Color(red: 0.2, green: 0.5, blue: 1) : Color.white.opacity(0.1)))
            if !mine { Spacer(minLength: 60) }
        }
    }
}

struct HomeView: View {
    @ObservedObject var model: NotchModel

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text(model.view.greeting ?? "").font(.system(size: 15, weight: .semibold)).foregroundStyle(.white)
                    Text(model.view.hint ?? "").font(.system(size: 11)).foregroundStyle(.white.opacity(0.5))
                }
                Spacer()
                CloseButton(model: model)
            }
            HStack(spacing: 8) {
                ForEach(model.view.homeActions ?? []) { item in
                    Button { model.send(item.id) } label: {
                        HStack(spacing: 6) {
                            Image(systemName: symbol(item.glyph)).font(.system(size: 12))
                            Text(item.title).font(.system(size: 12, weight: .medium)).lineLimit(1)
                        }
                        .padding(.horizontal, 12).frame(height: 34)
                        .background(RoundedRectangle(cornerRadius: 10).fill(Color.white.opacity(0.08)))
                        .foregroundStyle(.white)
                    }
                    .buttonStyle(.plain)
                }
            }
            if let recent = model.view.recent, !recent.isEmpty {
                HStack(spacing: 8) {
                    ForEach(recent) { item in
                        Button { model.send(item.id) } label: {
                            VStack(alignment: .leading, spacing: 4) {
                                Text(item.title).font(.system(size: 12, weight: .medium)).lineLimit(1)
                                Text(item.detail ?? "").font(.system(size: 11)).foregroundStyle(.white.opacity(0.5)).lineLimit(2)
                                Spacer(minLength: 0)
                                if let second = item.second {
                                    Pill(label: second.label) { model.send(second.id) }
                                }
                            }
                            .padding(10).frame(maxWidth: .infinity, minHeight: 88, maxHeight: 88, alignment: .topLeading)
                            .background(RoundedRectangle(cornerRadius: 12).fill(Color.white.opacity(0.06)))
                            .foregroundStyle(.white)
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
        .padding(.vertical, 14)
    }

    /// Segoe Fluent glyph names (or a literal character) as SF Symbols.
    func symbol(_ glyph: String) -> String {
        switch glyph.lowercased() {
        case let name where name.contains("ask") || name.contains("chat"): return "text.bubble"
        case let name where name.contains("form"): return "list.bullet.rectangle"
        case let name where name.contains("private") || name.contains("lock"): return "lock"
        case let name where name.contains("setting"): return "gearshape"
        default: return "sparkle"
        }
    }
}

struct FormView: View {
    let form: NotchForm
    @ObservedObject var model: NotchModel

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text(form.title).font(.system(size: 14, weight: .semibold)).foregroundStyle(.white).lineLimit(1)
                    Text(form.status).font(.system(size: 11)).foregroundStyle(.white.opacity(0.55)).lineLimit(1)
                }
                Spacer()
                CloseButton(model: model)
            }
            if let progress = form.progress {
                ProgressView(value: progress).progressViewStyle(.linear).tint(Color(red: 0.2, green: 0.5, blue: 1))
            }
            ScrollView {
                VStack(spacing: 8) {
                    ForEach(form.cards) { card in
                        VStack(alignment: .leading, spacing: 4) {
                            HStack {
                                Text(card.question).font(.system(size: 12, weight: .medium)).foregroundStyle(.white.opacity(0.85))
                                Spacer()
                                if card.filled { Image(systemName: "checkmark.circle.fill").foregroundStyle(.green) }
                                if card.state == "working" { ProgressView().controlSize(.mini) }
                            }
                            TextField(card.state == "unknown" ? "Not in your vault" : "", text: Binding(
                                get: { model.edits[card.id] ?? card.answer ?? "" },
                                set: { model.edits[card.id] = $0 }), axis: .vertical)
                                .textFieldStyle(.plain).font(.system(size: 13)).foregroundStyle(.white)
                            HStack(spacing: 6) {
                                if let source = card.source { Text(source).font(.system(size: 10)).foregroundStyle(.white.opacity(0.4)).lineLimit(1) }
                                if let note = card.note { Text(note).font(.system(size: 10)).foregroundStyle(.orange.opacity(0.8)).lineLimit(1) }
                                Spacer()
                                if let answer = model.edits[card.id] ?? card.answer, !answer.isEmpty {
                                    Button {
                                        NSPasteboard.general.clearContents()
                                        NSPasteboard.general.setString(answer, forType: .string)
                                    } label: { Image(systemName: "doc.on.doc").font(.system(size: 10)) }
                                        .buttonStyle(.plain).foregroundStyle(.white.opacity(0.6)).help("Copy")
                                }
                            }
                        }
                        .padding(10)
                        .background(RoundedRectangle(cornerRadius: 10).fill(Color.white.opacity(0.06)))
                    }
                }
            }
            HStack(spacing: 8) {
                ForEach(Array(form.buttons.enumerated()), id: \.element.id) { index, button in
                    Pill(label: button.label, primary: index == 0) {
                        if !model.edits.isEmpty, let data = try? JSONSerialization.data(withJSONObject: model.edits), let edits = String(data: data, encoding: .utf8) {
                            model.send(text: 1, edits) // typed answers go with whichever button was pressed
                            model.edits = [:]
                        }
                        model.send(button.id)
                    }
                }
                Spacer()
            }
        }
        .padding(.vertical, 14)
    }
}

struct EventView: View {
    let event: NotchEvent
    let model: NotchModel

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("New event").font(.system(size: 11)).foregroundStyle(.white.opacity(0.5))
                Spacer()
                CloseButton(model: model)
            }
            Text(event.title).font(.system(size: 16, weight: .semibold)).foregroundStyle(.white)
            Label(event.when, systemImage: "clock").font(.system(size: 12)).foregroundStyle(.white.opacity(0.8))
            if !event.location.isEmpty { Label(event.location, systemImage: "mappin").font(.system(size: 12)).foregroundStyle(.white.opacity(0.8)) }
            Spacer()
            HStack(spacing: 8) {
                Pill(label: "Add to \(event.destination)", primary: true) { model.send(event.add) }
                Pill(label: "Cancel") { model.send(event.cancel) }
                Spacer()
            }
        }
        .padding(.vertical, 14)
    }
}

/// Three soft colour blobs in the bottom-right corner (NotchSurface's), drifting while live.
struct CornerGlow: View {
    let colours: [UInt32]
    let moves: Bool
    let reach: Double
    let strength: Double
    static let blobs: [(fromRight: Double, down: Double, radius: Double, intensity: Double)] = [(0.05, 1.0, 1.05, 1.0), (0.22, 0.95, 0.85, 1.0), (0.42, 0.92, 0.65, 0.9)]

    var body: some View {
        TimelineView(.animation(paused: !moves)) { context in
            let t = moves ? context.date.timeIntervalSinceReferenceDate : 0
            GeometryReader { geometry in
                ZStack {
                    ForEach(0..<min(3, colours.count), id: \.self) { index in
                        let blob = Self.blobs[index]
                        let radius = reach * blob.radius * (1 + (moves ? 0.06 * sin(t * 1.3 + Double(index)) : 0))
                        RadialGradient(colors: [Color(cgColor: color(colours[index], 0.85 * blob.intensity * strength)), .clear], center: .center, startRadius: 0, endRadius: radius)
                            .frame(width: radius * 2, height: radius * 2)
                            .position(x: geometry.size.width * (1 - blob.fromRight) + (moves ? 8 * sin(t * 0.7 + Double(index) * 2) : 0),
                                      y: geometry.size.height * blob.down)
                    }
                }
            }
        }
        .blendMode(.plusLighter)
    }
}

/// The green light that sweeps along the sliver while background jobs run.
struct WorkingSweep: View {
    var body: some View {
        TimelineView(.animation) { context in
            GeometryReader { geometry in
                let t = context.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 1.6) / 1.6
                ZStack {
                    Color(cgColor: color(0x1ACB4B, 0.26))
                    LinearGradient(colors: [.clear, Color(cgColor: color(0xB8FFCB, 0.9)), .clear], startPoint: .leading, endPoint: .trailing)
                        .frame(width: 30)
                        .position(x: -15 + (geometry.size.width + 30) * t, y: geometry.size.height / 2)
                }
            }
            .clipShape(Capsule())
        }
    }
}
