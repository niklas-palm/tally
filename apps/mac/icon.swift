// Tally's icons, from apps/mac/icon.svg (the "T." on the install page's dark, full-bleed): `make icon` runs this, and
// the results are committed, so a build needs no drawing tools.
//   apps/mac/Tally.icns          the app's icon: the mark in macOS's rounded square, with its margin
//   site/apple-touch-icon.png    180 × 180, full-bleed: the install page's, and the account page's tile
//
//   swift apps/mac/icon.swift   (from the repository's root)
import AppKit

let svg = NSImage(contentsOf: URL(fileURLWithPath: "apps/mac/icon.svg"))!

/// The mark at `pixels` square: full-bleed, or in the rounded square of macOS's icon grid (824 of 1024, radius 185).
func render(_ pixels: Int, rounded: Bool) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let size = CGFloat(pixels)
    var frame = NSRect(x: 0, y: 0, width: size, height: size)
    if rounded {
        frame = frame.insetBy(dx: size * 100 / 1024, dy: size * 100 / 1024)
        NSBezierPath(roundedRect: frame, xRadius: size * 185 / 1024, yRadius: size * 185 / 1024).addClip()
    }
    svg.draw(in: frame)
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

let iconset = URL(fileURLWithPath: NSTemporaryDirectory()).appending(path: "Tally.iconset")
try? FileManager.default.removeItem(at: iconset)
try! FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
for points in [16, 32, 128, 256, 512] {
    try! render(points, rounded: true).write(to: iconset.appending(path: "icon_\(points)x\(points).png"))
    try! render(points * 2, rounded: true).write(to: iconset.appending(path: "icon_\(points)x\(points)@2x.png"))
}
let iconutil = Process()
iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
iconutil.arguments = ["-c", "icns", iconset.path, "-o", "apps/mac/Tally.icns"]
try! iconutil.run()
iconutil.waitUntilExit()
try! render(180, rounded: false).write(to: URL(fileURLWithPath: "site/apple-touch-icon.png"))
print("apps/mac/Tally.icns and site/apple-touch-icon.png from apps/mac/icon.svg")
