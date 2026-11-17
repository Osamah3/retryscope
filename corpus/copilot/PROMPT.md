You are labelling a .NET repository for a research study. Read the code; do not run anything; do not guess.

TASK: For each project listed below, find every HTTP client the project uses at runtime and report its
retry/timeout configuration as it behaves in the Production environment.

PROJECTS (label only these):
<paste the "project" paths from the label file here>

WHAT COUNTS AS ONE CLIENT
- each services.AddHttpClient("name") or AddHttpClient<TClient>() call reached from the project's Program.cs
  (follow helper/extension methods and referenced ServiceDefaults projects)
- each `new HttpClient(...)` that calls another service (clientKind "direct")
- AddGrpcClient is out of scope: ignore it

HOW TO COUNT ATTEMPTS
- Find every retry handler applied to the client:
  (a) ConfigureHttpClientDefaults(h => h.AddStandardResilienceHandler()), often inside AddServiceDefaults(),
      applies to EVERY client of the project;
  (b) handlers chained on the AddHttpClient call: AddStandardResilienceHandler, AddResilienceHandler(... AddRetry ...),
      AddPolicyHandler / AddTransientHttpErrorPolicy (Polly v7).
- Attempt bound = product over all retry handlers of (retries + 1). No retry handler = 1.
- Standard handler default = 3 retries (so 4), unless o.Retry.MaxRetryAttempts is set or bound from config.
- RemoveAllResilienceHandlers() removes every Polly v8 handler added before it, including the defaults.
- o.Retry.DisableForUnsafeHttpMethods(): POST/PUT/PATCH/DELETE get 1 attempt from that handler.
- Standard handler defaults: attempt timeout 10 s, total timeout 30 s, circuit breaker on.
- If a value depends on something you cannot see (runtime config, environment variables), use "?".

OUTPUT: only JSON, one object per project:
{"project": "<path>", "bindings": [ {
  "client": "<name; short type name for typed; \"\" for the default client; Type.Method for new HttpClient>",
  "clientKind": "named|typed|default|direct",
  "target": "<host from BaseAddress, e.g. catalog-api, or \"?\">",
  "attemptBoundGet": <int or "?">, "attemptBoundPost": <int or "?">,
  "retryCountSource": "none|default|explicit|config|unresolved|opaque",
  "attemptTimeoutSeconds": <number, null if none, "?">, "totalTimeoutSeconds": <number, null if none, "?">,
  "breaker": true|false|"?", "retryHandlers": <int>, "conditional": true|false,
  "evidence": ["<file>:<line> for every registration and handler you used"]
} ] }
A project with no HTTP clients gets "bindings": [].
Every value must be backed by an "evidence" line. If you are not sure, say "?" rather than guess.
