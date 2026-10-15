import json, re, sys, platform, warnings

warnings.simplefilter('ignore')  # a pattern that is merely suspicious is still a pattern that compiles
patterns = json.load(open(sys.argv[1], encoding='utf8'))
verdicts = []
for p in patterns:
    try:
        re.compile(p)
        verdicts.append(True)
    except Exception:
        verdicts.append(False)
print(json.dumps({'version': 'Python ' + platform.python_version(), 'verdicts': verdicts}))
