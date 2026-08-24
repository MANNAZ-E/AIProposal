---
name: commit
description: Stage, commit, and push the current changes to git in one step. Use when the user asks to commit, commit and push, or save work to git.
---

# Commit and push

1. Run `git status` and `git diff HEAD` to see what changed, and `git log -5 --oneline`
   to match the repo's commit message style.
2. Stage everything relevant: `git add -A` (skip junk — build output, secrets,
   scratch files; add them to `.gitignore` instead if they keep showing up).
3. Commit with a short imperative subject line (<72 chars) describing *why* the
   change was made, not a file-by-file list:

   ```
   git commit -m "$(cat <<'MSG'
   Short imperative summary

   Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
   MSG
   )"
   ```

4. Push: `git push`. If the branch has no upstream, use
   `git push -u origin HEAD`.
5. Report the commit hash, subject, and the branch it went to.

## Rules

- If `git status` shows nothing to commit, say so and stop — don't create an
  empty commit.
- Never use `--no-verify`, `--amend` on an already-pushed commit, or force push.
  If a hook fails, fix the underlying problem and retry.
- If the push is rejected as non-fast-forward, report it and ask before pulling
  or rebasing.
