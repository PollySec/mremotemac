#!/usr/bin/env python3
"""Converts Lucide SVG icons (ISC) into src/MRemoteMac/Converters/LucideData.cs (stroked path data, 24x24)."""
import os, re, sys, xml.etree.ElementTree as ET

def f(v): return f"{float(v):g}"
def rect(e):
    x=float(e.get('x',0)); y=float(e.get('y',0)); w=float(e.get('width')); h=float(e.get('height')); rx=float(e.get('rx',0) or 0); ry=float(e.get('ry',rx) or rx)
    if rx==0: return f"M{f(x)},{f(y)}h{f(w)}v{f(h)}h{f(-w)}Z"
    return (f"M{f(x+rx)},{f(y)}H{f(x+w-rx)}A{f(rx)},{f(ry)} 0 0 1 {f(x+w)},{f(y+ry)}V{f(y+h-ry)}A{f(rx)},{f(ry)} 0 0 1 {f(x+w-rx)},{f(y+h)}"
            f"H{f(x+rx)}A{f(rx)},{f(ry)} 0 0 1 {f(x)},{f(y+h-ry)}V{f(y+ry)}A{f(rx)},{f(ry)} 0 0 1 {f(x+rx)},{f(y)}Z")
def circle(e):
    cx=float(e.get('cx')); cy=float(e.get('cy')); r=float(e.get('r'))
    return f"M{f(cx-r)},{f(cy)}A{f(r)},{f(r)} 0 1 0 {f(cx+r)},{f(cy)}A{f(r)},{f(r)} 0 1 0 {f(cx-r)},{f(cy)}Z"
def ellipse(e):
    cx=float(e.get('cx')); cy=float(e.get('cy')); rx=float(e.get('rx')); ry=float(e.get('ry'))
    return f"M{f(cx-rx)},{f(cy)}A{f(rx)},{f(ry)} 0 1 0 {f(cx+rx)},{f(cy)}A{f(rx)},{f(ry)} 0 1 0 {f(cx-rx)},{f(cy)}Z"
def line(e): return f"M{f(e.get('x1'))},{f(e.get('y1'))}L{f(e.get('x2'))},{f(e.get('y2'))}"
def poly(e, close):
    pts=re.findall(r'[-\d.]+', e.get('points')); it=iter(pts); pairs=list(zip(it,it))
    return "M"+",".join(pairs[0])+"".join("L"+",".join(p) for p in pairs[1:])+("Z" if close else "")
NUM=r'[-+]?(?:\d+\.\d*|\.\d+|\d+)(?:[eE][-+]?\d+)?'
def path(d):
    # each <path> starts at (0,0): a leading relative 'm' is absolute; implicit pairs after it are relative linetos
    s=d.strip(); m=re.match(r'^m\s*('+NUM+r')[\s,]*('+NUM+r')\s*', s)
    if m:
        rest=s[m.end():]
        if re.match(r'^(?:'+NUM+')', rest): rest="l"+rest
        s=f"M{m.group(1)},{m.group(2)}{rest}"
    return s

def main(src_dir, out_file):
    icons={}
    for fn in sorted(os.listdir(src_dir)):
        if not fn.endswith('.svg'): continue
        parts=[]
        for e in ET.parse(os.path.join(src_dir, fn)).getroot().iter():
            t=e.tag.split('}')[-1]
            if t=='path': parts.append(path(e.get('d')))
            elif t=='rect': parts.append(rect(e))
            elif t=='circle': parts.append(circle(e))
            elif t=='ellipse': parts.append(ellipse(e))
            elif t=='line': parts.append(line(e))
            elif t=='polyline': parts.append(poly(e, False))
            elif t=='polygon': parts.append(poly(e, True))
        icons[fn[:-4]]=" ".join(parts)
    out=['// Generated from Lucide icons (https://lucide.dev), ISC License, Copyright (c) Lucide Contributors.',
         '// 24x24 viewbox, stroke 2, round caps and joins. Regenerate with tools/convert-lucide.py.',
         'using System.Collections.Frozen;','','namespace MRemoteMac.Converters;','','public static class LucideData','{',
         '    public static readonly FrozenDictionary<string, string> Paths = new Dictionary<string, string>(StringComparer.Ordinal)','    {']
    out+=[f'        ["{k}"] = "{v}",' for k,v in icons.items()]
    out+=['    }.ToFrozenDictionary();','}','']
    open(out_file,'w').write("\n".join(out))
    print(f"{len(icons)} icons -> {out_file}")

if __name__=='__main__':
    main(sys.argv[1], sys.argv[2] if len(sys.argv)>2 else 'src/MRemoteMac/Converters/LucideData.cs')
