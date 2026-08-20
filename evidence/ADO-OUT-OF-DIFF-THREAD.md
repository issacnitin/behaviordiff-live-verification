# Azure DevOps out-of-diff thread proof

Verified 2026-08-20 against disposable repository `BehaviorDiff-Thread-20260820-2128` in `devdiv/OnlineServices`.

- PR: [772049](https://devdiv.visualstudio.com/OnlineServices/_git/BehaviorDiff-Thread-20260820-2128/pullrequest/772049)
- PR diff: only `/samples/SampleApp/AccountStatus.cs`
- Summary thread: `9954025`, no file context
- Finding thread: `9954026`, `/samples/SampleApp/AccessControl.cs:8`
- API create/update: succeeded
- Reviewer visibility: visible on Overview by default; no manual file navigation required
- Iteration survival: active and returned for comparison after source iteration 2
- Idempotency: repeated production posts updated thread/comment `9954026/1`

The organization denied throwaway-project creation with `TF50309` (`Create new projects` required), so the proof used a uniquely named disposable repository in an existing project. No existing repository was modified.
