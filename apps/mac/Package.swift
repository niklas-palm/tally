// swift-tools-version:6.1
import PackageDescription

// Built with the Command Line Tools alone (no Xcode): `make app` wraps the binary as Tally.app.
// Swift 5 language mode, because Core Audio's callbacks don't fit Swift 6's strict concurrency checks without ceremony.
let package = Package(
    name: "Tally",
    // 15.6 in fact (Info.plist, site/install.sh): the oldest macOS whose Software Update offers Swift 6.1's tools
    platforms: [.macOS(.v15)],
    targets: [
        .executableTarget(name: "Tally", swiftSettings: [.swiftLanguageMode(.v5)]),
        .testTarget(name: "TallyTests", dependencies: ["Tally"], swiftSettings: [.swiftLanguageMode(.v5)]),
    ]
)
