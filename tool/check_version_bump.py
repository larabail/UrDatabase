#!/usr/bin/env python3
"""Fail a pull request that changes the app without moving the version.

Every merge to `main` runs the release workflow, which reads `<Version>` from
`Directory.Build.props`, tags `v<Version>` and publishes a GitHub release
carrying that version in all three asset names. When the version does not move,
that workflow finds the tag already there and does nothing -- correctly, because
re-tagging would either fail or silently point an existing release at different
bytes. The visible result is a merge that ships nothing, which nobody notices
until somebody asks why the download is still the old one.

So the rule this enforces is narrow and mechanical: **a pull request that
changes anything under `src/` must raise `<Version>` above the version on
`main`.** Nothing else. In particular:

  * `web/downloads/` is a static site deployed straight to Firebase Hosting.
    It is never packaged into a build and no install differs because of it, so
    demanding a version bump for a typo on a web page would publish a release
    containing nothing a user could find.
  * `tests/`, `docs/` and `.github/` change what CI does or what the repository
    says, not what the shipped application does.

There is one exception, and it runs the other way: **no pull request may leave
`Directory.Build.props` without a version the release workflow can read**, even
one that ships nothing. Blank that line, or write something the workflow cannot
parse, and it tags nothing and reports success on every merge afterwards --
including the ones that do change `src/`, which then land on `main` in no
release with nothing red to say so. That is the state
`tool/check_release_gate.py` exists to make loud, and it is the one route into
it that the gate cannot see, because a version it cannot read names no tag it
could compare against.

Deliberately not checked here: how *much* the version moved. Whether a change
is a patch, a minor or a major is a judgement call, and a script that guessed
would be wrong often enough to be argued with rather than trusted. Any increase
passes.

The version it is compared *against* is read here, out of git, and that is what
`--base-ref` is for. A pull request event also carries a `base.sha`, which is
what the base branch was when the event was created, and comparing against that
answers a question nobody asked. Two pull requests once both took 0.4.1 while
`main` sat at 0.4.0; both cleared their own recorded base, and the one that
merged second found `v0.4.1` already tagged and shipped nothing. So the caller
names a ref -- a freshly fetched `refs/remotes/origin/main` -- and it is read at
the moment the check runs.

That narrows the window rather than closing it. A required check reports the
state at the moment it ran, and GitHub does not re-run every open pull
request's checks when something lands on `main`, so two branches can still both
be green and merge seconds apart. Only a merge queue closes that, and this
repository does not have one. What catches the one that gets through is
`tool/check_release_gate.py`, which fails the release run rather than skipping
quietly when a merge leaves `src/` newer than the tag it would have released
as -- after the fact, but never silently.

Usage, with the workflow deciding which files changed and this deciding what
`main` says about the version:

    git diff --name-only "$BASE" "$HEAD" > changed.txt
    python3 tool/check_version_bump.py \
        --base-ref refs/remotes/origin/main \
        --head-props Directory.Build.props \
        --changed changed.txt

Exits 0 when the rule is satisfied and 1 when it is not, printing in both cases
why, because "Version: failed" with no explanation is the check people learn to
re-run rather than read.
"""

import argparse
import re
import subprocess
import sys
from collections import namedtuple

# Matched with a regular expression rather than an XML parser on purpose.
# `Directory.Build.props` is edited by hand far more often than it is
# generated, and a half-saved file with a stray tag should report "no <Version>
# here" rather than an XML traceback that reads like a bug in CI.
VERSION_ELEMENT = re.compile(r"<Version>\s*([^<]*?)\s*</Version>")

# The one place a version number is allowed to live, at the root of the
# repository.
PROPS_PATH = "Directory.Build.props"

# The directories whose contents end up inside a published build. Everything
# else can change freely without a release having anything new to say.
SHIPPED_PREFIXES = ("src/",)

Result = namedtuple("Result", "ok message")


class VersionError(ValueError):
    """A version that is not `MAJOR.MINOR.PATCH`."""


class RefError(LookupError):
    """A git ref that does not name a commit."""


def parse_version(raw):
    """The (major, minor, patch) in [raw].

    Refuses anything else rather than guessing. A four-part version, a
    `-preview` tail or an MSBuild property reference like `$(Foo)` are all
    things this script would order differently from the way the release
    workflow's tag does, and a check that disagrees with the thing it is
    protecting is worse than no check.
    """
    if not isinstance(raw, str):
        raise VersionError("a version has to be text, not %r" % (raw,))
    match = re.match(r"^(\d+)\.(\d+)\.(\d+)$", raw.strip())
    if not match:
        raise VersionError(
            "%r is not MAJOR.MINOR.PATCH, for example 0.2.0" % (raw.strip(),)
        )
    return tuple(int(part) for part in match.groups())


def show(version):
    """A parsed version back as text."""
    return ".".join(str(part) for part in version)


def version_from_props(text):
    """The version in a `Directory.Build.props`, or None when it states none.

    None rather than an exception, because "this file does not set a version"
    is an ordinary state on a repository that has not adopted one yet, and the
    caller says something different about it than about a version it cannot
    read.
    """
    if not isinstance(text, str):
        return None
    match = VERSION_ELEMENT.search(text)
    if not match:
        return None
    return match.group(1).strip() or None


def changed_paths(text):
    """The paths in the output of `git diff --name-only`.

    Blank lines are dropped. `git diff` between two identical trees prints
    nothing, which splits into one empty string, and an empty path would
    otherwise be compared against the prefixes below.
    """
    if not isinstance(text, str):
        return []
    return [line.strip() for line in text.splitlines() if line.strip()]


def touches_shipped_code(paths):
    """Whether any of [paths] can change what a published build contains."""
    return any(
        path.startswith(prefix) for path in paths for prefix in SHIPPED_PREFIXES
    )


def next_patch(version):
    """The smallest version above [version], as a suggestion for a message."""
    major, minor, patch = version
    return "%d.%d.%d" % (major, minor, patch + 1)


def bump_instructions(current):
    """What to actually do, spelled out.

    Written as steps rather than as a restatement of the rule, because the
    person reading it is looking at a red check and wants the edit, not the
    reasoning. The reasoning is last so it is there if they want it.
    """
    return (
        "  What to do:\n"
        "    1. Open Directory.Build.props at the root of the repository.\n"
        "    2. Raise <Version> above %s -- %s for a fix, %d.%d.0 for a\n"
        "       feature, %d.0.0 for a breaking change.\n"
        "    3. Commit and push. This check runs again on its own.\n"
        "\n"
        "  Why: merging to main tags v<Version> and publishes a release whose\n"
        "  three zips carry that version in their names. Leaving it at %s\n"
        "  means the tag already exists, so the merge publishes nothing and\n"
        "  the release run fails saying so."
        % (
            show(current),
            next_patch(current),
            current[0],
            current[1] + 1,
            current[0] + 1,
            show(current),
        )
    )


def readable(text):
    """Whether a props file states a version the release workflow could use.

    Both halves of "usable" in one answer -- present, and three numbers --
    because the release workflow makes no distinction either. Its
    `read-version` action rejects anything that is not `MAJOR.MINOR.PATCH` and
    reports it exactly as it reports an absent file.
    """
    raw = version_from_props(text)
    if raw is None:
        return False
    try:
        parse_version(raw)
    except VersionError:
        return False
    return True


def lost_the_version(base):
    """What to say when a branch leaves `main`'s version unusable.

    Blocked even though the pull request ships nothing, which is the one
    exception to the rule above, because the damage is not to this change --
    it is to the next one. The release workflow reads that single line to
    decide what to tag; with nothing readable there it tags nothing, publishes
    nothing and reports success, and the merge after it is stranded on `main`
    with no release and nothing red to say so. Every other route into that
    state is now loud, and this was the last quiet one.
    """
    return Result(
        False,
        "This pull request changes nothing under src/, but it leaves\n"
        "Directory.Build.props without a <Version> the release workflow can\n"
        "read. main carries %s.\n"
        "\n"
        "  What to do: put <Version> back as three numbers, at %s or above,\n"
        "  for example:\n"
        "\n"
        "      <Version>%s</Version>\n"
        "\n"
        "  Why this is blocked even though nothing shipped changed: that one\n"
        "  line is what the release workflow tags from. With nothing readable\n"
        "  there it tags nothing, publishes nothing and says so in green --\n"
        "  and the next merge that does change src/ lands on main with no\n"
        "  release and nothing red to point at it."
        % (show(base), show(base), show(base)),
    )


def check(base_props, head_props, paths):
    """Whether [paths] and the two props files satisfy the bump rule.

    [base_props] is the file as it is on `main` and may be None when `main`
    does not have one at all, which is the state of any repository the moment
    before it adopts a version. [head_props] is the file on the branch, and may
    be None for the same reason.
    """
    changed = changed_paths(paths) if isinstance(paths, str) else list(paths)

    if not touches_shipped_code(changed):
        if readable(base_props) and not readable(head_props):
            return lost_the_version(parse_version(version_from_props(base_props)))
        return Result(
            True,
            "Nothing under src/ changed, so no version bump is required.\n"
            "Changes to the downloads site, docs, tests and workflows cannot\n"
            "alter a published build.",
        )

    head_raw = version_from_props(head_props)
    if head_raw is None:
        return Result(
            False,
            "This pull request changes src/, but Directory.Build.props does not\n"
            "set a <Version>.\n"
            "\n"
            "  What to do: add a <Version> property to Directory.Build.props at\n"
            "  the root of the repository, for example:\n"
            "\n"
            "      <Project>\n"
            "        <PropertyGroup>\n"
            "          <Version>0.1.0</Version>\n"
            "        </PropertyGroup>\n"
            "      </Project>\n"
            "\n"
            "  Why: it is the single source of truth for the release version.\n"
            "  The release workflow reads it, tags v<Version> and names every\n"
            "  release asset after it.",
        )

    try:
        head = parse_version(head_raw)
    except VersionError as error:
        return Result(
            False,
            "Directory.Build.props sets a <Version> this cannot read: %s\n"
            "\n"
            "  What to do: write it as three numbers, for example 0.2.0. The\n"
            "  release workflow turns it straight into the tag v0.2.0 and into\n"
            "  asset names like UrDatabase-0.2.0-osx-arm64.zip, so anything\n"
            "  else would end up in a filename." % (error,),
        )

    base_raw = version_from_props(base_props)
    if base_raw is None:
        return Result(
            True,
            "main sets no version yet, and this branch sets %s.\n"
            "Anything is an increase on nothing, so this passes." % (show(head),),
        )

    try:
        base = parse_version(base_raw)
    except VersionError as error:
        # main being unreadable is not this pull request's fault, and blocking
        # it would leave the only branch that could fix main unable to merge.
        return Result(
            True,
            "main's Directory.Build.props sets a <Version> this cannot read:\n"
            "%s\n"
            "Passing anyway -- that is a problem on main, not in this branch --\n"
            "but it is worth fixing, because until it is, this check cannot\n"
            "tell whether a version went backwards." % (error,),
        )

    if head > base:
        return Result(
            True,
            "src/ changed and the version moved from %s to %s."
            % (show(base), show(head)),
        )

    if head == base:
        return Result(
            False,
            "This pull request changes src/, but <Version> is still %s, the\n"
            "same as on main.\n"
            "\n%s" % (show(base), bump_instructions(base)),
        )

    return Result(
        False,
        "This pull request changes src/ and moves <Version> backwards, from\n"
        "%s on main to %s here.\n"
        "\n"
        "  That is almost always a merge resolved the wrong way round: main\n"
        "  moved on while this branch was open and the older side won.\n"
        "\n%s" % (show(base), show(head), bump_instructions(base)),
    )


def ref_exists(ref, repo="."):
    """Whether [ref] names a commit in [repo]."""
    resolved = subprocess.run(
        ["git", "-C", repo, "rev-parse", "--verify", "--quiet", "%s^{commit}" % ref],
        capture_output=True,
    )
    return resolved.returncode == 0


def props_at_ref(ref, repo=".", path=PROPS_PATH):
    """The props file at [ref], or None when that commit does not have one.

    None means one thing only: the commit exists and does not contain the
    file, which is the state of any repository the moment before it adopts a
    version. A ref that does not resolve raises instead, and that distinction
    is the point of doing this here rather than in the shell. `git show` fails
    the same way for both, so a workflow that shrugged the failure off with
    `|| true` would read an unreachable `main` as "main states no version yet"
    -- and that answer passes every pull request, silently, exactly when the
    check has stopped working.
    """
    if not ref_exists(ref, repo):
        raise RefError("%s does not name a commit in %s" % (ref, repo))
    found = subprocess.run(
        ["git", "-C", repo, "show", "%s:%s" % (ref, path)],
        capture_output=True,
        text=True,
    )
    return found.stdout if found.returncode == 0 else None


def read_optional(path):
    """The contents of [path], or None when it is not there.

    Missing is an expected answer: a branch that has not adopted a version has
    no `Directory.Build.props` to read, and that is reported as an absent file
    rather than as an error.
    """
    if not path:
        return None
    try:
        with open(path, "r", encoding="utf-8") as handle:
            return handle.read()
    except OSError:
        return None


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--base-ref",
        required=True,
        help="A git ref for the branch this will merge into, read as it is "
        "now rather than as the pull request event recorded it. Usually "
        "refs/remotes/origin/main.",
    )
    parser.add_argument(
        "--repo",
        default=".",
        help="The repository to read --base-ref from. Defaults to the working "
        "directory.",
    )
    parser.add_argument(
        "--head-props",
        default=PROPS_PATH,
        help="%s as it is on this branch." % PROPS_PATH,
    )
    parser.add_argument(
        "--changed",
        required=True,
        help="A file holding the output of `git diff --name-only`.",
    )
    args = parser.parse_args(argv)

    changed = read_optional(args.changed)
    if changed is None:
        print("Could not read the list of changed files at %s." % args.changed)
        return 1

    try:
        base_props = props_at_ref(args.base_ref, args.repo)
    except RefError as error:
        print(
            "Could not read %s: %s.\n"
            "\n"
            "  What to do: this is the check being broken rather than the\n"
            "  branch. The workflow fetches the base branch immediately\n"
            "  before running this, so either that fetch failed or the ref it\n"
            "  fetches and the ref it reads have drifted apart.\n"
            "\n"
            "  Why this is fatal rather than ignored: a base nobody can read\n"
            "  would otherwise look exactly like a base that states no version\n"
            "  yet, which passes every pull request." % (PROPS_PATH, error)
        )
        print(
            "::error title=Version check could not read the base branch::%s"
            % (error,)
        )
        return 1

    result = check(base_props, read_optional(args.head_props), changed)
    print(result.message)
    if not result.ok:
        # An annotation as well as the text, so the reason shows at the top of
        # the pull request instead of only inside a collapsed log.
        first = result.message.splitlines()[0]
        print("::error title=Version bump required::%s" % first)
    return 0 if result.ok else 1


if __name__ == "__main__":
    sys.exit(main())
