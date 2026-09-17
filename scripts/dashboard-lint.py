#!/usr/bin/env python3
"""Lint the Grafana "Pulse Server" dashboard export against what the server exports.

Usage:
    python scripts/dashboard-lint.py [DASHBOARD_JSON] [--formatter PATH] [--strict] [--quiet-info]

Defaults: DASHBOARD_JSON = pulse-server-dashboard.json (repo root),
          --formatter    = src/DCLPulse/Metrics/PrometheusFormatter.cs.

Severities:
    E  error   — the dashboard is wrong or will not import cleanly. Exit code 1.
    W  warning — misleads an operator or breaks the house style. Exit 1 only with --strict.
    I  info    — consolidation candidates and coverage gaps. Never fails.

The mechanical rules live here so the dashboard-curator agent and a reviewer see the
same list. Judgement calls (is this panel answering a real question?) stay with the agent.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

METRIC_RE = re.compile(r"\b((?:dcl_pulse|dotnet|process)_[a-z0-9_]+)\b")
HISTOGRAM_SUFFIX_RE = re.compile(r"_(bucket|sum|count)$")
COUNTER_RE = re.compile(r"\b(dcl_pulse_[a-z0-9_]+_total)\b")
RANGE_FN_RE = re.compile(r"\b(rate|irate|increase|delta|idelta|\w+_over_time)\s*\(")
# A counter is also consumed correctly as an exact difference against its own past value:
# sum(x) - sum(x offset 1m). No extrapolation, so a test aid can read whole events.
COUNTER_DIFF_RE = re.compile(r"\boffset\s+\d+[smhdw]\b")
AGGREGATION_RE = re.compile(r"\b(sum|max|min|avg|count|topk|bottomk|quantile|group)\b\s*(?:by|without)?\s*\(")
LABEL_SPLIT_RE = re.compile(r"\b(?:by|without)\s*\(([^)]*)\)")
FIXED_WINDOW_RE = re.compile(r"\[\d+[smhdw]\]")
# The server writes le="1"; the scrape pipeline stores le="1.0". A literal match is silently empty.
EXACT_LE_RE = re.compile(r'le="(\d+(?:\.\d+)?)"')
DEPLOYMENT_NAME_RES = [
    re.compile(r"pulse-server-[a-z]+-[0-9a-f]{7}"),          # ECS service revision, e.g. blue/green + hash
    re.compile(r"[0-9a-f]{32}\.pulse-server[A-Za-z0-9.\-:_]*"),  # per-task Cloud Map hostname
]
INSTANCE_FILTER_RE = re.compile(r'instance\s*=~?\s*"[^"]*"')
DEFAULT_THRESHOLDS = [("green", None), ("red", 80)]
DESCRIBED_TYPES = {"timeseries", "stat", "heatmap", "gauge", "bargauge", "table", "barchart", "histogram"}
UNIT_TYPES = {"timeseries", "stat", "gauge", "bargauge"}
FULL_WIDTH = 24


class Findings:
    def __init__(self) -> None:
        self.rows: list[tuple[str, str]] = []

    def add(self, severity: str, panel: dict | None, message: str) -> None:
        where = f"id={panel.get('id')} '{panel.get('title')}'" if panel else "dashboard"
        self.rows.append((severity, f"{where}: {message}"))

    def count(self, severity: str) -> int:
        return sum(1 for s, _ in self.rows if s == severity)


def flatten(panels: list[dict]) -> list[dict]:
    """Every non-row panel, including those nested under collapsed rows."""
    out: list[dict] = []
    for p in panels:
        if p.get("type") != "row":
            out.append(p)
        out.extend(flatten(p.get("panels", [])))
    return out


def rows_in_order(panels: list[dict]) -> list[dict]:
    return [p for p in panels if p.get("type") == "row"]


def strings_in(node) -> list[str]:
    """Every string value anywhere inside a panel, unescaped, for name/filter scans."""
    if isinstance(node, str):
        return [node]
    if isinstance(node, dict):
        return [s for v in node.values() for s in strings_in(v)]
    if isinstance(node, list):
        return [s for v in node for s in strings_in(v)]
    return []


def exported_metrics(formatter_path: Path) -> set[str]:
    src = formatter_path.read_text(encoding="utf-8")
    return set(METRIC_RE.findall(src))


def metric_known(name: str, exported: set[str]) -> bool:
    return name in exported or HISTOGRAM_SUFFIX_RE.sub("", name) in exported


def target_exprs(panel: dict) -> list[tuple[dict, str]]:
    return [(t, (t.get("expr") or "").strip()) for t in panel.get("targets", [])]


def is_templated_datasource(ds) -> bool:
    if ds is None:
        return True  # inherits from the panel / dashboard default
    if isinstance(ds, str):
        return ds.startswith("$")
    uid = ds.get("uid", "")
    return uid.startswith("$") or uid in ("-- Grafana --", "-- Mixed --", "grafana")


def threshold_steps(panel: dict) -> list[tuple[str | None, float | None]]:
    steps = panel.get("fieldConfig", {}).get("defaults", {}).get("thresholds", {}).get("steps", [])
    return [(s.get("color"), s.get("value")) for s in steps]


def thresholds_drawn(panel: dict) -> bool:
    custom = panel.get("fieldConfig", {}).get("defaults", {}).get("custom", {})
    return custom.get("thresholdsStyle", {}).get("mode", "off") != "off"


def lint(dashboard: dict, exported: set[str], findings: Findings) -> None:
    for key in ("title", "uid", "schemaVersion", "panels"):
        if key not in dashboard:
            findings.add("E", None, f"missing top-level key '{key}'")

    top = dashboard.get("panels", [])
    panels = flatten(top)
    rows = rows_in_order(top)

    # --- identity --------------------------------------------------------------------------
    ids = Counter(p.get("id") for p in panels + rows)
    for pid, n in ids.items():
        if n > 1:
            findings.add("E", None, f"panel id {pid} used {n} times")

    referenced_metrics: set[str] = set()
    expr_owners: dict[str, list[dict]] = defaultdict(list)

    for p in panels:
        ptype = p.get("type")
        defaults = p.get("fieldConfig", {}).get("defaults", {})

        # --- datasource templating -----------------------------------------------------------
        if not is_templated_datasource(p.get("datasource")):
            findings.add("E", p, f"panel datasource is hard-coded: {p.get('datasource')}")
        for t in p.get("targets", []):
            if not is_templated_datasource(t.get("datasource")):
                findings.add("E", p, f"target {t.get('refId')} datasource is hard-coded: {t.get('datasource')}")

        # --- per-target query rules ----------------------------------------------------------
        for t, expr in target_exprs(p):
            if not expr:
                continue
            legend = t.get("legendFormat") or ""
            for name in METRIC_RE.findall(expr):
                referenced_metrics.add(name)
                if not metric_known(name, exported):
                    findings.add("E", p, f"target {t.get('refId')} references '{name}', not exported by PrometheusFormatter")
            if COUNTER_RE.search(expr) and not RANGE_FN_RE.search(expr) and not COUNTER_DIFF_RE.search(expr):
                findings.add("W", p, f"target {t.get('refId')} plots a _total counter raw; wrap in rate()/increase() (a lifetime total belongs on a stat, and says so in its description)")
            if RANGE_FN_RE.search(expr) and not AGGREGATION_RE.search(expr) and "{{" not in legend:
                findings.add("W", p, f"target {t.get('refId')} has no aggregation and a static legend '{legend}'; with several instances this draws N same-named series. Wrap in sum()")
            split = LABEL_SPLIT_RE.search(expr)
            if split and "{{" not in legend and not re.match(r"^\s*\$\{", expr):
                labels = [s.strip() for s in split.group(1).split(",") if s.strip() and s.strip() != "le"]
                if labels:
                    findings.add("W", p, f"target {t.get('refId')} splits by {labels} but legend '{legend}' has no {{{{label}}}} template")
            if FIXED_WINDOW_RE.search(expr) and "$__rate_interval" not in expr and "$__interval" not in expr:
                findings.add("I", p, f"target {t.get('refId')} uses a fixed range window {FIXED_WINDOW_RE.search(expr).group(0)}; $__rate_interval unless the fixed window is the point")
            for le in EXACT_LE_RE.findall(expr):
                findings.add("W", p, f"target {t.get('refId')} pins le=\"{le}\" literally; Prometheus stores bucket labels as floats (1.0), so match le=~\"{le.split('.')[0]}|{le.split('.')[0]}.0\" (no backslash: PromQL unescapes string literals Go-style first)")
            if legend == "__auto":
                findings.add("W", p, f"target {t.get('refId')} legend is '__auto'; name the series explicitly")
            expr_owners[re.sub(r"\s+", " ", expr)].append(p)

        # --- panel-level presentation rules -----------------------------------------------------
        if ptype in DESCRIBED_TYPES and not (p.get("description") or "").strip():
            findings.add("W", p, "no description; say what it measures, what normal looks like, and when to worry")
        if ptype in UNIT_TYPES and not defaults.get("unit"):
            findings.add("W", p, "no unit; counts use 'none', rates 'cps'/'cpm', bytes/s 'Bps', durations 'ms'/'µs', ratios 'percentunit'")
        steps = threshold_steps(p)
        if steps == DEFAULT_THRESHOLDS and not thresholds_drawn(p) and ptype != "stat":
            findings.add("W", p, "carries Grafana's default green/red@80 thresholds; clear to a single green step or make them meaningful and drawn")
        if steps == DEFAULT_THRESHOLDS and ptype == "stat":
            findings.add("W", p, "stat colors by the default red@80 threshold, which means nothing for this value")
        for o in p.get("fieldConfig", {}).get("overrides", []):
            m = o.get("matcher", {})
            if m.get("id") == "byNames" and m.get("options", {}).get("readOnly"):
                names = m.get("options", {}).get("names")
                findings.add("W", p, f"persisted legend-click hide: only {names} visible by default. Remove the readOnly byNames override")
        if ptype == "stat":
            calcs = p.get("options", {}).get("reduceOptions", {}).get("calcs", [])
            if not calcs:
                findings.add("W", p, "stat has no reducer; pick lastNotNull (current) or max (worst in window)")

        # --- environment coupling ------------------------------------------------------------
        texts = strings_in({k: v for k, v in p.items() if k != "panels"})
        for rx in DEPLOYMENT_NAME_RES:
            hits = {h for s in texts for h in rx.findall(s)}
            for hit in sorted(hits):
                findings.add("W", p, f"hard-coded deployment name '{hit}'; use a variable, a wildcard dimension, or an aggregate")
        instance_filters = {h for s in texts for h in INSTANCE_FILTER_RE.findall(s)}
        for hit in sorted(instance_filters):
            findings.add("I", p, f"instance filter {hit}; fine for shared runtime metrics (process_*, dotnet_*), a smell on dcl_pulse_* series")

        # --- layout ------------------------------------------------------------------------------
        gp = p.get("gridPos", {})
        if gp.get("x", 0) + gp.get("w", 0) > FULL_WIDTH:
            findings.add("E", p, f"gridPos overflows the 24-column grid: x={gp.get('x')} w={gp.get('w')}")

    # --- environment names pinned in templating (allowed there, and only there) ------------------------
    for v in dashboard.get("templating", {}).get("list", []):
        names = {h for s in strings_in(v) for rx in DEPLOYMENT_NAME_RES for h in rx.findall(s)}
        if names:
            findings.add("I", None, f"variable '{v.get('name')}' hard-codes {len(names)} deployment name(s); add the new one there after a blue/green rotation (values not printed: this output may be pasted into a public PR)")

    # --- coverage --------------------------------------------------------------------------------
    referenced_bases = {HISTOGRAM_SUFFIX_RE.sub("", m) for m in referenced_metrics} | referenced_metrics
    for name in sorted(exported):
        if name not in referenced_bases and HISTOGRAM_SUFFIX_RE.sub("", name) not in referenced_bases:
            findings.add("I", None, f"exported series '{name}' has no panel")

    # --- duplication -----------------------------------------------------------------------------
    for expr, owners in expr_owners.items():
        distinct = {o.get("id") for o in owners}
        if len(distinct) > 1:
            titles = ", ".join(f"id={o.get('id')} '{o.get('title')}'" for o in owners)
            findings.add("I", None, f"same query on {len(distinct)} panels ({titles}): {expr[:90]}")

    # --- rows --------------------------------------------------------------------------------------
    expanded = [r for r in rows if not r.get("collapsed")]
    loose = [p for p in top if p.get("type") != "row"]
    if rows and rows[0].get("collapsed") and loose:
        findings.add("I", None, f"the first row is collapsed while {len(loose)} panels sit expanded under '{expanded[0].get('title') if expanded else '?'}' further down; the dashboard opens on that row, not on an overview")
    for r in rows:
        nested = len(r.get("panels", []))
        if r.get("collapsed") and nested == 0:
            findings.add("W", r, "collapsed row with no panels")


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("dashboard", nargs="?", default="pulse-server-dashboard.json")
    ap.add_argument("--formatter", default="src/DCLPulse/Metrics/PrometheusFormatter.cs")
    ap.add_argument("--strict", action="store_true", help="warnings also fail the run")
    ap.add_argument("--quiet-info", action="store_true", help="suppress I rows")
    args = ap.parse_args(argv)

    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    findings = Findings()
    dash_path = Path(args.dashboard)
    try:
        dashboard = json.loads(dash_path.read_text(encoding="utf-8-sig"))
    except FileNotFoundError:
        print(f"[E] {dash_path} not found")
        return 1
    except json.JSONDecodeError as ex:
        print(f"[E] {dash_path} is not valid JSON: {ex}")
        return 1

    formatter_path = Path(args.formatter)
    if not formatter_path.exists():
        print(f"[E] formatter source {formatter_path} not found")
        return 1

    lint(dashboard, exported_metrics(formatter_path), findings)

    order = {"E": 0, "W": 1, "I": 2}
    for severity, message in sorted(findings.rows, key=lambda r: order[r[0]]):
        if severity == "I" and args.quiet_info:
            continue
        print(f"[{severity}] {message}")

    panels = flatten(dashboard.get("panels", []))
    print()
    print(f"{dash_path.name}: title={dashboard.get('title')!r} uid={dashboard.get('uid')} schema={dashboard.get('schemaVersion')} "
          f"version={dashboard.get('version')} rows={len(rows_in_order(dashboard.get('panels', [])))} panels={len(panels)}")
    print(f"errors={findings.count('E')} warnings={findings.count('W')} info={findings.count('I')}")

    if findings.count("E") or (args.strict and findings.count("W")):
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
