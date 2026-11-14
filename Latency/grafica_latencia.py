#!/usr/bin/env python3
"""
grafica_latencia.py - the latency chart for the presentation, from the Nov 10 measurement runs
(see README.md: the runs that are measurements). Standard Python only.

    python grafica_latencia.py        writes latencia_grafica.svg next to this file

One dot per cue (Unity sends it -> the motor's buzz at the PC microphone), grouped by how many
pods were connected; the line in each group is the median; the dashed line is the 100 ms target.
"""

import csv
import os
import statistics

HERE = os.path.dirname(os.path.abspath(__file__))
GROUPS = [
    ("1 pod conectado", ["latencia_20261110_173717.csv"], "#2a78d6"),
    ("Los 9 pods conectados", ["latencia_20261110_183626.csv", "latencia_20261110_183836.csv"], "#eb6834"),
]
TARGET = 100.0

SURFACE, INK, INK_2, INK_3, GRID = "#ffffff", "#0b0b0b", "#52514e", "#8a8984", "#e6e5e1"
FONT = "Inter, 'Segoe UI', Arial, sans-serif"


def read(names):
    values = []
    for name in names:
        with open(os.path.join(HERE, name), encoding="utf-8") as f:
            for row in csv.DictReader(f):
                if row["latencia_ms"]:
                    values.append(float(row["latencia_ms"]))
    return values


def main():
    data = [(label, read(files), color) for label, files, color in GROUPS]
    width, height = 1200, 700
    left, right, top, bottom = 110, 60, 150, 110
    plot_w, plot_h = width - left - right, height - top - bottom
    lo, hi = 0.0, 140.0

    def Y(v):
        return top + plot_h - (v - lo) / (hi - lo) * plot_h

    p = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" width="{width}" height="{height}" '
         f'font-family="{FONT}" role="img" aria-label="Latencia de una pista háptica">',
         f'<rect width="{width}" height="{height}" fill="{SURFACE}"/>',
         f'<text x="40" y="62" font-size="32" font-weight="600" fill="{INK}">¿Cuánto tarda una pista en sentirse?</text>',
         f'<text x="40" y="100" font-size="20" fill="{INK_2}">Desde que el sistema la manda hasta que vibra el motor · '
         f'un punto por pista · línea = mediana</text>']
    for v in range(0, 141, 20):
        p.append(f'<line x1="{left}" y1="{Y(v):.1f}" x2="{left + plot_w}" y2="{Y(v):.1f}" stroke="{GRID}" stroke-width="1"/>')
        p.append(f'<text x="{left - 14}" y="{Y(v) + 6:.1f}" font-size="18" fill="{INK_3}" text-anchor="end">{v}</text>')
    p.append(f'<text x="34" y="{top + plot_h / 2:.1f}" font-size="18" fill="{INK_2}" text-anchor="middle" '
             f'transform="rotate(-90 34 {top + plot_h / 2:.1f})">milisegundos</text>')

    # The target.
    p.append(f'<line x1="{left}" y1="{Y(TARGET):.1f}" x2="{left + plot_w}" y2="{Y(TARGET):.1f}" stroke="{INK_2}" '
             f'stroke-width="2" stroke-dasharray="8 6"/>')
    p.append(f'<text x="{left + plot_w - 4}" y="{Y(TARGET) - 10:.1f}" font-size="18" fill="{INK_2}" text-anchor="end">'
             f'Meta: menos de 100 ms</text>')

    band = plot_w / len(data)
    for i, (label, values, color) in enumerate(data):
        center = left + band * (i + 0.5)
        # Dots spread sideways where they crowd (a simple beeswarm), so every cue is visible.
        placed = []
        for v in sorted(values):
            y = Y(v)
            offset, k = 0.0, 0
            while any(abs(y - py) < 11 and abs(center + offset - px) < 11 for px, py in placed):
                k += 1
                offset = ((k + 1) // 2) * 11 * (1 if k % 2 else -1)
            placed.append((center + offset, y))
            p.append(f'<circle cx="{center + offset:.1f}" cy="{y:.1f}" r="5" fill="{color}" fill-opacity="0.9" '
                     f'stroke="{SURFACE}" stroke-width="2"><title>{v:.0f} ms</title></circle>')
        median = statistics.median(values)
        over = sum(1 for v in values if v > TARGET)
        p.append(f'<line x1="{center - 120:.1f}" y1="{Y(median):.1f}" x2="{center + 120:.1f}" y2="{Y(median):.1f}" '
                 f'stroke="{INK}" stroke-width="3" stroke-linecap="round"/>')
        p.append(f'<text x="{center + 132:.1f}" y="{Y(median) + 7:.1f}" font-size="22" font-weight="600" fill="{INK}">'
                 f'{median:.1f} ms</text>'.replace(".0 ms", " ms"))
        p.append(f'<circle cx="{center - 150:.1f}" cy="{top + plot_h + 44}" r="8" fill="{color}"/>')
        p.append(f'<text x="{center - 134:.1f}" y="{top + plot_h + 51}" font-size="22" font-weight="600" fill="{INK}">{label}</text>')
        p.append(f'<text x="{center - 134:.1f}" y="{top + plot_h + 80}" font-size="17" fill="{INK_2}">'
                 f'{len(values)} pistas · {len(values) - over} de {len(values)} bajo la meta</text>')
    p.append("</svg>")

    out = os.path.join(HERE, "latencia_grafica.svg")
    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(p))
    for label, values, _ in data:
        print(f"{label}: n={len(values)} mediana={statistics.median(values):.1f} ms "
              f"max={max(values):.0f} ms, sobre {TARGET:.0f} ms: {sum(v > TARGET for v in values)}")
    print(f"Escrito {out}")


if __name__ == "__main__":
    main()
