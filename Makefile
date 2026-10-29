# Tally's everyday commands. AWS commands use the machine's credentials, in config.json's account and region
# (the README says what each setting is).

STACK := $(shell jq -r .stackName config.json)
REGION := $(shell jq -r .region config.json)
output = $(shell aws cloudformation describe-stacks --region $(REGION) --stack-name $(STACK) --query "Stacks[0].Outputs[?OutputKey=='$(1)'].OutputValue" --output text)
export JSII_RUNTIME_PACKAGE_CACHE = disabled

.PHONY: test deploy image settings app app-test run install signing-cert icon windows-settings windows-test site publish-site

test:  # the pipeline and the API (with the pipeline's environment), and that the shell scripts parse
	for f in site/install.sh apps/mac/build.sh apps/mac/settings.sh apps/mac/signing-cert.sh; do bash -n $$f || exit 1; done
	cd pipeline && uv run ruff check . ../api && PYTHONPATH=../api uv run pytest -q . ../api

deploy:
	cd cdk && npx -y aws-cdk@latest deploy --all --require-approval never --tags project=tally $(shell jq -r '.tags // {} | to_entries[] | "--tags \(.key)=\(.value)"' config.json)

image:  # build and push the pipeline's image (deploy first: it uploads pipeline/ as the build's source)
	aws codebuild start-build --region $(REGION) --project-name $(call output,ImageBuild) --query build.id --output text

# The Mac app, built with the Command Line Tools alone (docs/macos.md), with the settings apps/mac/settings.sh reads from the
# deployed stack. Signed with the "Tally Local" certificate in the login keychain (`make signing-cert` once), so macOS
# keeps its microphone and system-audio permissions from build to build.
APP := apps/mac/.build/Tally.app

SETTINGS := apps/mac/.build/tally.json

settings:  # the app's settings, from the deployed stack (apps/mac/settings.sh)
	mkdir -p apps/mac/.build && apps/mac/settings.sh config.json > $(SETTINGS)

app: settings
	apps/mac/build.sh $(SETTINGS)

# The Windows app (docs/windows.md), with the same settings. It builds on Windows with the .NET SDK
# (`dotnet publish apps/windows/Tally/Tally.csproj -c Release -r win-x64 -p:Platform=x64`); its Core and its tests build anywhere.
windows-settings: settings
	mkdir -p apps/windows/.build && cp $(SETTINGS) apps/windows/.build/tally.json

windows-test:
	cd apps/windows && dotnet test --project Tally.Core.Tests

icon:  # apps/mac/Tally.icns and site/apple-touch-icon.png, from apps/mac/icon.svg (both committed: a build draws nothing)
	swift apps/mac/icon.swift

signing-cert:  # once per Mac: a self-signed code-signing certificate, "Tally Local", in the login keychain
	apps/mac/signing-cert.sh

LSREGISTER := /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister

install: app  # into /Applications, where Spotlight and Apps find it; never while it's recording, which quitting would cut short
	@if pgrep -x Tally >/dev/null && grep -qs '"state" *: *"recording"' "$(HOME)/Library/Application Support/Tally/recordings/"*/local.json; then \
		echo "Tally is recording: not installing until it's done."; exit 1; fi
	pkill -x Tally || true
	rm -rf /Applications/Tally.app && cp -R $(APP) /Applications/
	$(LSREGISTER) -f /Applications/Tally.app

run: install
	sleep 1 && open /Applications/Tally.app

SWIFT_FRAMEWORKS := /Library/Developer/CommandLineTools/Library/Developer/Frameworks
app-test:
	cd apps/mac && swift test --build-system native -Xswiftc -F$(SWIFT_FRAMEWORKS) -Xlinker -F$(SWIFT_FRAMEWORKS) -Xlinker -rpath -Xlinker $(SWIFT_FRAMEWORKS)

# The install page (docs/macos.md, docs/windows.md): the page, both install scripts, the admin dashboard, and each
# app's source with its settings, the Mac's named by the commit that last changed apps/mac/ or site/, the Windows app's by
# apps/windows/ or site/. Hosted in the stack's own bucket, behind its CloudFront.
SITE := site/.build
# An app's version: the date and commit that last changed these paths, and the settings' hash, so a changed setting (a
# new client, API or site) is a new version too, which installed apps update to
commit = $(shell git log -1 --format=%H -- $(1))
version = $(shell git log -1 --format=%cd-%h --date=format:%Y.%m.%d $(call commit,$(1)))-$(shell shasum $(SETTINGS) | cut -c1-6)

site: settings
	@test -z "$$(git status --porcelain -- apps/mac apps/windows site)" || { echo "Commit apps/mac/, apps/windows/ and site/ first: the version is the commit."; exit 1; }
	bash -n site/install.sh  # a script that doesn't parse would stop every install
	rm -rf $(SITE) && mkdir -p $(SITE)
	$(eval COMMIT := $(call commit,apps/mac site))
	$(eval VERSION := $(call version,apps/mac site))
	# The same bytes every time for one version (it's cached as immutable): that commit's files, with its times.
	# tally.json carries the version, so the app knows which it is and offers an update when version.json differs.
	git archive --format=tar --add-virtual-file="apps/mac/LICENSE.md:$$(git show $(COMMIT):LICENSE.md)" --add-virtual-file="apps/mac/tally.json:$$(jq --arg v $(VERSION) '. + {version: $$v}' $(SETTINGS))" $(COMMIT) apps/mac | gzip -n -9 > $(SITE)/tally-$(VERSION).tar.gz
	jq -n --arg v $(VERSION) '{version: $$v}' > $(SITE)/version.json
	# The Windows app the same way, its version from the commit that last changed apps/windows/ or site/ (docs/windows.md)
	$(eval WCOMMIT := $(call commit,apps/windows site))
	$(eval WVERSION := $(call version,apps/windows site))
	git archive --format=tar --add-virtual-file="apps/windows/LICENSE.md:$$(git show $(WCOMMIT):LICENSE.md)" --add-virtual-file="apps/windows/tally.json:$$(jq --arg v $(WVERSION) '. + {version: $$v}' $(SETTINGS))" $(WCOMMIT) apps/windows | gzip -n -9 > $(SITE)/tally-windows-$(WVERSION).tar.gz
	jq -n --arg v $(WVERSION) '{version: $$v}' > $(SITE)/windows.json
	$(eval SITE_URL := $(shell jq -r .site $(SETTINGS)))
	$(eval BUNDLE := $(shell jq -r .bundleId $(SETTINGS)))
	sed -e "s/@VERSION@/$(VERSION)/" -e "s/@SHA256@/$$(shasum -a 256 $(SITE)/tally-$(VERSION).tar.gz | cut -d' ' -f1)/" \
		-e "s|@SITE@|$(SITE_URL)|g" -e "s/@BUNDLE@/$(BUNDLE)/g" site/install.sh > $(SITE)/install.sh
	sed -e "s/@VERSION@/$(WVERSION)/" -e "s/@SHA256@/$$(shasum -a 256 $(SITE)/tally-windows-$(WVERSION).tar.gz | cut -d' ' -f1 | tr a-f A-F)/" \
		-e "s|@SITE@|$(SITE_URL)|g" site/install.ps1 > $(SITE)/install.ps1
	sed -e "s|@SITE@|$(SITE_URL)|g" -e "s/@BUNDLE@/$(BUNDLE)/g" -e "s|@SIGNIN@|$$(jq -r .signInName $(SETTINGS) | sed 's/[&|]/\\&/g')|g" \
		-e "s|@REGION@|$$(jq -r .region $(SETTINGS))|g" site/index.html > $(SITE)/index.html
	cp site/apple-touch-icon.png site/share.png $(SITE)/ && cp apps/mac/icon.svg $(SITE)/icon.svg
	sed -e "s|@AUTHORIZATION@|$$(jq -r .authorization $(SETTINGS))|" -e "s|@TOKEN@|$$(jq -r .token $(SETTINGS))|" \
		-e "s|@CLIENT@|$$(jq -r .clientId $(SETTINGS))|" -e "s|@API@|$$(jq -r .api $(SETTINGS))|" -e "s|@REGION@|$$(jq -r .region $(SETTINGS))|g" site/admin.html > $(SITE)/admin.html

publish-site: site
	$(eval BUCKET := s3://$(call output,SiteBucket))
	aws s3 cp $(SITE)/ $(BUCKET)/ --recursive --exclude "*" --include "*.tar.gz" --content-type application/gzip --cache-control "public, max-age=31536000, immutable"
	aws s3 cp $(SITE)/install.sh $(BUCKET)/install.sh --content-type "text/plain; charset=utf-8" --cache-control no-cache
	aws s3 cp $(SITE)/install.ps1 $(BUCKET)/install.ps1 --content-type "text/plain; charset=utf-8" --cache-control no-cache
	aws s3 cp $(SITE)/windows.json $(BUCKET)/windows.json --content-type application/json --cache-control no-cache
	aws s3 cp $(SITE)/index.html $(BUCKET)/index.html --content-type "text/html; charset=utf-8" --cache-control no-cache
	aws s3 cp $(SITE)/admin.html $(BUCKET)/admin.html --content-type "text/html; charset=utf-8" --cache-control no-cache
	aws s3 cp $(SITE)/icon.svg $(BUCKET)/icon.svg --content-type image/svg+xml --cache-control no-cache
	aws s3 cp $(SITE)/apple-touch-icon.png $(BUCKET)/apple-touch-icon.png --content-type image/png --cache-control no-cache
	aws s3 cp $(SITE)/share.png $(BUCKET)/share.png --content-type image/png --cache-control no-cache
	aws s3 cp $(SITE)/version.json $(BUCKET)/version.json --content-type application/json --cache-control no-cache
	aws cloudfront create-invalidation --distribution-id $(call output,SiteDistribution) --paths "/*" --query Invalidation.Id --output text
