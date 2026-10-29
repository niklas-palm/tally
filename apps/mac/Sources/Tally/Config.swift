import Foundation

/// Where Tally signs in and which API it talks to. Read from `tally.json` in the app bundle, which apps/mac/settings.sh
/// writes from the deployed stack and the sign-in provider's discovery document, so nothing deployment-specific
/// lives in the code.
struct Config: Decodable {
    let authorization: URL  // the provider's authorization endpoint, where the sign-in sheet opens
    let token: URL  // and its token endpoint
    let clientId: String
    let signInName: String  // what people know the sign-in by: "Sign In to <name>"
    let api: URL
    let site: URL?  // the install page, where updates come from
    let version: String?  // this build's, from the install page's tarball; nil for a build from the repository

    static let shared: Config = {
        guard let url = Bundle.main.url(forResource: "tally", withExtension: "json"),
              let config = try? JSONDecoder().decode(Config.self, from: Data(contentsOf: url))
        else { fatalError("tally.json is missing from the app bundle: build with `make app`") }
        return config
    }()
}
