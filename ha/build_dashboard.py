"""Builds ha/dashboard.yaml from the Jinja templates next to it.

The output has two parts to paste into a sections dashboard (raw configuration editor):
  computers_section  - goes into the "sections" list of an existing view (Wi-Fi table + graphs)
  hardware_view      - goes into the "views" list (one card per PC, up to MAX_PCS)

The table and the hardware cards find the PCs on their own. The graphs need a fixed entity list, and the
table maps BSSIDs to access point names. Both have examples here (LOCAL_EXAMPLE); put your own values into
the git-ignored ha/local.json (same shape). The script always writes dashboard.yaml with the examples, and
with ha/local.json also dashboard.local.yaml (git-ignored) with your values.
Usage: python ha/build_dashboard.py
"""
import json
import re
from pathlib import Path

HERE = Path(__file__).parent
MAX_PCS = 8

# ap_names: first 5 bytes of the BSSID → name. graph_pcs: name in the legend + entity_id prefix, to which
# _download / _signal are appended; "download" / "signal" give a full entity_id instead (e.g. older entities).
LOCAL_EXAMPLE = {
    "ap_names": {"aa:bb:cc:dd:00": "Router", "aa:bb:cc:dd:01": "Node 1"},
    "graph_pcs": [
        {"name": "Desktop", "prefix": "sensor.desktop"},
        {"name": "Notebook", "prefix": "sensor.notebook", "signal": "sensor.notebook_wi_fi_signal"},
    ],
}


def statistics_graph(title: str, suffix: str, pcs: list) -> dict:
    return {
        "type": "statistics-graph",
        "title": title,
        "entities": [{"entity": pc.get(suffix) or f"{pc['prefix']}_{suffix}", "name": pc["name"]} for pc in pcs],
        "days_to_show": 7,
        "period": "hour",
        "stat_types": ["mean"],
        "chart_type": "line",
        "hide_legend": False,
        # The section spans 4 view columns = 48 grid columns, so 24 is half the width.
        "grid_options": {"columns": 24},
    }


def with_ap_names(table: str, ap_names: dict) -> str:
    entries = "".join(f"\n  '{k}': '{v}'," for k, v in ap_names.items())
    out, n = re.subn(r"(\{%- set ap_names = \{).*?(\n\} -%\})", lambda m: m.group(1) + entries + m.group(2),
                     table, count=1, flags=re.S)
    assert n == 1, "ap_names block not found in computers.jinja"
    return out


def build(local: dict) -> dict:
    table = with_ap_names((HERE / "computers.jinja").read_text(encoding="utf-8").rstrip("\n"), local["ap_names"])
    hardware = (HERE / "hardware.jinja").read_text(encoding="utf-8").rstrip("\n")

    computers_section = {
        "type": "grid",
        "column_span": 4,
        "cards": [
            {"type": "heading", "heading": "Computers", "heading_style": "title", "icon": "mdi:monitor-multiple"},
            {"type": "markdown", "content": table, "grid_options": {"columns": "full"}},
            statistics_graph("Download (Mbit/s, hourly mean)", "download", local["graph_pcs"]),
            statistics_graph("Signal (dBm, hourly mean)", "signal", local["graph_pcs"]),
        ],
    }

    hardware_view = {
        "title": "Hardware",
        "path": "hardware",
        # No icon: with one, HA shows only the icon in the tab bar instead of the title.
        "type": "sections",
        "max_columns": 4,
        "sections": [
            {
                "type": "grid",
                "column_span": 2,
                "cards": [
                    {
                        "type": "markdown",
                        "content": hardware.replace("__INDEX__", str(i)),
                        "show_empty": False,
                        "grid_options": {"columns": "full"},
                    }
                ],
            }
            for i in range(MAX_PCS)
        ],
    }
    return {"computers_section": computers_section, "hardware_view": hardware_view}


# --- minimal YAML emitter (no PyYAML needed) --------------------------------------------------------------

def _scalar(v) -> str:
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, (int, float)):
        return str(v)
    s = str(v)
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def to_yaml(v, indent: int = 0) -> str:
    pad = " " * indent
    if isinstance(v, dict):
        lines = []
        for k, x in v.items():
            if isinstance(x, str) and "\n" in x:
                body = "\n".join((pad + "  " + line) if line else "" for line in x.rstrip("\n").split("\n"))
                lines.append(f"{pad}{k}: |-\n{body}")
            elif isinstance(x, (dict, list)) and x:
                lines.append(f"{pad}{k}:\n{to_yaml(x, indent + 2)}")
            else:
                lines.append(f"{pad}{k}: {_scalar(x) if not isinstance(x, (dict, list)) else ('{}' if isinstance(x, dict) else '[]')}")
        return "\n".join(lines)
    if isinstance(v, list):
        lines = []
        for x in v:
            if isinstance(x, (dict, list)):
                inner = to_yaml(x, indent + 2)
                lines.append(f"{pad}- {inner.lstrip()}")
            else:
                lines.append(f"{pad}- {_scalar(x)}")
        return "\n".join(lines)
    return pad + _scalar(v)


def write(name: str, local: dict) -> None:
    out = HERE / name
    out.write_text("# Generated by build_dashboard.py from computers.jinja / hardware.jinja; do not edit by hand.\n"
                   + to_yaml(build(local)) + "\n", encoding="utf-8")
    print(f"wrote {out}")


if __name__ == "__main__":
    write("dashboard.yaml", LOCAL_EXAMPLE)
    local_file = HERE / "local.json"
    if local_file.exists():
        write("dashboard.local.yaml", json.loads(local_file.read_text(encoding="utf-8")))
