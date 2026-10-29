# Tally

Tally turns a meeting recording into a transcript with the speakers' real names, a summary you can read in 30 seconds, and action items.

You record on your Mac or Windows PC, in any call app or in a room. A few minutes later the summary is there. You confirm who spoke once, and Tally knows their voice from then on.

It runs in your own AWS account. People sign in with their own accounts, and each person's meetings, people and voices are theirs alone. It's for a person or a small team comfortable with AWS.

![A meeting in Tally on a Mac](docs/images/mac-meeting.png)

<p>
  <img src="docs/images/mac-menu.png" alt="Tally's menu bar panel on a Mac" width="300">
  <img src="docs/images/windows-meeting.png" alt="A meeting in Tally on Windows" width="520">
</p>

**Cost:** about $0.16 of GPU for an hour-long meeting, scaling to zero between meetings, plus the model's tokens and little for the rest ([Costs](#costs)).

**Privacy:** recordings, transcripts and voiceprints stay in your AWS account. The summary is written by a model on Amazon Bedrock, through the inference profile you choose. Tell the people in a meeting that you're recording it: in many places the law requires it.

Someone who was sent a link to a Tally site only needs that page. This README is for setting up a Tally of your own:

1. [Ask for GPU quota](#what-you-need) and get a Hugging Face token.
2. [Fill in `config.json`](#configure).
3. [Deploy](#deploy): `make deploy`, `make image`, `make publish-site`.
4. Add people, and install the app from your site.

## How it works

- **The apps** record your microphone and everything the computer plays: the Mac's from the menu bar, the Windows app from the notification area. They send a recording once you're signed in, and show the summary when it's ready. You can edit a summary, file meetings into folders, search them and keep files with them. You can also keep a meetings folder, a copy of your meetings as Markdown and JSON in a folder on your computer, for local agents. [docs/parity.md](docs/parity.md) compares the two apps.
- **AWS does the rest,** in one CDK stack. An upload starts a GPU job on AWS Batch: Whisper transcribes, pyannote finds who spoke when, and known voices are matched. A Strands agent on Amazon Bedrock then works out who is who and writes the summary. An API on one Lambda serves the apps, S3 and DynamoDB keep everything, and CloudFront serves the install page and an admin dashboard. The architecture is in [docs/architecture.md](docs/architecture.md).
- **Sign-in** is an OIDC provider you already have, or a Cognito user pool the stack makes for you.

## What you need

- **An AWS account and a region** with Batch, G-type GPU instances and Bedrock. Everything runs in that region, except the site's certificate, which CloudFront needs in us-east-1.
- **GPU quota, asked for first,** since an increase can take a day. In Service Quotas, ask for at least 32 vCPUs of "Running On-Demand G and VT instances" in your region: Batch may launch a g6.8xlarge when smaller types have no capacity. To use all of `gpu.maxVcpus`, ask for that many.
- **Bedrock access** to the model in `modelId`.
- **A Hugging Face token.** Accept the terms of [pyannote/speaker-diarization-community-1](https://huggingface.co/pyannote/speaker-diarization-community-1), then make a read token and store it in Secrets Manager under the name in `hfTokenSecret`:
  ```
  aws secretsmanager create-secret --name tally/hf-token --secret-string "<token>" --region <region>
  ```
- **To deploy:** a clone of this repository, with git 2.37 or newer, `jq`, `curl`, `shasum`, `uv`, Node.js and the AWS CLI, on a Mac or Linux. No local Docker is needed, since CodeBuild builds the pipeline's image.
- **To build the apps yourself:** a Mac on macOS 26 with the Command Line Tools, or Windows 11 with the .NET 10 SDK. People who install from the site need neither.
- **Optional:** a Route 53 hosted zone for the site's domain, and your own OIDC provider.

## Configure

```
cp config.example.json config.json
```

`config.json` is gitignored, and nothing deployment-specific lives anywhere else.

| Setting | What it is |
|---|---|
| `account`, `region` | Where the stack goes |
| `stackName` | The stack's name, `Tally` |
| `timezone` | Your time zone (`Europe/Stockholm`), for the warm hours and the dashboard's "today" |
| `domain` | Optional: `{name, hostedZoneId}`, the site's domain and its Route 53 hosted zone. Without it, the site is at its CloudFront address |
| `auth` | Optional: your OIDC provider (below). Without it, the stack makes a Cognito user pool |
| `bundleId` | The Mac app's bundle id (`com.example.tally`). Keep it once people use the app, since macOS ties permissions and the saved sign-in to it |
| `modelId` | The Bedrock model, as a cross-region inference profile |
| `modelPrices` | The model's prices in dollars per million tokens (`input`, `output`, `cache_read`, `cache_write`), which the dashboard uses |
| `hfTokenSecret` | The Secrets Manager name of the Hugging Face token |
| `sharedVoices` | Optional, off unless `true`. Read the note below first |
| `crashEmail` | Optional: who gets an email when an app sends a crash report |
| `gpu` | `instanceTypes` (single-GPU types Batch may launch), `maxVcpus`, and `warm` (below) |
| `tags` | Optional: `{"key": "value"}` tags put on everything, beside `project=tally` |

**Another region than eu-north-1:** change `region`, the `us.`, `eu.` or `global.` prefix of `modelId`, `modelPrices`, `timezone` and `gpu.instanceTypes`. Check which types the region has with `aws ec2 describe-instance-type-offerings --region <region>`.

**`gpu.warm`** keeps a GPU running, so a meeting starts within seconds. Without it, an instance starts when a meeting arrives (a 6–7 minute cold start) and stops after. `{"vcpus": 4}` keeps one always on, and `{"vcpus": 4, "from": 7, "to": 21}` keeps one from 07:00 to 21:00 in `timezone`.

**`sharedVoices`:** with this on, every meeting is also matched against the people everyone else in your deployment has named, with their voiceprints. It never reaches beyond your deployment: nothing is shared with Tally's maintainer or with anyone else's Tally. A match becomes the owner's own person, shown as a suggestion until they confirm it, and nothing of anyone else's changes. Voiceprints are biometric data, and this shares the voices of people who never used Tally. Before you turn it on, be sure you have a legal basis for it (in the EU, under GDPR) and that your users know.

**`modelPrices`:** look them up for your region in the AWS Price List. A `eu.` or `us.` profile pays the "standard" rates, and a `global.` profile the "global" ones. For `cache_write`, use the plain rate, not the `1h` one.
```
aws pricing get-products --region us-east-1 --service-code AmazonBedrockFoundationModels \
  --filters Type=TERM_MATCH,Field=regionCode,Value=<region> --output json \
  | jq -r '.PriceList[] | fromjson | select(.product.attributes | tostring | test("Sonnet 5.5")) |
      [.product.attributes.usagetype, (.terms.OnDemand[].priceDimensions[].pricePerUnit.USD)] | @tsv'
```

**Your own OIDC provider:** set `auth` to `name` (what people know it by, so the app says "Sign In to `<name>`"), `issuer`, `clientId` and `adminClaim`. `adminClaim` is the token claim that lists a person's roles, as a dotted path (`resource_access.tally.roles` in Keycloak), and whoever has `admin` there sees the dashboard. The client must be public, with authorization code and PKCE S256, redirect to `tally://callback` and `https://<site>/admin.html`, and allow the web origin `https://<site>`. Its access tokens must name the client as their audience (`aud`), and their `sub` must be letters, digits, `-` and `_`.

Tally has been used with Keycloak and Cognito. Okta and Entra ID need scopes Tally doesn't ask for yet (`offline_access` in Okta, an API scope in Entra ID).

## Deploy

Once per account and region, CDK needs its bootstrap stack (us-east-1 is for the site's certificate, when there's a `domain`):
```
cd cdk && npx -y aws-cdk@latest bootstrap aws://<account>/<region> aws://<account>/us-east-1 && cd ..
```

Then:
```
make deploy        # the stacks
make image         # start building the pipeline's image in CodeBuild
make publish-site  # the install page, both install scripts, the admin dashboard and both apps' source
```

`make image` starts the build, prints its id and returns. The build takes about 15 minutes. A meeting sent before it succeeds waits, and fails after 3 hours. To check on it:
```
aws codebuild batch-get-builds --region <region> --ids <id> --query 'builds[0].buildStatus'
```

**With Cognito**, add people yourself, since nobody can sign up on their own. Those in the `admin` group see the dashboard. The pool's id is the last part of the stack's `Issuer` output:
```
aws cloudformation describe-stacks --stack-name <stackName> --region <region> --query "Stacks[0].Outputs[?OutputKey=='Issuer'].OutputValue" --output text
aws cognito-idp admin-create-user --user-pool-id <pool> --username someone@example.com \
  --user-attributes Name=email,Value=someone@example.com Name=email_verified,Value=true --region <region>
aws cognito-idp admin-add-user-to-group --user-pool-id <pool> --username someone@example.com --group-name admin --region <region>
```
Cognito emails each person a temporary password.

**Installing the apps:** open the site (the stack's `SiteUrl` output) and run its command, `curl -fsSL https://<site>/install.sh | bash` in a Mac's Terminal, or `irm https://<site>/install.ps1 | iex` in Windows PowerShell. It builds Tally on that computer, and the app then offers each new version. The dashboard is at `https://<site>/admin.html`.

On Windows, Tally won't install where Smart App Control or an organisation's app control (AppLocker, App Control for Business) is on: Windows blocks apps built on the PC, and Tally is one. The installer checks and says so ([troubleshooting](docs/troubleshooting.md#smart-app-control-or-app-control-blocks-tally)).

**Cost tracking:** the dashboard needs Cost Explorer turned on, or it won't load. Turn it on once in the Billing console. The dashboard counts the GPU by its instance types in the region, so other instances of those types in the account count too. It counts everything else by the `project=tally` tag, which `make deploy` puts on everything. About a day after the first deploy, make the tag an active cost allocation tag. In an AWS Organization, only the management account can do this:
```
aws ce update-cost-allocation-tags-status --cost-allocation-tags-status TagKey=project,Status=Active
```

## Update

```
git pull
cd cdk && npx -y aws-cdk@latest diff --all && cd ..
make deploy && make image && make publish-site
```

Read the diff before you deploy. It must show no replacement or removal of the data bucket, the tables, the KMS key, the API or its authorizer. `make publish-site` refuses while `apps/mac/`, `apps/windows/` or `site/` have uncommitted changes, since each app's version is its commit. Installed apps then offer the update.

With warm hours (`gpu.warm` with `from` and `to`), a deploy that changes the GPU launch template resets the minimum to 0 until the next schedule. In warm hours, set it back. The environment's name starts with `OnDemand`.
```
aws batch update-compute-environment --compute-environment <environment> --compute-resources minvCpus=<gpu.warm.vcpus> --region <region>
```

## Remove

`cd cdk && npx -y aws-cdk@latest destroy --all` removes the stacks. The data bucket, the tables and the KMS key are kept, so no meeting is lost by accident, and so are the site's bucket, the image repository, the Cognito pool and the logs. They keep costing a little. To be rid of them, delete them by hand in the S3, DynamoDB, ECR, Cognito and CloudWatch consoles, and schedule the KMS key's deletion, which takes 7 to 30 days.

## Costs

Measured on the maintainer's deployment in eu-north-1 ([docs/research.md](docs/research.md)):
- **The GPU:** about $0.16 for an hour-long meeting on a g6.xlarge, scaling to zero. That's about 11 minutes of instance time, 7 of them the cold start.
- **A warm instance** (a g6.4xlarge, which Batch picks) from 07:00 to 21:00 costs about $590 a month, or about $1,025 always on.
- **The model:** the agent's tokens are kept with each meeting, and the dashboard prices them at `modelPrices`.
- **The rest** (S3, DynamoDB, Lambda, API Gateway, CloudFront, KMS) is small.

## Limitations

- Tally transcribes English and Swedish.
- A recording stops at 4 hours.
- On Windows, Smart App Control or an organisation's app control blocks Tally, since it's built on the PC and not signed ([troubleshooting](docs/troubleshooting.md#smart-app-control-or-app-control-blocks-tally)).
- On a Mac, the keychain asks once after each install whether Tally may read its sign-in, since a locally signed app has no team ID.

## Develop

| Command | What it does |
|---|---|
| `make test` | The pipeline's and the API's tests, and that the shell scripts parse; no AWS |
| `make app-test` | The Mac app's tests |
| `make windows-test` | The Windows app's Core tests (the .NET 10 SDK, on any OS) |
| `make settings`, `make windows-settings` | The apps' settings, read from the deployed stack ([docs/windows.md](docs/windows.md)) |
| `make signing-cert` | Once per Mac: the "Tally Local" certificate the app is signed with |
| `make app` | Build the Mac app (needs AWS credentials) |
| `make install`, `make run` | Install it in /Applications (never while it's recording), and open it |

The Mac app is in `apps/mac/` and the Windows app in `apps/windows/`. The architecture is in [docs/architecture.md](docs/architecture.md), and the details in [docs/macos.md](docs/macos.md), [docs/windows.md](docs/windows.md), [docs/api.md](docs/api.md), [docs/data.md](docs/data.md), [docs/agent.md](docs/agent.md) and [docs/troubleshooting.md](docs/troubleshooting.md). The evals, and how to run them, are in [evals/README.md](evals/README.md). Agents working on the code start with [AGENTS.md](AGENTS.md).

## Contributing

Issues and pull requests are welcome: [CONTRIBUTING.md](CONTRIBUTING.md) says how, and [SECURITY.md](SECURITY.md) how to report a vulnerability privately. Everyone follows the [code of conduct](CODE_OF_CONDUCT.md).

## Licence

Tally is source-available under the [PolyForm Shield License 1.0.0](LICENSE.md): you may use, change and share it for any purpose, at work too, but not to offer a product that competes with it. That makes it not open source in the OSI sense. The models the pipeline downloads have their own licences, credited in [NOTICE.md](NOTICE.md).
