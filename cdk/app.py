"""The CDK app: the Tally stack, configured by the repository's config.json (the README says what each setting
is). With a domain, a small second stack in us-east-1 holds the site's certificate, where CloudFront needs it."""

import json
from pathlib import Path

import aws_cdk as cdk

from tally_stack import CertificateStack, TallyStack

config = json.loads((Path(__file__).parent.parent / "config.json").read_text())
app = cdk.App()
certificate = None
if domain := config.get("domain"):
    certificate = CertificateStack(app, f"{config['stackName']}Certificate", domain=domain, cross_region_references=True,
                                   env=cdk.Environment(account=config["account"], region="us-east-1")).certificate
TallyStack(app, config["stackName"], config=config, certificate=certificate, cross_region_references=bool(certificate),
           env=cdk.Environment(account=config["account"], region=config["region"]))
app.synth()
