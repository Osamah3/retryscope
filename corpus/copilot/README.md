# AI second annotator (GitHub Copilot)

Copilot labels corpus repositories as a **second, AI annotator**, to measure human–AI agreement (clarification C8).
Its output is never the answer key: the human labels in `corpus/labels/` are.

Rules:
1. Run Copilot on a repository only **after** the human label for it is finished and committed (`label_sheet.py done`).
2. Open only the corpus clone in VS Code (not the retryscope repository), use Agent mode, and paste `PROMPT.md`
   verbatim with the repository's project list.
3. Save the answer as `corpus/copilot/<owner>__<repo>.json` and record the model shown in Copilot's model picker in
   `corpus/copilot/runs.csv` (`repo,model,date,notes`).
4. Exception: `MCKRUZ/microsoft-agentic-harness` was run **before** its human label (13–17 Nov 2026). Its human label is
   marked `"aiExposure": "copilot-output-seen"` and the repository is excluded from agreement statistics.
