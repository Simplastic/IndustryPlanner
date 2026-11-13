# Usage: python convert_satisfactory.py data1.0.json ../Assets/Resources/Games/satisfactory.json
# Source data: https://github.com/greeny/SatisfactoryTools (data/data1.0.json, MIT licensed data).
# Converts SatisfactoryTools data1.0.json into IndustryPlanner's compact game file.
import json, sys
d = json.load(open(sys.argv[1]))
items = d['items']
def liquid(i): return items[i]['liquid'] if i in items else False
def amt(i, a): return round(a, 4)  # dataset already uses m3 for fluids
def per_min(i, a, t): return round(amt(i, a) * 60 / t, 4)
B = d['buildings']

# Hotbar layout: key, label, building ids. Virtual logistics nodes are prefixed IP_.
hotbar = [
 ("Digit1","Extraction",["Desc_MinerMk1_C","Desc_MinerMk2_C","Desc_MinerMk3_C","Desc_WaterPump_C","Desc_OilPump_C","Desc_FrackingExtractor_C"]),
 ("Digit2","Smelting",["Desc_SmelterMk1_C","Desc_FoundryMk1_C"]),
 ("Digit3","Constructor",["Desc_ConstructorMk1_C"]),
 ("Digit4","Assembler",["Desc_AssemblerMk1_C"]),
 ("Digit5","Manufacturer",["Desc_ManufacturerMk1_C"]),
 ("Digit6","Refining",["Desc_OilRefinery_C","Desc_Blender_C","Desc_Packager_C"]),
 ("Digit7","Advanced",["Desc_HadronCollider_C","Desc_Converter_C","Desc_QuantumEncoder_C"]),
 ("Digit8","Power",["Desc_GeneratorBiomass_Automated_C","Desc_GeneratorCoal_C","Desc_GeneratorFuel_C","Desc_GeneratorNuclear_C"]),
 ("Digit9","Logistics",["Desc_ConveyorAttachmentSplitter_C","Desc_ConveyorAttachmentMerger_C","IP_Input","IP_Output","IP_Bus"]),
 ("Digit0","Productions",["IP_Production"]),
]
waste = {"Desc_NuclearFuelRod_C":("Desc_NuclearWaste_C",50),"Desc_PlutoniumFuelRod_C":("Desc_PlutoniumWaste_C",10)}

buildings = []
for slot in hotbar:
  for bid in slot[2]:
    b = {"id":bid,"category":slot[1],"kind":"producer","power":0.0,"powerExponent":1.321929,"extractRate":0.0,"resources":[],"fuels":[]}
    if bid in ("Desc_ConveyorAttachmentSplitter_C", "Desc_ConveyorAttachmentMerger_C"):
      split = bid.endswith("Splitter_C")
      b.update(name="Splitter" if split else "Merger", kind="splitter" if split else "merger", powerExponent=1,
               maxIn=1 if split else 3, maxOut=3 if split else 1)
    elif bid.startswith("IP_"):
      b.update(name={"IP_Input":"Input","IP_Output":"Output","IP_Bus":"Bus (any number of links)","IP_Production":"Production line"}[bid],
               kind={"IP_Input":"input","IP_Output":"output","IP_Bus":"bus","IP_Production":"production"}[bid], powerExponent=1)
    else:
      src = B[bid]; b["name"] = src["name"]; m = src["metadata"]
      b["power"] = float(m.get("powerConsumption",0)); b["powerExponent"] = float(m.get("powerConsumptionExponent",1.321929))
      if bid in d['miners']:
        mi = d['miners'][bid]; b["kind"]="extractor"; b["resources"]=mi["allowedResources"]
        b["extractRate"] = round(mi["itemsPerCycle"]/(1000 if mi["allowLiquids"] else 1)/mi["extractCycleTime"]*60,4)
      elif bid == "Desc_WaterPump_C":
        b.update(kind="extractor", resources=["Desc_Water_C"], extractRate=120.0)
      elif bid in d['generators']:
        g = d['generators'][bid]; b["kind"]="generator"; b["power"]=-float(g["powerProduction"]); b["powerExponent"]=1
        water = round(g["powerProduction"]*g["waterToPowerRatio"]/1000*60,4) if g["waterToPowerRatio"] else 0
        for f in g["fuel"]:
          e = items[f]["energyValue"]  # MJ per item (or per m3 for fluids)
          if e <= 0: continue
          rate = round(g["powerProduction"]/e*60,4)
          w = waste.get(f); b["fuels"].append({"item":f,"perMin":rate,"waterPerMin":water,"byproduct":w[0] if w else "","byproductPerMin":round(rate*w[1],4) if w else 0})
    buildings.append(b)
bids = {b["id"] for b in buildings}

# --- Progression: the tier each recipe/building unlocks at (milestones and tutorial), MAM falls back to its building.
S = d['schematics']
def unlock_tier(rid):
  t = [x['tier'] for x in S.values() if rid in x['unlock'].get('recipes', []) and x['type'] in ('EST_Milestone', 'EST_Tutorial')]
  return min(t) if t else None
def building_tier(bid):
  if bid.startswith("IP_") or "ConveyorAttachment" in bid: return 0
  ts = [unlock_tier(k) for k, r in d['recipes'].items() if any(p['item'] == bid for p in r['products'])]
  ts = [t for t in ts if t is not None]
  return min(ts) if ts else 0
# Somersloop slots per production building (1.0).
sloops = {"Desc_SmelterMk1_C":1,"Desc_ConstructorMk1_C":1,"Desc_AssemblerMk1_C":2,"Desc_FoundryMk1_C":2,"Desc_OilRefinery_C":2,
  "Desc_Converter_C":2,"Desc_ManufacturerMk1_C":4,"Desc_Blender_C":4,"Desc_HadronCollider_C":4,"Desc_QuantumEncoder_C":4}
for b in buildings:
  b["tier"] = building_tier(b["id"]); b["amplifierSlots"] = sloops.get(b["id"], 0)
btier = {b["id"]: b["tier"] for b in buildings}

recipes = []
for r in d['recipes'].values():
  if not r['inMachine'] or not r['producedIn'] or r['producedIn'][0] not in bids: continue
  t = r['time']
  rec = {"id":r['className'],"name":r['name'].replace("Alternate: ",""),"building":r['producedIn'][0],"alternate":r['alternate'],"time":t,
   "inputs":[{"item":x['item'],"perMin":per_min(x['item'],x['amount'],t)} for x in r['ingredients']],
   "outputs":[{"item":x['item'],"perMin":per_min(x['item'],x['amount'],t)} for x in r['products']],
   "power": round((r['minPower']+r['maxPower'])/2,2) if r['isVariablePower'] else 0,
   "tier": max(unlock_tier(r['className']) or 0, btier[r['producedIn'][0]])}
  recipes.append(rec)
recipes.sort(key=lambda r:(r['building'],r['alternate'],r['name']))

used = set()
for r in recipes: used.update(x['item'] for x in r['inputs']+r['outputs'])
for b in buildings:
  used.update(b['resources'])
  for f in b['fuels']: used.update([f['item'], f['byproduct']] if f['byproduct'] else [f['item']])
used.add("Desc_Water_C")
# Item tier: earliest recipe that makes it; raw resources: earliest building/recipe that can use them.
itier = {}
def lower(i, t): itier[i] = min(itier.get(i, 99), t)
for r in recipes:
  if r['alternate']: continue
  for x in r['outputs']: lower(x['item'], r['tier'])
for b in buildings:
  for res in b['resources']:
    users = [r['tier'] for r in recipes if not r['alternate'] and any(x['item']==res for x in r['inputs'])]
    users += [g['tier'] for g in buildings if any(f['item']==res or (res=="Desc_Water_C" and f['waterPerMin']>0) for f in g['fuels'])]
    lower(res, max(b['tier'], min(users or [b['tier']])))
  for f in b['fuels']: lower(f['item'], itier.get(f['item'], b['tier']))
out_items = sorted(({"id":k,"name":v['name'],"fluid":v['liquid'],"sinkPoints":v.get('sinkPoints',0),"tier":itier.get(k,0)} for k,v in items.items() if k in used), key=lambda i:i['name'])

# Belts and pipes: in-game speeds per minute (not in the dataset), unlock tier from the schematics.
transport = []
for bid, name, rate, fluid in [("Desc_ConveyorBeltMk1_C","Belt Mk.1",60,False),("Desc_ConveyorBeltMk2_C","Belt Mk.2",120,False),
    ("Desc_ConveyorBeltMk3_C","Belt Mk.3",270,False),("Desc_ConveyorBeltMk4_C","Belt Mk.4",480,False),
    ("Desc_ConveyorBeltMk5_C","Belt Mk.5",780,False),("Desc_ConveyorBeltMk6_C","Belt Mk.6",1200,False),
    ("Desc_Pipeline_C","Pipe Mk.1",300,True),("Desc_PipelineMK2_C","Pipe Mk.2",600,True)]:
  transport.append({"id":bid,"name":name,"rate":rate,"fluid":fluid,"tier":building_tier(bid)})
phase = ["Start","Start","Start","Phase 1 delivered","Phase 1 delivered","Phase 2 delivered","Phase 2 delivered","Phase 3 delivered","Phase 3 delivered","Phase 4 delivered"]
game = {"id":"satisfactory","name":"Satisfactory","version":"1.0","themeClass":"theme-satisfactory","logo":"Icons/Satisfactory/Logo",
 "progressionLabel":"Tier","progression":[{"value":t,"name":f"Tier {t}","detail":phase[t]} for t in range(10)],
 "transport":transport,
 "amplifierName":"Somersloops","boostName":"Power shards","boostStep":0.5,"maxClock":2.5,
 "tagline":"Plan factories from your first miner to Project Assembly.",
 "items":out_items,"buildings":buildings,"recipes":recipes,
 "hotbar":[{"key":k,"label":l,"buildings":bs} for k,l,bs in hotbar]}
json.dump(game, open(sys.argv[2],'w'), separators=(',',':'))
print(len(out_items),"items",len(recipes),"recipes",len(buildings),"buildings")
for b in buildings: print(b['id'],b['name'],b['kind'],b['power'],b['extractRate'],[ (f['item'],f['perMin'],f['waterPerMin']) for f in b['fuels']][:3])
