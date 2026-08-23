#!/usr/bin/env python3
"""Decide whether a merge to `main` has anything to release, and go red when it
should have had something and did not.

Every push to `main` runs the release workflow. It reads `<Version>` from
`Directory.Build.props`, and if `v<Version>` is not tagged yet it builds, tags
and publishes. If the tag is already there it publishes nothing, because
re-tagging would either fail or point an existing release at different bytes.

That skip is right, and it used to be *silent*: the run wrote a line saying the
tag existed and reported success. Two pull requests both took `0.4.1` while
`main` sat at `0.4.0`, both went green, and they merged twelve seconds apart.
The first tagged `v0.4.1`; the second merged a bug fix into `main` that was in
no release at all, and nothing anywhere was red. It happened again hours later
at `0.4.2`. Both cost far more to find than to fix, and the reason was the
silence rather than the collision.

So the skip is split in two, and which one a merge gets turns on the same
question the pull request check asks -- did anything under `src/` change:

  * **Nothing shipped changed since the tag.** The ordinary outcome for a merge
    that touched docs, workflows or the downloads site. There is genuinely
    nothing to publish; the run says so and stays green.
  * **`src/` changed since the tag.** Somebody's shipped code is on `main` and
    in no release, and only a version bump can rescue it. The run fails.

That is deliberately a *report* rather than a guard. It cannot stop the
collision -- by the time this runs the merge has happened -- and nothing here
tries to. Only a merge queue would prevent it. What this does is make sure the
state is never reached quietly: a failed run on `main` mails the owner, where a
green one saying "already tagged" trains everybody to read nothing.

The comparison is against the tagged commit rather than against the previous
push, because the question is "what is on `main` that no release contains", and
several merges can accumulate before anybody looks.

Usage, with the workflow answering the one question that needs the network --
whether the remote already has the tag -- and this deciding what follows:

    python3 tool/check_release_gate.py \
        --version 0.4.1 \
        --tag-exists true \
        --tag v0.4.1 \
        --head "$GITHUB_SHA"

Writes `publish=true|false` to `$GITHUB_OUTPUT` for the steps that follow,
appends an explanation to `$GITHUB_STEP_SUMMARY`, and exits 1 only for the
stranded case above.
"""

import argparse
import os
import subprocess
import sys
from collections import namedtuple

# Shared with the pull request check on purpose. "Which paths can change a
# published build" is one rule with two enforcement points -- before the merge
# and after it -- and two copies of the answer would drift the first time
# somebody adds a directory to one of them.
from check_version_bump import (
    RefError,
    VersionError,
    changed_paths,
    next_patch,
    parse_version,
    ref_exists,
    touches_shipped_code,
)

# How many stranded paths to name before summarising the rest. Long enough to
# recognise the change, short enough that a summary stays readable when a merge
# rewrote half the application.
PATHS_SHOWN = 20

Decision = namedtuple("Decision", "publish ok message summary")


def shipped(paths):
    """The paths in [paths] that end up inside a published build."""
    return [path for path in paths if touches_shipped_code([path])]


def listing(paths, bullet="- ", quote="`"):
    """[paths] as a list, with a tail when there are more than fit.

    Used twice over the same paths: once as markdown for the run summary and
    once as indented plain text for the log, which is why the decoration is
    passed in rather than baked in.
    """
    shown = paths[:PATHS_SHOWN]
    lines = ["%s%s%s%s" % (bullet, quote, path, quote) for path in shown]
    remaining = len(paths) - len(shown)
    if remaining > 0:
        lines.append("%s...and %d more" % (bullet, remaining))
    return "\n".join(lines)


def bump_suggestion(version):
    """What to raise the version to, as text, or None when it cannot be read.

    A version this cannot parse is not a reason to say nothing: the release
    workflow only reaches this script with a version it already validated, so
    the fallback exists for the caller who runs it by hand.
    """
    try:
        current = parse_version(version)
    except VersionError:
        return None
    return "`%s` for a fix, `%d.%d.0` for a feature" % (
        next_patch(current),
        current[0],
        current[1] + 1,
    )


def no_version():
    """What to do when the tree states no version at all.

    Green, and it has to be: a repository is in this state until it adopts a
    version, and failing here would block the very merge that adds one. It is
    also the one answer here that cannot be checked -- a version the workflow
    cannot read names no tag, so there is nothing to compare `main` against and
    no way to tell a repository with nothing to release from one that has
    stranded everything.

    What keeps that from being a way round this check is the pull request side:
    `check_version_bump` refuses any branch that leaves `main`'s version
    unreadable, whether or not it ships anything. This state is therefore
    reachable only before the first version, or by a push that went round
    branch protection.
    """
    return Decision(
        publish=False,
        ok=True,
        message=(
            "Directory.Build.props states no <Version>, so there is nothing to "
            "tag.\nNot an error: a repository is in this state until it adopts "
            "a version."
        ),
        summary=(
            "### Nothing released\n"
            "\n"
            "`Directory.Build.props` does not set a `<Version>`, so there is no\n"
            "version to tag. Add one at the repository root and the next merge\n"
            "publishes it.\n"
        ),
    )


def publishable(tag):
    """What to do when the tag is free."""
    return Decision(
        publish=True,
        ok=True,
        message="%s does not exist yet. Publishing." % tag,
        summary="",
    )


def nothing_new(tag):
    """What to do when the tag exists and no shipped file has moved since."""
    return Decision(
        publish=False,
        ok=True,
        message=(
            "%s is already tagged and nothing under src/ has changed since it\n"
            "was published, so this merge ships nothing. That is the ordinary\n"
            "outcome for a merge that only touched docs, workflows or the\n"
            "downloads site." % (tag,)
        ),
        summary=(
            "### Nothing released\n"
            "\n"
            "The tag `%s` already exists and nothing under `src/` has changed\n"
            "since it was published, so this merge has nothing new to ship. That\n"
            "is the ordinary outcome for a merge that only touched docs,\n"
            "workflows or the downloads site.\n"
            "\n"
            "To release, raise `<Version>` in `Directory.Build.props`.\n" % (tag,)
        ),
    )


def stranded(version, tag, paths):
    """What to do when shipped code is on `main` and in no release.

    The message says what is stuck, what to do about it, and what not to do:
    moving the existing tag onto this commit would republish an already
    published name with different bytes, which is worse than a skipped number.
    That is the call made when this happened the first time, recorded here so
    it does not have to be made again under pressure.
    """
    suggestion = bump_suggestion(version)
    raise_it = (
        "Raise <Version> above %s -- %s." % (version, suggestion.replace("`", ""))
        if suggestion
        else "Raise <Version> above %s." % (version,)
    )
    return Decision(
        publish=False,
        ok=False,
        message=(
            "This merge changed %d file(s) under src/, but %s is already tagged,\n"
            "so nothing was published and those changes are in no release.\n"
            "\n"
            "  Stranded:\n"
            "%s\n"
            "\n"
            "  What to do:\n"
            "    1. Open Directory.Build.props at the root of the repository.\n"
            "    2. %s\n"
            "    3. Merge that on its own. The release it triggers carries\n"
            "       everything that has accumulated since %s, this merge\n"
            "       included.\n"
            "\n"
            "  Do not move %s onto this commit: its assets are already\n"
            "  published under that name, and a skipped version number is\n"
            "  cheaper than two different builds sharing one.\n"
            "\n"
            "  Why this is red rather than a quiet skip: this state is only\n"
            "  reachable by a mistake -- two branches taking the same version,\n"
            "  or a bump that was forgotten -- and it used to report success,\n"
            "  so it was found by somebody wondering why the download was still\n"
            "  the old one."
            % (
                len(paths),
                tag,
                listing(paths, bullet="    ", quote=""),
                raise_it,
                tag,
                tag,
            )
        ),
        summary=(
            "### This merge shipped nothing, and it should have\n"
            "\n"
            "`main` has changed **%d file(s) under `src/`** since `%s` was\n"
            "published, but `Directory.Build.props` still says `%s` and that tag\n"
            "already exists. Those commits are on `main` and in no release, so\n"
            "nobody can download them.\n"
            "\n"
            "%s\n"
            "\n"
            "**To ship them:** raise `<Version>` in `Directory.Build.props` above\n"
            "`%s` -- %s -- in a pull request of its own. Merging it publishes\n"
            "everything that has accumulated since `%s`, this merge included.\n"
            "\n"
            "**Do not move the `%s` tag onto this commit.** Its assets are\n"
            "already published under that name, and a skipped version number is\n"
            "cheaper than two different builds sharing one.\n"
            % (
                len(paths),
                tag,
                version,
                listing(paths),
                version,
                suggestion or "any higher version",
                tag,
                tag,
            )
        ),
    )


def decide(version, tag_exists, tag=None, changed=()):
    """Whether to publish, given the version, the tag and what has changed.

    [changed] is every path that differs between the tagged commit and the
    commit being released, and is only consulted when [tag_exists]. When the
    tag is free there is a release to make regardless of what moved.
    """
    if not version:
        return no_version()

    name = tag or "v%s" % version

    if not tag_exists:
        return publishable(name)

    paths = changed_paths(changed) if isinstance(changed, str) else list(changed)
    stuck = shipped(paths)
    if not stuck:
        return nothing_new(name)
    return stranded(version, name, stuck)


def changed_between(first, second, repo="."):
    """The paths that differ between two commits.

    Both refs are checked before the diff so an unreachable one is a loud
    failure rather than an empty diff. `git diff` against a ref it cannot
    resolve fails, but a caller that shrugged that off would read it as "no
    shipped file changed" -- which is the answer that publishes nothing and
    says everything is fine, exactly when this check has stopped working.
    """
    for ref in (first, second):
        if not ref_exists(ref, repo):
            raise RefError("%s does not name a commit in %s" % (ref, repo))
    diff = subprocess.run(
        [
            "git",
            "-C",
            repo,
            "diff",
            "--name-only",
            "%s^{commit}" % first,
            "%s^{commit}" % second,
        ],
        capture_output=True,
        text=True,
    )
    if diff.returncode != 0:
        raise RefError(
            "could not diff %s against %s in %s: %s"
            % (first, second, repo, diff.stderr.strip())
        )
    return changed_paths(diff.stdout)


def append(variable, text):
    """Append [text] to the file named by environment variable [variable].

    Absent outside Actions, where running this by hand should print rather than
    write anywhere, so a missing variable is not an error.
    """
    path = os.environ.get(variable)
    if not path or not text:
        return False
    with open(path, "a", encoding="utf-8") as handle:
        handle.write(text if text.endswith("\n") else text + "\n")
    return True


def flag(raw):
    """A `true`/`false` argument as a bool.

    Strict, because the two mistakes are not symmetrical: a typo read as
    "the tag does not exist" would send the workflow on to push a tag that is
    already there, and it would fail late, halfway through a release.
    """
    lowered = str(raw).strip().lower()
    if lowered in ("true", "false"):
        return lowered == "true"
    raise argparse.ArgumentTypeError("expected true or false, not %r" % (raw,))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--version",
        default="",
        help="The version in Directory.Build.props. Empty when it states none.",
    )
    parser.add_argument(
        "--tag",
        default="",
        help="The tag that version releases as, for example v0.4.1.",
    )
    parser.add_argument(
        "--tag-exists",
        required=True,
        type=flag,
        help="Whether the remote already has that tag. Answered by the "
        "workflow, which is the only part of this with a network.",
    )
    parser.add_argument(
        "--head",
        default="HEAD",
        help="The commit being released. Compared against the tag to find "
        "shipped code that no release contains.",
    )
    parser.add_argument(
        "--repo",
        default=".",
        help="The repository to read. Defaults to the working directory.",
    )
    args = parser.parse_args(argv)

    changed = ()
    if args.version and args.tag_exists:
        try:
            changed = changed_between(
                args.tag or "v%s" % args.version, args.head, args.repo
            )
        except RefError as error:
            print(
                "Could not compare this commit against the tag it would have\n"
                "released as: %s.\n"
                "\n"
                "  What to do: this is the check being broken rather than the\n"
                "  merge. The workflow fetches the tag immediately before\n"
                "  running this, so either that fetch failed or the tag it\n"
                "  fetches and the ref read here have drifted apart.\n"
                "\n"
                "  Why this is fatal rather than ignored: an unreadable tag\n"
                "  looks exactly like a tag nothing has changed since, and that\n"
                "  answer publishes nothing and reports success." % (error,)
            )
            print(
                "::error title=Release gate could not read the tag::%s" % (error,)
            )
            # Recorded even though this step is about to fail, so nothing that
            # runs afterwards can read an absent output as permission to
            # publish.
            append("GITHUB_OUTPUT", "publish=false")
            return 1

    decision = decide(args.version, args.tag_exists, args.tag, changed)

    print(decision.message)
    append("GITHUB_OUTPUT", "publish=%s" % ("true" if decision.publish else "false"))
    append("GITHUB_STEP_SUMMARY", decision.summary)
    if not decision.ok:
        # An annotation as well as the log, so the reason is on the run page
        # rather than inside a collapsed step nobody opens.
        print(
            "::error title=Merged code is in no release::%s"
            % decision.message.splitlines()[0]
        )
    return 0 if decision.ok else 1


if __name__ == "__main__":
    sys.exit(main())
