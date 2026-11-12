# Labelling workflow (evaluation corpus)

How to label one corpus repository. The rules for *what* to write are in `docs/labelling-guide.md` v1.2; this page is
the procedure. Protocol: `docs/corpus-protocol.md` section 5.

## Ground rules

- **Never run the extractor** (`retryscope extract`) on a corpus repository, and do not look at any tool output for it,
  until every round-1 label is finished and tagged `labels-v1`.
- Label what the code at the pinned commit does, in the **Production** environment (guide hard case 1).
- Do not change the guide while labelling. When it is ambiguous, decide, write the decision in `guideAmbiguities`,
  and lower `confidence`.
- Reading the code is all that is needed. Do not run the repository's services.

## Per repository

1. `python tools/label_sheet.py new owner/repo`. This creates `corpus/labels/owner__repo.json` with the projects that
   built. The clone is in `D:\corpus-work\owner__repo`, already checked out at the pinned commit.
2. Open the clone in VS Code. For each project in the sheet:
   1. Start at its `Program.cs` (or `Startup.cs`) and follow every registration call, including helper methods and the
      shared ServiceDefaults project.
   2. Search the project and anything it references for `AddHttpClient`, `ConfigureHttpClientDefaults`,
      `AddStandardResilienceHandler`, `AddResilienceHandler`, `AddPolicyHandler`, `AddTransientHttpErrorPolicy`,
      `AddStandardHedgingHandler`, `RemoveAllResilienceHandlers` and `new HttpClient`.
   3. Add one binding per client (guide §2), using the template below.
   4. If the project turns out not to be deployable (e.g. a library or a test project classified as in-scope), set
      `"inScope": false` and explain in `notes` (clarification C3). Do not delete it.
3. `python tools/label_sheet.py check` until it prints `OK`.
4. `python tools/label_sheet.py done owner/repo <minutes>`, then commit:
   `git add corpus/labels && git commit -m "Label owner/repo"`.

A project with no HTTP clients keeps `"bindings": []`. That is a valid and useful label (it tests for false positives).

## Binding template

```json
{
  "client": "catalog",
  "clientKind": "named",
  "target": "catalog-api",
  "patternCodes": ["P1", "P2"],
  "attemptBoundGet": 16,
  "attemptBoundPost": 16,
  "retryCountSource": "default",
  "attemptTimeoutSeconds": 10,
  "totalTimeoutSeconds": 30,
  "breaker": true,
  "retryHandlers": 2,
  "conditional": false,
  "confidence": 3,
  "notes": ""
}
```

| Guide §3 field | JSON | Values |
| --- | --- | --- |
| client name | `client` | name given to `AddHttpClient`, short type name for typed clients, `""` for the default client, `Type.Method` for `new HttpClient` |
| `client_kind` | `clientKind` | `named`, `typed`, `default`, `direct` |
| `target` | `target` | host of `BaseAddress` (e.g. `catalog-api` from `https+http://catalog-api`), or `"?"` |
| `pattern_codes` | `patternCodes` | list, e.g. `["P1", "P2"]` |
| `attempt_bound_get/post` | `attemptBoundGet`, `attemptBoundPost` | integer ≥ 1, or `"?"` |
| `retry_handlers` | `retryHandlers` | integer |
| `attempt_timeout_s` | `attemptTimeoutSeconds` | number; `null` for none; `"?"` if unknown |
| `total_timeout_s` | `totalTimeoutSeconds` | number; `null` for none; `"?"` if unknown |
| `breaker` | `breaker` | `true`, `false`, `"?"` |
| `retry_count_source` | `retryCountSource` | `none` (no retry), `explicit`, `config`, `default`, `unresolved`, `opaque` |
| `conditional` | `conditional` | `true`, `false` |
| `confidence` | `confidence` | 1, 2, 3; `notes` required below 3 |

`retrying` (guide §3) is not stored: it follows from the bounds.

## Order and pace

Label round 1 in ledger order (`python tools/label_sheet.py status` lists it). Record honest minutes per repository;
labelling effort is reported. A random 25% is labelled again at least 14 days later (protocol section 6), so finish
round 1 with that in mind.
