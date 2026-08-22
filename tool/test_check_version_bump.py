#!/usr/bin/env python3
"""Tests for the version bump check.

Run with: python3 -m unittest discover -s tool -p "test_*.py"

The check is a gate on every pull request, so its two failure modes are both
expensive: letting a change through without a version means a merge that
publishes nothing, and blocking a change that needed no version means a
documentation fix sitting behind a red check nobody can clear. Both are decided
by inputs -- a diff and two files -- that are awkward to reproduce by hand, so
they are pinned here rather than discovered on somebody's branch.
"""

import unittest

from check_version_bump import (
    VersionError,
    changed_paths,
    check,
    parse_version,
    touches_shipped_code,
    version_from_props,
)


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


if __name__ == "__main__":
    unittest.main()
