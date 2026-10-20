#!/usr/bin/env bash
# Proves that the CI gates catch failures, using ONE throwaway branch (ci-trial/negative) instead of one branch per defect.
#
#   scripts/ci/negative-trials.sh <base-commit>
#
# For every defect the script makes a commit on top of <base-commit>, pushes it to ci-trial/negative (force), waits until
# GitHub has started a run for that commit, then moves on. Each commit gets its own run (see the concurrency group in
# .github/workflows/ci.yml). No pull request is opened. Delete the branch afterwards:
#   git push origin --delete ci-trial/negative
# Fake values only; nothing here is a real credential.
set -euo pipefail
BASE="${1:?usage: negative-trials.sh <base-commit>}"
BRANCH="ci-trial/negative"
ROOT="$(git rev-parse --show-toplevel)"
WT="$(mktemp -d)"
cleanup() { git -C "$ROOT" worktree remove --force "$WT" >/dev/null 2>&1 || true; }
trap cleanup EXIT
git -C "$ROOT" worktree add --detach "$WT" "$BASE" >/dev/null

push_trial() {  # name, commit subject; the caller has changed files in $WT
  git -C "$WT" add -A
  git -C "$WT" -c user.name="ci-trial" -c user.email="ci-trial@example.invalid" commit -q -m "ci-trial: $2"
  local sha; sha="$(git -C "$WT" rev-parse HEAD)"
  git -C "$WT" push -q --force origin "HEAD:refs/heads/$BRANCH"
  echo "pushed $1 ($sha)"
  for _ in $(seq 1 30); do  # wait for the run of this commit to exist before the next push
    gh run list --branch "$BRANCH" --json headSha --jq '.[].headSha' | grep -q "$sha" && break
    sleep 2
  done
  git -C "$WT" reset -q --hard "$BASE"; git -C "$WT" clean -qfd
}

mkdir -p "$WT/BC-LocalAI.Tests"
printf 'namespace BC_LocalAI.Tests;\n\npublic sealed class CiTrialFailingTest\n{\n    [Fact]\n    public void Trial_IntentionalFailure() => Assert.Fail("ci-trial: intentional");\n}\n' > "$WT/BC-LocalAI.Tests/CiTrialFailingTest.cs"
push_trial dotnet "a .NET test that fails"
printf "import test from 'node:test';\nimport assert from 'node:assert/strict';\ntest('ci-trial', () => { assert.equal(1, 2); });\n" > "$WT/tests/ui/ci-trial.test.mjs"
push_trial node "a JavaScript test that fails"
printf 'import unittest\n\n\nclass CiTrial(unittest.TestCase):\n    def test_fail(self):\n        self.fail("ci-trial")\n' > "$WT/tests/test_ci_trial.py"
push_trial python "a Python test that fails"
printf 'api_key = "ci-trial-notarealvalue-7f3a9c1d"\n' > "$WT/docs/ci-trial-secret.txt"  # ci-allow-secret (fake value for the trial)
push_trial secret-assignment "a fake secret in an assignment"
printf 'Server=db1;Database=x;Uid=u;%s=CiTrial123x;\n' "Pwd" > "$WT/docs/ci-trial-connection.txt"
push_trial secret-connection "a fake connection string with a password"
printf '# trial\n\nSee [missing](does-not-exist-ci-trial.md).\n' > "$WT/docs/ci-trial-link.md"
push_trial link "a link to a file that does not exist"
git -C "$WT" rm -q tests/test_ollama_script.py
push_trial count-drop "a whole test file removed"
printf 'import unittest\n\n\nclass CiTrialSkip(unittest.TestCase):\n    @unittest.skip("ci-trial")\n    def test_skipped(self):\n        pass\n' > "$WT/tests/test_ci_trial_skip.py"
push_trial skip "a test that is skipped"
echo "done; see: python scripts/ci/summarize-trial-runs.py --repo <owner/name>"
