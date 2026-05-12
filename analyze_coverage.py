import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path


def analyze_coverage(xml_file):
    tree = ET.parse(xml_file)
    root = tree.getroot()

    class_coverage = defaultdict(lambda: {'covered': 0, 'not_covered': 0, 'package': ''})

    for pkg in root.findall('.//package'):
        pkg_name = pkg.get('name', '')
        if 'Tests' in pkg_name:
            continue
        for cls in pkg.findall('.//class'):
            cls_name = cls.get('name', 'Unknown')
            for line in cls.findall('.//line'):
                hits = int(line.get('hits', 0))
                if hits > 0:
                    class_coverage[cls_name]['covered'] += 1
                else:
                    class_coverage[cls_name]['not_covered'] += 1
            class_coverage[cls_name]['package'] = pkg_name

    sorted_classes = sorted(class_coverage.items(), key=lambda x: x[1]['not_covered'], reverse=True)

    print(f"{'Package':<45} {'Class':<50} {'Covered':<10} {'Uncovered':<10} {'%':<8}")
    print('-' * 125)
    for cls_name, stats in sorted_classes[:30]:
        total = stats['covered'] + stats['not_covered']
        pct = stats['covered'] / total * 100 if total else 0
        print(f"{stats['package']:<45} {cls_name:<50} {stats['covered']:<10} {stats['not_covered']:<10} {pct:<8.1f}")


if __name__ == "__main__":
    results_dir = Path('coverage-results')
    xmls = sorted(results_dir.glob('*/coverage.cobertura.xml'), key=lambda p: p.stat().st_mtime)
    if xmls:
        latest = xmls[-1]
        print(f"Using: {latest}\n")
        analyze_coverage(latest)
    else:
        analyze_coverage('coverage.xml')
