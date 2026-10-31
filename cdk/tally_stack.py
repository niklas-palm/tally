"""Tally's AWS side, in one stack.

The apps (Mac and Windows) talk to an HTTP API, whose JWT authorizer accepts signed-in people's tokens (from the OIDC provider in
config.json's `auth`, or a Cognito user pool made here without one), and one Lambda behind it (api/); everything is
keyed by the person's `sub`. The app uploads a recording to a presigned URL, and
that upload starts the pipeline: S3 tells EventBridge, which submits a Batch job on an on-demand GPU instance,
running the pipeline's image. CodeBuild builds that image, on x86, with the models baked in. Meetings, people,
voiceprints, the owner's folders and who's used Tally live in five DynamoDB tables. The install page and the admin
dashboard are a private bucket behind CloudFront, at config.json's `domain` when there is one.
"""

import json
from pathlib import Path

import aws_cdk as cdk
from aws_cdk import aws_apigatewayv2 as apigw
from aws_cdk import aws_apigatewayv2_authorizers as authorizers
from aws_cdk import aws_apigatewayv2_integrations as integrations
from aws_cdk import aws_batch as batch
from aws_cdk import aws_certificatemanager as acm
from aws_cdk import aws_cloudfront as cloudfront
from aws_cdk import aws_cloudfront_origins as origins
from aws_cdk import aws_codebuild as codebuild
from aws_cdk import aws_cognito as cognito
from aws_cdk import aws_dynamodb as dynamodb
from aws_cdk import aws_ec2 as ec2
from aws_cdk import aws_ecr as ecr
from aws_cdk import aws_ecs as ecs
from aws_cdk import aws_events as events
from aws_cdk import aws_events_targets as targets
from aws_cdk import aws_iam as iam
from aws_cdk import aws_kms as kms
from aws_cdk import aws_lambda as lambda_
from aws_cdk import aws_logs as logs
from aws_cdk import aws_route53 as route53
from aws_cdk import aws_route53_targets as route53_targets
from aws_cdk import aws_s3 as s3
from aws_cdk import aws_s3_assets as assets
from aws_cdk import aws_scheduler as scheduler
from aws_cdk import aws_secretsmanager as secretsmanager
from aws_cdk import aws_sns as sns
from aws_cdk import aws_sns_subscriptions as subscriptions
from constructs import Construct

PIPELINE = Path(__file__).parent.parent / "pipeline"
API = Path(__file__).parent.parent / "api"


class CertificateStack(cdk.Stack):
    """The site's certificate, in us-east-1 because CloudFront only takes one from there, checked through the domain's
    hosted zone."""

    def __init__(self, scope: Construct, id: str, domain: dict, **kwargs) -> None:
        super().__init__(scope, id, **kwargs)
        zone = route53.HostedZone.from_hosted_zone_attributes(self, "Zone", hosted_zone_id=domain["hostedZoneId"], zone_name=domain["name"])
        self.certificate = acm.Certificate(self, "Site", domain_name=domain["name"], validation=acm.CertificateValidation.from_dns(zone))


class TallyStack(cdk.Stack):
    def __init__(self, scope: Construct, id: str, config: dict, certificate: acm.ICertificate | None, **kwargs) -> None:
        super().__init__(scope, id, **kwargs)
        gpu = config["gpu"]
        warm = gpu.get("warm")  # none: no GPU when nothing runs; {vcpus}: always one warm; {vcpus, from, to[, days]}: in those hours

        key = kms.Key(self, "DataKey", enable_key_rotation=True, description="Tally's meetings, transcripts and voiceprints")
        bucket = s3.Bucket(self, "Data", encryption=s3.BucketEncryption.KMS, encryption_key=key, bucket_key_enabled=True,
                           block_public_access=s3.BlockPublicAccess.BLOCK_ALL, enforce_ssl=True, event_bridge_enabled=True,
                           removal_policy=cdk.RemovalPolicy.RETAIN)
        # Every table is keyed by owner first, the signed-in person whose rows they are: one person's meetings, people
        # and voices never reach another's (docs/data.md).
        def table(name: str, sort_key: str | None = None) -> dynamodb.TableV2:
            return dynamodb.TableV2(self, name, partition_key=dynamodb.Attribute(name="owner", type=dynamodb.AttributeType.STRING),
                                    sort_key=dynamodb.Attribute(name=sort_key, type=dynamodb.AttributeType.STRING) if sort_key else None,
                                    point_in_time_recovery_specification=dynamodb.PointInTimeRecoverySpecification(point_in_time_recovery_enabled=True))

        meetings, people, voiceprints = table("MeetingsByOwner", "meeting_id"), table("PeopleByOwner", "person_id"), table("VoiceprintsByOwner", "voiceprint_id")
        folders = table("FoldersByOwner", "folder_id")  # the owner's own folders
        users = table("Users")  # who uses Tally: their name from the token, when last, on which version (the admin dashboard)

        # The image: CodeBuild on x86 builds pipeline/ (uploaded with each deploy) and pushes it; `make image` starts it.
        repository = ecr.Repository(self, "Images", lifecycle_rules=[ecr.LifecycleRule(max_image_count=5)])
        source = assets.Asset(self, "PipelineSource", path=str(PIPELINE), exclude=[".venv", "**/__pycache__", ".pytest_cache", ".ruff_cache"])
        hf_token = secretsmanager.Secret.from_secret_name_v2(self, "HfToken", config["hfTokenSecret"])
        build = codebuild.Project(
            self, "ImageBuild",
            source=codebuild.Source.s3(bucket=source.bucket, path=source.s3_object_key),
            environment=codebuild.BuildEnvironment(build_image=codebuild.LinuxBuildImage.STANDARD_7_0,
                                                   compute_type=codebuild.ComputeType.LARGE, privileged=True),
            environment_variables={"REPOSITORY": codebuild.BuildEnvironmentVariable(value=repository.repository_uri),
                                   "HF_TOKEN": codebuild.BuildEnvironmentVariable(value=config["hfTokenSecret"],
                                                                                  type=codebuild.BuildEnvironmentVariableType.SECRETS_MANAGER)},
            timeout=cdk.Duration.hours(2),
            build_spec=codebuild.BuildSpec.from_object({
                "version": "0.2",
                "phases": {
                    "pre_build": {"commands": ["aws ecr get-login-password | docker login --username AWS --password-stdin ${REPOSITORY%%/*}"]},
                    "build": {"commands": [
                        "DOCKER_BUILDKIT=1 docker build --secret id=hf_token,env=HF_TOKEN --build-arg BUILDKIT_INLINE_CACHE=1"
                        " --cache-from $REPOSITORY:latest -t $REPOSITORY:latest .",
                        "docker push $REPOSITORY:latest",
                    ]},
                },
            }),
        )
        repository.grant_pull_push(build)
        hf_token.grant_read(build)

        # The network: public subnets, no NAT, and a security group that lets nothing in.
        vpc = ec2.Vpc(self, "Network", max_azs=2, nat_gateways=0,
                      subnet_configuration=[ec2.SubnetConfiguration(name="public", subnet_type=ec2.SubnetType.PUBLIC)])
        group = ec2.SecurityGroup(self, "Jobs", vpc=vpc, allow_all_outbound=True, description="Tally jobs: no ingress")
        # The instances' user data (MIME multipart, as Batch wants):
        # - The agent's shell has no network. Containers share the host's network and uids, so the host rejects everything
        #   from the shell's uid (9999, the Dockerfile's agent), loopback included; the job checks the rule first.
        # - The image (about 15 GB) stays on the instance: otherwise the ECS agent fetched it again for every job
        #   (measured: 3½ min, plus 1½ to unpack). A timer pulls it at boot and every 10 minutes.
        registry = repository.repository_uri.split("/")[0]
        pull = (f"aws ecr get-login-password --region {self.region} | docker login --username AWS --password-stdin {registry}"
                f" && docker pull {repository.repository_uri}:latest && docker image prune -f")
        user_data = ec2.UserData.custom("\n".join([
            'MIME-Version: 1.0', 'Content-Type: multipart/mixed; boundary="==TALLY=="', "", "--==TALLY==",
            'Content-Type: text/x-shellscript; charset="us-ascii"', "", "#!/bin/bash", "set -e",
            "iptables -I OUTPUT -m owner --uid-owner 9999 -j REJECT",
            "ip6tables -I OUTPUT -m owner --uid-owner 9999 -j REJECT",
            "printf 'ECS_IMAGE_PULL_BEHAVIOR=prefer-cached\\nECS_DISABLE_IMAGE_CLEANUP=true\\n' >> /etc/ecs/ecs.config",
            f"printf '[Service]\\nType=oneshot\\nExecStart=/bin/bash -c \"{pull}\"\\n' > /etc/systemd/system/tally-image.service",
            "printf '[Timer]\\nOnBootSec=10s\\nOnUnitActiveSec=10min\\n[Install]\\nWantedBy=timers.target\\n' > /etc/systemd/system/tally-image.timer",
            "systemctl daemon-reload && systemctl enable --now --no-block tally-image.timer",
            "", "--==TALLY==--", ""]))
        # The 100 GB disk is for the unpacked image.
        template = ec2.LaunchTemplate(self, "JobDisk", user_data=user_data, require_imdsv2=True, http_put_response_hop_limit=1, block_devices=[ec2.BlockDevice(
            device_name="/dev/xvda", volume=ec2.BlockDeviceVolume.ebs(100, volume_type=ec2.EbsDeviceVolumeType.GP3, encrypted=True))])
        cdk.Tags.of(template).add("project", "tally")  # onto the GPU instances themselves, so their cost is Tally's in Cost Explorer

        # On-demand only: GPU spot in eu-north-1 had a placement score of 1/10 and failed every launch, and Batch moves
        # to a second environment only when the first is full, not when spot has no capacity.
        # Many types, because on-demand GPUs run out too: with g6.xlarge alone, a job waited while every launch
        # failed for capacity. A bigger type costs more for the same one GPU.
        compute = batch.ManagedEc2EcsComputeEnvironment(
            self, "OnDemand", vpc=vpc, vpc_subnets=ec2.SubnetSelection(subnet_type=ec2.SubnetType.PUBLIC), security_groups=[group],
            # minVcpus: one GPU instance kept warm, so a meeting starts in about a minute rather than after a 6-7 minute
            # cold start. With hours, only then: the schedules below set it, and the
            # template's own value is 0, the night's.
            spot=False, minv_cpus=warm["vcpus"] if warm and "from" not in warm else 0, maxv_cpus=gpu["maxVcpus"],
            use_optimal_instance_classes=False,
            instance_types=[ec2.InstanceType(t) for t in gpu["instanceTypes"]],
            images=[batch.EcsMachineImage(image_type=batch.EcsMachineImageType.ECS_AL2023_NVIDIA)],
            allocation_strategy=batch.AllocationStrategy.BEST_FIT_PROGRESSIVE, launch_template=template,
            # A template change updates the environment: running jobs (up to 2 hours) finish before their instances go.
            terminate_on_update=False, update_timeout=cdk.Duration.minutes(120))
        # The template's newest version by number: Batch resolves `$Default` and `$Latest` once, when the environment is
        # created or updated, so a changed template reaches new instances only through a changed number.
        compute.node.default_child.add_property_override("ComputeResources.LaunchTemplate.Version", template.latest_version_number)
        if warm and "from" in warm:
            self.warm_hours(compute, warm, config["timezone"])
        queue = batch.JobQueue(self, "Queue", compute_environments=[batch.OrderedComputeEnvironment(compute_environment=compute, order=1)])

        # The job's role reaches every user's meetings, the three tables and the one model, and nothing else. The agent's
        # shell never holds it: it runs as an unprivileged user without AWS credentials or network (tally/tools.py).
        role = iam.Role(self, "JobRole", assumed_by=iam.ServicePrincipal("ecs-tasks.amazonaws.com"))
        bucket.grant_read_write(role, "users/*")
        role.add_to_policy(iam.PolicyStatement(actions=["s3:ListBucket"], resources=[bucket.bucket_arn],
                                               conditions={"StringLike": {"s3:prefix": "users/*"}}))
        for table in (meetings, people, voiceprints):
            table.grant_read_write_data(role)
        model = config["modelId"].split(".", 1)[1]  # the model behind the cross-region profile (eu., us., apac., global.)
        role.add_to_policy(iam.PolicyStatement(
            actions=["bedrock:InvokeModel", "bedrock:InvokeModelWithResponseStream"],
            resources=[f"arn:aws:bedrock:{self.region}:{self.account}:inference-profile/{config['modelId']}",
                       f"arn:aws:bedrock:*::foundation-model/{model}"]))  # wherever the profile sends it

        log_group = logs.LogGroup(self, "JobLogs", retention=logs.RetentionDays.ONE_MONTH)
        job = batch.EcsJobDefinition(
            self, "Job", timeout=cdk.Duration.hours(2), retry_attempts=3,
            propagate_tags=True,  # its project tag onto the tasks it runs, so they're counted as Tally's
            # retried only when its instance went away under it
            retry_strategies=[batch.RetryStrategy.of(batch.Action.RETRY, batch.Reason.SPOT_INSTANCE_RECLAIMED),
                              batch.RetryStrategy.of(batch.Action.EXIT, batch.Reason.custom(on_reason="*"))],
            container=batch.EcsEc2ContainerDefinition(
                self, "Container", image=ecs.ContainerImage.from_ecr_repository(repository, "latest"),
                cpu=4, memory=cdk.Size.mebibytes(14_000), gpu=1, job_role=role, command=["Ref::key"],
                environment={"TALLY_BUCKET": bucket.bucket_name, "TALLY_PEOPLE_TABLE": people.table_name,
                             "TALLY_VOICEPRINTS_TABLE": voiceprints.table_name, "TALLY_MEETINGS_TABLE": meetings.table_name,
                             "TALLY_MODEL_ID": config["modelId"],
                             # Everyone's named people matched too: off unless config.json says so
                             **({"TALLY_SHARED_VOICES": "true"} if config.get("sharedVoices") else {}),
                             "AWS_REGION": self.region},
                logging=ecs.LogDriver.aws_logs(stream_prefix="tally", log_group=log_group)))

        events.Rule(self, "AudioUploaded", event_pattern=events.EventPattern(
            source=["aws.s3"], detail_type=["Object Created"],
            detail={"bucket": {"name": [bucket.bucket_name]}, "object": {"key": events.Match.wildcard("users/*/meetings/*/audio.*")}}),
            targets=[targets.BatchJob(queue.job_queue_arn, queue, job.job_definition_arn, job, job_name="tally-meeting",
                                      event=events.RuleTargetInput.from_object({"Parameters": {"key": events.EventField.from_path("$.detail.object.key")}}),
                                      retry_attempts=3, max_event_age=cdk.Duration.hours(2))])

        site, distribution, site_url = self.site(config.get("domain"), certificate)
        issuer, client_id, admin_claim = self.sign_in(config.get("auth"), site_url)

        # Crash reports from people's Macs and PCs, sent with their say-so: kept, and emailed to config.json's crashEmail
        # (SNS mails a confirmation link once).
        crashes = sns.Topic(self, "Crashes", display_name="Tally crashes")
        if email := config.get("crashEmail"):
            crashes.add_subscription(subscriptions.EmailSubscription(email))

        # The API: every route needs the owner's token, for this client (its audience, or Cognito's client_id), and
        # the function reaches the meetings and the five tables.
        function = lambda_.Function(
            self, "ApiFunction", runtime=lambda_.Runtime.PYTHON_3_13, architecture=lambda_.Architecture.ARM_64,
            handler="tally_api.handler", code=lambda_.Code.from_asset(str(API), exclude=["tests", "__pycache__", ".pytest_cache"]),
            memory_size=1024, timeout=cdk.Duration.seconds(29),  # search reads every meeting's files: more memory is more CPU
            environment={"TALLY_BUCKET": bucket.bucket_name, "TALLY_MEETINGS_TABLE": meetings.table_name,
                         "TALLY_PEOPLE_TABLE": people.table_name, "TALLY_VOICEPRINTS_TABLE": voiceprints.table_name,
                         "TALLY_FOLDERS_TABLE": folders.table_name, "TALLY_USERS_TABLE": users.table_name,
                         "TALLY_JOB_QUEUE": queue.job_queue_arn, "TALLY_JOB_DEFINITION": job.job_definition_arn, "TALLY_COMPUTE_ENVIRONMENT": compute.compute_environment_arn,
                         "TALLY_ADMIN_CLAIM": admin_claim, "TALLY_MODEL_PRICES": json.dumps(config["modelPrices"]), "TALLY_INSTANCE_TYPES": ",".join(gpu["instanceTypes"]),
                         "TALLY_CRASH_TOPIC": crashes.topic_arn,
                         "TALLY_TIMEZONE": config["timezone"]},  # the dashboard's "today"
            log_group=logs.LogGroup(self, "ApiLogs", retention=logs.RetentionDays.ONE_MONTH))
        bucket.grant_read_write(function, "users/*")
        crashes.grant_publish(function)
        bucket.grant_put(function, "crashes/*")
        function.add_to_role_policy(iam.PolicyStatement(actions=["s3:ListBucket"], resources=[bucket.bucket_arn],
                                                        conditions={"StringLike": {"s3:prefix": ["users/*", "crashes/*"]}}))
        for table in (meetings, people, voiceprints, folders, users):
            table.grant_read_write_data(function)
        # The admin dashboard's figures: the GPU queue, crash reports' count, and the month's cost.
        # Batch's List and Describe, and Cost Explorer, have no resource to narrow them to.
        function.add_to_role_policy(iam.PolicyStatement(actions=["batch:ListJobs", "batch:DescribeComputeEnvironments", "ce:GetCostAndUsage"],
                                                        resources=["*"]))
        # A summary written again without background voices: the job, on its queue, and nothing else
        function.add_to_role_policy(iam.PolicyStatement(actions=["batch:SubmitJob"], resources=[queue.job_queue_arn, job.job_definition_arn,
                                                                                                f"arn:aws:batch:{self.region}:{self.account}:job-definition/{job.job_definition_name}:*"]))
        # The admin dashboard on the install page calls it from the browser: that one origin, reading only
        # ("Keycloak" is only the authorizer's id, from its first provider: a new id would replace the live one)
        api = apigw.HttpApi(self, "Api", default_authorizer=authorizers.HttpJwtAuthorizer(
            "Keycloak", issuer, jwt_audience=[client_id]),
            cors_preflight=apigw.CorsPreflightOptions(allow_origins=[site_url], allow_methods=[apigw.CorsHttpMethod.GET],
                                                      allow_headers=["authorization"], max_age=cdk.Duration.hours(1)))
        integration = integrations.HttpLambdaIntegration("Function", function)
        for route in ("GET /meetings", "PUT /meetings/{id}", "POST /meetings/{id}/uploaded", "GET /meetings/{id}", "DELETE /meetings/{id}",
                      "PUT /meetings/{id}/speakers/{speaker}", "PUT /meetings/{id}/summary", "DELETE /meetings/{id}/summary", "POST /meetings/{id}/summary/rewrite", "GET /people", "PATCH /people/{id}", "DELETE /people/{id}",
                      "PUT /meetings/{id}/folders/{folder}", "DELETE /meetings/{id}/folders/{folder}", "GET /folders", "PUT /folders/{id}", "DELETE /folders/{id}", "GET /search",
                      "GET /meetings/{id}/files", "POST /meetings/{id}/files", "DELETE /meetings/{id}/files/{file}",
                      "POST /crashes", "GET /admin/stats"):
            method, path = route.split(" ")
            api.add_routes(path=path, methods=[apigw.HttpMethod(method)], integration=integration)

        for name, value in {"ApiUrl": api.api_endpoint, "MeetingsTable": meetings.table_name, "BucketName": bucket.bucket_name, "Repository": repository.repository_uri, "ImageBuild": build.project_name,
                            "JobQueue": queue.job_queue_name, "JobDefinition": job.job_definition_name, "LogGroup": log_group.log_group_name,
                            "PeopleTable": people.table_name, "VoiceprintsTable": voiceprints.table_name,
                            # What the install page and the app are built with (make site)
                            "SiteUrl": site_url, "SiteBucket": site.bucket_name, "SiteDistribution": distribution.distribution_id,
                            "Issuer": issuer, "ClientId": client_id}.items():
            cdk.CfnOutput(self, f"{name}Output", key=name, value=value)

    def site(self, domain: dict | None, certificate: acm.ICertificate | None) -> tuple[s3.Bucket, cloudfront.Distribution, str]:
        """The install page, the admin dashboard and the app's source for each version: a private bucket that only
        CloudFront reads, at the domain when there is one, else at CloudFront's own name. `make publish-site` fills it."""
        bucket = s3.Bucket(self, "Site", block_public_access=s3.BlockPublicAccess.BLOCK_ALL, enforce_ssl=True,
                           encryption=s3.BucketEncryption.S3_MANAGED, removal_policy=cdk.RemovalPolicy.RETAIN)
        # The pages run only their own scripts and can't be framed. They call the sign-in provider and the API, which
        # are known only after the site is (each needs the site's address), so any https address may be called.
        policy = ("default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; "
                  "connect-src 'self' https:; frame-ancestors 'none'; base-uri 'none'; object-src 'none'")
        headers = cloudfront.ResponseHeadersPolicy(self, "SiteHeaders", security_headers_behavior=cloudfront.ResponseSecurityHeadersBehavior(
            content_security_policy=cloudfront.ResponseHeadersContentSecurityPolicy(content_security_policy=policy, override=True),
            frame_options=cloudfront.ResponseHeadersFrameOptions(frame_option=cloudfront.HeadersFrameOption.DENY, override=True),
            content_type_options=cloudfront.ResponseHeadersContentTypeOptions(override=True),
            referrer_policy=cloudfront.ResponseHeadersReferrerPolicy(referrer_policy=cloudfront.HeadersReferrerPolicy.STRICT_ORIGIN_WHEN_CROSS_ORIGIN, override=True),
            strict_transport_security=cloudfront.ResponseHeadersStrictTransportSecurity(access_control_max_age=cdk.Duration.days(730), override=True)))
        distribution = cloudfront.Distribution(
            self, "SiteDistribution", default_root_object="index.html",
            # TLS 1.2 or newer at the site's own domain; CloudFront's default certificate sets its own
            minimum_protocol_version=cloudfront.SecurityPolicyProtocol.TLS_V1_2_2021 if certificate else None,
            default_behavior=cloudfront.BehaviorOptions(origin=origins.S3BucketOrigin.with_origin_access_control(bucket),
                                                        viewer_protocol_policy=cloudfront.ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
                                                        response_headers_policy=headers),
            domain_names=[domain["name"]] if domain else None, certificate=certificate)
        if not domain:
            return bucket, distribution, f"https://{distribution.distribution_domain_name}"
        zone = route53.HostedZone.from_hosted_zone_attributes(self, "Zone", hosted_zone_id=domain["hostedZoneId"], zone_name=domain["name"])
        target = route53.RecordTarget.from_alias(route53_targets.CloudFrontTarget(distribution))
        route53.ARecord(self, "SiteAddress", zone=zone, record_name=f"{domain['name']}.", target=target)
        route53.AaaaRecord(self, "SiteAddress6", zone=zone, record_name=f"{domain['name']}.", target=target)
        return bucket, distribution, f"https://{domain['name']}"

    def sign_in(self, auth: dict | None, site_url: str) -> tuple[str, str, str]:
        """Who may sign in: the OIDC provider in config.json's `auth` (its issuer, the app's client, and the claim
        listing a person's roles, which holds `admin` for an admin), or without one a Cognito user pool, made here,
        whose people are added by an admin and whose `admin` group sees the dashboard."""
        if auth:
            return auth["issuer"], auth["clientId"], auth["adminClaim"]
        pool = cognito.UserPool(self, "SignIn", self_sign_up_enabled=False, sign_in_aliases=cognito.SignInAliases(email=True),
                                account_recovery=cognito.AccountRecovery.EMAIL_ONLY, removal_policy=cdk.RemovalPolicy.RETAIN)
        pool.add_domain("SignInDomain", cognito_domain=cognito.CognitoDomainOptions(domain_prefix=f"{self.stack_name.lower()}-{self.account}"))
        client = pool.add_client("App", generate_secret=False, prevent_user_existence_errors=True,
                                 refresh_token_validity=cdk.Duration.days(90),
                                 o_auth=cognito.OAuthSettings(flows=cognito.OAuthFlows(authorization_code_grant=True),
                                                              scopes=[cognito.OAuthScope.OPENID, cognito.OAuthScope.EMAIL, cognito.OAuthScope.PROFILE],
                                                              callback_urls=["tally://callback", f"{site_url}/admin.html"]))
        cognito.CfnUserPoolGroup(self, "Admins", user_pool_id=pool.user_pool_id, group_name="admin")
        return f"https://cognito-idp.{self.region}.amazonaws.com/{pool.user_pool_id}", client.user_pool_client_id, "cognito:groups"

    def warm_hours(self, compute: batch.ManagedEc2EcsComputeEnvironment, warm: dict, timezone: str) -> None:
        """One GPU instance warm in the day, none at night: EventBridge Scheduler calls Batch itself, at
        the start and the end of config.json's warm hours, in its time zone (so summer time takes care of itself), on
        its warm days (every day, unless `days` names some, as `MON-FRI`)."""
        role = iam.Role(self, "WarmHoursRole", assumed_by=iam.ServicePrincipal("scheduler.amazonaws.com"),
                        description="Sets the GPU environment's minimum, morning and night")
        role.add_to_policy(iam.PolicyStatement(actions=["batch:UpdateComputeEnvironment"], resources=[compute.compute_environment_arn]))
        for name, hour, minimum in (("WarmFrom", warm["from"], warm["vcpus"]), ("WarmUntil", warm["to"], 0)):
            scheduler.CfnSchedule(
                self, name, schedule_expression=f"cron(0 {hour} ? * {warm.get('days', '*')} *)", schedule_expression_timezone=timezone,
                flexible_time_window=scheduler.CfnSchedule.FlexibleTimeWindowProperty(mode="OFF"),
                target=scheduler.CfnSchedule.TargetProperty(
                    arn="arn:aws:scheduler:::aws-sdk:batch:updateComputeEnvironment", role_arn=role.role_arn,
                    input=json.dumps({"ComputeEnvironment": compute.compute_environment_arn, "ComputeResources": {"MinvCpus": minimum}}),
                    retry_policy=scheduler.CfnSchedule.RetryPolicyProperty(maximum_retry_attempts=5)))

