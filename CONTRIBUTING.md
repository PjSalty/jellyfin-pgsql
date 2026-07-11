# Contributing

PRs welcome. Ground rules:

- The repo is an overlay: changes to upstream files go in `patches/` (git format-patch, one logical change per patch, with an Intent and Drop-when in the message), new files go in `overlay/`. Never edit files under `upstream/`; that directory is generated.
- `./build/assemble.sh && dotnet publish ... && ./tests/smoke.sh` must pass locally before you open a PR. CI runs exactly that.
- Anything that grows the divergence from upstream needs a matching row in `DIVERGENCE.md` and a reason it can't be upstreamed instead.
- Conventional commit messages (`feat:`, `fix:`, `cache:`, `ci:`); the changelog is generated from them. The commit-msg hook accepts `cache` as an extra type (configured in `.pre-commit-config.yaml`).
