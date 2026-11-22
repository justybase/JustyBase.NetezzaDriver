# Vendored conformance inputs

`cases/handshake.json` is a copy of the handshake category from the shared
`conformance/` corpus. It is vendored so this repository's CI can run the
handshake profile without checking out its sibling repositories.

When a handshake case changes in the shared corpus:

```powershell
Copy-Item ..\..\conformance\cases\handshake.json eng\conformance\cases\handshake.json
```

The wire fixtures under
`src/JustyBase.NetezzaDriver.TestSupport/Fixtures/Wire/` follow the same rule:
copy them from `justybase_nz-rust/tests/fixtures/wire/` and keep
`wire-manifest.json` in sync with `conformance/fixtures/manifest.json`.
`WireFixtureManifestTests` checks the pinned SHA-256 values on every test run.

Run the offline profile locally with:

```powershell
dotnet run --project tools\JustyBase.NetezzaDriver.Conformance -- `
  --profile handshake --offline --cases-dir eng\conformance\cases
```
