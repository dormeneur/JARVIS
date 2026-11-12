import AppKit

/// The menu bar item. C# owns the menu's content: it is asked for it each time the menu opens
/// (menuNeedsUpdate), and answers with jv_status_set_menu.
final class StatusController: NSObject, NSMenuDelegate {
    static var shared: StatusController?

    let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
    let menu = NSMenu()
    let willOpen: VoidCallback
    let clicked: IntCallback

    init(willOpen: VoidCallback, clicked: IntCallback) {
        self.willOpen = willOpen
        self.clicked = clicked
        super.init()
        menu.delegate = self
        menu.autoenablesItems = false
        item.menu = menu
        if let button = item.button {
            let image = NSImage(systemSymbolName: "cursorarrow.rays", accessibilityDescription: "Jarvis")
            image?.isTemplate = true
            button.image = image
        }
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        willOpen()
    }

    func setMenu(_ entries: [[String: Any]]) {
        menu.removeAllItems()
        for entry in entries {
            if entry["separator"] as? Bool == true {
                menu.addItem(.separator())
                continue
            }

            let item = NSMenuItem(title: entry["title"] as? String ?? "", action: #selector(pick(_:)), keyEquivalent: "")
            item.target = self
            item.tag = entry["id"] as? Int ?? -1
            item.isEnabled = entry["enabled"] as? Bool ?? true
            item.state = entry["checked"] as? Bool == true ? .on : .off
            item.indentationLevel = entry["indent"] as? Int ?? 0
            menu.addItem(item)
        }
    }

    @objc func pick(_ sender: NSMenuItem) {
        clicked(Int32(sender.tag))
    }
}

@_cdecl("jv_status_create")
public func jv_status_create(_ willOpen: VoidCallback, _ clicked: IntCallback) {
    onMain { StatusController.shared = StatusController(willOpen: willOpen, clicked: clicked) }
}

@_cdecl("jv_status_set_menu")
public func jv_status_set_menu(_ entries: UnsafePointer<CChar>?) {
    guard let list = parseJSON(entries) as? [[String: Any]] else { return }
    onMain { StatusController.shared?.setMenu(list) }
}

@_cdecl("jv_status_set_tooltip")
public func jv_status_set_tooltip(_ text: UnsafePointer<CChar>?) {
    let tip = string(text)
    onMain { StatusController.shared?.item.button?.toolTip = tip }
}

@_cdecl("jv_status_set_dimmed")
public func jv_status_set_dimmed(_ dimmed: Int32) {
    onMain { StatusController.shared?.item.button?.appearsDisabled = dimmed != 0 }
}
