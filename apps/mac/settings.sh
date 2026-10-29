#!/bin/bash
# The app's settings (tally.json, printed): where it signs in, which API it talks to, and where its updates come
# from. Read from the deployed stack's outputs, the sign-in provider's discovery document, and config.json (the
# bundle id, and the name people know the sign-in by: `auth.name`, else Tally's own). `make app` and `make site`
# build with it.
#
#   apps/mac/settings.sh <config.json>
set -euo pipefail
config=$(cat "$1")
outputs=$(aws cloudformation describe-stacks --region "$(jq -r .region <<<"$config")" --stack-name "$(jq -r .stackName <<<"$config")" \
  --query "Stacks[0].Outputs" --output json | jq 'map({(.OutputKey): .OutputValue}) | add')
discovery=$(curl -fsS "$(jq -r .Issuer <<<"$outputs")/.well-known/openid-configuration")
jq -n --argjson o "$outputs" --argjson d "$discovery" --arg bundle "$(jq -r .bundleId <<<"$config")" \
  --arg name "$(jq -r '.auth.name // "Tally"' <<<"$config")" --arg region "$(jq -r .region <<<"$config")" \
  '{issuer: $o.Issuer, clientId: $o.ClientId, authorization: $d.authorization_endpoint, token: $d.token_endpoint,
    signInName: $name, api: $o.ApiUrl, site: $o.SiteUrl, bundleId: $bundle, region: $region}'
