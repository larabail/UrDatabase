#!/usr/bin/env python3
"""Tests for the version bump check.

Run with: python3 -m unittest discover -s tool -p "test_*.py"

The check is a gate on every pull request, so its two failure modes are both
expensive: letting a change through without a version means a merge that
publishes nothing, and blocking a change that needed no version means a
documentation fix sitting behind a red check nobody can clear. Both are decided
by inputs -- a diff, a file and a commit on `main` -- that are awkward to
reproduce by hand, so they are pinned here rather than discovered on somebody's
branch.

Some of these build a real git repository in a temporary directory. Which
commit the check reads is the thing that went wrong, and a stub for `git show`
would only prove that the stub was asked politely.
"""

import contextlib
import io
import re
import subprocess
import tempfile
import unittest
from pathlib import Path

from check_version_bump import (
    RefError,
    VersionError,
    changed_paths,
    check,
    main,
    parse_version,
    props_at_ref,
    touches_shipped_code,
    version_from_props,
)

REPO_ROOT = Path(__file__).resolve().parent.parent


def props(version):
    """A `Directory.Build.props` the way the repository actually writes one."""
    return (
        "<Project>\n"
        "  <PropertyGroup>\n"
        "    <Version>%s</Version>\n"
        "    <Nullable>enable</Nullable>\n"
        "  </PropertyGroup>\n"
        "</Project>\n" % version
    )


class Repository:
    """A throwaway git repository to read refs out of."""

    def __init__(self, path):
        self.path = str(path)
        self.git("init", "--quiet")
        # Named rather than inherited: `git init` takes the default branch
        # name from whatever the machine is configured with, and these tests
        # say `main`.
        self.git("symbolic-ref", "HEAD", "refs/heads/main")
        self.git("config", "user.email", "checks@example.invalid")
        self.git("config", "user.name", "Version check tests")
        # A contributor who signs commits globally would otherwise watch these
        # fail for a reason that has nothing to do with versions.
        self.git("config", "commit.gpgsign", "false")

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
        self.git("add", "-A")
        self.git("commit", "--quiet", "--message", message)
        return self.git("rev-parse", "HEAD").strip()


def temporary_repository(test):
    """A `Repository` that is deleted when [test] finishes."""
    directory = tempfile.TemporaryDirectory()
    test.addCleanup(directory.cleanup)
    return Repository(directory.name)


class ParseVersionTests(unittest.TestCase):
    def test_reads_three_numbers(self):
        self.assertEqual(parse_version("0.1.0"), (0, 1, 0))
        self.assertEqual(parse_version("12.3.45"), (12, 3, 45))

    def test_tolerates_surrounding_space(self):
        self.assertEqual(parse_version("  1.2.3\n"), (1, 2, 3))

    def test_refuses_anything_that_is_not_a_version(self):
        for raw in ["", "1.2", "1.2.3.4", "v1.2.3", "1.2.3-preview", "$(Ver)", None, 3]:
            with self.assertRaises(VersionError, msg="accepted %r" % (raw,)):
                parse_version(raw)

    def test_orders_by_number_not_by_text(self):
        # As text "0.9.0" sorts after "0.10.0", which would let a branch move
        # the version backwards on the tenth minor release -- exactly when
        # nobody is looking for it.
        self.assertGreater(parse_version("0.10.0"), parse_version("0.9.0"))


class VersionFromPropsTests(unittest.TestCase):
    def test_reads_the_version_element(self):
        self.assertEqual(version_from_props(props("0.3.1")), "0.3.1")

    def test_tolerates_whitespace_inside_the_element(self):
        self.assertEqual(version_from_props("<Version> 1.0.0 </Version>"), "1.0.0")

    def test_returns_none_when_the_file_states_no_version(self):
        self.assertIsNone(version_from_props("<Project></Project>"))
        self.assertIsNone(version_from_props("<Version></Version>"))
        self.assertIsNone(version_from_props(None))

    def test_returns_the_raw_text_so_the_caller_can_complain_about_it(self):
        # Reading and validating are separate steps: the message for "that is
        # not a version" needs the text that was actually there.
        self.assertEqual(version_from_props(props("nonsense")), "nonsense")


class ChangedPathsTests(unittest.TestCase):
    def test_splits_lines(self):
        self.assertEqual(changed_paths("a.txt\nb/c.txt\n"), ["a.txt", "b/c.txt"])

    def test_drops_the_empty_line_an_empty_diff_leaves(self):
        self.assertEqual(changed_paths(""), [])
        self.assertEqual(changed_paths("\n\n"), [])


class TouchesShippedCodeTests(unittest.TestCase):
    def test_src_ships(self):
        self.assertTrue(touches_shipped_code(["src/UrDatabase.App/App.axaml.cs"]))

    def test_the_downloads_site_does_not(self):
        self.assertFalse(touches_shipped_code(["web/downloads/index.html"]))

    def test_docs_tests_and_workflows_do_not(self):
        self.assertFalse(
            touches_shipped_code(
                [
                    "README.md",
                    "docs/releases.md",
                    "tests/UrDatabase.Tests/DatabaseTests.cs",
                    ".github/workflows/pr.yml",
                    "firebase.json",
                ]
            )
        )

    def test_one_shipped_file_among_many_is_enough(self):
        self.assertTrue(
            touches_shipped_code(["README.md", "src/UrDatabase.App/Program.cs"])
        )

    def test_a_directory_that_merely_starts_the_same_way_does_not_count(self):
        # "srcinfo.md" starts with "src" but not with "src/".
        self.assertFalse(touches_shipped_code(["srcinfo.md"]))


class CheckTests(unittest.TestCase):
    def test_passes_a_change_that_ships_nothing(self):
        result = check(props("0.1.0"), props("0.1.0"), ["web/downloads/index.html"])
        self.assertTrue(result.ok)
        self.assertIn("no version bump is required", result.message)

    def test_passes_a_docs_only_change_at_the_same_version(self):
        result = check(props("0.1.0"), props("0.1.0"), ["docs/releases.md"])
        self.assertTrue(result.ok)

    def test_passes_when_src_changed_and_the_version_moved(self):
        result = check(props("0.1.0"), props("0.1.1"), ["src/UrDatabase.App/Db.cs"])
        self.assertTrue(result.ok)
        self.assertIn("0.1.0 to 0.1.1", result.message)

    def test_passes_a_minor_and_a_major_bump_too(self):
        for version in ["0.2.0", "1.0.0"]:
            result = check(props("0.1.0"), props(version), ["src/App.axaml"])
            self.assertTrue(result.ok, version)

    def test_fails_when_src_changed_and_the_version_did_not(self):
        result = check(props("0.1.0"), props("0.1.0"), ["src/UrDatabase.App/Db.cs"])
        self.assertFalse(result.ok)
        self.assertIn("still 0.1.0", result.message)
        # The message has to name the file to edit and a version to put in it,
        # or it is just the rule restated at somebody who has already read it.
        self.assertIn("Directory.Build.props", result.message)
        self.assertIn("0.1.1", result.message)

    def test_fails_when_the_version_went_backwards(self):
        result = check(props("0.4.0"), props("0.3.9"), ["src/UrDatabase.App/Db.cs"])
        self.assertFalse(result.ok)
        self.assertIn("backwards", result.message)

    def test_fails_when_src_changed_and_there_is_no_version_at_all(self):
        result = check(None, "<Project></Project>", ["src/UrDatabase.App/Db.cs"])
        self.assertFalse(result.ok)
        self.assertIn("does not\nset a <Version>", result.message)

    def test_fails_on_a_version_the_release_workflow_could_not_use(self):
        result = check(props("0.1.0"), props("0.2.0-preview"), ["src/App.axaml"])
        self.assertFalse(result.ok)
        self.assertIn("MAJOR.MINOR.PATCH", result.message)

    def test_passes_when_main_has_no_version_yet(self):
        # The state of this repository until Directory.Build.props lands. The
        # first branch to add one must not be blocked by the check that reads
        # it.
        result = check(None, props("0.1.0"), ["src/UrDatabase.App/Db.cs"])
        self.assertTrue(result.ok)
        self.assertIn("main sets no version yet", result.message)

    def test_passes_but_complains_when_main_is_unreadable(self):
        # A broken version on main must not block the branch that fixes it.
        result = check(props("garbage"), props("0.1.0"), ["src/UrDatabase.App/Db.cs"])
        self.assertTrue(result.ok)
        self.assertIn("main's Directory.Build.props", result.message)

    def test_accepts_the_raw_output_of_git_diff(self):
        result = check(props("0.1.0"), props("0.1.0"), "src/App.axaml\nREADME.md\n")
        self.assertFalse(result.ok)

    def test_an_empty_diff_requires_nothing(self):
        result = check(props("0.1.0"), props("0.1.0"), "")
        self.assertTrue(result.ok)


class PropsAtRefTests(unittest.TestCase):
    def setUp(self):
        self.repo = temporary_repository(self)

    def test_reads_the_file_as_it_stood_at_that_ref(self):
        self.repo.write("Directory.Build.props", props("0.4.0"))
        first = self.repo.commit("adopt a version")
        self.repo.write("Directory.Build.props", props("0.4.1"))
        self.repo.commit("bump")

        self.assertEqual(
            version_from_props(props_at_ref("main", self.repo.path)), "0.4.1"
        )
        self.assertEqual(
            version_from_props(props_at_ref(first, self.repo.path)), "0.4.0"
        )

    def test_a_ref_without_the_file_reads_as_no_version_yet(self):
        # The state of this repository until Directory.Build.props landed.
        # The workflow used to spell this `|| true`, and it has to keep
        # meaning "main states no version yet" rather than becoming an error.
        self.repo.write("README.md", "# UrDatabase\n")
        self.repo.commit("first commit")
        self.assertIsNone(props_at_ref("main", self.repo.path))

    def test_a_ref_that_does_not_resolve_is_an_error_and_not_a_shrug(self):
        # The failure that would be worse than the bug this replaced. If an
        # unreachable main read as an absent file, every pull request would
        # pass with "main sets no version yet" and nothing would say so.
        self.repo.write("Directory.Build.props", props("0.4.0"))
        self.repo.commit("adopt a version")
        with self.assertRaises(RefError):
            props_at_ref("refs/remotes/origin/main", self.repo.path)


class LiveBaseTests(unittest.TestCase):
    """The version is compared against main as it is now.

    Two pull requests were once both green carrying 0.4.1 while main sat at
    0.4.0. One merged and took the tag; the other merged twelve seconds later,
    found v0.4.1 already published, and shipped nothing at all. Both had
    cleared a check that read the base commit their own event recorded rather
    than the branch they were about to land on. That is the repository state
    built below.
    """

    def setUp(self):
        self.repo = temporary_repository(self)
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)

        # main as both branches saw it when they opened.
        self.repo.write("Directory.Build.props", props("0.4.0"))
        self.repo.write("src/UrDatabase.App/Config.cs", "// one\n")
        self.recorded_base = self.repo.commit("the base both branches recorded")

        # Somebody else's pull request merges, taking 0.4.1 with it.
        self.repo.write("Directory.Build.props", props("0.4.1"))
        self.repo.commit("fix(posters): stage cache writes")

        # This branch, which took 0.4.1 too, from the same 0.4.0.
        self.head_props = Path(scratch.name) / "head.props"
        self.head_props.write_text(props("0.4.1"), encoding="utf-8")
        self.changed = Path(scratch.name) / "changed.txt"
        self.changed.write_text("src/UrDatabase.App/Config.cs\n", encoding="utf-8")

    def run_check(self, base_ref):
        """The check against [base_ref], as (exit code, what it printed)."""
        printed = io.StringIO()
        with contextlib.redirect_stdout(printed):
            code = main(
                [
                    "--base-ref",
                    base_ref,
                    "--repo",
                    self.repo.path,
                    "--head-props",
                    str(self.head_props),
                    "--changed",
                    str(self.changed),
                ]
            )
        return code, printed.getvalue()

    def test_fails_against_the_version_main_carries_now(self):
        code, printed = self.run_check("main")
        self.assertEqual(code, 1)
        self.assertIn("still 0.4.1", printed)

    def test_the_recorded_base_is_what_used_to_let_this_through(self):
        # Not a rule being asserted -- its opposite. This pins the difference
        # between the two refs, so "compare against the tip" cannot be quietly
        # weakened back into "compare against the event's base" while every
        # test stays green.
        code, _ = self.run_check(self.recorded_base)
        self.assertEqual(code, 0)

    def test_passes_once_the_branch_clears_the_version_main_reached(self):
        self.head_props.write_text(props("0.4.2"), encoding="utf-8")
        code, printed = self.run_check("main")
        self.assertEqual(code, 0)
        self.assertIn("0.4.1 to 0.4.2", printed)

    def test_a_base_ref_it_cannot_resolve_fails_loudly(self):
        code, printed = self.run_check("refs/remotes/origin/main")
        self.assertEqual(code, 1)
        self.assertIn("refs/remotes/origin/main", printed)


class WorkflowTests(unittest.TestCase):
    """What the workflow hands the check.

    Everything above tests the script, and the script was never the part that
    was wrong -- it compared what it was given. Nothing else in this suite can
    see `pr.yml`, so a change there that went back to the base the pull
    request event recorded would restore the bug with the whole suite still
    green.
    """

    def setUp(self):
        self.workflow = (REPO_ROOT / ".github" / "workflows" / "pr.yml").read_text(
            encoding="utf-8"
        )

    def test_the_check_is_given_a_ref_rather_than_the_events_base(self):
        named = re.search(r"--base-ref\s+\"?([^\"\s\\]+)", self.workflow)
        self.assertIsNotNone(named, "pr.yml does not call the check with --base-ref")
        base_ref = named.group(1)
        self.assertNotIn("BASE_SHA", base_ref)
        self.assertIn("refs/remotes/origin/", base_ref)

    def test_the_base_branch_is_fetched_before_it_is_read(self):
        # A remote-tracking ref left behind by the checkout is only as fresh
        # as the checkout. The fetch is what makes "now" mean now.
        #
        # Asserted on the index rather than with assertIn so that a failure
        # prints a sentence instead of the whole workflow.
        self.assertNotEqual(
            self.workflow.find("git fetch"), -1, "pr.yml never fetches the base branch"
        )
        self.assertLess(
            self.workflow.index("git fetch"),
            self.workflow.index("--base-ref"),
            "pr.yml reads the base branch before it fetches it",
        )


if __name__ == "__main__":
    unittest.main()
