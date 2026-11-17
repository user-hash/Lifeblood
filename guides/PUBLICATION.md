# Public source boundary

Public material includes source, synthetic fixtures, schemas, reusable tools,
the MCP skill, maintained product guides in `guides/`, and contributor contracts
in `invariants/`. Working notes, intake/backlog ledgers, investigations, consumer
captures, local deployments and machine configuration stay outside tracked
source. The local `docs/` and `devmemory/` directories are ignored in full.

Keeping a file ignored does not remove a previously tracked copy or its history.
Review the complete proposed source tree and every outgoing branch/tag history.
An archive of a clean tree carries no Git history, while pushing an existing
branch can expose its ancestors. Old tags, branches, release source archives,
cached URLs and previously published packages require their own verification.

Run the source-export check against the exact candidate commit:

```sh
python tools/prepare-public-source.py --revision HEAD --check
python tools/prepare-public-source.py --revision HEAD --output /absolute/output/Lifeblood-src.zip
```

The exporter rejects working-record paths, unexpected root entries, build
outputs and machine-specific workspace paths before producing an archive. It
reads only the named Git tree, so ignored local files cannot enter the payload.
Review its result alongside a consumer-specific content scan and source/package
checks. A passing mechanical check is not proof that arbitrary prose contains
no confidential information, or that hosting caches have been erased.

Check packaged binaries separately with
`python tools/check-public-packages.py --commit <release-commit> <package.nupkg>`.
The build and publisher run this check before uploading packages. Official
builds normalize debug source paths; the package check rejects embedded machine
workspace paths and excluded working records, and verifies the source commit.

Prepare and inspect locally. Remote changes, workflow runs, tags and package
publication remain separate operations requiring authorization.
