import AppKit
import SwiftUI

/// The drawer that drops down from Tally's menu bar icon to offer recording a call. SwiftUI's
/// `MenuBarExtra` can't be opened from code, so it's a borderless panel of its own: it never takes focus from the
/// call app, shows over a full-screen call, and asks to be left out of screen shares (`sharingType`).
@MainActor final class Drawer {
    private var panel: NSPanel?
    private var showing = 0  // counts showings: a fold scheduled for an older one leaves a newer one be
    /// Shown, and folded back after `folds`: an offer waits for an answer a while, a word that it's recording needn't.
    /// `unanswered` runs when it folds by itself, not when it's hidden by an answer.
    func show(_ content: some View, announcing text: String, folds: Duration, unanswered: (() -> Void)? = nil) {
        hide(animated: false)
        let hosting = FirstClickHosting(rootView: AnyView(content))
        let size = hosting.fittingSize
        let panel = DrawerPanel(contentRect: NSRect(origin: .zero, size: size), styleMask: [.borderless, .nonactivatingPanel],
                                backing: .buffered, defer: false)
        panel.contentView = hosting
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.level = .statusBar
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        panel.hidesOnDeactivate = false
        panel.sharingType = .none
        Self.statusItemWindow()?.sharingType = .none

        let final = Self.frame(for: size)
        panel.setFrame(final.offsetBy(dx: 0, dy: 8), display: false)
        panel.alphaValue = 0
        panel.orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.2
            panel.animator().setFrame(final, display: true)
            panel.animator().alphaValue = 1
        }
        self.panel = panel
        NSAccessibility.post(element: NSApp as Any, notification: .announcementRequested,
                             userInfo: [.announcement: text, .priority: NSAccessibilityPriorityLevel.high.rawValue])
        showing += 1
        let shown = showing
        Task { @MainActor in
            try? await Task.sleep(for: folds)
            if shown == self.showing, self.panel != nil {
                self.hide()
                unanswered?()
            }
        }
    }

    func hide(animated: Bool = true) {
        guard let panel else { return }
        self.panel = nil
        guard animated else { return panel.orderOut(nil) }
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.2
            panel.animator().setFrame(panel.frame.offsetBy(dx: 0, dy: 8), display: true)
            panel.animator().alphaValue = 0
        } completionHandler: {
            panel.orderOut(nil)
        }
    }

    /// The window that holds Tally's menu bar icon. Found by its class: the one fragile part, with a fallback.
    static func statusItemWindow() -> NSWindow? {
        NSApp.windows.first { String(describing: type(of: $0)) == "NSStatusBarWindow" && $0.isVisible }
    }

    /// Centred under the icon, its top at the bottom of the menu bar and kept on screen; or, with the icon hidden
    /// (by the notch, say), the top right of the screen with the menu bar.
    private static func frame(for size: NSSize) -> NSRect {
        let icon = statusItemWindow()?.frame
        let screen = NSScreen.screens.first { icon.map($0.frame.intersects) ?? false } ?? NSScreen.main ?? NSScreen.screens[0]
        let visible = screen.visibleFrame
        let top = icon.map { $0.minY - 4 } ?? visible.maxY - 8
        let x = icon.map { $0.midX - size.width / 2 } ?? visible.maxX - size.width - 8
        return NSRect(x: min(max(x, visible.minX + 8), visible.maxX - size.width - 8), y: top - size.height, width: size.width, height: size.height)
    }
}

/// Never key: clicking Record leaves the call app with the keyboard.
private final class DrawerPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// A click on a panel that isn't key goes straight to the button.
private final class FirstClickHosting: NSHostingView<AnyView> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

/// What the drawer says. Offering: which app is on the microphone, Record, and Not Now. In auto mode, that Tally
/// is recording this call, and Don't Record.
struct CallOffer: View {
    let model: AppModel
    let app: String?
    let recording: Bool

    var body: some View {
        DrawerCard(title: recording ? "Tally is recording this call" : "Record this call with Tally?",
                   text: app.map { "\($0) is using your microphone." } ?? "An app is using your microphone.") {
            if recording {
                Button("Don't Record") { model.dontRecord() }
            } else {
                Button("Record") { Task { await model.recordCall() } }.buttonStyle(.borderedProminent).tint(.red)
                Button("Not Now") { model.notNow() }
            }
        }
    }
}

/// A call started while the last one's recording was stopped, and can still go on: the same meeting (one
/// recording), or a new one (the last is sent, and this one recorded on its own).
struct SameMeeting: View {
    let model: AppModel
    let app: String?

    var body: some View {
        DrawerCard(title: "A new call started",
                   text: "\(app ?? "An app") is using your microphone. Is it the meeting Tally just recorded?") {
            Button("Same Meeting") { model.sameMeeting() }.buttonStyle(.borderedProminent).tint(.red)
            Button("New Meeting") { Task { await model.newMeeting() } }
        }
    }
}

/// What every drawer looks like: Tally's icon, a title, a sentence, and the buttons that answer it.
private struct DrawerCard<Buttons: View>: View {
    let title: String
    let text: String
    @ViewBuilder let buttons: Buttons

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 32, height: 32)  // Tally's own icon: plainly Tally
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 8) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).font(.headline)
                    Text(text).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                HStack(spacing: 8) { buttons }
                    .controlSize(.regular)
            }
        }
        .padding(14)
        .frame(width: 300, alignment: .leading)
        .background(.regularMaterial, in: .rect(cornerRadius: 12))
    }
}
