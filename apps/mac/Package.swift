// swift-tools-version:6.2
import PackageDescription

// Built with the Command Line Tools alone (no Xcode): `make app` wraps the binary as Tally.app.
// Swift 5 language mode, because Core Audio's callbacks don't fit Swift 6's strict concurrency checks without ceremony.
let package = Package(
    name: "Tally",
    platforms: [.macOS(.v26)],
    targets: [
        .executableTarget(name: "Tally", swiftSettings: [.swiftLanguageMode(.v5)]),
        .testTarget(name: "TallyTests", dependencies: ["Tally"], swiftSettings: [.swiftLanguageMode(.v5)]),
    ]
)
