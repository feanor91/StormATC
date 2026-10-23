#!/usr/bin/env python3
"""Extracts the facts printed in the header and runway table of SIA VAC charts into VFR profiles.

Usage:  python scripts/extract_vac_profiles.py <folder with AD-2.<ICAO>.pdf> [--out data/vfr-profiles] [--only LFAB,LFPL]

Needs `pdftotext` (poppler) on the PATH. Only what the chart prints as text is recorded: the services and their
frequencies (APP, TWR, GND, ATIS, AFIS, A/A), the elevation, the position, the magnetic variation and the runway
designators with their QFU. Circuit rules and reporting points are drawings, not text: they are NOT extracted, and a
profile made here says so. A profile already in the output folder is never overwritten (hand-made ones are better).
Flight simulation only: never use for real navigation.
"""
import argparse
import json
import os
import re
import subprocess
import sys

NAMES_CS = os.path.join(os.path.dirname(__file__), '..', 'src', 'StormATC.Application', 'Charts', 'AerodromeNames.cs')
MONTHS = {m: i for i, m in enumerate(['JAN', 'FEB', 'MAR', 'APR', 'MAY', 'JUN', 'JUL', 'AUG', 'SEP', 'OCT', 'NOV', 'DEC'], 1)}


def pdf_text(path):
    out = subprocess.run(['pdftotext', '-layout', path, '-'], capture_output=True, check=True).stdout
    return out.decode('utf-8', errors='replace')


def aerodrome_names():
    names = {}
    try:
        for line in open(NAMES_CS, encoding='utf-8'):
            m = re.match(r'\s*\["(\w+)"\] = "(.*)",', line)
            if m:
                names[m.group(1)] = m.group(2)
    except OSError:
        pass
    return names


def dms(text):
    m = re.search(r'(\d{1,3})\D+(\d{1,2})\D+(\d{1,2}(?:[.,]\d+)?)\D*([NSEW])', text)
    if not m:
        return None
    value = int(m.group(1)) + int(m.group(2)) / 60 + float(m.group(3).replace(',', '.')) / 3600
    return round(-value if m.group(4) in 'SW' else value, 5)


def header_of(text):
    lines = text.splitlines()
    return '\n'.join(lines[:16])


def frequencies(header):
    found = {}
    for label, value in re.findall(r'\b(APP|TWR|GND|SOL|ATIS(?:/V)?|AFIS|A/A|FIS|DEP|INFO)\b[^:\n(]{0,40}[:(]\s*([^\n]*)', header):
        label = {'SOL': 'GND', 'ATIS/V': 'ATIS'}.get(label, label)
        if re.match(r'\s*NIL', value):
            found.setdefault(label, None)
            continue
        m = re.search(r'(\d{3}\.\d{1,3})', value)
        if m:
            found.setdefault(label, float(m.group(1)))
    return found


def service_of(header, freqs):
    if freqs.get('TWR'):
        return 'TowerAndGround' if freqs.get('GND') else 'Tower'
    if freqs.get('AFIS') or re.search(r'\bAFIS\b', header):
        return 'Afis'
    if freqs.get('APP') or freqs.get('DEP'):
        return 'Approach'
    if freqs.get('A/A') or 'TWR' in freqs:
        return 'None'
    return None


def runways_of(text):
    lines = text.splitlines()
    for i, line in enumerate(lines):
        if re.search(r'\bRWY\s+QFU\b', line):
            block = []
            for follow in lines[i + 1:i + 22]:
                if re.search(r'Aides lumineuses|Lighting aids', follow):
                    break
                block.append(follow)
            runways = []
            for designator, qfu in re.findall(r'(?<![\d.])(\d{2}[LRC]?)\s+(\d{3})(?![\d.])', '\n'.join(block)):
                if 1 <= int(designator[:2]) <= 36 and 0 <= int(qfu) <= 360:
                    runways.append((designator, int(qfu)))
            return runways
    return []


def pair_runways(runways):
    """Groups the designators into physical runways: 09/27, 13L/31R."""
    by_id = {}
    for designator, qfu in runways:
        by_id.setdefault(designator, qfu)
    out, used = [], set()
    for designator, qfu in by_id.items():
        if designator in used:
            continue
        number = int(designator[:2])
        suffix = {'L': 'R', 'R': 'L', 'C': 'C', '': ''}[designator[2:]]
        reciprocal = f'{(number + 18 - 1) % 36 + 1:02d}{suffix}'
        if reciprocal in by_id and reciprocal not in used:
            low, high = sorted([designator, reciprocal], key=lambda d: int(d[:2]))
            out.append({'id': f'{low}/{high}', 'qfu': [by_id[low], by_id[high]]})
            used.update([designator, reciprocal])
        else:
            out.append({'id': designator, 'qfu': [qfu]})
            used.add(designator)
    return out


def chart_date(header):
    m = re.search(r'\b(\d{1,2})\s+(JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)\s+(\d{2,4})\b', header)
    if not m:
        return None
    year = int(m.group(3))
    year += 2000 if year < 100 else 0
    return f'{year:04d}-{MONTHS[m.group(2)]:02d}-{int(m.group(1)):02d}'


def build(icao, path, names):
    text = pdf_text(path)
    header = header_of(text)
    freqs = frequencies(header)
    elevation = (re.search(r'ALT\s*(?:Water\s*)?AD\s*:\s*(-?\d+)', header)
                 or re.search(r'ALT\s*(?:AD\s*)?SUP\s*:\s*(-?\d+)', header))
    lat = re.search(r'LAT\s*:\s*([^\n]*?)(?:\s{3,}|$)', header, re.M)
    lon = re.search(r'LONG\s*:\s*([^\n]*?)(?:\s{3,}|$)', header, re.M)
    var = re.search(r'VAR\s*:?\s*\n?[^\n]*?(\d+(?:[.,]\d)?)\s*\D{0,3}\s*([EW])\s*\((\d{4})\)', header + '\n' + '\n'.join(text.splitlines()[16:20]))
    if not (elevation and lat and lon):
        return None
    magvar = None
    if var:
        magvar = float(var.group(1).replace(',', '.')) * (1 if var.group(2) == 'E' else -1)
    kind = service_of(header, freqs)
    frequency_block = {}
    labels = {'APP': 'approach', 'TWR': 'tower', 'GND': 'ground', 'ATIS': 'atis', 'AFIS': 'afis', 'A/A': 'airAir', 'FIS': 'fis', 'DEP': 'departure', 'INFO': 'info'}
    for key, value in freqs.items():
        if value and key in labels:
            frequency_block[labels[key]] = value
    date = chart_date(header)
    data = {
        '$comment': [
            f'EXTRACTED AUTOMATICALLY from the text of the SIA chart AD 2 {icao} (AIP France, chart dated {date}), not read by hand.',
            'Only what the chart prints as text is recorded: services and frequencies, elevation, position, magnetic variation,',
            'runway designators and QFU. Circuit rules and reporting points are drawings and are NOT extracted: the engine uses its defaults.',
            'The charts themselves are (c) SIA and are not redistributed. Flight simulation only: never use for real navigation.',
        ],
        'icao': icao,
        'name': names.get(icao, icao),
        'source': {'publication': f'AIP France AD 2 {icao}', 'chartDate': date, 'extraction': 'automatic'},
        'referencePosition': {'latitude': dms(lat.group(1)), 'longitude': dms(lon.group(1))},
        'elevationFeet': int(elevation.group(1)),
    }
    if magvar is not None:
        data['magneticVariationDegrees'] = magvar
    if kind:
        data['service'] = {'kind': kind}
    if frequency_block:
        data['frequencies'] = frequency_block
    runways = pair_runways(runways_of(text))
    if runways:
        data['runways'] = runways
    return data


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('folder')
    parser.add_argument('--out', default=os.path.join(os.path.dirname(__file__), '..', 'data', 'vfr-profiles'))
    parser.add_argument('--only')
    parser.add_argument('--overwrite', action='store_true', help='also replace an existing automatic profile')
    args = parser.parse_args()
    names = aerodrome_names()
    only = set(args.only.split(',')) if args.only else None
    written = skipped = failed = 0
    for file in sorted(os.listdir(args.folder)):
        m = re.fullmatch(r'AD-2\.(L[FG][A-Z]{2})\.pdf', file)
        if not m or (only and m.group(1) not in only):
            continue
        icao = m.group(1)
        target = os.path.join(args.out, f'{icao}.json')
        if os.path.exists(target):
            existing = open(target, encoding='utf-8').read()
            if not args.overwrite or '"extraction": "automatic"' not in existing:
                skipped += 1
                continue
        try:
            data = build(icao, os.path.join(args.folder, file), names)
        except Exception as ex:  # a chart that cannot be read is reported, not fatal
            print(f'{icao}: {ex}', file=sys.stderr)
            failed += 1
            continue
        if data is None:
            print(f'{icao}: header not recognised', file=sys.stderr)
            failed += 1
            continue
        with open(target, 'w', encoding='utf-8', newline='\n') as out:
            json.dump(data, out, ensure_ascii=False, indent=2)
            out.write('\n')
        written += 1
    print(f'written {written}, kept {skipped}, failed {failed}')


if __name__ == '__main__':
    main()
