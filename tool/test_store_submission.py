import copy
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

from store_submission import (
    StoreError, StoreApi, check_ci_authorization, inspect_package,
    package_update, poll_submission, report_status, submit_package,
)


def published():
    return {
        "id": "100", "status": "Published", "fileUploadUrl": "https://upload.blob.core.windows.net/private?sig=secret",
        "listings": {"en-us": {"baseListing": {"description": "Keep this listing"}}},
        "pricing": {"priceId": "Free"},
        "targetPublishMode": "Manual",
        "applicationPackages": [
            {"fileName": "old.msix", "fileStatus": "Uploaded", "architecture": "x64", "version": "1.21.0.0"},
            {"fileName": "arm.msix", "fileStatus": "Uploaded", "architecture": "ARM64", "version": "1.21.0.0"},
        ],
    }


class FakeApi:
    def __init__(self):
        self.calls = []
        self.pending = None
        self.live = published()
        self.draft = None
        self.statuses = ["PreProcessing"]

    def app(self):
        return {"id": "9N6B4KTL3LB2", "packageIdentityName": "UrActor.UrDatabase",
                "publisherName": "CN=53FDA7CE-E84E-4A01-A833-B1C55FBA5540",
                "lastPublishedApplicationSubmission": {"id": "100"} if self.live else None,
                "pendingApplicationSubmission": self.pending}

    def submission(self, submission_id):
        return copy.deepcopy(self.live if submission_id == "100" else self.draft)

    def create(self):
        self.calls.append("create")
        self.draft = copy.deepcopy(self.live)
        self.draft.update(id="200", status="PendingCommit")
        self.pending = {"id": "200"}
        return copy.deepcopy(self.draft)

    def update(self, submission_id, payload):
        self.calls.append("update")
        self.draft = dict(copy.deepcopy(payload), id=submission_id, status="PendingCommit")
        return copy.deepcopy(self.draft)

    def upload(self, url, data):
        self.calls.append("upload")
        with zipfile.ZipFile(data) as archive:
            assert archive.namelist() == ["new.msix"]

    def commit(self, submission_id):
        self.calls.append("commit")
        return {"status": "CommitStarted"}

    def status(self, submission_id):
        return {"status": self.statuses.pop(0) if len(self.statuses) > 1 else self.statuses[0]}


class StoreSubmissionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.package = self.root / "new.msix"
        self.package.write_bytes(b"package fixture")
        self.state = self.root / "state.json"
        self.api = FakeApi()

    def submit(self):
        return submit_package(self.api, self.package, "1.22.0.0", self.state, sleep=lambda _: None)

    def test_existing_pending_submissions_are_never_mutated(self):
        for pending in ({"id": "150"}, {}, "ambiguous"):
            with self.subTest(pending=pending):
                self.api.pending = pending
                with self.assertRaises(StoreError):
                    self.submit()
                self.assertEqual([], self.api.calls)

    def test_requires_first_manual_publication_and_expected_identity(self):
        self.api.live = None
        with self.assertRaises(StoreError):
            self.submit()
        self.assertEqual([], self.api.calls)

    def test_preserves_metadata_and_other_architectures_and_sets_immediate_publication(self):
        source = published()
        payload = package_update(source, "new.msix", "1.22.0.0")
        self.assertEqual(source["listings"], payload["listings"])
        self.assertEqual("Free", payload["pricing"]["priceId"])
        self.assertEqual("Immediate", payload["targetPublishMode"])
        self.assertNotIn("fileUploadUrl", payload)
        self.assertNotIn("status", payload)
        self.assertEqual("Uploaded", source["applicationPackages"][0]["fileStatus"])
        self.assertEqual("PendingDelete", payload["applicationPackages"][0]["fileStatus"])
        self.assertEqual(source["applicationPackages"][1], payload["applicationPackages"][1])
        self.assertEqual({
            "fileName": "new.msix", "fileStatus": "PendingUpload",
            "minimumDirectXVersion": "None", "minimumSystemRam": "None",
        }, payload["applicationPackages"][-1])

    def test_ambiguous_packages_pricing_and_downgrades_are_refused(self):
        for changes in ({"architecture": "neutral"}, {"fileName": "old.msixbundle"},
                        {"architecture": None}, {"version": "2.0.0.0"}, {"version": "1.22.0.0"}):
            source = published()
            source["applicationPackages"][0].update(changes)
            with self.subTest(changes=changes), self.assertRaises(StoreError):
                package_update(source, "new.msix", "1.22.0.0")
        source = published()
        source["pricing"]["isAdvancedPricingModel"] = True
        with self.assertRaises(StoreError):
            package_update(source, "new.msix", "1.22.0.0")

    def test_lifecycle_only_updates_newly_created_draft_and_persists_safe_state(self):
        result = self.submit()
        self.assertEqual(["create", "update", "upload", "commit"], self.api.calls)
        self.assertEqual("PreProcessing", result["status"])
        text = self.state.read_text()
        self.assertEqual("200", json.loads(text)["submissionId"])
        self.assertNotIn("secret", text)
        self.assertNotIn("fileUploadUrl", text)
        self.assertNotIn("published", result["phase"])

    def test_server_create_conflict_is_not_retried_or_recovered_destructively(self):
        def conflict():
            self.api.calls.append("create")
            raise StoreError("HTTP 409")
        self.api.create = conflict
        with self.assertRaises(StoreError):
            self.submit()
        self.assertEqual(["create"], self.api.calls)
        self.assertEqual("creating", json.loads(self.state.read_text())["phase"])

    def test_changed_draft_or_pending_owner_is_refused_before_put(self):
        create = self.api.create
        def changed():
            result = create()
            self.api.draft["listings"]["en-us"]["baseListing"]["description"] = "Manual change"
            return result
        self.api.create = changed
        with self.assertRaises(StoreError):
            self.submit()
        self.assertEqual(["create"], self.api.calls)

    def test_manual_change_after_upload_is_not_committed(self):
        upload = self.api.upload
        def changed(url, data):
            upload(url, data)
            self.api.pending = {"id": "300"}
        self.api.upload = changed
        with self.assertRaises(StoreError):
            self.submit()
        self.assertEqual(["create", "update", "upload"], self.api.calls)

    def test_ambiguous_commit_is_recorded_and_never_retried(self):
        def timeout(submission_id):
            self.api.calls.append("commit")
            raise StoreError("Network timeout")
        self.api.commit = timeout
        with self.assertRaises(StoreError):
            self.submit()
        self.assertEqual(1, self.api.calls.count("commit"))
        self.assertEqual("committing", json.loads(self.state.read_text())["phase"])

    def test_existing_state_and_equal_or_older_versions_do_not_create_another_draft(self):
        self.state.write_text('{"submissionId":"200"}')
        with self.assertRaises(StoreError):
            self.submit()
        self.state.unlink()
        self.api.live["applicationPackages"][0]["version"] = "1.22.0.0"
        with self.assertRaises(StoreError):
            self.submit()
        self.assertEqual([], self.api.calls)

    def test_polling_reports_processing_and_fails_on_failure_unknown_or_timeout(self):
        self.api.statuses = ["CommitStarted", "Certification"]
        self.assertEqual("Certification", poll_submission(self.api, "200", sleep=lambda _: None)["status"])
        for status in ("CertificationFailed", "Canceled", "NotAStatus", "PendingCommit", "CommitStarted"):
            self.api.statuses = [status]
            with self.subTest(status=status), self.assertRaises(StoreError):
                poll_submission(self.api, "200", sleep=lambda _: None, attempts=2)

    def test_publication_requires_explicit_opt_in_and_trusted_main_context(self):
        env = {"STORE_PUBLISH_ENABLED": "true", "GITHUB_REF": "refs/heads/main",
               "GITHUB_REPOSITORY": "larabail/UrDatabase", "GITHUB_EVENT_NAME": "push"}
        check_ci_authorization(env)
        for key, value in (("STORE_PUBLISH_ENABLED", ""), ("GITHUB_REF", "refs/heads/feature"),
                           ("GITHUB_EVENT_NAME", "pull_request"), ("GITHUB_REPOSITORY", "fork/UrDatabase")):
            with self.subTest(key=key), self.assertRaises(StoreError):
                check_ci_authorization(dict(env, **{key: value}))

    def test_archive_identity_and_version_are_checked_before_authentication(self):
        from make_store_package import ROOT
        text = (ROOT / "packaging/windows/AppxManifest.xml").read_text().replace("@VERSION@", "1.22.0.0")
        with zipfile.ZipFile(self.package, "w") as archive:
            archive.writestr("AppxManifest.xml", text)
            archive.writestr("distribution-channel.txt", "MicrosoftStore\n")
        self.assertEqual("1.22.0.0", inspect_package(self.package, "1.22.0.0"))
        with self.assertRaises(StoreError):
            inspect_package(self.package, "1.23.0.0")

    def test_transport_uses_encoded_credentials_and_never_sends_bearer_to_blob(self):
        calls = []
        def send(method, url, headers, data):
            calls.append((method, url, headers, data))
            if "login.microsoftonline.com" in url:
                return json.dumps({"access_token": "fake-token", "expires_in": 3600}).encode()
            return b"{}"
        api = StoreApi("9N6B4KTL3LB2", "00000000-0000-0000-0000-000000000001",
                       "00000000-0000-0000-0000-000000000002", "fake&secret", send=send)
        api.app()
        api.upload("https://upload.blob.core.windows.net/path?sig=fake", self.package)
        self.assertIn(b"client_secret=fake%26secret", calls[0][3])
        self.assertEqual("Bearer fake-token", calls[1][2]["Authorization"])
        self.assertNotIn("Authorization", calls[2][2])
        self.assertEqual("BlockBlob", calls[2][2]["x-ms-blob-type"])
        with self.assertRaises(StoreError):
            api.upload("https://example.test/path?sig=fake", self.package)

    def test_status_is_read_only_even_for_manual_drafts_and_certification_failures(self):
        for status in ("PendingCommit", "Published", "CertificationFailed", "Certification"):
            self.api.statuses = [status]
            self.api.pending = {"id": "150"}
            self.assertEqual(status, report_status(self.api)["status"])
        self.assertEqual([], self.api.calls)

    def test_identity_mismatch_or_late_manual_draft_blocks_creation(self):
        original = self.api.app
        def mismatch():
            return dict(original(), publisherName="CN=NotOurPublisher")
        self.api.app = mismatch
        with self.assertRaises(StoreError):
            self.submit()
        self.assertEqual([], self.api.calls)
        reads = 0
        def late_draft():
            nonlocal reads
            reads += 1
            return dict(original(), pendingApplicationSubmission={"id": "150"}) if reads > 1 else original()
        self.api.app = late_draft
        with self.assertRaises(StoreError):
            self.submit()
        self.assertEqual([], self.api.calls)


class WorkflowTests(unittest.TestCase):
    def test_submission_is_gated_after_a_real_main_release_and_has_separate_non_canceling_concurrency(self):
        from make_store_package import ROOT
        release = (ROOT / ".github/workflows/release.yml").read_text()
        caller = release.split("\n  store:\n")[1]
        self.assertIn("needs: release", caller)
        self.assertIn("needs.release.outputs.published == 'true'", caller)
        self.assertIn("vars.STORE_PUBLISH_ENABLED == 'true'", caller)
        self.assertIn("github.ref == 'refs/heads/main'", caller)
        self.assertIn("uses: ./.github/workflows/store.yml", caller)
        self.assertIn("steps.publish.outcome == 'success'", release)
        self.assertIn("runs-on: macos-14", release)
        workflow = (ROOT / ".github/workflows/store.yml").read_text()
        self.assertIn("inputs.publish && 'publication' || 'build'", workflow)
        self.assertIn("cancel-in-progress: ${{ !inputs.publish }}", workflow)
        package = workflow.split("\n  package:\n")[1].split("\n  submit:\n")[0]
        self.assertNotIn("AZURE_AD_APPLICATION_SECRET", package)
        submission = workflow.split("\n  submit:\n")[1]
        for gate in ("inputs.publish", "vars.STORE_PUBLISH_ENABLED == 'true'", "github.ref == 'refs/heads/main'"):
            self.assertIn(gate, submission)
        self.assertIn("environment: microsoft-store", submission)
        self.assertIn("if: always()", submission)
        self.assertIn("store-submission-state.json", submission)

    def test_status_and_website_jobs_do_not_publish_or_expose_store_credentials_to_the_site(self):
        from make_store_package import ROOT
        status = (ROOT / ".github/workflows/store-status.yml").read_text()
        self.assertIn("run: python tool/store_submission.py status", status)
        self.assertNotIn("store_submission.py submit", status)
        website = (ROOT / ".github/workflows/deploy-downloads.yml").read_text()
        self.assertIn("node tool/configure_store_site.mjs", website)
        self.assertIn("vars.STORE_LISTING_LIVE", website)
        self.assertNotIn("AZURE_AD_APPLICATION_SECRET", website)


if __name__ == "__main__":
    unittest.main()
