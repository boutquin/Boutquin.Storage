#!/usr/bin/env python3
"""Recompute readiness evidence counts without promoting hypotheses into dispositions."""
import argparse
import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
EVIDENCE = ROOT / '.code-arena/oss-comparison/2026-10-26-followthrough'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--results-directory', type=Path, default=EVIDENCE / 'final-tests')
    results_directory = parser.parse_args().results_directory.resolve()
    original = (ROOT / '.code-arena/oss-comparison/2026-10-26/remaining-leads.md').read_text()
    required = set(re.findall(r'F-[AB]-C\d+-\d+', original))
    reports = {}
    for lane in ['io', 'columnar', 'distributed']:
        path = EVIDENCE / lane / 'lead-dispositions.md'
        reports[lane] = sorted(set(re.findall(r'F-[AB]-C\d+-\d+', path.read_text()))) if path.exists() else []
    present = set().union(*map(set, reports.values()))
    runs = []
    totals = dict(total=0, executed=0, passed=0, failed=0, notExecuted=0)
    for path in sorted(results_directory.glob('*.trx')):
        root = ET.parse(path).getroot()
        counters = next(item for item in root.iter() if item.tag.endswith('Counters'))
        counts = {key: int(counters.get(key, 0)) for key in totals}
        runs.append(dict(file=str(path.relative_to(ROOT)), **counts))
        for key, value in counts.items():
            totals[key] += value
    inventory = json.loads((EVIDENCE / 'source-inventory.json').read_text())
    summary = dict(result_directory=str(results_directory.relative_to(ROOT)),
                   required_lead_count=len(required), lead_ids_by_report=reports,
                   missing_lead_ids=sorted(required - present),
                   source_file_count=len(inventory),
                   source_only_distributed_files=sum(row['package'] == 'source-only' for row in inventory),
                   runs=runs, totals=totals,
                   caution='Lead ID presence is reconciliation only; read each disposition and executable evidence.')
    (EVIDENCE / 'computed-summary.json').write_text(json.dumps(summary, indent=2) + '\n')
    print(json.dumps(summary, indent=2))


if __name__ == '__main__':
    main()
