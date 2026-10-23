#!/usr/bin/env python3
"""Builds VFR profiles for the military bases from the SIA eAIP page AD 2 <ICAO> (HTML).

Usage:  python scripts/extract_military_profiles.py LFBC,LFBM,... [--cycle eAIP_01_OCT_2026/FRANCE/AIRAC-2026-10-01]

Needs beautifulsoup4. Records what the page prints as text: position, elevation, magnetic variation, runways with
their magnetic QFU and size, and the VHF frequencies of APP, TWR, GND, PAR, SRE... (the UHF ones are counted in a
note: MSFS has no UHF radio). A profile already in data/vfr-profiles is never overwritten.
Flight simulation only: never use for real navigation.
"""
import argparse
import json
import os
import re
import sys
import urllib.request

from bs4 import BeautifulSoup

ROOT = os.path.join(os.path.dirname(__file__), '..')
NAMES_CS = os.path.join(ROOT, 'src', 'StormATC.Application', 'Charts', 'AerodromeNames.cs')
BASE = 'https://www.sia.aviation-civile.gouv.fr/media/dvd/{cycle}/html/eAIP/FR-AD-2.{icao}-fr-FR.html'
MONTHS = {m: i for i, m in enumerate(['JAN', 'FEB', 'MAR', 'APR', 'MAY', 'JUN', 'JUL', 'AUG', 'SEP', 'OCT', 'NOV', 'DEC'], 1)}
SERVICES = {'APP', 'TWR', 'GND', 'ATIS', 'DEP', 'PAR', 'SRE', 'CLR', 'AFIS', 'DEL'}


def names():
    out = {}
    for line in open(NAMES_CS, encoding='utf-8'):
        m = re.match(r'\s*\["(\w+)"\] = "(.*)",', line)
        if m:
            out[m.group(1)] = m.group(2)
    return out


def page_text(icao, cycle):
    request = urllib.request.Request(BASE.format(cycle=cycle, icao=icao), headers={'User-Agent': 'Mozilla/5.0'})
    with urllib.request.urlopen(request, timeout=60) as response:
        raw = response.read()
    soup = BeautifulSoup(raw, 'html.parser')
    for tag in soup(['script', 'style']):
        tag.decompose()
    text = soup.get_text('\n').replace('\r', '')
    text = re.sub(r'[ \t\xa0]+', ' ', text)
    return re.sub(r'\n\s*\n+', '\n', text)


def section(text, icao, number):
    start = re.search(rf'{icao}\.AD 2\.{number}\n', text)
    if not start:
        return ''
    end = re.search(rf'{icao}\.AD 2\.{number + 1}\n', text[start.end():])
    return text[start.end(): start.end() + end.start()] if end else text[start.end():]


def dms(degrees, minutes, seconds, hemisphere):
    value = int(degrees) + int(minutes) / 60 + float(seconds) / 3600
    return round(-value if hemisphere in 'SW' else value, 5)


def position(block):
    lat = re.search(r"(\d{2})\D(\d{2})\D(\d{2}(?:\.\d+)?)\"?([NS])", block)
    lon = re.search(r"(\d{3})\D(\d{2})\D(\d{2}(?:\.\d+)?)\"?([EW])", block)
    if not (lat and lon):
        return None
    return {'latitude': dms(*lat.groups()), 'longitude': dms(*lon.groups())}


def elevation(block):
    m = re.search(r'Reference elevation\s*\n\s*(-?\d+(?:\.\d+)?)\s*\n\s*ft', block)
    return int(round(float(m.group(1)))) if m else None


def variation(block):
    m = re.search(r'Magnetic variation\s*\n\s*(\d+(?:\.\d+)?)\D{0,3}([EW])', block)
    return round(float(m.group(1)) * (1 if m.group(2) == 'E' else -1), 1) if m else None


def runways(block):
    found = []
    pattern = (r'\n(\d{2}[LRC]?)\n\s*\d{3}\.\d{2}\n\s*\((\d{3})(?:\.\d+)?\)\n\s*(\d{3,4})\n\s*x\n\s*(\d{2,3})\n'
               r'(?:\d+ [A-Z]/[A-Z]/[A-Z]/[A-Z][^\n]*\n)?([^\n]*)\n')
    for m in re.finditer(pattern, block):
        surface = m.group(5).split('/')[-1].strip().lower()
        found.append((m.group(1), int(m.group(2)), int(m.group(3)), int(m.group(4)), surface))
    by_id = {designator: (qfu, length, width, surface) for designator, qfu, length, width, surface in found}
    out, used = [], set()
    for designator, (qfu, length, width, surface) in by_id.items():
        if designator in used:
            continue
        number = int(designator[:2])
        suffix = {'L': 'R', 'R': 'L', 'C': 'C', '': ''}[designator[2:]]
        reciprocal = f'{(number + 18 - 1) % 36 + 1:02d}{suffix}'
        if reciprocal in by_id:
            low, high = sorted([designator, reciprocal], key=lambda d: int(d[:2]))
            out.append({'id': f'{low}/{high}', 'qfu': [by_id[low][0], by_id[high][0]], 'surface': surface,
                        'lengthMetres': length, 'widthMetres': width})
            used.update([designator, reciprocal])
        else:
            out.append({'id': designator, 'qfu': [qfu], 'surface': surface, 'lengthMetres': length, 'widthMetres': width})
            used.add(designator)
    return out


def frequencies(block):
    lines = [line.strip() for line in block.splitlines()]
    label = None
    vhf, uhf = {}, 0
    for index, line in enumerate(lines):
        if line in SERVICES:
            label = line
            continue
        if label and re.fullmatch(r'\d{3}\.\d{3}', line) and index + 1 < len(lines) and lines[index + 1] == 'MHz':
            mhz = float(line)
            if 118 <= mhz <= 137:
                remark = lines[index + 3] if index + 3 < len(lines) else ''
                vhf.setdefault(label.lower(), []).append({'mhz': mhz, 'remark': remark.split('/')[0].strip()})
            elif mhz >= 225:
                uhf += 1
    return vhf, uhf


def build(icao, text, name_table):
    geo = section(text, icao, 2)
    pos, elev = position(geo), elevation(geo)
    if not (pos and elev is not None):
        return None
    vhf, uhf = frequencies(section(text, icao, 18))
    valid = re.search(r'valide .{1,6} partir du (\d{2}) ([A-Z]{3}) (\d{4})', text)
    date = f'{valid.group(3)}-{MONTHS[valid.group(2)]:02d}-{valid.group(1)}' if valid else None
    data = {
        '$comment': [
            f'EXTRACTED AUTOMATICALLY from the SIA eAIP page AD 2 {icao} (AIP France, valid from {date}), not read by hand.',
            'This is a military air base (CAM regimes). Only what the page prints as text is recorded: position, elevation,',
            'magnetic variation, runways and the VHF frequencies. Circuit rules and reporting points are NOT extracted.',
            'MSFS exposes only VHF radios: the UHF frequencies of the base are not usable and are not listed.',
            'Flight simulation only: never use for real navigation.',
        ],
        'icao': icao,
        'name': name_table.get(icao, icao),
        'source': {'publication': f'AIP France AD 2 {icao}', 'chartDate': date, 'extraction': 'automatic'},
        'referencePosition': pos,
        'elevationFeet': elev,
        'service': {'kind': 'MilitaryTower' if 'twr' in vhf else 'MilitaryApproach', 'regimes': ['CAM V', 'CAM I']},
        'frequencies': vhf,
    }
    magvar = variation(geo)
    if magvar is not None:
        data['magneticVariationDegrees'] = magvar
    rwy = runways(section(text, icao, 12))
    if rwy:
        data['runways'] = rwy
    data['notes'] = [f'The base also publishes {uhf} UHF frequencies that MSFS cannot tune.']
    return data


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('icaos')
    parser.add_argument('--cycle', default='eAIP_01_OCT_2026/FRANCE/AIRAC-2026-10-01')
    parser.add_argument('--out', default=os.path.join(ROOT, 'data', 'vfr-profiles'))
    args = parser.parse_args()
    table = names()
    for icao in args.icaos.split(','):
        target = os.path.join(args.out, f'{icao}.json')
        if os.path.exists(target):
            print(f'{icao}: kept (already there)')
            continue
        try:
            data = build(icao, page_text(icao, args.cycle), table)
        except Exception as ex:
            print(f'{icao}: {ex}', file=sys.stderr)
            continue
        if data is None:
            print(f'{icao}: page not recognised', file=sys.stderr)
            continue
        with open(target, 'w', encoding='utf-8', newline='\n') as out:
            json.dump(data, out, ensure_ascii=False, indent=2)
            out.write('\n')
        print(f'{icao}: {data["service"]["kind"]}, {len(data.get("runways", []))} runway(s), {sum(len(v) for v in data["frequencies"].values())} VHF')


if __name__ == '__main__':
    main()
