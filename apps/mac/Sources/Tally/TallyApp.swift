import SwiftUI

/// Tally lives in the menu bar (record, stop, what's on its way) with one window for meetings and people.
///
/// No `@State` anywhere: its macro ships only with Xcode, and Tally builds with the Command Line Tools. View state
/// lives in the `@Observable` AppModel instead (Observation's macros do come with the toolchain).
@main
struct TallyApp: App {
    @NSApplicationDelegateAdaptor private var delegate: AppDelegate
    private var model: AppModel { delegate.model }

    var body: some Scene {
        MenuBarExtra {
            MenuView().environment(model)
        } label: {
            MenuBarLabel().environment(model)
        }
        .menuBarExtraStyle(.window)

        Window("Tally", id: "main") {
            MainWindow().environment(model)
                .onOpenURL { url in  // tally://meeting/<id>, from a "Summary ready" notification
                    if url.host() == "meeting" { model.open(url.lastPathComponent) }
                }
        }
        // A friendly size, centred, on any screen: 980 × 640 at most, and never more than 85% wide or 80% tall (a
        // MacBook's screen is about 950 points tall). Not restored from last time, so every open starts the same.
        .defaultWindowPlacement { _, context in
            let screen = context.defaultDisplay.visibleRect.size
            return WindowPlacement(.center, size: CGSize(width: min(980, screen.width * 0.85), height: min(640, screen.height * 0.8)))
        }
        .restorationBehavior(.disabled)
        .handlesExternalEvents(matching: ["meeting"])
        // The first launch opens the window, with a welcome; after that Tally starts in the menu bar only.
        .defaultLaunchBehavior(UserDefaults.standard.bool(forKey: "welcomed") ? .suppressed : .presented)
        .commands {
            CommandGroup(after: .newItem) {
                Button(model.recorder == nil ? "Start Recording" : "Stop Recording") { Task { await model.toggleRecording() } }
                    .keyboardShortcut("r")
                Button("New Folder") { Task { await model.newFolder() } }.keyboardShortcut("n", modifiers: [.command, .shift])
            }
        }
    }
}

/// Quitting while recording ends the recording, so it's kept: the file is closed at once, and Tally saves and sends
/// it at the next launch, as after a crash. Encoding an hour first would hold the quit for 20 seconds.
@MainActor final class AppDelegate: NSObject, NSApplicationDelegate {
    let model = AppModel()

    /// Tally's menu bar icon is left out of screen shares, if macOS lets it be.
    func applicationDidFinishLaunching(_ notification: Notification) {
        DispatchQueue.main.async { Drawer.statusItemWindow()?.sharingType = .none }
        Task { await model.checkLoginItem() }  // known before the menu first opens
        // A right-click (or Control-click) on the menu bar icon: a plain menu, as pro apps have, to stop without the panel
        NSEvent.addLocalMonitorForEvents(matching: [.rightMouseDown, .leftMouseDown]) { [weak self] event in
            guard let self, let window = event.window, window === Drawer.statusItemWindow(),
                  event.type == .rightMouseDown || event.modifierFlags.contains(.control), let view = window.contentView else { return event }
            self.quickMenu().popUp(positioning: nil, at: NSPoint(x: 0, y: -4), in: view)
            return nil
        }
    }

    private func quickMenu() -> NSMenu {
        let menu = NSMenu()
        let recording = model.recorder != nil
        menu.addItem(Action(recording ? "Stop Recording" : "Start Recording") { [model] in Task { await model.toggleRecording() } })
        menu.addItem(.separator())
        menu.addItem(Action("Open Tally") { [model] in model.openMain?() })
        menu.addItem(.separator())
        menu.addItem(Action(recording ? "Stop and Quit" : "Quit Tally") { NSApp.terminate(nil) })
        return menu
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        model.endRecording()
        return .terminateNow
    }
}

/// The menu bar icon: Tally's "T.", its full stop red while recording.
private struct MenuBarLabel: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow

    private static let idle = Mark.image(label: "Tally")
    private static let armed = Mark.image(dot: .ring, label: "Tally, recording calls automatically")
    private static let recording = Mark.image(red: true, label: "Tally is recording")
    private static let update = Mark.image(badge: true, label: "Tally: an update is ready")
    private static let armedUpdate = Mark.image(dot: .ring, badge: true, label: "Tally: an update is ready")

    private static let noMic: NSImage = {
        let image = NSImage(systemSymbolName: "mic.slash.fill", accessibilityDescription: "Not recording: no microphone")!
            .withSymbolConfiguration(.init(paletteColors: [.systemOrange]))!
        image.isTemplate = false
        return image
    }()

    var body: some View {
        mark.onAppear {  // always there, so it hands the right-click menu the window
            model.openMain = { [openWindow] in openWindow(id: "main"); NSApp.activate() }
        }
    }

    @ViewBuilder private var mark: some View {
        if let recorder = model.recorder, model.stoppedByCall == nil {
            // No time beside it: a wider icon is the first macOS hides when the menu bar is full.
            // The time is still read, so this redraws every second and a lost mic shows without opening the menu.
            let _ = model.elapsed
            Image(nsImage: recorder.levels.lost ? Self.noMic : Self.recording)
        } else if model.update != nil {
            Image(nsImage: model.callMode == .auto ? Self.armedUpdate : Self.update)
        } else {
            Image(nsImage: model.callMode == .auto ? Self.armed : Self.idle)
        }
    }
}

/// The "T." for the menu bar, from the icon's own shapes (apps/mac/icon.svg's 64-unit grid), so the two always match.
enum Mark {
    enum Dot { case solid, ring }

    /// `red`: the full stop in red, recording. `badge`: the accent-coloured update dot. Without either it's a
    /// template, which macOS draws in the menu bar's own colour; with one, the T is drawn in that colour itself.
    static func image(dot: Dot = .solid, red: Bool = false, badge: Bool = false, label: String) -> NSImage {
        let scale: CGFloat = 16 / 36  // the mark is 36 units tall: 16 points in the menu bar
        let size = NSSize(width: 40.5 * scale + (badge ? 3 : 0), height: 16)
        let template = !red && !badge
        let image = NSImage(size: size, flipped: true) { _ in
            let transform = NSAffineTransform()
            transform.scale(by: scale)
            transform.translateX(by: -12, yBy: -14)  // the mark's corner in the grid
            transform.concat()
            (template ? NSColor.black : NSColor.labelColor).set()
            NSBezierPath(rect: NSRect(x: 12, y: 14, width: 32, height: 9.5)).fill()
            NSBezierPath(rect: NSRect(x: 23, y: 23.5, width: 10, height: 26.5)).fill()
            if red { NSColor(srgbRed: 0xe0 / 255, green: 0x38 / 255, blue: 0x3e / 255, alpha: 1).set() }
            switch dot {
            case .solid: NSBezierPath(ovalIn: NSRect(x: 42.5, y: 40, width: 10, height: 10)).fill()
            case .ring:
                let ring = NSBezierPath(ovalIn: NSRect(x: 43.6, y: 41.1, width: 7.8, height: 7.8))
                ring.lineWidth = 2.2
                ring.stroke()
            }
            if badge {
                NSColor.controlAccentColor.setFill()
                NSBezierPath(ovalIn: NSRect(x: 49.5, y: 14, width: 9.5, height: 9.5)).fill()  // top right, beside the bar
            }
            return true
        }
        image.isTemplate = template
        image.accessibilityDescription = label
        return image
    }
}

/// A menu item that runs a closure: the right-click menu's, which AppKit builds with targets and actions.
private final class Action: NSMenuItem {
    private let run: () -> Void

    init(_ title: String, run: @escaping () -> Void) {
        self.run = run
        super.init(title: title, action: #selector(chosen), keyEquivalent: "")
        target = self
    }

    required init(coder: NSCoder) { fatalError("made in code only") }

    @objc private func chosen() { run() }
}
