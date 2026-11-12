# Evaluation corpus protocol (RQ1: extraction accuracy)

Status: **frozen before sampling**. Written 12 Nov 2026, before any candidate list was retrieved. Any later change is
recorded in `docs/deviations.md` with its date and reason. This protocol becomes part of the pre-registration.

## 1. Unit of analysis

A **binding**: one HttpClient (named, typed, default or direct) in one deployable project, with the labelling-guide
fields (`docs/labelling-guide.md` v1.2). Repositories are sampled; within a sampled repository every binding in every
in-scope project is labelled, including projects where no binding is found (they test for false positives).

In-scope projects: deployable .NET projects (Web or Worker SDK, or `OutputType` Exe) that are not tests
(`IsTestProject`, names ending `Tests`/`Test`), not Aspire AppHosts, and not MAUI/mobile/desktop clients. At most
**5 projects per repository**, chosen with the seeded shuffle below, so one large repository cannot dominate.

## 2. Sampling frame

GitHub code search (`gh api search/code`, C# files, default branches), run once by `tools/sample_corpus.py frame` and
committed as `corpus/frame/` with the retrieval date. The API returns at most 1000 results per query, so each query is
split into file-size ranges (`size:0..1500`, `1501..3000`, `3001..6000`, `>6000`) to widen the frame. This cap is a
documented limitation: the frame is the union of what the API returns, not all of GitHub.

| Stratum | Query terms | Why |
| --- | --- | --- |
| S3 custom pipeline | `AddResilienceHandler` | rare; sampled first |
| S4 Polly v7 | `AddTransientHttpErrorPolicy`, `AddPolicyHandler` | rare; legacy pattern |
| S2 per-client standard | `AddStandardResilienceHandler AddHttpClient` | per-client handlers, stacking |
| S1 Aspire defaults | `ConfigureHttpClientDefaults AddStandardResilienceHandler` | template-dominated |

A repository found by several queries belongs to the first stratum in the order above (rarest first). Strata describe
how repositories were drawn; accuracy per pattern is reported by the **labelled** pattern codes, not by stratum.

Excluded before drawing: forks; the development set (`docs/dev-set.md`: dotnet/eShop, dotnet/aspire-samples); this
project's own repository (its fixtures match every query).

## 3. Drawing

For each stratum, the frame's repositories are sorted by full name and shuffled with Python's `random.Random` seeded
with the string `retryscope-corpus-v1-<stratum>` (seed version 2, deterministic across platforms). The order is
committed (`corpus/frame/<stratum>-order.txt`) before any repository is opened.

Repositories are taken in that order, **5 per stratum per round** (20 per round), and recorded in the ledger
`corpus/sample.csv` with the default branch's commit at drawing time. Every repository is analysed at that pinned
commit.

**Stopping rule:** rounds continue until at least **150 bindings** are labelled or **60 repositories** are included,
whichever comes first. A stratum whose order is exhausted is skipped. The 150 target gives a 95% interval of about
±5 percentage points on a proportion near 0.90.

## 4. Replacement and exclusions after drawing

A drawn repository is replaced by the next one in the same stratum's order, and the reason is logged in the ledger, when:

1. no in-scope project builds with the free .NET SDK within 15 minutes (`dotnet build`), e.g. private NuGet feeds,
   paid components, or missing workloads;
2. the repository was deleted or made private before cloning;
3. after reading the code, no in-scope project exists (e.g. a library-only repository).

A repository with in-scope projects but **no bindings is kept** (it counts toward precision). Replacement counts and
reasons per stratum are reported. Exclusion decisions are made before the extractor runs on the repository and are
never revisited after results are seen.

## 5. Labelling (blind)

1. Labels are written from the source at the pinned commit, following labelling guide v1.2, into
   `corpus/labels/<owner>__<repo>.json` (same format as the fixtures' `expected.json`).
2. The extractor is **not run** on a corpus repository until all labels are complete and frozen with git tag
   `labels-v1`. Before then, extractor output on corpus repositories is neither produced nor looked at.
3. The annotator records time spent and every case where the guide was ambiguous (`notes`), without changing the guide
   mid-corpus. Guide changes needed for later work become v1.3 and are logged as deviations.

## 6. Intra-rater reliability

A random 25% of labelled repositories (seeded: `retryscope-relabel-v1`) is labelled again at least **14 days** after
its first labelling, without opening the first labels. Agreement is reported per field: exact agreement for attempt
bounds and timeouts, Cohen's kappa for categorical fields (`retry_count_source`, `breaker`, `conditional`, `client_kind`).
It is reported as **intra-rater consistency of a single annotator**, never as independent validation.

## 7. After unblinding

1. Extractor output is produced for every labelled project and compared with the frozen labels by a script committed
   before tag `labels-v1`.
2. A label may be corrected after unblinding **only** with evidence from the executable oracle (running the project's
   real registration code against a failing stub, as for the fixtures) or from library source. Every correction is
   logged with before/after values and the evidence, and results are reported **both before and after** corrections.
3. Metrics follow the specification: binding detection precision, recall and F1; per-field accuracy; error
   distributions; recall per labelled pattern, with every pattern below 0.80 reported with its missed count, causes
   and implications. Targets (precision and recall ≥ 0.90) are acceptance targets, not assumed results.

## 8. What is published

Repository URLs, pinned commits, labels, extractor outputs and comparison results, under CC BY 4.0. No source code
from corpus repositories is copied into this repository.
