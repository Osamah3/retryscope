# Deviations from the pre-registration

Every change made after the `prereg-v1` tag is recorded here and mirrored as a transparent update on the OSF
registration. The paper's Deviations subsection is generated from this table.

| ID | Date | What changed | Why | Outcomes affected | Data seen when decided |
| --- | --- | --- | --- | --- | --- |
| — | — | No deviations yet (not yet registered). | — | — | — |

## Clarifications to frozen protocols (before pre-registration)

Operational details a frozen protocol did not specify, recorded before the data they affect were seen.

| ID | Date | Protocol | Clarification | Data seen when decided |
| --- | --- | --- | --- | --- |
| C1 | 2026-11-12 | corpus §1, §4 | Within an included repository, in-scope projects are built in their seeded order until 5 build; projects that fail to build are logged in `corpus/scope.csv` and not labelled. A repository is replaced only if none builds (§4.1). | Round-1 ledger (repository names and commits only); no repository cloned or opened |
| C2 | 2026-11-12 | corpus §4 | Builds run in the official .NET SDK container (`mcr.microsoft.com/dotnet/sdk:<major>.0`, major taken from the repository's `global.json`, default 10.0), with only the clone mounted, because building runs untrusted code. Restores use public NuGet only. | Same as C1 |
| C3 | 2026-11-12 | corpus §1 | In-scope classification is mechanical (`tools/prepare_corpus.py`): test = `IsTestProject`, test-framework packages, or a name ending in Test/Tests; AppHost = Aspire AppHost SDK or `IsAspireHost`; mobile/desktop = MAUI, WPF, WinForms or platform-specific target frameworks; deployable = Web, Worker, BlazorWebAssembly or Functions SDK, or `OutputType` Exe. A misclassification found while labelling is logged, not silently fixed. | Same as C1 |
