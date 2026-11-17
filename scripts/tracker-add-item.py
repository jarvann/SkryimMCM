#!/usr/bin/env python3
"""Insert a work item into project_work_items.json after a sibling item.

Usage: scripts/tracker-add-item.py <sibling-id> '<item json>'  (run from the repo root)
"""
import json, sys
p='project_work_items.json'
d=json.load(open(p))
item=json.loads(sys.argv[2])
target=sys.argv[1]  # id of a sibling item; insert after it
def w(o):
    if isinstance(o,list):
        for i,v in enumerate(o):
            if isinstance(v,dict) and v.get('id')==target:
                o.insert(i+1,item); return True
            if w(v): return True
    elif isinstance(o,dict):
        for v in o.values():
            if w(v): return True
    return False
assert w(d)
open(p,'w').write(json.dumps(d,indent=2,ensure_ascii=True)+'\n')
