#!/usr/bin/env python3
"""Hardware-as-code linter for MetroDom (ADR 0008).

Checks the records under hardware/ and Instruments/*/instrument.toml:
  1. parts/catalog.csv ids are unique lowercase slugs.
  2. inventory/stock.csv and every modules/*/bom.csv reference catalog parts
     (BOM rows may also be `cad:<path>` pointing at a file inside the module folder).
  3. Every modules/<id>/module.toml has the required keys, id == folder name, a known status,
     and interfaces defined in hardware/interfaces.md. Non-planned modules need README + BOM.
  4. Instruments reference existing modules.
  5. Build records (builds/MD-*.md) name an existing module or instrument.
  6. inventory/equipment.csv ids are unique slugs with a known role. Instruments'
     `requires_equipment` reference them.
  7. Module `fabrication` processes and fabrication/queue.csv rows use processes from
     fabrication/capabilities.csv. Queue rows reference existing modules and a known status.

  --buildable   also print how many of each module current stock can build.
  --fab-queue   also print open fabrication jobs grouped by process, with providers.

Stdlib only. Exit code 1 on any failure.
"""
from __future__ import annotations

import csv
import re
import sys
import tomllib
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
HW = ROOT / "hardware"
SLUG = re.compile(r"^[a-z0-9]+(-[a-z0-9]+)*$")
STATUSES = {"planned", "prototype", "proven", "deprecated"}
EQUIPMENT_ROLES = {"shop", "show", "both"}
QUEUE_STATUSES = {"queued", "batched", "done", "cancelled"}
MODULE_KEYS = ("id", "name", "revision", "status", "job", "interfaces")


def read_csv(path: Path, required: tuple[str, ...], errors: list[str]) -> list[dict[str, str]]:
    if not path.exists():
        errors.append(f"{rel(path)}: missing")
        return []
    with path.open(newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        missing = [c for c in required if c not in (reader.fieldnames or [])]
        if missing:
            errors.append(f"{rel(path)}: missing column(s) {', '.join(missing)}")
            return []
        return list(reader)


def rel(p: Path) -> str:
    return str(p.relative_to(ROOT))


def load_toml(path: Path, errors: list[str]) -> dict:
    try:
        return tomllib.loads(path.read_text(encoding="utf-8"))
    except tomllib.TOMLDecodeError as e:
        errors.append(f"{rel(path)}: invalid TOML ({e})")
        return {}


def interface_ids() -> set[str]:
    text = (HW / "interfaces.md").read_text(encoding="utf-8")
    return set(re.findall(r"^\|\s*`([a-z0-9-]+)`\s*\|", text, re.M))


def main() -> int:
    errors: list[str] = []

    catalog = read_csv(HW / "parts" / "catalog.csv", ("id", "name", "category"), errors)
    parts: set[str] = set()
    for i, row in enumerate(catalog, start=2):
        pid = row["id"]
        if not SLUG.match(pid):
            errors.append(f"hardware/parts/catalog.csv:{i}: id '{pid}' is not a lowercase slug")
        if pid in parts:
            errors.append(f"hardware/parts/catalog.csv:{i}: duplicate id '{pid}'")
        parts.add(pid)

    stock_rows = read_csv(HW / "inventory" / "stock.csv", ("part_id", "qty", "location"), errors)
    stock: dict[str, int] = {}
    for i, row in enumerate(stock_rows, start=2):
        if row["part_id"] not in parts:
            errors.append(f"hardware/inventory/stock.csv:{i}: unknown part '{row['part_id']}'")
        try:
            stock[row["part_id"]] = stock.get(row["part_id"], 0) + int(row["qty"])
        except ValueError:
            errors.append(f"hardware/inventory/stock.csv:{i}: qty '{row['qty']}' is not an integer")

    equipment: set[str] = set()
    eq_path = HW / "inventory" / "equipment.csv"
    for i, row in enumerate(read_csv(eq_path, ("id", "name", "role"), errors), start=2):
        if not SLUG.match(row["id"]) or row["id"] in equipment:
            errors.append(f"{rel(eq_path)}:{i}: id '{row['id']}' must be a unique lowercase slug")
        if row["role"] not in EQUIPMENT_ROLES:
            errors.append(f"{rel(eq_path)}:{i}: role must be one of {sorted(EQUIPMENT_ROLES)}")
        equipment.add(row["id"])

    fab_dir = HW / "fabrication"
    providers: dict[str, list[str]] = {}
    for row in read_csv(fab_dir / "capabilities.csv", ("id", "process", "provider"), errors):
        providers.setdefault(row["process"], []).append(row["id"])

    known_ifaces = interface_ids()
    modules: dict[str, dict] = {}
    boms: dict[str, list[dict[str, str]]] = {}
    for mdir in sorted(p for p in (HW / "modules").iterdir() if p.is_dir()):
        if mdir.name.startswith("_"):
            continue  # _template
        manifest = mdir / "module.toml"
        if not manifest.exists():
            errors.append(f"{rel(mdir)}: missing module.toml")
            continue
        m = load_toml(manifest, errors)
        for key in MODULE_KEYS:
            if key not in m:
                errors.append(f"{rel(manifest)}: missing '{key}'")
        if m.get("id") != mdir.name:
            errors.append(f"{rel(manifest)}: id '{m.get('id')}' must equal folder name")
        if m.get("status") not in STATUSES:
            errors.append(f"{rel(manifest)}: status must be one of {sorted(STATUSES)}")
        for iface in m.get("interfaces", []):
            if iface not in known_ifaces:
                errors.append(f"{rel(manifest)}: interface '{iface}' not in hardware/interfaces.md")
        for proc in m.get("fabrication", []):
            if proc not in providers:
                errors.append(f"{rel(manifest)}: fabrication process '{proc}' not in capabilities.csv")
        modules[mdir.name] = m

        bom_path = mdir / "bom.csv"
        needs_docs = m.get("status") != "planned"
        if needs_docs and not (mdir / "README.md").exists():
            errors.append(f"{rel(mdir)}: status '{m.get('status')}' requires README.md")
        if bom_path.exists() or needs_docs:
            rows = read_csv(bom_path, ("part_id", "qty"), errors)
            for i, row in enumerate(rows, start=2):
                pid = row["part_id"]
                if pid.startswith("cad:"):
                    if not (mdir / pid[4:]).exists():
                        errors.append(f"{rel(bom_path)}:{i}: CAD file '{pid[4:]}' not found")
                elif pid not in parts:
                    errors.append(f"{rel(bom_path)}:{i}: unknown part '{pid}'")
                if not row["qty"].isdigit():
                    errors.append(f"{rel(bom_path)}:{i}: qty '{row['qty']}' is not an integer")
            boms[mdir.name] = rows

    instruments: set[str] = set()
    for manifest in sorted((ROOT / "Instruments").glob("*/instrument.toml")):
        inst = load_toml(manifest, errors)
        instruments.add(inst.get("id", manifest.parent.name))
        for entry in inst.get("modules", []):
            if entry.get("id") not in modules:
                errors.append(f"{rel(manifest)}: unknown module '{entry.get('id')}'")
        for eq in inst.get("requires_equipment", []):
            if eq not in equipment:
                errors.append(f"{rel(manifest)}: unknown equipment '{eq}'")

    queue_path = fab_dir / "queue.csv"
    queue = read_csv(queue_path, ("id", "item", "module", "process", "qty", "status"), errors)
    for i, row in enumerate(queue, start=2):
        if row["process"] not in providers:
            errors.append(f"{rel(queue_path)}:{i}: unknown process '{row['process']}'")
        if row["module"] and row["module"] not in modules:
            errors.append(f"{rel(queue_path)}:{i}: unknown module '{row['module']}'")
        if row["status"] not in QUEUE_STATUSES:
            errors.append(f"{rel(queue_path)}:{i}: status must be one of {sorted(QUEUE_STATUSES)}")

    targets = {i.upper() for i in modules} | {i.upper() for i in instruments}
    for build in sorted((HW / "builds").glob("MD-*.md")):
        text = build.read_text(encoding="utf-8")
        ref = re.search(r"^\|\s*(?:Module|Instrument)\s*\|\s*([a-z0-9-]+)\s*\|", text, re.M)
        if not ref:
            errors.append(f"{rel(build)}: needs a '| Module | <id> |' or '| Instrument | <id> |' row")
        elif ref.group(1).upper() not in targets:
            errors.append(f"{rel(build)}: '{ref.group(1)}' is not a known module or instrument")
        if not re.match(r"^MD-[A-Z0-9-]+-\d{3}$", build.stem):
            errors.append(f"{rel(build)}: name must look like MD-<ID>-NNN.md")

    if "--buildable" in sys.argv:
        print("Buildable from current stock:")
        for mid, rows in sorted(boms.items()):
            need = [(r["part_id"], int(r["qty"])) for r in rows
                    if not r["part_id"].startswith("cad:") and r["qty"].isdigit() and int(r["qty"]) > 0]
            if not need:
                continue
            n = min(stock.get(pid, 0) // qty for pid, qty in need)
            short = [f"{pid} (have {stock.get(pid, 0)}, need {qty})"
                     for pid, qty in need if stock.get(pid, 0) < qty]
            print(f"  {mid}: {n}" + (f"   short: {', '.join(short)}" if n == 0 else ""))

    if "--fab-queue" in sys.argv:
        open_jobs = [r for r in queue if r["status"] in ("queued", "batched")]
        print(f"Open fabrication jobs: {len(open_jobs)}")
        for proc in sorted({r["process"] for r in open_jobs}):
            print(f"  {proc}  (providers: {', '.join(providers.get(proc, [])) or 'none'})")
            for r in open_jobs:
                if r["process"] == proc:
                    batch = f" [{r['batch']}]" if r.get("batch") else ""
                    print(f"    {r['qty']} x {r['item']} ({r['module']}, {r.get('material') or '?'}) "
                          f"{r['status']}{batch}")

    if errors:
        print(f"check_hardware: {len(errors)} problem(s)")
        for e in errors:
            print(f"  - {e}")
        return 1
    print(f"check_hardware: OK ({len(parts)} parts, {len(modules)} modules, "
          f"{len(instruments)} instrument manifest(s), {len(equipment)} equipment, "
          f"{len(queue)} fab job(s))")
    return 0


if __name__ == "__main__":
    sys.exit(main())
