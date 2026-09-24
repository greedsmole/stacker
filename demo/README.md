# Demo in the Stacker repository

Open this repository in Stacker while checked out on `master`.
The example source files exist only on their demo branches, outside the application projects.
No GitHub PRs or remote branches are needed.

## Authorization

`master → demo/auth-model → demo/auth-api → demo/auth-ui`

- Layer 1 adds `demo/Session.cs` with a guest session.
- Layer 2 changes the same file: guest becomes authenticated and a signed-in flag is added.
- Layer 3 adds a display name to the session and a separate `demo/login.json`.
- Click the stack title for the final result. Click layer 2 for its contribution.
- Through this layer on layer 2 shows the result of layers 1–2.
- Check layers 1 and 3: layer 3 still compares against layer 2, not layer 1.

## Payments

`master → demo/payment-model → demo/payment-validation`

- Layer 1 adds `demo/Payment.cs`.
- Layer 2 adds positive-amount validation and currency to the same file.
- Switch between stacks to check independent file/filter state.

Remain on master throughout. The files need not exist in the working tree for the viewer to show their committed contents.
The release publishes these demo branches. After a fresh clone, create their local refs once without switching away from master:

```sh
git branch --track demo/auth-model origin/demo/auth-model
git branch --track demo/auth-api origin/demo/auth-api
git branch --track demo/auth-ui origin/demo/auth-ui
git branch --track demo/payment-model origin/demo/payment-model
git branch --track demo/payment-validation origin/demo/payment-validation
```

These are ordinary Git branches; no GitHub PRs are required.
