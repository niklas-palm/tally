# The Windows app's settings (tally.json): where it signs in, which API it talks to, and where its updates come from.
# The same as apps/mac/settings.sh writes for the Mac, for building on Windows without bash: read from the deployed stack's
# outputs, the sign-in provider's discovery document, and config.json (the bundle id, and the name people know the
# sign-in by: auth.name, else Tally's own), so nothing is copied by hand. Needs the AWS CLI with credentials.
#
#   powershell -ExecutionPolicy Bypass -File apps\windows\settings.ps1 [config.json] [out]
param(
    [string]$Config = (Join-Path $PSScriptRoot '..\..\config.json'),
    [string]$Out = (Join-Path $PSScriptRoot '.build\tally.json')
)
$ErrorActionPreference = 'Stop'
$config = Get-Content -Raw $Config | ConvertFrom-Json
$outputs = @{}
$described = aws cloudformation describe-stacks --region $config.region --stack-name $config.stackName --query 'Stacks[0].Outputs' --output json
if ($LASTEXITCODE -ne 0) { throw "Couldn't read the stack's outputs: is the AWS CLI signed in to $($config.account)?" }
# In parentheses: Windows PowerShell 5.1 would pass the whole array on as one object
($described | Out-String | ConvertFrom-Json) | ForEach-Object { $outputs[$_.OutputKey] = $_.OutputValue }
$discovery = Invoke-RestMethod "$($outputs.Issuer)/.well-known/openid-configuration"
$name = if ($config.auth -and $config.auth.name) { $config.auth.name } else { 'Tally' }
New-Item (Split-Path $Out) -ItemType Directory -Force | Out-Null
$json = [ordered]@{
    issuer = $outputs.Issuer; clientId = $outputs.ClientId
    authorization = $discovery.authorization_endpoint; token = $discovery.token_endpoint
    signInName = $name; api = $outputs.ApiUrl; site = $outputs.SiteUrl; bundleId = $config.bundleId; region = $config.region
} | ConvertTo-Json
# Without a byte-order mark, which Windows PowerShell's Set-Content -Encoding UTF8 writes and Tally's JSON reader refuses
[IO.File]::WriteAllText((Join-Path (Resolve-Path (Split-Path $Out)) (Split-Path $Out -Leaf)), $json)
"Wrote $Out"
