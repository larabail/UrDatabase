#!/usr/bin/env python3
"""Tests for the release gate.

Run with: python3 -m unittest discover -s tool -p "test_*.py"

This decides, after a merge has already happened, whether `main` has a release
to make -- and its expensive answer is the wrong kind of quiet. A merge that
strands shipped code outside every release used to report success, and it cost
two unnoticed releases before anybody worked out why the download was still the
old one. So the case that must fail is pinned here, together with the two that
must stay green, because getting either wrong is worse than the bug: a release
workflow that goes red on ordinary documentation merges is one nobody reads,
and that is how the silence gets rebuilt.

Several of these build a real git repository. Which commits the gate compares
is the whole question, and a stub for `git diff` would only prove the stub was
asked politely.
"""

import contextlib
import io
import os
import re
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from check_release_gate import (
    RefError,
    changed_between,
    decide,
    listing,
    main,
    shipped,
)
from check_version_bump import touches_shipped_code

REPO_ROOT = Path(__file__).resolve().parent.parent


class Repository:
    """A throwaway git repository with real commits and real tags."""

    def __init__(self, path):
        self.path = str(path)
        self.git("init", "--quiet")
        self.git("symbolic-ref", "HEAD", "refs/heads/main")
        self.git("config", "user.email", "checks@example.invalid")
        self.git("config", "user.name", "Release gate tests")
        # A contributor who signs commits or tags globally would otherwise
        # watch these fail for a reason that has nothing to do with releases.
        self.git("config", "commit.gpgsign", "false")
        self.git("config", "tag.gpgsign", "false")

    def git(self, *args):
        return subprocess.run(
            ["git", "-C", self.path] + list(args),
            check=True,
            capture_output=True,
            text=True,
        ).stdout

    def write(self, name, text):
        path = Path(self.path) / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def commit(self, message):
        self.git("add", "--all")
        self.git("commit", "--quiet", "--message", message)
        return self.git("rev-parse", "HEAD").strip()

    def tag(self, name):
        # Annotated, because that is what the release workflow pushes, and an
        # annotated tag is an object that has to be dereferenced before it can
        # be diffed.
        self.git("tag", "--annotate", name, "--message", name)


def temporary_repository(test):
    """A `Repository` in a temporary directory, cleaned up with [test]."""
    scratch = tempfile.TemporaryDirectory()
    test.addCleanup(scratch.cleanup)
    return Repository(scratch.name)


class DecisionTests(unittest.TestCase):
    """The rule itself, with the git work already done."""

    def test_no_version_is_not_an_error(self):
        # A repository that has not adopted a version yet must be able to
        # merge. Failing here would block the very pull request that adds one.
        decision = decide("", tag_exists=False)
        self.assertFalse(decision.publish)
        self.assertTrue(decision.ok)
        self.assertIn("does not set a `<Version>`", decision.summary)

    def test_a_free_tag_publishes(self):
        decision = decide("0.4.2", tag_exists=False, tag="v0.4.2")
        self.assertTrue(decision.publish)
        self.assertTrue(decision.ok)
        self.assertIn("v0.4.2", decision.message)

    def test_the_tag_is_derived_when_the_caller_gives_none(self):
        decision = decide("0.4.2", tag_exists=False)
        self.assertIn("v0.4.2", decision.message)

    def test_an_existing_tag_with_nothing_shipped_since_stays_green(self):
        decision = decide(
            "0.4.1",
            tag_exists=True,
            tag="v0.4.1",
            changed=[
                "README.md",
                "docs/releases.md",
                "web/downloads/index.html",
                "tests/UrDatabase.Tests/ScanServiceTests.cs",
                ".github/workflows/pr.yml",
            ],
        )
        self.assertFalse(decision.publish)
        self.assertTrue(decision.ok)
        self.assertIn("Nothing released", decision.summary)

    def test_an_existing_tag_with_shipped_code_since_fails(self):
        # The #50 / #52 collision, stated as a rule: this is the state where a
        # merge shipped nothing and should have.
        decision = decide(
            "0.4.1",
            tag_exists=True,
            tag="v0.4.1",
            changed=["README.md", "src/UrDatabase.App/Services/PosterCache.cs"],
        )
        self.assertFalse(decision.publish)
        self.assertFalse(decision.ok)
        self.assertIn("PosterCache.cs", decision.message)
        # The thing to actually do, and the thing not to do.
        self.assertIn("0.4.2", decision.summary)
        self.assertIn("Do not move the `v0.4.1` tag", decision.summary)

    def test_only_the_shipped_paths_are_listed(self):
        decision = decide(
            "0.4.1",
            tag_exists=True,
            tag="v0.4.1",
            changed=["docs/releases.md", "src/UrDatabase.App/Program.cs"],
        )
        self.assertIn("Program.cs", decision.summary)
        self.assertNotIn("releases.md", decision.summary)

    def test_a_diff_given_as_text_is_read_the_way_git_prints_it(self):
        decision = decide(
            "0.4.1",
            tag_exists=True,
            tag="v0.4.1",
            changed="src/UrDatabase.App/Program.cs\n\nREADME.md\n",
        )
        self.assertFalse(decision.ok)
        self.assertIn("Program.cs", decision.message)

    def test_an_unreadable_version_still_fails_rather_than_guessing(self):
        # The workflow validates the version before this runs, so this is the
        # hand-run case. It must not crash trying to suggest the next patch.
        decision = decide(
            "$(SomeProperty)",
            tag_exists=True,
            tag="v$(SomeProperty)",
            changed=["src/UrDatabase.App/Program.cs"],
        )
        self.assertFalse(decision.ok)
        self.assertIn("any higher version", decision.summary)

    def test_a_long_list_is_summarised_rather_than_dumped(self):
        paths = ["src/UrDatabase.App/File%02d.cs" % index for index in range(40)]
        decision = decide("0.4.1", tag_exists=True, tag="v0.4.1", changed=paths)
        self.assertIn("...and 20 more", decision.summary)
        self.assertIn("40 file(s)", decision.summary)

    def test_listing_leaves_a_short_list_alone(self):
        self.assertEqual(listing(["a", "b"], bullet="", quote=""), "a\nb")


class SharedRuleTests(unittest.TestCase):
    """The two checks have to mean the same thing by "shipped".

    The pull request check demands a bump when `src/` changes; this one calls
    a merge stranded on the same grounds. If they ever disagreed, one of two
    things happens: a change is blocked before merging and then declared
    unreleasable after it, or -- worse -- neither notices.
    """

    PATHS = [
        "src/UrDatabase.App/Program.cs",
        "src/UrDatabase.App/Data/schema.sql",
        "README.md",
        "docs/releases.md",
        "tests/UrDatabase.Tests/ScanServiceTests.cs",
        "web/downloads/index.html",
        ".github/workflows/release.yml",
        "tool/check_release_gate.py",
        "Directory.Build.props",
    ]

    def test_both_checks_agree_on_every_path(self):
        for path in self.PATHS:
            with self.subTest(path=path):
                self.assertEqual(
                    bool(shipped([path])),
                    touches_shipped_code([path]),
                    "%s is shipped code to one check and not the other" % path,
                )

    def test_the_rule_is_imported_rather_than_copied(self):
        # A second list of prefixes in this file would pass the test above on
        # the day it was written and drift the day either is extended.
        source = (REPO_ROOT / "tool" / "check_release_gate.py").read_text(
            encoding="utf-8"
        )
        self.assertNotIn("SHIPPED_PREFIXES =", source)
        self.assertIn("from check_version_bump import", source)


class DiffTests(unittest.TestCase):
    """Reading the diff out of git."""

    def setUp(self):
        self.repo = temporary_repository(self)
        self.repo.write("Directory.Build.props", "<Project/>\n")
        self.repo.write("src/UrDatabase.App/Program.cs", "// one\n")
        self.repo.commit("the released commit")
        self.repo.tag("v0.4.1")

    def test_nothing_changed_since_the_tag(self):
        self.assertEqual(changed_between("v0.4.1", "HEAD", self.repo.path), [])

    def test_what_changed_since_the_tag(self):
        self.repo.write("src/UrDatabase.App/Program.cs", "// two\n")
        self.repo.write("README.md", "docs\n")
        self.repo.commit("a fix and a note")
        self.assertEqual(
            sorted(changed_between("v0.4.1", "HEAD", self.repo.path)),
            ["README.md", "src/UrDatabase.App/Program.cs"],
        )

    def test_a_tag_that_is_not_there_is_loud(self):
        # The failure this must never have is a quiet empty diff, which reads
        # as "nothing shipped since" and publishes nothing while reporting
        # success -- the exact bug this gate exists to end.
        with self.assertRaises(RefError):
            changed_between("v9.9.9", "HEAD", self.repo.path)

    def test_a_head_that_is_not_there_is_loud(self):
        with self.assertRaises(RefError):
            changed_between("v0.4.1", "0" * 40, self.repo.path)


class GateTests(unittest.TestCase):
    """End to end, the way the workflow calls it.

    Built as the repository actually was at 23:53 on the night of the first
    collision: `v0.4.1` published, and a further commit on `main` carrying a
    bug fix that no release contains.
    """

    def setUp(self):
        self.repo = temporary_repository(self)
        self.repo.write("Directory.Build.props", "<Version>0.4.1</Version>\n")
        self.repo.write("src/UrDatabase.App/Services/PosterCache.cs", "// one\n")
        self.repo.commit("the commit v0.4.1 was cut from")
        self.repo.tag("v0.4.1")

        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        self.output = Path(self.scratch.name) / "output.txt"
        self.summary = Path(self.scratch.name) / "summary.md"

    def run_gate(self, version="0.4.1", tag="v0.4.1", tag_exists="true"):
        """The gate, as (exit code, printed, $GITHUB_OUTPUT, the summary).

        Both Actions files are redirected into this test's temporary
        directory. Left alone they are whatever the surrounding job set, and
        this suite runs inside one -- a test that appended to the real
        `$GITHUB_STEP_SUMMARY` would write its own fixtures onto the run page.
        """
        printed = io.StringIO()
        environment = {
            "GITHUB_OUTPUT": str(self.output),
            "GITHUB_STEP_SUMMARY": str(self.summary),
        }
        with mock.patch.dict(os.environ, environment), contextlib.redirect_stdout(
            printed
        ):
            code = main(
                [
                    "--version",
                    version,
                    "--tag",
                    tag,
                    "--tag-exists",
                    tag_exists,
                    "--head",
                    "HEAD",
                    "--repo",
                    self.repo.path,
                ]
            )
        return (
            code,
            printed.getvalue(),
            self.output.read_text(encoding="utf-8") if self.output.exists() else "",
            self.summary.read_text(encoding="utf-8") if self.summary.exists() else "",
        )

    def test_a_merge_that_stranded_shipped_code_fails(self):
        self.repo.write("src/UrDatabase.App/Services/PosterCache.cs", "// two\n")
        self.repo.commit("fix(posters): expand the cache path")

        code, printed, output, summary = self.run_gate()

        self.assertEqual(code, 1)
        self.assertIn("publish=false", output)
        self.assertIn("PosterCache.cs", summary)
        self.assertIn("in no release", summary)
        # An annotation as well, so this is on the run page rather than only in
        # a step nobody expands.
        self.assertIn("::error title=Merged code is in no release::", printed)

    def test_a_documentation_merge_stays_green_and_quiet(self):
        self.repo.write("README.md", "a note\n")
        self.repo.write("docs/releases.md", "another\n")
        self.repo.commit("docs: explain the gate")

        code, printed, output, summary = self.run_gate()

        self.assertEqual(code, 0)
        self.assertIn("publish=false", output)
        self.assertIn("Nothing released", summary)
        self.assertNotIn("::error", printed)

    def test_re_running_on_the_tagged_commit_itself_is_a_no_op(self):
        # `workflow_dispatch` against a `main` that is exactly the release.
        code, _, output, _ = self.run_gate()
        self.assertEqual(code, 0)
        self.assertIn("publish=false", output)

    def test_a_bumped_version_publishes(self):
        self.repo.write("Directory.Build.props", "<Version>0.4.2</Version>\n")
        self.repo.write("src/UrDatabase.App/Services/PosterCache.cs", "// two\n")
        self.repo.commit("fix(posters): expand the cache path")

        code, _, output, summary = self.run_gate(
            version="0.4.2", tag="v0.4.2", tag_exists="false"
        )

        self.assertEqual(code, 0)
        self.assertIn("publish=true", output)
        # Nothing written to the run summary: the steps that follow describe
        # the release they actually made.
        self.assertEqual(summary, "")

    def test_a_tag_the_remote_has_and_this_clone_does_not_fails_loudly(self):
        # The workflow fetches the tag first, so reaching this means the fetch
        # failed. Publishing nothing and reporting success here would be the
        # original bug wearing a different hat.
        code, printed, output, _ = self.run_gate(tag="v0.9.9")
        self.assertEqual(code, 1)
        self.assertIn("publish=false", output)
        self.assertIn("Release gate could not read the tag", printed)

    def test_no_version_publishes_nothing_and_passes(self):
        code, _, output, summary = self.run_gate(
            version="", tag="", tag_exists="false"
        )
        self.assertEqual(code, 0)
        self.assertIn("publish=false", output)
        self.assertIn("does not set a `<Version>`", summary)

    def test_an_unreadable_tag_exists_answer_is_refused(self):
        # Not defaulted to false. That answer sends the workflow on to push a
        # tag that is already there, which fails halfway through a release
        # rather than before it starts.
        with self.assertRaises(SystemExit), contextlib.redirect_stderr(io.StringIO()):
            main(["--version", "0.4.1", "--tag-exists", "probably"])


class WorkflowTests(unittest.TestCase):
    """What the workflow hands the gate.

    Everything above tests the script, and the script is not the part that can
    silently stop working. Nothing else in this suite can see `release.yml`, so
    a gate that stopped calling this, stopped fetching the tag, or swallowed
    its exit code would restore the silence with the whole suite green.
    """

    def setUp(self):
        self.workflow = (
            REPO_ROOT / ".github" / "workflows" / "release.yml"
        ).read_text(encoding="utf-8")

    # Matched on the invocation rather than on the filename: the step above it
    # names the script in a comment, and a test that found the comment would
    # pass on a workflow that had stopped running anything.
    INVOCATION = "python3 tool/check_release_gate.py"

    def gate(self):
        """Just the step that decides, so the assertions below cannot be
        satisfied by something elsewhere in a 500-line workflow."""
        start = self.workflow.index("- name: Decide whether to publish")
        return self.workflow[start : self.workflow.index("- uses: actions/setup-dotnet@v4")]

    def test_the_gate_runs_the_check(self):
        self.assertIn(self.INVOCATION, self.workflow)

    def test_the_tag_is_fetched_before_it_is_compared(self):
        # The checkout brings the tags that existed when it ran; the reason the
        # remote is consulted at all is that one may have appeared since.
        self.assertNotEqual(
            self.workflow.find("git fetch"),
            -1,
            "release.yml never fetches the tag it compares against",
        )
        self.assertLess(
            self.workflow.index("git fetch"),
            self.workflow.index(self.INVOCATION),
            "release.yml compares against the tag before fetching it",
        )

    def test_the_gate_is_given_the_commit_being_released(self):
        called = re.search(
            re.escape(self.INVOCATION) + r"(?:[^\n]*\\\n)*[^\n]*", self.workflow
        )
        self.assertIsNotNone(called)
        self.assertIn("--head", called.group(0))
        self.assertIn("--tag-exists", called.group(0))

    def test_the_gates_failure_is_not_swallowed(self):
        self.assertNotIn("continue-on-error", self.gate())
        self.assertNotIn("|| true", self.gate())
        self.assertIn("set -euo pipefail", self.gate())

    def test_a_remote_that_cannot_be_reached_is_not_read_as_a_free_tag(self):
        # `git ls-remote --exit-code` says 2 for "no such tag" and 128 for
        # "could not ask". Collapsing those into one boolean would send a run
        # that could not reach the remote on to build, sign and notarize for
        # the best part of an hour before failing on a tag push that was never
        # going to work.
        gate = self.gate()
        self.assertNotIn(
            "if [ -n \"$TAG\" ] \\", gate, "release.yml ignores the lookup's exit code"
        )
        self.assertIn("LOOKUP=$?", gate)
        self.assertIn("2)", gate)


if __name__ == "__main__":
    unittest.main()
