#!/usr/bin/env python3
"""Submit a new Store update without deleting or adopting an existing draft."""

import argparse
import copy
from email.utils import formatdate
import hashlib
import json
import os
from pathlib import Path
import re
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener
from uuid import UUID
from xml.etree import ElementTree as ET
import zipfile

from make_store_package import IDENTITY, NS, ROOT, package_version, release_version

API = "https://manage.devcenter.microsoft.com"
MAX_UPLOAD_BYTES = 64 * 1024 * 1024  # Also supported by older SAS service versions.
RESPONSE_FIELDS = {"id", "status", "statusDetails", "fileUploadUrl", "friendlyName"}
PROCESSING = {"PreProcessing", "Certification", "Release", "PendingPublication", "Publishing"}
FAILURES = {"CommitFailed", "PreProcessingFailed", "CertificationFailed", "ReleaseFailed", "PublishFailed"}


class StoreError(RuntimeError):
    pass


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def send_http(method, url, headers, data):
    try:
        with build_opener(NoRedirect()).open(
            Request(url, data=data, headers=headers, method=method), timeout=180
        ) as response:
            return response.read()
    except HTTPError as error:
        raise StoreError(f"{method} failed (HTTP {error.code}); response and URL redacted. "
                         "Do not retry mutations before inspecting Partner Center.") from None
    except (URLError, TimeoutError, OSError, ValueError):
        raise StoreError(f"{method} network failure; the server may have completed the request. "
                         "Inspect the saved submission ID and Partner Center before retrying.") from None


def object_response(data):
    try:
        value = json.loads(data)
    except (ValueError, UnicodeError):
        raise StoreError("Store response was not valid JSON (body redacted).") from None
    if not isinstance(value, dict):
        raise StoreError("Store response was not a JSON object.")
    return value


def submission_id(value):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9]+", value):
        raise StoreError("Missing or invalid submission ID; inspect Partner Center.")
    return value


class StoreApi:
    def __init__(self, product_id, tenant, client, secret, send=send_http):
        if not re.fullmatch(r"9[A-Z0-9]{11}", product_id):
            raise StoreError("Invalid public Store product ID.")
        try:
            tenant, client = str(UUID(tenant)), str(UUID(client))
        except (ValueError, AttributeError):
            raise StoreError("Configure the Entra tenant and application client IDs as UUIDs.") from None
        if not secret:
            raise StoreError("Configure AZURE_AD_APPLICATION_SECRET in the microsoft-store environment.")
        self.product_id = product_id
        self.tenant, self.client, self.secret = tenant, client, secret
        self.send = send
        self.token, self.expires = None, 0

    def request(self, method, suffix="", payload=None):
        if self.expires < time.monotonic() + 60:
            data = urlencode({"grant_type": "client_credentials", "client_id": self.client,
                              "client_secret": self.secret, "resource": API}).encode()
            response = object_response(self.send(
                "POST", f"https://login.microsoftonline.com/{self.tenant}/oauth2/token",
                {"Content-Type": "application/x-www-form-urlencoded"}, data,
            ))
            self.token = response.get("access_token")
            if not isinstance(self.token, str) or not self.token:
                raise StoreError("Entra did not return an access token (response redacted).")
            self.expires = time.monotonic() + int(response.get("expires_in", 3600))
        headers = {"Authorization": f"Bearer {self.token}", "Accept": "application/json"}
        # Create and commit require an empty body, not an empty JSON object.
        data = None if method == "GET" else b""
        if payload is not None:
            data = json.dumps(payload).encode()
            headers["Content-Type"] = "application/json"
        return object_response(self.send(method, f"{API}/v1.0/my/applications/{self.product_id}{suffix}",
                                         headers, data))

    def app(self):
        return self.request("GET")

    def submission(self, ident):
        return self.request("GET", f"/submissions/{submission_id(ident)}")

    def create(self):
        return self.request("POST", "/submissions")

    def update(self, ident, payload):
        return self.request("PUT", f"/submissions/{submission_id(ident)}", payload)

    def commit(self, ident):
        return self.request("POST", f"/submissions/{submission_id(ident)}/commit")

    def status(self, ident):
        return self.request("GET", f"/submissions/{submission_id(ident)}/status")

    def upload(self, url, path):
        try:
            parts = urlsplit(url)
            port = parts.port
        except ValueError:
            raise StoreError("Invalid upload destination; SAS URL redacted.") from None
        if (parts.scheme != "https" or not parts.hostname
                or not parts.hostname.endswith(".blob.core.windows.net")
                or parts.username or parts.password or port not in (None, 443)
                or not parts.query or parts.fragment):
            raise StoreError("Unexpected upload destination; SAS URL redacted.")
        size = path.stat().st_size
        if size > MAX_UPLOAD_BYTES:
            raise StoreError("Upload exceeds the conservative 64 MiB single-blob limit.")
        self.send("PUT", url, {"Content-Type": "application/zip", "Content-Length": str(size),
                              "x-ms-blob-type": "BlockBlob", "x-ms-version": "2021-12-02",
                              "x-ms-date": formatdate(usegmt=True)},
                  path.read_bytes())


def product_id():
    return json.loads((ROOT / "packaging/windows/store-product.json").read_text())["productId"]


def validate_app(app):
    if (app.get("id") != product_id()
            or app.get("packageIdentityName") != IDENTITY["Name"]
            or app.get("publisherName") != IDENTITY["Publisher"]):
        raise StoreError("Partner Center application does not match the configured Store identity.")


def inspect_package(path, expected_version):
    with zipfile.ZipFile(path) as package:
        names = package.namelist()
        if len(names) != len(set(names)) or "AppxSignature.p7x" in names:
            raise StoreError("Expected the inspected, unsigned Store-upload package.")
        manifest = ET.fromstring(package.read("AppxManifest.xml"))
        identity = manifest.find("p:Identity", NS)
        if identity is None or any(identity.get(key) != value for key, value in IDENTITY.items()):
            raise StoreError("MSIX identity/architecture does not match this Store product.")
        if identity.get("Version") != expected_version:
            raise StoreError("MSIX version differs from this checkout's derived product version.")
        if package.read("distribution-channel.txt").decode("utf-8-sig").strip() != "MicrosoftStore":
            raise StoreError("MSIX does not identify itself as the Store distribution.")
        return expected_version


def version_parts(value):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+", value):
        raise StoreError("Unrecognized package version; no packages changed.")
    parts = tuple(map(int, value.split(".")))
    if max(parts) > 65535:
        raise StoreError("Package version component exceeds 65535.")
    return parts


def writable(submission):
    return {key: copy.deepcopy(value) for key, value in submission.items() if key not in RESPONSE_FIELDS}


def package_update(submission, filename, version):
    result = writable(submission)
    if not isinstance(result.get("listings"), dict) or not result["listings"]:
        raise StoreError("A complete published listing is required; keep the first submission manual.")
    pricing = result.get("pricing")
    if (not isinstance(pricing, dict) or pricing.get("isAdvancedPricingModel")
            or pricing.get("priceId") != "Free"):
        raise StoreError("Only the existing free, non-advanced pricing baseline is supported.")
    pricing.pop("isAdvancedPricingModel", None)
    packages = result.get("applicationPackages")
    if not isinstance(packages, list) or not packages:
        raise StoreError("No published package baseline; complete the first submission manually.")
    replaced = 0
    for package in packages:
        if not isinstance(package, dict):
            raise StoreError("Unrecognized published package.")
        architecture = package.get("architecture")
        filename_on_server = package.get("fileName")
        if (not isinstance(architecture, str) or architecture.lower() not in {"x64", "x86", "arm", "arm64"}
                or not isinstance(filename_on_server, str)
                or Path(filename_on_server).suffix.lower() not in {".msix", ".appx"}
                or package.get("fileStatus") not in {"Uploaded", "None"}):
            raise StoreError("Ambiguous package/bundle/status; update these packages manually.")
        if architecture.lower() == "x64":
            if version_parts(version) <= version_parts(package.get("version")):
                raise StoreError("The x64 version must increase. Never republish different bytes under one version.")
            package["fileStatus"] = "PendingDelete"
            replaced += 1
    if not replaced:
        raise StoreError("No existing x64 package to update; add a new architecture manually.")
    packages.append({"fileName": filename, "fileStatus": "PendingUpload",
                     "minimumDirectXVersion": "None", "minimumSystemRam": "None"})
    result["targetPublishMode"] = "Immediate"
    result.pop("targetPublishDate", None)
    return result


def own_draft(api, ident, expected):
    app = api.app()
    validate_app(app)
    pending = app.get("pendingApplicationSubmission")
    if not isinstance(pending, dict) or pending.get("id") != ident:
        raise StoreError("Pending submission changed; refusing to edit or commit it.")
    current = api.submission(ident)
    if current.get("status") != "PendingCommit" or writable(current) != writable(expected):
        raise StoreError("The new draft changed outside this run; refusing to edit or commit it.")


def poll_submission(api, ident, sleep=time.sleep, attempts=30):
    for _ in range(attempts):
        result = api.status(ident)
        status = result.get("status")
        if status in PROCESSING | {"Published"}:
            return result
        if status in FAILURES:
            raise StoreError(f"Submission {ident}: {status}. Inspect certification details in Partner Center.")
        if status != "CommitStarted":
            raise StoreError(f"Submission {ident} is not progressing through commit; inspect Partner Center.")
        sleep(20)
    raise StoreError(f"Submission {ident} is still pending after the polling window. "
                     "Use the read-only status workflow; do not recreate the draft.")


def submit_package(api, package, version, state_file, sleep=time.sleep):
    if state_file.exists():
        raise StoreError("Submission state already exists; inspect it instead of rerunning mutations.")
    app = api.app()
    validate_app(app)
    # Missing/null means none; an empty object or any other shape is ambiguous.
    if app.get("pendingApplicationSubmission") is not None:
        raise StoreError("A pending/manual submission already exists. Nothing was changed; resolve it manually.")
    baseline = app.get("lastPublishedApplicationSubmission")
    if not isinstance(baseline, dict):
        raise StoreError("First submission must be published manually before enabling automation.")
    live = api.submission(submission_id(baseline.get("id")))
    if live.get("status") != "Published":
        raise StoreError("The baseline is not Published; refusing to create a submission.")
    package_update(live, package.name, version)

    with package.open("rb") as source:
        digest = hashlib.file_digest(source, "sha256").hexdigest()
    state = {"productId": product_id(), "packageVersion": version, "sha256": digest,
             "commit": os.environ.get("GITHUB_SHA", "")}

    def save(phase, **fields):
        state.update(phase=phase, **fields)
        state_file.write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8")

    with tempfile.TemporaryDirectory(prefix="urdb-store-upload-") as directory:
        upload = Path(directory) / "upload.zip"
        with zipfile.ZipFile(upload, "w", compression=zipfile.ZIP_STORED) as archive:
            archive.write(package, package.name)
        if upload.stat().st_size > MAX_UPLOAD_BYTES:
            raise StoreError("ZIP exceeds 64 MiB; use a manual upload or implement tested block uploads.")
        # A second preflight narrows manual-editor races; the API has no documented
        # conditional create/ETag. Operators must not edit Partner Center during CI.
        latest = api.app()
        validate_app(latest)
        if (latest.get("pendingApplicationSubmission") is not None
                or latest.get("lastPublishedApplicationSubmission") != baseline):
            raise StoreError("Application changed during preflight; no draft was created.")
        save("creating")
        draft = api.create()  # Never retry this POST: a timeout may still have created a draft.
        ident = submission_id(draft.get("id"))
        save("draft_created", submissionId=ident)
        if ident == live.get("id") or draft.get("status") != "PendingCommit":
            raise StoreError("Create did not return a new pending draft.")
        payload = package_update(draft, package.name, version)
        own_draft(api, ident, draft)
        save("updating")
        updated = api.update(ident, payload)
        save("uploading")
        api.upload(draft.get("fileUploadUrl", ""), upload)
        own_draft(api, ident, updated)
        save("committing")
        response = api.commit(ident)
        if response.get("status") != "CommitStarted":
            raise StoreError("Unexpected commit result. Inspect the recorded draft; do not retry automatically.")
        save("processing", status="CommitStarted")
        result = poll_submission(api, ident, sleep=sleep)
        save("published" if result["status"] == "Published" else "processing", status=result["status"])
    return state


def check_ci_authorization(env):
    if (env.get("STORE_PUBLISH_ENABLED") != "true" or env.get("GITHUB_REF") != "refs/heads/main"
            or env.get("GITHUB_REPOSITORY") != "larabail/UrDatabase"
            or env.get("GITHUB_EVENT_NAME") not in {"push", "workflow_dispatch"}):
        raise StoreError("Publishing requires explicit opt-in and a trusted main release run.")


def report_status(api):
    app = api.app()
    validate_app(app)
    reference = app.get("pendingApplicationSubmission")
    if reference is None:
        reference = app.get("lastPublishedApplicationSubmission")
    if not isinstance(reference, dict):
        raise StoreError("No published or pending submission; first submission remains manual.")
    ident = submission_id(reference.get("id"))
    status = api.status(ident).get("status")
    if status not in PROCESSING | FAILURES | {"Published", "PendingCommit", "CommitStarted", "Canceled"}:
        raise StoreError("Unknown submission status; inspect Partner Center.")
    return {"productId": product_id(), "submissionId": ident, "status": status}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["submit", "status"])
    parser.add_argument("--package", type=Path)
    parser.add_argument("--state-file", type=Path)
    args = parser.parse_args()
    if args.action == "submit":
        check_ci_authorization(os.environ)
        if not args.package or not args.state_file:
            parser.error("submit requires --package and --state-file")
        version = inspect_package(args.package, package_version(release_version()))
    api = StoreApi(product_id(), os.environ.get("AZURE_AD_TENANT_ID", ""),
                   os.environ.get("AZURE_AD_APPLICATION_CLIENT_ID", ""),
                   os.environ.get("AZURE_AD_APPLICATION_SECRET", ""))
    if args.action == "submit":
        result = submit_package(api, args.package, version, args.state_file)
    else:
        result = report_status(api)
    status = result["status"]
    message = (f"Store {result['productId']}, submission {result['submissionId']}: {status}.\n"
               + ("Published by Microsoft.\n" if status == "Published"
                  else "Not confirmed published. Review status and certification details in Partner Center.\n"))
    print(message)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with Path(os.environ["GITHUB_STEP_SUMMARY"]).open("a", encoding="utf-8") as summary:
            summary.write("### Microsoft Store submission\n\n" + message)
    if status in FAILURES | {"Canceled"}:
        raise StoreError("Store processing failed or was canceled; manual investigation required.")


if __name__ == "__main__":
    try:
        main()
    except (StoreError, OSError, ValueError, KeyError, zipfile.BadZipFile, ET.ParseError) as error:
        raise SystemExit(str(error)) from None
