import AppKit
import SwiftUI

// The Settings window. C# (MacSettings) describes the pages and their rows as JSON, with current
// values; a change comes back through the text callback as {"key": "Section:Sub:Name", "value": …}
// (kind 2) and a button press through the action callback. C# re-sends the pages after each change.

struct SettingsChoice: Decodable, Hashable {
    let value: String
    let label: String
}

struct SettingsRow: Decodable, Identifiable {
    let id: String
    let type: String // toggle, choice, text, secret, number, button, note, group
    let label: String
    let detail: String?
    var on: Bool?
    var text: String?
    var number: Double?
    let minimum: Double?
    let maximum: Double?
    let step: Double?
    let choices: [SettingsChoice]?
    let action: Int?
    let enabled: Bool?

    /// The setting this row edits; "Section:Name#custom" is a second row for the same one.
    var setting: String { String(id.split(separator: "#").first ?? Substring(id)) }
}

struct SettingsPage: Decodable, Identifiable {
    var id: String { title }
    let title: String
    let symbol: String
    var rows: [SettingsRow]
}

final class SettingsModel: ObservableObject {
    @Published var pages: [SettingsPage] = []
    @Published var selected: String?
    @Published var banner: String?
    let action: IntCallback
    let text: TextCallback

    init(action: IntCallback, text: TextCallback) {
        self.action = action
        self.text = text
    }

    func change(_ key: String, _ value: Any) {
        // Shown at once (every row editing that setting); C#'s re-send moments later confirms it.
        let setting = String(key.split(separator: "#").first ?? Substring(key))
        for page in pages.indices {
            for row in pages[page].rows.indices where pages[page].rows[row].setting == setting {
                switch value {
                case let on as Bool: pages[page].rows[row].on = on
                case let number as Double: pages[page].rows[row].number = number
                case let text as String: pages[page].rows[row].text = text
                default: break
                }
            }
        }
        guard let data = try? JSONSerialization.data(withJSONObject: ["key": key, "value": value]), let json = String(data: data, encoding: .utf8) else { return }
        json.withCString { text(2, $0) }
    }
}

final class SettingsWindow: NSObject, NSWindowDelegate {
    static var shared: SettingsWindow?
    var lastPages = ""
    let model: SettingsModel
    var window: NSWindow?
    var previousApp: NSRunningApplication?

    init(action: IntCallback, text: TextCallback) {
        model = SettingsModel(action: action, text: text)
    }

    func show(section: String?) {
        if let section { model.selected = section }
        if model.selected == nil { model.selected = model.pages.first?.title }
        if window == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 760, height: 560), styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView], backing: .buffered, defer: false)
            window.title = "Jarvis Settings"
            window.titlebarAppearsTransparent = true
            window.isReleasedWhenClosed = false
            window.contentView = FirstClickHostingView(rootView: SettingsRoot(model: model))
            window.setFrameAutosaveName("JarvisSettings")
            window.center()
            window.delegate = self
            self.window = window
        }
        // Jarvis is a menu bar app (no Dock icon); while Settings is open it acts like a normal app.
        NSApp.setActivationPolicy(.regular)
        NSApp.activate()
        window?.makeKeyAndOrderFront(nil)
    }

    func windowWillClose(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
    }
}

@_cdecl("jv_settings_start")
public func jv_settings_start(_ action: IntCallback, _ text: TextCallback) {
    onMain { SettingsWindow.shared = SettingsWindow(action: action, text: text) }
}

/// Replaces the pages (keeps the selected one); show != 0 also opens the window on `section`.
@_cdecl("jv_settings_update")
public func jv_settings_update(_ pages: UnsafePointer<CChar>?, _ banner: UnsafePointer<CChar>?, _ show: Int32, _ section: UnsafePointer<CChar>?) {
    guard let pages, let decoded = try? JSONDecoder().decode([SettingsPage].self, from: Data(bytes: pages, count: strlen(pages))) else { return }
    let raw = String(cString: pages)
    let note = string(banner)
    let page = string(section)
    onMain {
        guard let settings = SettingsWindow.shared else { return }
        // Nothing new: leave the view alone, so an open menu or a half-typed field isn't reset.
        if show == 0 && raw == settings.lastPages && note == settings.model.banner { return }
        settings.lastPages = raw
        settings.model.pages = decoded
        settings.model.banner = note
        if show != 0 { settings.show(section: page) }
    }
}

struct SettingsRoot: View {
    @ObservedObject var model: SettingsModel

    var body: some View {
        NavigationSplitView {
            List(model.pages, selection: $model.selected) { page in
                Label(page.title, systemImage: page.symbol).tag(page.title)
            }
            .navigationSplitViewColumnWidth(min: 180, ideal: 200)
        } detail: {
            if let page = model.pages.first(where: { $0.title == model.selected }) ?? model.pages.first {
                VStack(spacing: 0) {
                    if let banner = model.banner {
                        HStack {
                            Image(systemName: "arrow.clockwise.circle")
                            Text(banner).font(.callout)
                            Spacer()
                        }
                        .padding(10)
                        .background(Color.accentColor.opacity(0.12))
                    }
                    Form {
                        ForEach(sections(page.rows), id: \.0) { title, rows in
                            Section(title) {
                                ForEach(rows) { row in RowView(row: row, model: model) }
                            }
                        }
                    }
                    .formStyle(.grouped)
                }
                .navigationTitle(page.title)
            }
        }
    }

    /// Rows split at each "group" row into titled sections.
    func sections(_ rows: [SettingsRow]) -> [(String, [SettingsRow])] {
        var result: [(String, [SettingsRow])] = [("", [])]
        for row in rows {
            if row.type == "group" { result.append((row.label, [])) } else { result[result.count - 1].1.append(row) }
        }
        return result.filter { !$0.1.isEmpty }
    }
}

struct RowView: View {
    let row: SettingsRow
    @ObservedObject var model: SettingsModel
    @State private var draft = ""

    var body: some View {
        Group {
            switch row.type {
            case "toggle":
                Toggle(isOn: Binding(get: { row.on ?? false }, set: { if $0 != (row.on ?? false) { model.change(row.id, $0) } })) { labels }
            case "choice":
                // Only a real change is saved: opening the menu can set the same value again.
                Picker(selection: Binding(get: { row.text ?? "" }, set: { if $0 != (row.text ?? "") { model.change(row.id, $0) } })) {
                    ForEach(row.choices ?? [], id: \.self) { choice in Text(choice.label).tag(choice.value) }
                } label: { labels }
            case "number":
                HStack {
                    labels
                    Spacer()
                    Text(format(row.number ?? 0)).monospacedDigit().foregroundStyle(.secondary)
                    Stepper("", value: Binding(get: { row.number ?? 0 }, set: { model.change(row.id, $0) }), in: (row.minimum ?? 0)...(row.maximum ?? 100), step: row.step ?? 1).labelsHidden()
                }
            case "text", "secret":
                HStack {
                    labels
                    Spacer(minLength: 24)
                    Group {
                        if row.type == "secret" { SecureField("", text: $draft) } else { TextField("", text: $draft) }
                    }
                    .textFieldStyle(.roundedBorder).frame(maxWidth: 300)
                    .onSubmit { model.change(row.id, draft) }
                    .onAppear { draft = row.text ?? "" }
                    .onChange(of: row.text) { _, value in draft = value ?? "" }
                    if draft != (row.text ?? "") {
                        Button("Save") { model.change(row.id, draft) }
                    }
                }
            case "button":
                HStack {
                    labels
                    Spacer()
                    Button(row.text ?? row.label) { if let id = row.action { model.action(Int32(id)) } }
                        .disabled(row.enabled == false)
                }
            default:
                labels
            }
        }
    }

    var labels: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(row.label)
            if let detail = row.detail, !detail.isEmpty {
                Text(detail).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    func format(_ value: Double) -> String {
        value.rounded() == value ? String(Int(value)) : String(format: "%.2f", value)
    }
}
