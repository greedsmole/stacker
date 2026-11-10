# Agent instructions

## Test execution discipline

Do not run the full test suite merely to confirm a documentation-only change, formatting change, or other change whose behavior is unchanged.

- Prefer static inspection, `git diff --check`, and `dotnet build -c Release --no-restore` first.
- Run only the smallest targeted test class or test filter that exercises changed behavior.
- Run the full `dotnet test` suite only when the user explicitly requests it, when production behavior or shared infrastructure changed, or when a targeted test cannot provide meaningful coverage.
- If a full suite is necessary, state why before launching it and use a bounded timeout. Do not leave a long-running test process unattended.
- Never rerun a hanging or already-running full suite without first identifying the cause and explaining the additional cost.

The goal is to spend execution time on evidence that can change the result, not on redundant waiting.
